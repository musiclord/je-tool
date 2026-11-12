using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// production composition 的最小 audit journey：初次匯入／初次 mapping 不留痕；重匯入、recommit、
/// 正式報表發布／汰換與明示清理各由唯一寫入點 append 一筆。
/// </summary>
public sealed class ProjectOperationLoggingJourneyTests
{
    [Fact]
    public async Task LoggedOperations_AppendExactMinimalAuditEvents()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        Assert.Equal(0, await AuditCountAsync(host, context.ProjectId));

        var glFile = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportGlFile");
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
        {
            filePath = glFile.GetProperty("filePath").GetString(),
            fileName = glFile.GetProperty("fileName").GetString()
        }));
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                context.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
            amountMode = context.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));

        var tbFile = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportTbFile");
        await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new
        {
            filePath = tbFile.GetProperty("filePath").GetString(),
            fileName = tbFile.GetProperty("fileName").GetString()
        }));
        await host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                context.Demo.GetProperty("tb").GetProperty("mapping").GetRawText()),
            changeMode = context.Demo.GetProperty("tb").GetProperty("changeMode").GetString()
        }));

        var validation = await host.DispatchAsync("validate.run");
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString();
        await host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new { resultId }));
        await host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new { resultId }));

        var rows = await DemoProjectPipeline.QueryStringListAsync(
            host,
            context.ProjectId,
            "SELECT operation || '|' || target_type || '|' || target_id || '|' || " +
            "subject_count || '|' || replaced_count FROM operation_log " +
            "ORDER BY occurred_utc, event_id;");

        // 報告發布各留一筆，第二次是覆蓋同名檔的汰換。
        Assert.True(rows.Count == 6, $"Audit rows: {string.Join(" || ", rows)}");
        Assert.Contains($"{ProjectOperations.DataReimport}|dataset|gl|14000|1", rows);
        Assert.Contains($"{ProjectOperations.DataReimport}|dataset|tb|150|1", rows);
        Assert.Contains($"{ProjectOperations.MappingRecommit}|mapping|gl|14000|1", rows);
        Assert.Contains($"{ProjectOperations.MappingRecommit}|mapping|tb|150|1", rows);
        Assert.Contains($"{ProjectOperations.ReportPublish}|reportCatalog|infReport+validationReport|2|0", rows);
        Assert.Contains($"{ProjectOperations.ReportPublish}|reportCatalog|infReport+validationReport|2|2", rows);
    }

    private static Task<long> AuditCountAsync(HandlerTestHost host, string projectId) =>
        DemoProjectPipeline.QueryScalarAsync(host, projectId, "SELECT COUNT(*) FROM operation_log;");
}
