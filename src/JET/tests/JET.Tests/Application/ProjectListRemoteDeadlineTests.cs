using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Master spec「本機先顯示、線上手動同步」的 Application seam：本機查詢不持有遠端埠，
/// 手動同步的 registry／lock 查詢共用一個有界 deadline，且 caller cancellation 不得被失聯降級吞掉。
/// </summary>
public sealed class ProjectListRemoteDeadlineTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TestWaitLimit = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task ListLocal_ConstructorExcludesRemotePorts_AndReturnsOnlyLocalProviders()
    {
        var constructor = Assert.Single(typeof(ProjectListLocalHandler).GetConstructors());
        Assert.Equal(
            [typeof(IProjectStore)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));

        var store = new ThrowingRemoteSpyProjectStore(
        [
            Document("sqlite-local", ProjectDocument.DefaultDatabaseProvider, createdDay: 1),
            Document("duckdb-local", ProjectDocument.DuckDbDatabaseProvider, createdDay: 2),
            Document("sql-cache", ProjectDocument.SqlServerDatabaseProvider, createdDay: 3),
        ]);
        var handler = new ProjectListLocalHandler(store);

        var data = Wire(await handler.HandleAsync(default, CancellationToken.None));

        Assert.Equal(0, store.RemoteCallCount);
        Assert.Equal(["projects"], data.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["duckdb-local", "sqlite-local"],
            data.GetProperty("projects").EnumerateArray()
                .Select(project => project.GetProperty("projectId").GetString()));
    }

    [Fact]
    public void List_PublicConstructorPinsTheRemoteDeadlineToThirtySeconds()
    {
        var handler = new ProjectListHandler(
            new FixedProjectStore([]),
            new StubProjectRegistry(),
            StubLockService.Empty,
            new CurrentPrincipal("CONTOSO\\auditor"));
        var deadlineField = typeof(ProjectListHandler).GetField(
            "_remoteDeadline",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(deadlineField);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.IsType<TimeSpan>(deadlineField.GetValue(handler)));
    }

    [Fact]
    public async Task List_RemoteRegistryAndLockStartConcurrentlyWithOneDeadlineToken()
    {
        var registryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken registryToken = default;
        CancellationToken lockToken = default;
        var serverDocument = Document("server-only", ProjectDocument.SqlServerDatabaseProvider, createdDay: 4);

        var registry = new StubProjectRegistry
        {
            ListVisible = async (_, cancellationToken) =>
            {
                registryToken = cancellationToken;
                registryStarted.TrySetResult();
                await lockStarted.Task.WaitAsync(cancellationToken);
                return [new RegisteredProject(serverDocument, serverDocument.CreatedUtc, null)];
            },
        };
        var locks = new StubLockService
        {
            ListActive = async cancellationToken =>
            {
                lockToken = cancellationToken;
                lockStarted.TrySetResult();
                await registryStarted.Task.WaitAsync(cancellationToken);
                return
                [
                    new ProjectLockInfo(
                        serverDocument.ProjectId,
                        "CONTOSO\\holder",
                        "AUDIT-LAPTOP",
                        serverDocument.CreatedUtc),
                ];
            },
        };
        var handler = Handler([], registry, locks, TimeSpan.FromSeconds(1));

        var data = Wire(await handler.HandleAsync(default, CancellationToken.None).WaitAsync(TestWaitLimit));

        Assert.True(registryStarted.Task.IsCompletedSuccessfully);
        Assert.True(lockStarted.Task.IsCompletedSuccessfully);
        Assert.True(registryToken.CanBeCanceled);
        Assert.Equal(registryToken, lockToken);
        Assert.True(data.GetProperty("online").GetProperty("reachable").GetBoolean());
        var project = Project(data, serverDocument.ProjectId);
        Assert.Equal("serverOnly", project.GetProperty("syncStatus").GetString());
        Assert.Equal("CONTOSO\\holder", project.GetProperty("lock").GetProperty("lockedBy").GetString());
    }

    [Fact]
    public async Task List_CallerCancellationPropagatesInsteadOfDegrading()
    {
        var registryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registryCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registryCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new StubProjectRegistry
        {
            ListVisible = async (_, cancellationToken) =>
            {
                registryStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return [];
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    registryCancellationObserved.TrySetResult();
                    throw;
                }
                finally
                {
                    registryCompleted.TrySetResult();
                }
            },
        };
        var locks = new StubLockService
        {
            ListActive = async cancellationToken =>
            {
                lockStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return [];
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lockCancellationObserved.TrySetResult();
                    throw;
                }
                finally
                {
                    lockCompleted.TrySetResult();
                }
            },
        };
        var handler = Handler(
            [Document("local", ProjectDocument.DefaultDatabaseProvider, createdDay: 1)],
            registry,
            locks,
            TimeSpan.FromSeconds(5));
        using var callerCancellation = new CancellationTokenSource();

        var operation = handler.HandleAsync(default, callerCancellation.Token);
        await Task.WhenAll(registryStarted.Task, lockStarted.Task).WaitAsync(TestWaitLimit);
        callerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operation.WaitAsync(TestWaitLimit));
        await Task.WhenAll(registryCancellationObserved.Task, lockCancellationObserved.Task)
            .WaitAsync(TestWaitLimit);
        await Task.WhenAll(registryCompleted.Task, lockCompleted.Task)
            .WaitAsync(TestWaitLimit);
    }

    [Fact]
    public async Task List_RegistryDeadlineDegradesButRetainsLocalSnapshot()
    {
        var registry = new StubProjectRegistry
        {
            ListVisible = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            },
        };
        var handler = Handler(
        [
            Document("sqlite-local", ProjectDocument.DefaultDatabaseProvider, createdDay: 1),
            Document("sql-cache", ProjectDocument.SqlServerDatabaseProvider, createdDay: 2),
        ],
            registry,
            StubLockService.Empty,
            TestDeadline);

        var data = Wire(await handler.HandleAsync(default, CancellationToken.None).WaitAsync(TestWaitLimit));

        Assert.False(data.GetProperty("online").GetProperty("reachable").GetBoolean());
        Assert.Equal("sqlite", Project(data, "sqlite-local").GetProperty("databaseProvider").GetString());
        Assert.Equal("localOnly", Project(data, "sql-cache").GetProperty("syncStatus").GetString());
    }

    [Fact]
    public async Task List_HandlerDeadlineBoundsRemoteTasksThatIgnoreCancellationTokens()
    {
        var neverRegistry = new TaskCompletionSource<IReadOnlyList<RegisteredProject>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverLocks = new TaskCompletionSource<IReadOnlyList<ProjectLockInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new StubProjectRegistry
        {
            ListVisible = (_, _) => neverRegistry.Task,
        };
        var locks = new StubLockService
        {
            ListActive = _ => neverLocks.Task,
        };
        var handler = Handler(
            [Document("sqlite-local", ProjectDocument.DefaultDatabaseProvider, createdDay: 1)],
            registry,
            locks,
            TestDeadline);

        var data = Wire(await handler.HandleAsync(default, CancellationToken.None).WaitAsync(TestWaitLimit));

        Assert.False(data.GetProperty("online").GetProperty("reachable").GetBoolean());
        Assert.Equal("sqlite", Project(data, "sqlite-local").GetProperty("databaseProvider").GetString());
        Assert.False(neverRegistry.Task.IsCompleted);
        Assert.False(neverLocks.Task.IsCompleted);
    }

    [Fact]
    public async Task List_LockDeadlineDoesNotChangeSuccessfulRegistryReachability()
    {
        var serverDocument = Document("online", ProjectDocument.SqlServerDatabaseProvider, createdDay: 5);
        var registry = new StubProjectRegistry
        {
            ListVisible = (_, _) => Task.FromResult<IReadOnlyList<RegisteredProject>>(
                [new RegisteredProject(serverDocument, serverDocument.CreatedUtc, null)]),
        };
        var locks = new StubLockService
        {
            ListActive = async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            },
        };
        var handler = Handler([], registry, locks, TestDeadline);

        var data = Wire(await handler.HandleAsync(default, CancellationToken.None).WaitAsync(TestWaitLimit));

        Assert.True(data.GetProperty("online").GetProperty("reachable").GetBoolean());
        var project = Project(data, serverDocument.ProjectId);
        Assert.Equal("serverOnly", project.GetProperty("syncStatus").GetString());
        Assert.Equal(JsonValueKind.Null, project.GetProperty("lock").ValueKind);
    }

    [Fact]
    public async Task List_LockExceptionDoesNotChangeSuccessfulRegistryReachability()
    {
        var serverDocument = Document("online", ProjectDocument.SqlServerDatabaseProvider, createdDay: 5);
        var registry = new StubProjectRegistry
        {
            ListVisible = (_, _) => Task.FromResult<IReadOnlyList<RegisteredProject>>(
                [new RegisteredProject(serverDocument, serverDocument.CreatedUtc, null)]),
        };
        var locks = new StubLockService
        {
            ListActive = _ => throw new InvalidOperationException("lock unavailable"),
        };

        var data = Wire(await Handler([], registry, locks, TestDeadline)
            .HandleAsync(default, CancellationToken.None));

        Assert.True(data.GetProperty("online").GetProperty("reachable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Project(data, "online").GetProperty("lock").ValueKind);
    }

    [Fact]
    public async Task List_RegistryExceptionDoesNotDiscardLocalSnapshotOrSuccessfulLockTask()
    {
        var localCache = Document("cache", ProjectDocument.SqlServerDatabaseProvider, createdDay: 1);
        var registry = new StubProjectRegistry
        {
            ListVisible = (_, _) => throw new InvalidOperationException("registry unavailable"),
        };
        var locks = new StubLockService
        {
            ListActive = _ => Task.FromResult<IReadOnlyList<ProjectLockInfo>>(
            [
                new ProjectLockInfo(
                    localCache.ProjectId,
                    "CONTOSO\\holder",
                    "AUDIT-LAPTOP",
                    localCache.CreatedUtc),
            ]),
        };

        var data = Wire(await Handler([localCache], registry, locks, TestDeadline)
            .HandleAsync(default, CancellationToken.None));

        Assert.False(data.GetProperty("online").GetProperty("reachable").GetBoolean());
        var project = Project(data, localCache.ProjectId);
        Assert.Equal("localOnly", project.GetProperty("syncStatus").GetString());
        Assert.Equal("CONTOSO\\holder", project.GetProperty("lock").GetProperty("lockedBy").GetString());
    }

    [Fact]
    public async Task List_ExistsChecksShareOneTotalDeadlineWithoutRestartingIt()
    {
        // 第一段刻意耗掉 700/1200 ms；共用總期限留給第二段約 500 ms，若逐筆重啟則會拿到完整
        // 1200 ms 並越過 900 ms 上限。兩側各保留約 300 ms，避免把 scheduler 抖動當成產品失敗。
        var totalDeadline = TimeSpan.FromMilliseconds(1200);
        var firstStageHold = TimeSpan.FromMilliseconds(700);
        var maximumRemainingTime = TimeSpan.FromMilliseconds(900);
        var boundedWait = TimeSpan.FromSeconds(4);
        CancellationToken listToken = default;
        CancellationToken lockToken = default;
        var existsTokens = new List<CancellationToken>();
        var firstExistsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 這是測試內的明示放行閘；同步喚醒 handler，避免 thread-pool 負載被誤算進產品共用 deadline。
        var releaseFirstExists = new TaskCompletionSource();
        var secondExistsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondExistsCancellationObserved = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan secondExistsStartedAt = default;
        var registry = new StubProjectRegistry
        {
            ListVisible = (_, cancellationToken) =>
            {
                listToken = cancellationToken;
                return Task.FromResult<IReadOnlyList<RegisteredProject>>([]);
            },
            Exists = async (projectId, cancellationToken) =>
            {
                existsTokens.Add(cancellationToken);
                if (projectId == "cache-a")
                {
                    firstExistsStarted.TrySetResult();
                    await releaseFirstExists.Task.WaitAsync(cancellationToken);
                    return false;
                }

                secondExistsStartedAt = stopwatch.Elapsed;
                secondExistsStarted.TrySetResult();
                var cancellationDelay = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                using var registration = cancellationToken.Register(
                    () => secondExistsCancellationObserved.TrySetResult(stopwatch.Elapsed));
                await cancellationDelay;
                return false;
            },
        };
        var locks = new StubLockService
        {
            ListActive = cancellationToken =>
            {
                lockToken = cancellationToken;
                return Task.FromResult<IReadOnlyList<ProjectLockInfo>>([]);
            },
        };
        var handler = Handler(
        [
            Document("cache-a", ProjectDocument.SqlServerDatabaseProvider, createdDay: 1),
            Document("cache-b", ProjectDocument.SqlServerDatabaseProvider, createdDay: 2),
        ],
            registry,
            locks,
            totalDeadline);

        var operation = handler.HandleAsync(default, CancellationToken.None);
        await firstExistsStarted.Task.WaitAsync(boundedWait);
        await Task.Delay(firstStageHold);
        releaseFirstExists.TrySetResult();
        await secondExistsStarted.Task.WaitAsync(boundedWait);

        var secondExistsCancelledAt = await secondExistsCancellationObserved.Task.WaitAsync(boundedWait);
        var data = Wire(await operation.WaitAsync(boundedWait));
        var secondStageElapsed = secondExistsCancelledAt - secondExistsStartedAt;

        Assert.Equal(2, existsTokens.Count);
        Assert.True(listToken.CanBeCanceled);
        Assert.Equal(listToken, lockToken);
        Assert.All(existsTokens, token => Assert.Equal(listToken, token));
        Assert.True(
            secondStageElapsed < maximumRemainingTime,
            $"第二次 Exists 使用了 {secondStageElapsed.TotalMilliseconds:N0} ms；共用 {totalDeadline.TotalMilliseconds:N0} ms deadline 不得逐筆重啟。");
        Assert.False(data.GetProperty("online").GetProperty("reachable").GetBoolean());
        Assert.All(
            data.GetProperty("projects").EnumerateArray(),
            project => Assert.Equal("localOnly", project.GetProperty("syncStatus").GetString()));
    }

    private static ProjectListHandler Handler(
        IReadOnlyList<ProjectDocument> documents,
        IProjectRegistry registry,
        ILockService lockService,
        TimeSpan deadline) =>
        new(
            new FixedProjectStore(documents),
            registry,
            lockService,
            new CurrentPrincipal("CONTOSO\\auditor"),
            deadline);

    private static ProjectDocument Document(string projectId, string provider, int createdDay) =>
        new(
            projectId,
            $"CODE-{createdDay}",
            $"Entity {createdDay}",
            "operator",
            "2025-01-01",
            "2025-12-31",
            LastAccountingPeriodDate: null,
            ProjectDocument.DefaultMoneyScale,
            ProjectDocument.DefaultRoundingMode,
            new DateTimeOffset(2025, 1, createdDay, 0, 0, 0, TimeSpan.Zero),
            CurrentStep: 1,
            ProjectDocument.CurrentSchemaVersion,
            provider);

    private static JsonElement Wire(object? value) =>
        JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static JsonElement Project(JsonElement response, string projectId) =>
        Assert.Single(
            response.GetProperty("projects").EnumerateArray(),
            project => project.GetProperty("projectId").GetString() == projectId);

    private sealed class FixedProjectStore(IReadOnlyList<ProjectDocument> documents) : IProjectStore
    {
        public Task CreateAsync(ProjectDocument document, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(documents);
        }

        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(ProjectDocument document, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// 同一物件刻意同時實作遠端 ports；若 listLocal 偷做 interface cast 或下探，測試會立即丟例外。
    /// </summary>
    private sealed class ThrowingRemoteSpyProjectStore(IReadOnlyList<ProjectDocument> documents)
        : IProjectStore, IProjectRegistry, ILockService
    {
        public int RemoteCallCount { get; private set; }

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(documents);
        }

        public Task CreateAsync(ProjectDocument document, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(ProjectDocument document, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        Task IProjectRegistry.RegisterAsync(
            ProjectDocument document,
            string principal,
            CancellationToken cancellationToken) => ThrowRemote<Task>();

        Task IProjectRegistry.UpdateDocumentAsync(
            ProjectDocument document,
            CancellationToken cancellationToken) => ThrowRemote<Task>();

        Task<bool> IProjectRegistry.ExistsAsync(string projectId, CancellationToken cancellationToken) =>
            ThrowRemote<Task<bool>>();

        Task<IReadOnlyList<RegisteredProject>> IProjectRegistry.ListVisibleAsync(
            string principal,
            CancellationToken cancellationToken) => ThrowRemote<Task<IReadOnlyList<RegisteredProject>>>();

        Task<RegisteredProject?> IProjectRegistry.FindVisibleAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) => ThrowRemote<Task<RegisteredProject?>>();

        Task IProjectRegistry.TouchLastOpenedAsync(string projectId, CancellationToken cancellationToken) =>
            ThrowRemote<Task>();

        Task IProjectRegistry.UnregisterAsync(string projectId, CancellationToken cancellationToken) =>
            ThrowRemote<Task>();

        Task<LockOutcome> ILockService.AcquireAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) => ThrowRemote<Task<LockOutcome>>();

        Task ILockService.RenewAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) => ThrowRemote<Task>();

        Task ILockService.ReleaseAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) => ThrowRemote<Task>();

        Task<IReadOnlyList<ProjectLockInfo>> ILockService.ListActiveAsync(CancellationToken cancellationToken) =>
            ThrowRemote<Task<IReadOnlyList<ProjectLockInfo>>>();

        private T ThrowRemote<T>()
        {
            RemoteCallCount++;
            throw new InvalidOperationException("project.listLocal touched a remote port.");
        }
    }

    private sealed class StubProjectRegistry : IProjectRegistry
    {
        public Func<string, CancellationToken, Task<bool>> Exists { get; init; } =
            (_, _) => Task.FromResult(false);

        public Func<string, CancellationToken, Task<IReadOnlyList<RegisteredProject>>> ListVisible { get; init; } =
            (_, _) => Task.FromResult<IReadOnlyList<RegisteredProject>>([]);

        public Task RegisterAsync(
            ProjectDocument document,
            string principal,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpdateDocumentAsync(
            ProjectDocument document,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string projectId, CancellationToken cancellationToken) =>
            Exists(projectId, cancellationToken);

        public Task<IReadOnlyList<RegisteredProject>> ListVisibleAsync(
            string principal,
            CancellationToken cancellationToken) =>
            ListVisible(principal, cancellationToken);

        public Task<RegisteredProject?> FindVisibleAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task TouchLastOpenedAsync(string projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UnregisterAsync(string projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubLockService : ILockService
    {
        internal static StubLockService Empty { get; } = new();

        public Func<CancellationToken, Task<IReadOnlyList<ProjectLockInfo>>> ListActive { get; init; } =
            _ => Task.FromResult<IReadOnlyList<ProjectLockInfo>>([]);

        public Task<LockOutcome> AcquireAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RenewAsync(string projectId, string principal, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(string projectId, string principal, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectLockInfo>> ListActiveAsync(CancellationToken cancellationToken) =>
            ListActive(cancellationToken);
    }
}
