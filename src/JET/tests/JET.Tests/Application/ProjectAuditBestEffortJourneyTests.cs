using System.Text.Json;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 2026-10-02 使用者裁定本機案件寫操作紀錄失敗不擋作業。操作紀錄表損壞時，
/// SQLite 與 DuckDB 案件的 GL 重新匯入、GL 重新配對與匯出報告都照常完成並回報成功，
/// 失敗只記在支援日誌的 project_audit.write_failed 事件。
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
        var firstBatch = await host.DispatchAsync(
            "query.dataPreview", JsonSerializer.Serialize(new { dataset = "glEntries", limit = 1 }));
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
        var projected = recommit.GetProperty("projectedRowCount").GetInt64();
        Assert.True(projected > 0);
        var entries = await host.DispatchAsync(
            "query.dataPreview", JsonSerializer.Serialize(new { dataset = "glEntries", limit = 1 }));
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
        // 讀取失敗時寧可少記一筆，所以這裡只有讀取階段的事件，沒有寫入。
        await AssertSupportEventAsync(host, projectId, "audit-best-effort-recommit", "mapping.recommit", "check");
        await AssertSupportEventAsync(
            host, projectId, "audit-best-effort-recommit-again", "mapping.recommit", "append");
        await AssertSupportEventAsync(host, projectId, "audit-best-effort-export", "report.publish", "append");
    }

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
