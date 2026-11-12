using System.Text.Json;
using JET.Application;
using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 作業序列化閘：變更型作業同一時間至多一項、第二項回 operation_in_progress；
/// 唯讀作業不佔閘、可併行；依序 await 的變更型作業皆成功；分類漂移守衛。
/// </summary>
public sealed class ActionSerializationTests
{
    // export.accountMappingTemplate 列在「會開案件資料庫的 action」清單裡。
    [Fact]
    public void AccountMappingTemplateExport_OpensProjectDatabase()
    {
        Assert.True(ActionExecutionPolicy.OpensProjectDatabase("export.accountMappingTemplate"));
    }

    // 「會開案件資料庫的 action」清單決定哪些變更型作業結束後併回 SQLite 記錄檔，所以用這個測試守住它。
    [Fact]
    public void ProjectDatabaseActions_AreRegisteredAndClassified_AndCaseLifecycleActionsNeverOpen()
    {
        using var host = new HandlerTestHost(enableDevTools: true);
        var registered = host.Dispatcher.RegisteredActions.ToHashSet(StringComparer.Ordinal);

        foreach (var action in ActionExecutionPolicy.ProjectDatabaseActionNames)
        {
            Assert.True(registered.Contains(action), $"會開案件資料庫的 {action} 沒有在 dispatcher 註冊");
            Assert.True(ActionExecutionPolicy.IsClassified(action), $"會開案件資料庫的 {action} 沒有歸類");
            Assert.True(ActionExecutionPolicy.OpensProjectDatabase(action));
        }

        foreach (var action in new[] { "project.create", "project.load", "project.delete", "project.releaseLock" })
        {
            Assert.False(ActionExecutionPolicy.OpensProjectDatabase(action), $"{action} 不得列為會開案件資料庫的 action");
            Assert.False(ActionExecutionPolicy.CheckpointsProjectDatabase(action), $"{action} 不得由 dispatcher 併回記錄檔");
        }

        Assert.False(ActionExecutionPolicy.OpensProjectDatabase("not.a.real.action"));
    }
    [Fact]
    public void OperationCancel_IsConcurrentSoItCanReachAnInFlightExclusiveAction()
    {
        Assert.True(ActionExecutionPolicy.IsClassified("operation.cancel"));
        Assert.False(ActionExecutionPolicy.IsExclusive("operation.cancel"));
    }

    // 阻塞式 handler：進入後停在 Release 上，讓測試把「作業卡在進行中」的狀態固定下來。
    private sealed class BlockingHandler(string action, TaskCompletionSource release) : IApplicationActionHandler
    {
        public string Action { get; } = action;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return new { ok = true };
        }
    }

    private sealed class ImmediateHandler(string action) : IApplicationActionHandler
    {
        public string Action { get; } = action;

        public int Calls { get; private set; }

        public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<object?>(new { ok = true });
        }
    }

    private sealed class CancellationBlockingHandler(string action) : IApplicationActionHandler
    {
        public string Action { get; } = action;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    private static ActionDispatcher Dispatcher(params IApplicationActionHandler[] handlers)
        => new(handlers, NullLogger<ActionDispatcher>.Instance, new ProjectSession());

    [Fact]
    public void ReleaseLock_UsesConditionalGateInsteadOfDispatcherExclusiveOrConcurrentClassification()
    {
        Assert.True(ActionExecutionPolicy.IsClassified("project.releaseLock"));
        Assert.False(ActionExecutionPolicy.IsExclusive("project.releaseLock"));
        Assert.True(ActionExecutionPolicy.RequiresConditionalGate("project.releaseLock"));
    }

    [Fact]
    public async Task ProductionComposition_ReleaseLockUsesTheDispatchersExactGateInstance()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        using var held = Assert.IsAssignableFrom<IDisposable>(
            host.Dispatcher.TryAcquireExecutionLease());

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.releaseLock"));

        Assert.Equal(JetErrorCodes.OperationInProgress, exception.Code);
    }

    [Fact]
    public async Task SecondExclusiveAction_WhileFirstInFlight_IsRejectedWithOperationInProgress()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler("validate.run", release);
        var dispatcher = Dispatcher(handler);
        using var payload = JsonDocument.Parse("{}");

        // 第一項變更型作業卡在 handler 內（閘已取得）
        var first = dispatcher.DispatchAsync("validate.run", payload.RootElement, CancellationToken.None);
        await handler.Entered.Task;

        // 第二項同型作業立即被回絕，不排隊
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => dispatcher.DispatchAsync("validate.run", payload.RootElement, CancellationToken.None));
        Assert.Equal(JetErrorCodes.OperationInProgress, ex.Code);

        release.SetResult();
        Assert.NotNull(await first); // 第一項照常完成
    }

    [Fact]
    public async Task OperationCancel_CancelsTargetRequest_AndExclusiveGateIsReleased()
    {
        var registry = new RequestCancellationRegistry();
        var blocking = new CancellationBlockingHandler("validate.run");
        var immediate = new ImmediateHandler("mapping.commit.gl");
        var dispatcher = new ActionDispatcher(
            [blocking, immediate, new OperationCancelHandler(registry)],
            NullLogger<ActionDispatcher>.Instance,
            new ProjectSession(),
            engineErrorTranslator: null,
            cancellationRegistry: registry);
        using var emptyPayload = JsonDocument.Parse("{}");
        using var targetScope = registry.Begin("target-request");

        var target = dispatcher.DispatchAsync(
            "validate.run", emptyPayload.RootElement, targetScope.Token);
        await blocking.Entered.Task;

        using var cancelPayload = JsonDocument.Parse("{\"requestId\":\"target-request\"}");
        var cancelResult = await dispatcher.DispatchAsync(
            "operation.cancel", cancelPayload.RootElement, CancellationToken.None);

        Assert.True((bool)cancelResult!.GetType().GetProperty("requested")!.GetValue(cancelResult)!);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target);

        // finally 已釋放 exclusive gate；下一個變更型 action 不被 operation_in_progress 擋下。
        await dispatcher.DispatchAsync("mapping.commit.gl", emptyPayload.RootElement, CancellationToken.None);
        Assert.Equal(1, immediate.Calls);
    }

    [Fact]
    public async Task ReadOnlyAction_RunsConcurrently_WhileExclusiveActionInFlight()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exclusive = new BlockingHandler("validate.run", release);
        var readOnly = new ImmediateHandler("query.dataPreview");
        var dispatcher = Dispatcher(exclusive, readOnly);
        using var payload = JsonDocument.Parse("{}");

        var first = dispatcher.DispatchAsync("validate.run", payload.RootElement, CancellationToken.None);
        await exclusive.Entered.Task;

        // 唯讀動作不佔閘：變更型作業仍在進行中，query.dataPreview 照樣完成
        var readResult = await dispatcher.DispatchAsync("query.dataPreview", payload.RootElement, CancellationToken.None);
        Assert.NotNull(readResult);
        Assert.Equal(1, readOnly.Calls);

        release.SetResult();
        await first;
    }

    [Fact]
    public async Task LogAppend_RunsConcurrently_WhileExclusiveActionInFlight()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exclusive = new BlockingHandler("validate.run", release);
        var messages = new RecordingMessageLogStore();
        // log.append 從作用中案件的資料庫組取訊息紀錄，替身放進資料庫組。
        var session = new ProjectSession();
        session.Enter(
            "project-1",
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                MessageLog = messages,
            });
        var dispatcher = new ActionDispatcher(
            [
                exclusive,
                new LogAppendHandler(session)
            ],
            NullLogger<ActionDispatcher>.Instance,
            session);
        using var payload = JsonDocument.Parse("{}");

        var first = dispatcher.DispatchAsync(
            "validate.run",
            payload.RootElement,
            CancellationToken.None);
        await exclusive.Entered.Task;

        using var logPayload = JsonDocument.Parse("""{ "text": "仍在處理" }""");
        await dispatcher.DispatchAsync(
            "log.append",
            logPayload.RootElement,
            CancellationToken.None);

        Assert.Equal([("project-1", "info", "仍在處理")], messages.Appended);

        release.SetResult();
        await first;
    }

    [Fact]
    public async Task ReleaseLock_WhileExclusiveActionInFlight_PreservesLockUntilCancellationCompletes()
    {
        using var root = new TempProjectRoot();
        const string projectId = "release-gate-race";
        const string ownerPrincipal = "CONTOSO\\owner";
        const string contenderPrincipal = "CONTOSO\\contender";
        Directory.CreateDirectory(Path.Combine(root.Path, projectId));
        var folder = new JetProjectFolder(root.Path);
        using var ownerLocks = new LocalFileLockService(folder);
        using var contenderLocks = new LocalFileLockService(folder);
        Assert.IsType<LockOutcome.Acquired>(
            await ownerLocks.AcquireAsync(projectId, ownerPrincipal, CancellationToken.None));

        var session = new ProjectSession();
        session.Enter(
            projectId,
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                LockService = ownerLocks,
            });
        var gate = new ActionExecutionGate();
        var registry = new RequestCancellationRegistry();
        var blocking = new CancellationBlockingHandler("validate.run");
        var dispatcher = new ActionDispatcher(
            [
                blocking,
                new ProjectReleaseLockHandler(
                    new CurrentPrincipal(ownerPrincipal),
                    session,
                    gate),
                new OperationCancelHandler(registry)
            ],
            NullLogger<ActionDispatcher>.Instance,
            session,
            engineErrorTranslator: null,
            cancellationRegistry: registry,
            executionGate: gate);
        using var emptyPayload = JsonDocument.Parse("{}");
        using var targetScope = registry.Begin("release-gate-target");

        var target = dispatcher.DispatchAsync(
            "validate.run",
            emptyPayload.RootElement,
            targetScope.Token);
        await blocking.Entered.Task;

        var blocked = await Assert.ThrowsAsync<JetActionException>(
            () => dispatcher.DispatchAsync(
                "project.releaseLock",
                emptyPayload.RootElement,
                CancellationToken.None));
        Assert.Equal(JetErrorCodes.OperationInProgress, blocked.Code);
        Assert.Equal(projectId, session.CurrentProjectId);
        Assert.IsType<LockOutcome.Held>(
            await contenderLocks.AcquireAsync(
                projectId,
                contenderPrincipal,
                CancellationToken.None));

        using var cancelPayload = JsonDocument.Parse("""{ "requestId": "release-gate-target" }""");
        var cancellation = await dispatcher.DispatchAsync(
            "operation.cancel",
            cancelPayload.RootElement,
            CancellationToken.None);
        Assert.True((bool)cancellation!.GetType().GetProperty("requested")!.GetValue(cancellation)!);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target);

        await dispatcher.DispatchAsync(
            "project.releaseLock",
            emptyPayload.RootElement,
            CancellationToken.None);
        Assert.Null(session.CurrentProjectId);
        Assert.IsType<LockOutcome.Acquired>(
            await contenderLocks.AcquireAsync(
                projectId,
                contenderPrincipal,
                CancellationToken.None));
    }

    [Fact]
    public async Task UnknownAction_WhileExclusiveActionInFlight_IsReportedAsUnknown()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exclusive = new BlockingHandler("validate.run", release);
        var dispatcher = Dispatcher(exclusive);
        using var payload = JsonDocument.Parse("{}");

        var first = dispatcher.DispatchAsync("validate.run", payload.RootElement, CancellationToken.None);
        await exclusive.Entered.Task;

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => dispatcher.DispatchAsync("validate.rnu", payload.RootElement, CancellationToken.None));
        Assert.Contains("validate.rnu", exception.Message, StringComparison.Ordinal);

        release.SetResult();
        await first;
    }

    [Fact]
    public async Task SequentialExclusiveActions_BothSucceed()
    {
        var handler = new ImmediateHandler("validate.run");
        var dispatcher = Dispatcher(handler);
        using var payload = JsonDocument.Parse("{}");

        // 依序 await：閘在每次結束後釋放，第二項不會被誤擋
        await dispatcher.DispatchAsync("validate.run", payload.RootElement, CancellationToken.None);
        await dispatcher.DispatchAsync("validate.run", payload.RootElement, CancellationToken.None);

        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("validate.run", true)]
    [InlineData("prescreen.run", true)]
    [InlineData("import.gl.fromFile", true)]
    [InlineData("mapping.commit.gl", true)]
    [InlineData("filter.commit", true)]
    // 一次只存、刪或重新檢查一個情境的三個動作都是變更型，同一時間只能有一項。
    [InlineData("filter.scenario.save", true)]
    [InlineData("filter.scenario.remove", true)]
    [InlineData("filter.scenario.recheck", true)]
    [InlineData("export.validationReports", true)]
    [InlineData("export.accountMappingTemplate", true)]
    [InlineData("export.prescreenReport", true)]
    [InlineData("export.criteriaSelectionReport", true)]
    [InlineData("export.workingPaperStream", true)]
    [InlineData("project.create", true)]
    [InlineData("project.update", true)]
    [InlineData("query.dataPreview", false)]
    [InlineData("filter.preview", false)]
    [InlineData("system.ping", false)]
    [InlineData("project.listLocal", false)]
    [InlineData("project.releaseLock", false)]
    [InlineData("host.selectFiles", false)]
    [InlineData("dev.log.exportFile", false)]
    [InlineData("support.log.export", false)]
    public void Policy_ClassifiesKnownActions(string action, bool exclusive)
        => Assert.Equal(exclusive, ActionExecutionPolicy.IsExclusive(action));

    [Theory]
    [InlineData("project.loadDemo")]
    [InlineData("demo.exportGlFile")]
    [InlineData("demo.exportTbFile")]
    [InlineData("demo.exportAccountMappingFile")]
    [InlineData("demo.exportAuthorizedPreparerFile")]
    public void DemoActions_RemainCompletelyClassifiedAsConcurrent(string action)
    {
        Assert.True(ActionExecutionPolicy.IsClassified(action));
        Assert.False(ActionExecutionPolicy.IsExclusive(action));
        Assert.False(ActionExecutionPolicy.RequiresConditionalGate(action));
    }

    // 漂移守衛：每個已註冊 action 都必須被 ActionExecutionPolicy 明確歸類（新增 action 未歸類時這個測試會失敗）。
    [Fact]
    public void EveryRegisteredAction_IsClassifiedByPolicy()
    {
        using var host = new HandlerTestHost();

        foreach (var action in host.Dispatcher.RegisteredActions)
        {
            Assert.True(
                ActionExecutionPolicy.IsClassified(action),
                $"action 未在 ActionExecutionPolicy 歸類：{action}（新增 action 時須明確歸為 exclusive、concurrent 或 conditional-gate）");
        }
    }

    private sealed class RecordingMessageLogStore : IMessageLogStore
    {
        public List<(string ProjectId, string Level, string Text)> Appended { get; } = [];

        public Task AppendAsync(
            string projectId,
            string level,
            string text,
            CancellationToken cancellationToken)
        {
            Appended.Add((projectId, level, text));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MessageLogEntry>> GetRecentAsync(
            string projectId,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MessageLogEntry>>([]);
    }
}
