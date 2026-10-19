using System.Text.Json;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 2026-10-02 使用者裁定本機案件寫操作紀錄失敗不擋作業。操作紀錄表損壞時，
/// SQLite 與 DuckDB 案件的 GL 重新匯入、GL 重新配對與匯出報告都照常完成並回報成功，
/// 失敗只記在支援日誌的 project_audit.write_failed 事件。2026-10-19 起，重新匯入與重新配對的回應
/// 另在 warnings 附提醒（使用者裁定「回報成功並提醒」）。匯出報告同日另題裁定同樣提醒，
/// 四種產生報告的提醒在 ReportExportAuditLogWarningTests 檢查，這裡不重複。
/// </summary>
public sealed class ProjectAuditBestEffortJourneyTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AuditTableMissing_ReimportRecommitAndExport_SucceedAndLogSupportEvent(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var projectId = context.ProjectId;
        // 使用者 2026-10-08 裁定資料名稱全系統統一：資料預覽 wire key 與型別名稱同步改名。
        var firstBatch = await host.DispatchAsync(
            "query.dataPreview", JsonSerializer.Serialize(new { dataset = "gl", limit = 1 }));
        Assert.True(firstBatch.GetProperty("totalCount").GetInt64() > 0);

        await DropAuditTableAsync(host, projectId, provider);

        // GL 重新匯入：資料照常寫入並回報成功。
        var glFile = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportGlFile");
        var reimport = await host.DispatchAsync(
            "import.gl.fromFile",
            JsonSerializer.Serialize(new
            {
                filePath = glFile.GetProperty("filePath").GetString(),
                fileName = glFile.GetProperty("fileName").GetString()
            }),
            CancellationToken.None,
            "audit-best-effort-reimport");
        Assert.True(reimport.GetProperty("rowCount").GetInt64() > 0);
        Assert.False(string.IsNullOrWhiteSpace(reimport.GetProperty("batchId").GetString()));
        // 使用者 2026-10-19 裁定：寫不進操作紀錄時回報成功並提醒。本機案件不擋作業，但同樣要提醒。
        // 新增的斷言；修正前兩個資料庫的 warnings 都是空的（收據 20261019-125810518-202248fc37a24cb889e2ee537ada1604）。
        Assert.Contains(AuditLogNotWritten, Warnings(reimport));

        // GL 重新配對：投影照常完成並回報成功。
        var recommit = await host.DispatchAsync(
            "mapping.commit.gl",
            JsonSerializer.Serialize(new
            {
                mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    context.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
                amountMode = context.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
            }),
            CancellationToken.None,
            "audit-best-effort-recommit");
        Assert.True(recommit.GetProperty("ok").GetBoolean());
        Assert.Equal(reimport.GetProperty("batchId").GetString(), recommit.GetProperty("batchId").GetString());
        // 讀不到紀錄表時無法判斷這次算不算重新配對：不寫可能不實的紀錄，但照樣提醒這次沒有寫入操作紀錄。
        // 這條斷言原本是 DoesNotContain；獨立複審指出它和「寫不進操作紀錄時提醒」的裁定不符，改成 Contains
        // （修正前的失敗收據 20261019-133654138-d0d92aee886d478cae2074fd4f48c340）。
        Assert.Contains(AuditLogNotWritten, Warnings(recommit));
        var projected = recommit.GetProperty("projectedRowCount").GetInt64();
        Assert.True(projected > 0);
        var entries = await host.DispatchAsync(
            "query.dataPreview", JsonSerializer.Serialize(new { dataset = "gl", limit = 1 }));
        // 重新匯入同一份 demo 檔，投影後的分錄數應與表格損壞前相同。
        Assert.Equal(
            firstBatch.GetProperty("totalCount").GetInt64(),
            entries.GetProperty("totalCount").GetInt64());

        // 已有提交配對時再配對一次，一定要寫重新配對紀錄；寫入失敗也照常回報成功。
        var recommitAgain = await host.DispatchAsync(
            "mapping.commit.gl",
            JsonSerializer.Serialize(new
            {
                mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    context.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
                amountMode = context.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
            }),
            CancellationToken.None,
            "audit-best-effort-recommit-again");
        Assert.True(recommitAgain.GetProperty("ok").GetBoolean());
        Assert.Equal(projected, recommitAgain.GetProperty("projectedRowCount").GetInt64());
        Assert.Contains(AuditLogNotWritten, Warnings(recommitAgain));

        // 匯出報告：報告檔照常寫出並回報成功。
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString();
        var export = await host.DispatchAsync(
            "export.validationArtifacts",
            JsonSerializer.Serialize(new { runId }),
            CancellationToken.None,
            "audit-best-effort-export");
        Assert.True(export.GetProperty("ok").GetBoolean());
        var artifacts = export.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.Equal(2, artifacts.Length);
        Assert.All(artifacts, artifact => Assert.True(File.Exists(artifact.GetProperty("fullPath").GetString())));

        await AssertSupportEventAsync(host, projectId, "audit-best-effort-reimport", "data.reimport", "append");
        // 重新匯入會清掉已提交的配對，重新配對前要讀紀錄表判斷需不需要記重新配對。
        // 讀取失敗時寧可少記一筆，所以這裡只有讀取階段的事件，沒有寫入；回應照樣附提醒。
        await AssertSupportEventAsync(host, projectId, "audit-best-effort-recommit", "mapping.recommit", "check");
        await AssertSupportEventAsync(
            host, projectId, "audit-best-effort-recommit-again", "mapping.recommit", "append");
        await AssertSupportEventAsync(host, projectId, "audit-best-effort-export", "report.publish", "append");
    }

    private const string AuditLogNotWritten = "變更已儲存，但這次變更沒有寫入操作紀錄。";

    private static string[] Warnings(JsonElement response) =>
        [.. response.GetProperty("warnings").EnumerateArray().Select(static warning => warning.GetString()!)];

    private static async Task DropAuditTableAsync(HandlerTestHost host, string projectId, string provider)
    {
        ILocalProjectDatabase database = provider == "sqlite"
            ? new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot))
            : new DuckDbProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE audit_event_log;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertSupportEventAsync(
        HandlerTestHost host, string projectId, string correlationId, string operation, string phase)
    {
        var export = await host.DispatchAsync(
            "support.log.export",
            JsonSerializer.Serialize(new { projectId, correlationId }));
        var lines = await File.ReadAllLinesAsync(export.GetProperty("filePath").GetString()!);
        var events = lines
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(entry => entry.GetProperty("eventName").GetString() == "project_audit.write_failed")
            .ToArray();
        var entry = Assert.Single(events);
        var fields = entry.GetProperty("fields");
        Assert.Equal(operation, fields.GetProperty("operation").GetString());
        Assert.Equal(phase, fields.GetProperty("phase").GetString());
        Assert.Equal("Warning", entry.GetProperty("level").GetString());
    }
}
