using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// production composition 的最小 audit journey：初次匯入／初次 mapping 不留痕；重匯入、recommit、
/// 正式報表發布／汰換與明示清理各由唯一寫入點 append 一筆。
/// </summary>
public sealed class ProjectAuditLoggingJourneyTests
{
    [Fact]
    public async Task ApprovedOperations_AppendExactMinimalAuditEvents()
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
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString();
        await host.DispatchAsync("export.validationArtifacts", JsonSerializer.Serialize(new { runId }));
        await host.DispatchAsync("export.validationArtifacts", JsonSerializer.Serialize(new { runId }));

        await host.DispatchAsync("validate.run");
        var cleanupPreview = await host.DispatchAsync("report.cleanupPreview");
        var candidateCount = cleanupPreview.GetProperty("candidateCount").GetInt32();
        Assert.Equal(3, candidateCount);
        await host.DispatchAsync("report.cleanupConfirm", JsonSerializer.Serialize(new
        {
            catalogRevision = cleanupPreview.GetProperty("catalogRevision").GetString()
        }));

        var rows = await DemoProjectPipeline.QueryStringListAsync(
            host,
            context.ProjectId,
            "SELECT operation || '|' || target_type || '|' || target_id || '|' || " +
            "subject_count || '|' || replaced_count FROM audit_event_log " +
            "ORDER BY occurred_utc, event_id;");

        Assert.True(rows.Count == 7, $"Audit rows: {string.Join(" || ", rows)}");
        Assert.Contains($"{ProjectAuditOperations.DataReimport}|dataset|gl|14000|1", rows);
        Assert.Contains($"{ProjectAuditOperations.DataReimport}|dataset|tb|150|1", rows);
        Assert.Contains($"{ProjectAuditOperations.MappingRecommit}|mapping|gl|14000|1", rows);
        Assert.Contains($"{ProjectAuditOperations.MappingRecommit}|mapping|tb|150|1", rows);
        Assert.Contains($"{ProjectAuditOperations.ReportPublish}|reportCatalog|accountMapping+infReport+validationReport|3|0", rows);
        Assert.Contains($"{ProjectAuditOperations.ReportPublish}|reportCatalog|accountMapping+infReport+validationReport|3|3", rows);
        Assert.Contains($"{ProjectAuditOperations.ReportCleanup}|reportCatalog|formalReports|3|0", rows);
    }

    private static Task<long> AuditCountAsync(HandlerTestHost host, string projectId) =>
        DemoProjectPipeline.QueryScalarAsync(host, projectId, "SELECT COUNT(*) FROM audit_event_log;");
}
