using System.Reflection;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// ProjectSession 與 releaseLock 的狀態轉換測試。以呼叫開始時捕捉的 projectId 作為 compare-and-clear
/// oracle，避免延遲 release 清掉其間已切換的新案件。
/// </summary>
public sealed class ProjectSessionStateTests
{
    [Fact]
    public void CurrentProjectId_DoesNotExposeSetter()
    {
        var property = typeof(ProjectSession).GetProperty(
            nameof(ProjectSession.CurrentProjectId),
            BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(property);
        Assert.Null(property!.GetSetMethod(nonPublic: true));
    }

    [Fact]
    public async Task ReleaseLock_Succeeds_ClearsCapturedProject()
    {
        string? releasedProjectId = null;
        var locks = new StubLockService(
            release: (projectId, _, _) =>
            {
                releasedProjectId = projectId;
                return Task.CompletedTask;
            });
        var session = ActiveSession("project-a", locks);
        var handler = new ProjectReleaseLockHandler(
            new CurrentPrincipal("CONTOSO\\auditor"),
            session,
            new ActionExecutionGate());

        var response = await handler.HandleAsync(EmptyPayload(), CancellationToken.None);

        Assert.Equal("project-a", releasedProjectId);
        Assert.Null(session.CurrentProjectId);
        Assert.True(JsonSerializer.SerializeToElement(response).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ReleaseLock_Throws_PreservesCapturedProjectForRetry()
    {
        var expected = new IOException("release failed");
        var locks = new StubLockService(
            release: (_, _, _) => Task.FromException(expected));
        var session = ActiveSession("project-a", locks);
        var handler = new ProjectReleaseLockHandler(
            new CurrentPrincipal("CONTOSO\\auditor"),
            session,
            new ActionExecutionGate());

        var actual = await Assert.ThrowsAsync<IOException>(
            () => handler.HandleAsync(EmptyPayload(), CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal("project-a", session.CurrentProjectId);
    }

    [Fact]
    public async Task ReleaseLock_IsCancelled_PreservesCapturedProjectForRetry()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var locks = new StubLockService(
            release: (_, _, cancellationToken) => Task.FromCanceled(cancellationToken));
        var session = ActiveSession("project-a", locks);
        var handler = new ProjectReleaseLockHandler(
            new CurrentPrincipal("CONTOSO\\auditor"),
            session,
            new ActionExecutionGate());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(EmptyPayload(), cancellation.Token));

        Assert.Equal("project-a", session.CurrentProjectId);
    }

    [Fact]
    public async Task ReleaseLock_DelayedWhileSessionChanges_DoesNotClearNewProject()
    {
        var releaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var locks = new StubLockService(
            release: async (_, _, cancellationToken) =>
            {
                releaseStarted.TrySetResult();
                await allowRelease.Task.WaitAsync(cancellationToken);
            });
        var session = ActiveSession("project-a", locks);
        var handler = new ProjectReleaseLockHandler(
            new CurrentPrincipal("CONTOSO\\auditor"),
            session,
            new ActionExecutionGate());

        var releaseTask = handler.HandleAsync(EmptyPayload(), CancellationToken.None);
        await releaseStarted.Task;
        session.Enter("project-b", Repositories(locks));
        allowRelease.TrySetResult();
        await releaseTask;

        Assert.Equal("project-b", session.CurrentProjectId);
    }

    [Fact]
    public async Task ReleaseLock_NoActiveProject_ReturnsOkWithoutCallingLockService()
    {
        var called = false;
        var locks = new StubLockService(
            release: (_, _, _) =>
            {
                called = true;
                return Task.CompletedTask;
            });
        var handler = new ProjectReleaseLockHandler(
            new CurrentPrincipal("CONTOSO\\auditor"),
            new ProjectSession(),
            new ActionExecutionGate());

        var response = await handler.HandleAsync(EmptyPayload(), CancellationToken.None);

        Assert.False(called);
        Assert.True(JsonSerializer.SerializeToElement(response).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ReleaseLock_WhenExecutionGateIsHeld_FailsFastWithoutReleasingOrLeaving()
    {
        var called = false;
        var locks = new StubLockService(
            release: (_, _, _) =>
            {
                called = true;
                return Task.CompletedTask;
            });
        var session = ActiveSession("project-a", locks);
        var gate = new ActionExecutionGate();
        using var held = Assert.IsAssignableFrom<IDisposable>(gate.TryAcquire());
        var handler = new ProjectReleaseLockHandler(
            new CurrentPrincipal("CONTOSO\\auditor"),
            session,
            gate);

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => handler.HandleAsync(EmptyPayload(), CancellationToken.None));

        Assert.Equal(JetErrorCodes.OperationInProgress, exception.Code);
        Assert.False(called);
        Assert.Equal("project-a", session.CurrentProjectId);
    }

    private static JsonElement EmptyPayload()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    // releaseLock 從作用中案件的資料庫組取鎖服務，
    // 所以測試把替身鎖放進資料庫組再進入 session。
    private static ProjectSession ActiveSession(string projectId, ILockService locks)
    {
        var session = new ProjectSession();
        session.Enter(projectId, Repositories(locks));
        return session;
    }

    private static ProjectRepositories Repositories(ILockService locks) =>
        TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with { LockService = locks };

    private sealed class StubLockService(
        Func<string, string, CancellationToken, Task>? release = null) : ILockService
    {
        public Task<LockOutcome> AcquireAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) =>
            Task.FromResult<LockOutcome>(new LockOutcome.Acquired());

        public Task ReleaseAsync(string projectId, string principal, CancellationToken cancellationToken) =>
            release?.Invoke(projectId, principal, cancellationToken) ?? Task.CompletedTask;
    }
}
