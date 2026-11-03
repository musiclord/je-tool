using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;
using static JET.Tests.Application.WorkingPaperExportTestSupport;

namespace JET.Tests.Application;

/// <summary>WorkingPaper 匯出：過期結果與已淘汰欄位的請求驗證。</summary>
public sealed class WorkingPaperExportRequestValidationTests
{
    [Fact]
    public void WorkingPaperFileSearch_FindsTimestampedFilesInSubdirectories()
    {
        using var host = new HandlerTestHost();
        var folder = Path.Combine(host.ProjectsRoot, "synthetic-outputs");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Synthetic_WorkingPaper_20261007_120000.xlsx");
        File.WriteAllText(path, "synthetic filename probe");
        Assert.Equal(path, Assert.Single(FindWorkingPapers(host.ProjectsRoot)));
    }
    [Fact]
    public async Task Export_StaleRunOrRevision_IsRejectedWithoutReport()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        var folder = Path.Combine(host.ProjectsRoot, prepared.ProjectId);

        var staleRun = JsonSerializer.Serialize(new
        {
            validationResultId = new string('a', 32),
            scenarioRevision = prepared.Revision,
            scenarioPositions = prepared.Positions
        });
        var runError = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workingPaperStream", staleRun));
        Assert.Equal(JetErrorCodes.StaleResult, runError.Code);
        // 裁定 2026-10-14-01 確認了這句的用字（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。
        Assert.Equal(
            "這份報告依據的資料驗證結果已不是目前的結果，請重新執行資料驗證後再產生。",
            runError.Message);

        var staleRevision = JsonSerializer.Serialize(new
        {
            validationResultId = prepared.ValidationResultId,
            scenarioRevision = "stale",
            scenarioPositions = prepared.Positions
        });
        var revisionError = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workingPaperStream", staleRevision));
        Assert.Equal(JetErrorCodes.StaleResult, revisionError.Code);
        // 2026-10-07：時間尾碼與子目錄反例首次失敗 ae7200be73cc4231bc617b19d5305d3d。
        Assert.Empty(FindWorkingPapers(folder));
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
            ["validationResultId"] = prepared.ValidationResultId,
            ["scenarioRevision"] = prepared.Revision,
            ["scenarioPositions"] = prepared.Positions,
            [field] = field == "sheets" ? new[] { WorkingPaperSheetCatalog.Cover } : "C:\\outside.xlsx"
        };

        var error = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(payload)));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
    }
}
