using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 裁定 2026-10-19-20 要求回報成功並提醒：產生報告時，報告檔寫好後才寫操作紀錄。寫不進去時兩種案件都回報成功，
/// 回應的 warnings 附「檔案已產生，但這次產生的檔案沒有寫入操作紀錄。」，失敗記進支援日誌。
/// 本機案件拿掉操作紀錄表，走正式的四種產生報告（修正前八種情境都沒有 warnings，
/// 收據 20261019-153310896-290b88f1b8b144509949c84cd11d2086）。SQL Server 案件寫入失敗時會往外丟錯誤，
/// 這裡用丟出錯誤的替身代替，不啟動 SQL Server。
/// </summary>
public sealed class ReportExportOperationLogWarningTests
{
    private const string ReportNotLogged = "檔案已產生，但這次產生的檔案沒有寫入操作紀錄。";

    [Theory]
    [InlineData("sqlite", "export.validationReports")]
    [InlineData("duckdb", "export.validationReports")]
    [InlineData("sqlite", "export.prescreenReport")]
    [InlineData("duckdb", "export.prescreenReport")]
    [InlineData("sqlite", "export.criteriaSelectionReport")]
    [InlineData("duckdb", "export.criteriaSelectionReport")]
    [InlineData("sqlite", "export.workingPaperStream")]
    [InlineData("duckdb", "export.workingPaperStream")]
    public async Task LocalCase_ReportIsPublishedWithTheReminderWhenTheAuditTableIsGone(string provider, string action)
    {
        using var host = new HandlerTestHost();
        var prepared = await ReportRevisionStateTests.PrepareAsync(host, provider);
        var prescreenResultId = action == "export.prescreenReport"
            ? (await host.DispatchAsync("prescreen.run")).GetProperty("resultRef").GetProperty("resultId").GetString()
            : null;
        await ReportRevisionStateTests.ExecuteAsync(host, prepared.Id, provider, "DROP TABLE operation_log;");

        var response = await host.DispatchAsync(action, Payload(action, prepared, prescreenResultId));

        Assert.True(response.GetProperty("ok").GetBoolean());
        JsonElement[] published = response.TryGetProperty("publishedReports", out var reports)
            ? [.. reports.EnumerateArray()]
            : [response.GetProperty("publishedReport")];
        Assert.NotEmpty(published);
        Assert.All(published, report => Assert.True(File.Exists(report.GetProperty("fullPath").GetString())));
        Assert.Equal([ReportNotLogged], response.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task ThrowingOperationLog_PublishesTheReportAndReturnsTheReminder()
    {
        const string projectId = "audit-log-throws";
        using var root = new TempProjectRoot();
        using var log = new SupportRingBufferLoggerProvider(10);
        var folder = new JetProjectFolder(root.Path);
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var store = new AuditedReportStore(
            new ProjectReportStore(folder), new ThrowingOperationLog(), log.CreateLogger("report-test"));
        var plan = JetAuditProgram.Plan(new ReportExportRequest("export.prescreenReport", projectId, PrescreenResultId: "prescreen-run"));
        var request = new ReportWriteRequest(
            ReportKind.PrescreenReport, plan.SourceRef, (stream, ct) => stream.WriteAsync(new byte[] { 1 }, ct).AsTask());
        var publishing = new List<ReportKind>();

        var facts = await new ReportExecutionPort(store, [request], publishing.Add).ExecuteAsync(plan, CancellationToken.None);

        var report = Assert.Single(facts.Reports);
        Assert.True(File.Exists(report.FullPath));
        Assert.Equal([ReportKind.PrescreenReport], publishing);
        Assert.Equal(ReportNotLogged, facts.OperationLogWarning);
        var entry = Assert.Single(log.Snapshot(), item => item.EventName == "operation_log.append_failed");
        Assert.Equal(ProjectOperations.ReportPublish, entry.Fields["operation"]);
        Assert.Equal(nameof(InvalidOperationException), entry.Fields["error_type"]);
        Assert.DoesNotContain("Synthetic audit failure", JsonSerializer.Serialize(log.Snapshot()));

        // 不經產生報告動作的寫入也一樣：報告檔照常發布，操作紀錄的失敗只記支援日誌。
        var direct = await store.WriteAsync(projectId, request, CancellationToken.None);
        Assert.True(File.Exists(direct.FullPath));
    }

    private static string Payload(string action, ReportRevisionStateTests.Prepared prepared, string? prescreenResultId) => action switch
    {
        "export.validationReports" => JsonSerializer.Serialize(new { resultId = prepared.ValidationResultId }),
        "export.prescreenReport" => JsonSerializer.Serialize(new { resultId = prescreenResultId }),
        "export.criteriaSelectionReport" => JsonSerializer.Serialize(new
        { validationResultId = prepared.ValidationResultId, revision = prepared.Revision }),
        _ => JsonSerializer.Serialize(new
        { validationResultId = prepared.ValidationResultId, scenarioRevision = prepared.Revision, scenarioPositions = new[] { 1 } })
    };

    private sealed class ThrowingOperationLog : IProjectOperationLog
    {
        public Task AppendAsync(string projectId, ProjectOperationEvent operationEvent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synthetic audit failure");

        public Task<bool> RequiresMappingRecommitAuditAsync(string projectId, string dataset, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
