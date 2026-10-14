using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;
using static JET.Tests.Application.WorkpaperExportTestSupport;

namespace JET.Tests.Application;

/// <summary>WorkingPaper 匯出：過期結果與已淘汰欄位的請求驗證。</summary>
public sealed class WorkpaperExportRequestValidationTests
{
    [Fact]
    public void WorkpaperFileSearch_FindsTimestampedFilesInSubdirectories()
    {
        using var host = new HandlerTestHost();
        var folder = Path.Combine(host.ProjectsRoot, "synthetic-outputs");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Synthetic_WorkingPaper_20261007_120000.xlsx");
        File.WriteAllText(path, "synthetic filename probe");
        Assert.Equal(path, Assert.Single(FindWorkpapers(host.ProjectsRoot)));
    }
    [Fact]
    public async Task Export_StaleRunOrRevision_IsRejectedWithoutArtifact()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        var folder = Path.Combine(host.ProjectsRoot, prepared.ProjectId);

        var staleRun = JsonSerializer.Serialize(new
        {
            validationRunId = new string('a', 32),
            scenarioRevision = prepared.Revision,
            scenarioPositions = prepared.Positions
        });
        var runError = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workpaperStream", staleRun));
        Assert.Equal(JetErrorCodes.StaleResult, runError.Code);
        // 使用者 2026-10-14 第三輪勾選頁確認 R3-55（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。
        Assert.Equal(
            "這份報告依據的資料驗證結果已不是目前的結果，請重新執行資料驗證後再產生。",
            runError.Message);

        var staleRevision = JsonSerializer.Serialize(new
        {
            validationRunId = prepared.ValidationRunId,
            scenarioRevision = "stale",
            scenarioPositions = prepared.Positions
        });
        var revisionError = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workpaperStream", staleRevision));
        Assert.Equal(JetErrorCodes.StaleResult, revisionError.Code);
        // 2026-10-07：時間尾碼與子目錄反例首次失敗 ae7200be73cc4231bc617b19d5305d3d。
        Assert.Empty(FindWorkpapers(folder));
    }

    [Theory]
    [InlineData("sheets")]
    [InlineData("outputPath")]
    public async Task Export_DeprecatedSheetOrPathField_IsRejected(string field)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        var payload = new Dictionary<string, object?>
        {
            ["validationRunId"] = prepared.ValidationRunId,
            ["scenarioRevision"] = prepared.Revision,
            ["scenarioPositions"] = prepared.Positions,
            [field] = field == "sheets" ? new[] { WorkpaperSheetCatalog.Cover } : "C:\\outside.xlsx"
        };

        var error = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(payload)));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
    }
}
