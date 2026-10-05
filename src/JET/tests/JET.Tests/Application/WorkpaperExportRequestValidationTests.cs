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
        Assert.Equal(
            "指定的 validate 執行結果已不是目前版本，請重新執行並使用最新結果。",
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
        Assert.Empty(Directory.GetFiles(folder, "*_WorkingPaper.xlsx"));
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
