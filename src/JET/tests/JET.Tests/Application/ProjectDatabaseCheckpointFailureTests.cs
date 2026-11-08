using System.Text.Json;
using JET.Application;
using JET.Bridge;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 記錄檔併回或關閉案件資料庫失敗時，作業結果照舊，只在支援日誌記一筆步驟失敗（見 IProjectDatabaseCheckpoint）。
/// 第 30 步量涵蓋率時發現這段失敗的路徑沒有測試執行到。這是新增的測試。
/// </summary>
public sealed class ProjectDatabaseCheckpointFailureTests
{
    private const int ActionStepFailedEventId = 1005;

    [Fact]
    public async Task CheckpointFailure_AfterAChangingOperation_KeepsTheResult_AndLogsTheStep()
    {
        var session = new ProjectSession();
        session.Enter("project-1", TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider));
        var checkpoint = new FailingCheckpoint();
        var logger = new RecordingLogger<ActionDispatcher>();
        var handler = new FixedResultHandler("import.gl.fromFile");
        var dispatcher = new ActionDispatcher(
            [handler],
            logger,
            session,
            databaseCheckpoint: checkpoint);
        using var payload = JsonDocument.Parse("{}");

        var result = await dispatcher.DispatchAsync("import.gl.fromFile", payload.RootElement, CancellationToken.None);

        Assert.Same(handler.Result, result);
        Assert.Equal(["project-1"], checkpoint.Checkpointed);
        Assert.Empty(checkpoint.Closed);
        var failure = Assert.Single(logger.Entries, entry => entry.EventId == ActionStepFailedEventId);
        Assert.Equal("action step database_checkpoint failed", failure.Message);
        Assert.Same(checkpoint.Failure, failure.Exception);
    }

    [Fact]
    public async Task CheckpointFailure_AfterAFailedOperation_KeepsTheOriginalError()
    {
        var session = new ProjectSession();
        session.Enter("project-1", TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider));
        var checkpoint = new FailingCheckpoint();
        var logger = new RecordingLogger<ActionDispatcher>();
        var original = new JetActionException(JetErrorCodes.InvalidPayload, "測試用的作業錯誤。");
        var dispatcher = new ActionDispatcher(
            [new FailingHandler("import.gl.fromFile", original)],
            logger,
            session,
            databaseCheckpoint: checkpoint);
        using var payload = JsonDocument.Parse("{}");

        var thrown = await Assert.ThrowsAsync<JetActionException>(
            () => dispatcher.DispatchAsync("import.gl.fromFile", payload.RootElement, CancellationToken.None));

        Assert.Equal(original.Code, thrown.Code);
        Assert.Equal(original.Message, thrown.Message);
        Assert.Equal(["project-1"], checkpoint.Checkpointed);
        Assert.Single(logger.Entries, entry => entry.EventId == ActionStepFailedEventId);
    }

    [Fact]
    public async Task CloseFailure_WhenLeavingTheCase_StillLeavesTheCase_AndLogsTheStep()
    {
        var session = new ProjectSession();
        var locks = new RecordingLockService();
        session.Enter(
            "project-1",
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with { LockService = locks });
        var checkpoint = new FailingCheckpoint();
        var logger = new RecordingLogger<ProjectReleaseLockHandler>();
        var handler = new ProjectReleaseLockHandler(
            new CurrentPrincipal("CONTOSO\\auditor"),
            session,
            new ActionExecutionGate(),
            checkpoint,
            logger);
        using var payload = JsonDocument.Parse("{}");

        var result = await handler.HandleAsync(payload.RootElement, CancellationToken.None);

        Assert.Equal("""{"ok":true}""", JsonSerializer.Serialize(result));
        Assert.Null(session.CurrentProjectId);
        Assert.Equal(["project-1"], locks.Released);
        Assert.Equal(["project-1"], checkpoint.Closed);
        var failure = Assert.Single(logger.Entries, entry => entry.EventId == ActionStepFailedEventId);
        Assert.Equal("action step database_close failed", failure.Message);
    }

    [Fact]
    public void CloseFailure_WhenJetEnds_DoesNotThrow_AndLogsTheStep()
    {
        var session = new ProjectSession();
        session.Enter("project-1", TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider));
        var checkpoint = new FailingCheckpoint();
        var logger = new RecordingLogger<ProjectDatabaseShutdown>();
        var shutdown = new ProjectDatabaseShutdown(session, checkpoint, logger);

        shutdown.Dispose();
        shutdown.Dispose();

        Assert.Equal(["project-1"], checkpoint.Closed);
        var failure = Assert.Single(logger.Entries, entry => entry.EventId == ActionStepFailedEventId);
        Assert.Equal("action step database_close failed", failure.Message);
    }

    private sealed class FailingCheckpoint : IProjectDatabaseCheckpoint
    {
        public InvalidOperationException Failure { get; } = new("測試用的併回失敗。");

        public List<string> Checkpointed { get; } = [];

        public List<string> Closed { get; } = [];

        public Task CheckpointAsync(string projectId, CancellationToken cancellationToken)
        {
            Checkpointed.Add(projectId);
            throw Failure;
        }

        public Task CloseAsync(string projectId, CancellationToken cancellationToken)
        {
            Closed.Add(projectId);
            throw Failure;
        }
    }

    private sealed class FixedResultHandler(string action) : IApplicationActionHandler
    {
        public object Result { get; } = new { ok = true };

        public string Action { get; } = action;

        public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken) =>
            Task.FromResult<object?>(Result);
    }

    private sealed class FailingHandler(string action, Exception failure) : IApplicationActionHandler
    {
        public string Action { get; } = action;

        public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken) =>
            Task.FromException<object?>(failure);
    }

    private sealed class RecordingLockService : ILockService
    {
        public List<string> Released { get; } = [];

        public Task<LockOutcome> AcquireAsync(string projectId, string principal, CancellationToken cancellationToken) =>
            Task.FromResult<LockOutcome>(new LockOutcome.Acquired());

        public Task ReleaseAsync(string projectId, string principal, CancellationToken cancellationToken)
        {
            Released.Add(projectId);
            return Task.CompletedTask;
        }
    }

    private sealed record LogEntry(int EventId, string Message, Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly Lock gate = new();
        private readonly List<LogEntry> entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (gate)
                {
                    return [.. entries];
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (gate)
            {
                entries.Add(new LogEntry(eventId.Id, formatter(state, exception), exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
