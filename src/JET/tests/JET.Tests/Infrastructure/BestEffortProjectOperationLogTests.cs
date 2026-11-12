using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 本機案件寫操作紀錄失敗不擋作業，包裝類別吞下寫入與讀取失敗，
/// 只記一筆支援日誌事件；取消照常往外丟，成功時原樣轉交。
/// </summary>
public sealed class BestEffortProjectOperationLogTests
{
    private const string EventName = "project_audit.write_failed";

    [Theory]
    [InlineData(ProjectOperations.DataReimport, ProjectOperationTargetTypes.Dataset, "gl")]
    [InlineData(ProjectOperations.MappingRecommit, ProjectOperationTargetTypes.Mapping, "tb")]
    [InlineData(ProjectOperations.ReportPublish, ProjectOperationTargetTypes.ReportCatalog, "validationReport")]
    public async Task Append_InnerFails_DoesNotThrowAndRecordsSupportEvent(
        string operation, string targetType, string targetId)
    {
        using var provider = new SupportRingBufferLoggerProvider(capacity: 8);
        var inner = new FakeOperationLog { AppendFailure = new InvalidOperationException(@"PRIVATE C:\Secret\case.db") };
        var audit = new BestEffortProjectOperationLog(inner, provider.CreateLogger("JET.Tests.Audit"));

        await audit.AppendAsync(
            "project-1",
            ProjectOperationEvent.Create(operation, targetType, targetId, 3, 1),
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
        var inner = new FakeOperationLog
        {
            CheckResult = true,
            CheckFailure = new InvalidOperationException("PRIVATE no such table")
        };
        var audit = new BestEffortProjectOperationLog(inner, provider.CreateLogger("JET.Tests.Audit"));

        var requires = await audit.RequiresMappingRecommitAuditAsync("project-1", "gl", CancellationToken.None);

        Assert.False(requires);
        Assert.Equal(1, inner.CheckCalls);
        var entry = Assert.Single(provider.Snapshot());
        Assert.Equal(EventName, entry.EventName);
        Assert.Equal(ProjectOperations.MappingRecommit, entry.Fields["operation"]);
        Assert.Equal("check", entry.Fields["phase"]);
        Assert.DoesNotContain("PRIVATE", SupportDiagnosticNdjson.SerializeLine(entry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiresRecommitCheck_Cancelled_PropagatesWithoutSupportEvent()
    {
        using var provider = new SupportRingBufferLoggerProvider(capacity: 8);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var inner = new FakeOperationLog { CheckFailure = new OperationCanceledException(cancellation.Token) };
        var audit = new BestEffortProjectOperationLog(inner, provider.CreateLogger("JET.Tests.Audit"));

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
        var inner = new FakeOperationLog { AppendFailure = new OperationCanceledException(cancellation.Token) };
        var audit = new BestEffortProjectOperationLog(inner, provider.CreateLogger("JET.Tests.Audit"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => audit.AppendAsync(
            "project-1",
            ProjectOperationEvent.Create(ProjectOperations.DataReimport, ProjectOperationTargetTypes.Dataset, "gl", 1),
            cancellation.Token));

        Assert.Empty(provider.Snapshot());
    }

    // 寫不進操作紀錄時回報成功並提醒；TryAppendAsync 告訴呼叫端有沒有寫進去，失敗仍不往外丟。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryAppend_ReportsWhetherTheEventWasWritten(bool innerFails)
    {
        using var provider = new SupportRingBufferLoggerProvider(capacity: 8);
        var inner = new FakeOperationLog { AppendFailure = innerFails ? new InvalidOperationException("PRIVATE no such table") : null };
        var audit = new BestEffortProjectOperationLog(inner, provider.CreateLogger("JET.Tests.Audit"));

        var written = await audit.TryAppendAsync(
            "project-1",
            ProjectOperationEvent.Create(ProjectOperations.DataReimport, ProjectOperationTargetTypes.Dataset, "gl", 1),
            CancellationToken.None);

        Assert.Equal(!innerFails, written);
        Assert.Equal(1, inner.AppendCalls);
        Assert.Equal(innerFails ? 1 : 0, provider.Snapshot().Count());
    }

    // 確認配對前讀不到紀錄表時回 null，讓呼叫端分得出「不需要紀錄」與「無法判斷」，後者照樣提醒。
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, null)]
    public async Task TryRequiresRecommitCheck_ReturnsNullOnlyWhenTheTableCannotBeRead(bool? checkResult, bool? expected)
    {
        using var provider = new SupportRingBufferLoggerProvider(capacity: 8);
        var inner = new FakeOperationLog
        {
            CheckResult = checkResult ?? true,
            CheckFailure = checkResult is null ? new InvalidOperationException("PRIVATE no such table") : null
        };
        var audit = new BestEffortProjectOperationLog(inner, provider.CreateLogger("JET.Tests.Audit"));

        var requires = await audit.TryRequiresMappingRecommitAuditAsync("project-1", "gl", CancellationToken.None);

        Assert.Equal(expected, requires);
        Assert.Equal(1, inner.CheckCalls);
        Assert.Equal(checkResult is null ? 1 : 0, provider.Snapshot().Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Success_ForwardsToInnerUnchanged(bool checkResult)
    {
        using var provider = new SupportRingBufferLoggerProvider(capacity: 8);
        var inner = new FakeOperationLog { CheckResult = checkResult };
        var audit = new BestEffortProjectOperationLog(inner, provider.CreateLogger("JET.Tests.Audit"));
        var operationEvent = ProjectOperationEvent.Create(
            ProjectOperations.DataReimport, ProjectOperationTargetTypes.Dataset, "gl", 5, 1);
        using var cancellation = new CancellationTokenSource();

        await audit.AppendAsync("project-1", operationEvent, cancellation.Token);
        var requires = await audit.RequiresMappingRecommitAuditAsync("project-1", "tb", cancellation.Token);

        Assert.Equal(checkResult, requires);
        Assert.Same(operationEvent, inner.LastEvent);
        Assert.Equal("project-1", inner.LastProjectId);
        Assert.Equal("tb", inner.LastDataset);
        Assert.Equal(cancellation.Token, inner.LastToken);
        Assert.Empty(provider.Snapshot());
    }

    private sealed class FakeOperationLog : IProjectOperationLog
    {
        public Exception? AppendFailure { get; init; }
        public Exception? CheckFailure { get; init; }
        public bool CheckResult { get; init; }
        public int AppendCalls { get; private set; }
        public int CheckCalls { get; private set; }
        public ProjectOperationEvent? LastEvent { get; private set; }
        public string? LastProjectId { get; private set; }
        public string? LastDataset { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task AppendAsync(string projectId, ProjectOperationEvent operationEvent, CancellationToken cancellationToken)
        {
            AppendCalls++;
            LastProjectId = projectId;
            LastEvent = operationEvent;
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
