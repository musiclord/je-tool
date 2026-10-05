using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 2026-10-02 使用者裁定本機案件寫操作紀錄失敗不擋作業：包裝類別吞下寫入與讀取失敗，
/// 只記一筆支援日誌事件；取消照常往外丟，成功時原樣轉交。
/// </summary>
public sealed class BestEffortProjectAuditLogTests
{
    private const string EventName = "project_audit.write_failed";

    [Theory]
    [InlineData(ProjectAuditOperations.DataReimport, ProjectAuditTargetTypes.Dataset, "gl")]
    [InlineData(ProjectAuditOperations.MappingRecommit, ProjectAuditTargetTypes.Mapping, "tb")]
    [InlineData(ProjectAuditOperations.ReportPublish, ProjectAuditTargetTypes.ReportCatalog, "validationReport")]
    public async Task Append_InnerFails_DoesNotThrowAndRecordsSupportEvent(
        string operation, string targetType, string targetId)
    {
        using var provider = new SupportRingBufferLoggerProvider(capacity: 8);
        var inner = new FakeAuditLog { AppendFailure = new InvalidOperationException(@"PRIVATE C:\Secret\case.db") };
        var audit = new BestEffortProjectAuditLog(inner, provider.CreateLogger("JET.Tests.Audit"));

        await audit.AppendAsync(
            "project-1",
            ProjectAuditEvent.Create(operation, targetType, targetId, 3, 1),
            CancellationToken.None);

        Assert.Equal(1, inner.AppendCalls);
        var entry = Assert.Single(provider.Snapshot());
        Assert.Equal(EventName, entry.EventName);
        Assert.Equal("Warning", entry.Level);
        Assert.Equal(operation, entry.Fields["operation"]);
        Assert.Equal("append", entry.Fields["phase"]);
        var line = SupportDiagnosticNdjson.SerializeLine(entry);
        Assert.Contains("InvalidOperationException", line, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", line, StringComparison.Ordinal);
        Assert.DoesNotContain("case.db", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiresRecommitCheck_InnerFails_ReturnsFalseAndRecordsSupportEvent()
    {
        using var provider = new SupportRingBufferLoggerProvider(capacity: 8);
        var inner = new FakeAuditLog
        {
            CheckResult = true,
            CheckFailure = new InvalidOperationException("PRIVATE no such table")
        };
        var audit = new BestEffortProjectAuditLog(inner, provider.CreateLogger("JET.Tests.Audit"));

        var requires = await audit.RequiresMappingRecommitAuditAsync("project-1", "gl", CancellationToken.None);

        Assert.False(requires);
        Assert.Equal(1, inner.CheckCalls);
        var entry = Assert.Single(provider.Snapshot());
        Assert.Equal(EventName, entry.EventName);
        Assert.Equal(ProjectAuditOperations.MappingRecommit, entry.Fields["operation"]);
        Assert.Equal("check", entry.Fields["phase"]);
        Assert.DoesNotContain("PRIVATE", SupportDiagnosticNdjson.SerializeLine(entry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiresRecommitCheck_Cancelled_PropagatesWithoutSupportEvent()
    {
        using var provider = new SupportRingBufferLoggerProvider(capacity: 8);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var inner = new FakeAuditLog { CheckFailure = new OperationCanceledException(cancellation.Token) };
        var audit = new BestEffortProjectAuditLog(inner, provider.CreateLogger("JET.Tests.Audit"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            audit.RequiresMappingRecommitAuditAsync("project-1", "gl", cancellation.Token));

        Assert.Empty(provider.Snapshot());
    }

    [Fact]
    public async Task Append_Cancelled_PropagatesWithoutSupportEvent()
    {
        using var provider = new SupportRingBufferLoggerProvider(capacity: 8);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var inner = new FakeAuditLog { AppendFailure = new OperationCanceledException(cancellation.Token) };
        var audit = new BestEffortProjectAuditLog(inner, provider.CreateLogger("JET.Tests.Audit"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => audit.AppendAsync(
            "project-1",
            ProjectAuditEvent.Create(ProjectAuditOperations.DataReimport, ProjectAuditTargetTypes.Dataset, "gl", 1),
            cancellation.Token));

        Assert.Empty(provider.Snapshot());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Success_ForwardsToInnerUnchanged(bool checkResult)
    {
        using var provider = new SupportRingBufferLoggerProvider(capacity: 8);
        var inner = new FakeAuditLog { CheckResult = checkResult };
        var audit = new BestEffortProjectAuditLog(inner, provider.CreateLogger("JET.Tests.Audit"));
        var auditEvent = ProjectAuditEvent.Create(
            ProjectAuditOperations.DataReimport, ProjectAuditTargetTypes.Dataset, "gl", 5, 1);
        using var cancellation = new CancellationTokenSource();

        await audit.AppendAsync("project-1", auditEvent, cancellation.Token);
        var requires = await audit.RequiresMappingRecommitAuditAsync("project-1", "tb", cancellation.Token);

        Assert.Equal(checkResult, requires);
        Assert.Same(auditEvent, inner.LastEvent);
        Assert.Equal("project-1", inner.LastProjectId);
        Assert.Equal("tb", inner.LastDataset);
        Assert.Equal(cancellation.Token, inner.LastToken);
        Assert.Empty(provider.Snapshot());
    }

    private sealed class FakeAuditLog : IProjectAuditLog
    {
        public Exception? AppendFailure { get; init; }
        public Exception? CheckFailure { get; init; }
        public bool CheckResult { get; init; }
        public int AppendCalls { get; private set; }
        public int CheckCalls { get; private set; }
        public ProjectAuditEvent? LastEvent { get; private set; }
        public string? LastProjectId { get; private set; }
        public string? LastDataset { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task AppendAsync(string projectId, ProjectAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            AppendCalls++;
            LastProjectId = projectId;
            LastEvent = auditEvent;
            LastToken = cancellationToken;
            return AppendFailure is null ? Task.CompletedTask : Task.FromException(AppendFailure);
        }

        public Task<bool> RequiresMappingRecommitAuditAsync(
            string projectId,
            string dataset,
            CancellationToken cancellationToken)
        {
            CheckCalls++;
            LastDataset = dataset;
            LastToken = cancellationToken;
            return CheckFailure is null ? Task.FromResult(CheckResult) : Task.FromException<bool>(CheckFailure);
        }
    }
}
