using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 資料存檔後才寫的操作紀錄寫不進去時，回報成功並附提醒，不把已生效的變更報成失敗。
/// GL、TB 重新匯入與重新確認配對都經過這裡。
/// </summary>
public sealed class CommittedOperationLogTests
{
    [Fact]
    public async Task AppendFailure_ReturnsTheConfirmedWarning()
    {
        var warning = await CommittedOperationLog.AppendAsync(
            new FailingOperationLog(), "synthetic-project", Reimport(), NullLogger.Instance);

        Assert.Equal("變更已儲存，但這次變更沒有寫入操作紀錄。", warning);
    }

    // 本機案件的包裝把寫入失敗吞下、只記支援日誌；提醒仍要出現。
    [Fact]
    public async Task LocalWrapperSwallowsTheFailure_StillReturnsTheConfirmedWarning()
    {
        var local = new BestEffortProjectOperationLog(new FailingOperationLog(), NullLogger.Instance);

        var warning = await CommittedOperationLog.AppendAsync(local, "synthetic-project", Reimport(), NullLogger.Instance);

        Assert.Equal("變更已儲存，但這次變更沒有寫入操作紀錄。", warning);
    }

    // 寫不進操作紀錄時要留得下原因，事件必須進得了可匯出的支援日誌，而且只帶錯誤類型。
    [Fact]
    public async Task AppendFailure_IsRecordedInTheSupportLogWithOnlyTheErrorType()
    {
        using var log = new SupportRingBufferLoggerProvider(10);

        await CommittedOperationLog.AppendAsync(new FailingOperationLog(), "synthetic-project", Reimport(), log.CreateLogger("audit-test"));

        var entry = Assert.Single(log.Snapshot());
        Assert.Equal("operation_log.append_failed", entry.EventName);
        Assert.Equal(ProjectOperations.DataReimport, entry.Fields["operation"]);
        Assert.Equal(nameof(IOException), entry.Fields["error_type"]);
        Assert.DoesNotContain("Synthetic audit log failure", SupportDiagnosticNdjson.SerializeLine(entry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppendSuccess_WritesTheEventAndReturnsNoWarning()
    {
        var log = new RecordingOperationLog();
        var operationEvent = Reimport();

        var warning = await CommittedOperationLog.AppendAsync(log, "synthetic-project", operationEvent, logger: null);

        Assert.Null(warning);
        Assert.Same(operationEvent, Assert.Single(log.Events));
    }

    [Fact]
    public void WithWarning_AppendsAfterExistingWarningsOnlyWhenPresent()
    {
        IReadOnlyList<string> existing = new[] { "既有提醒" };

        Assert.Same(existing, CommittedOperationLog.WithWarning(existing, null));
        Assert.Equal(new[] { "既有提醒", "新提醒" }, CommittedOperationLog.WithWarning(existing, "新提醒"));
    }

    private static ProjectOperationEvent Reimport() =>
        ProjectOperationEvent.Create(
            ProjectOperations.DataReimport, ProjectOperationTargetTypes.Dataset, "gl", subjectCount: 10, replacedCount: 1);

    private sealed class FailingOperationLog : IProjectOperationLog
    {
        public Task AppendAsync(string projectId, ProjectOperationEvent operationEvent, CancellationToken cancellationToken) =>
            throw new IOException("Synthetic audit log failure");

        public Task<bool> RequiresMappingRecommitAuditAsync(string projectId, string dataset, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class RecordingOperationLog : IProjectOperationLog
    {
        public List<ProjectOperationEvent> Events { get; } = [];

        public Task AppendAsync(string projectId, ProjectOperationEvent operationEvent, CancellationToken cancellationToken)
        {
            Events.Add(operationEvent);
            return Task.CompletedTask;
        }

        public Task<bool> RequiresMappingRecommitAuditAsync(string projectId, string dataset, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
