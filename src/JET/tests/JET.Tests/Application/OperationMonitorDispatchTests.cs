using System.Diagnostics;
using System.Text.Json;
using JET.Application;
using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 操作監看模組接在 dispatcher 與組裝上的情形：長時間操作的定時紀錄帶同一個 correlation_id，
/// 摘要寫在 action 結束之後，取消時記下要求取消到真正停下的時間，失敗與取消的診斷紀錄帶失敗步驟，
/// 匯出報表時的進度與 SQL 統計進入診斷日誌。
/// </summary>
public sealed class OperationMonitorDispatchTests
{
    private const string MonitoredAction = "test.monitored";

    private static OperationMonitorOptions Fast(TimeSpan stillRunningAfter) => new()
    {
        CheckInterval = TimeSpan.FromMilliseconds(20),
        SlowSqlThreshold = TimeSpan.FromMilliseconds(100),
        SlowSqlRepeat = TimeSpan.FromHours(1),
        StillRunningAfter = stillRunningAfter,
        StillRunningRepeat = TimeSpan.FromHours(1),
        ExportProgressInterval = TimeSpan.FromHours(1),
        SummaryAfter = TimeSpan.FromHours(1),
    };

    [Fact]
    public async Task LongAction_LogsStillRunningWithTheActionCorrelation_AndSummaryAfterActionEnd()
    {
        using var provider = new RingBufferLoggerProvider(10_000);
        using var factory = Factory(provider);
        using var monitor = new OperationMonitor(factory.CreateLogger<OperationMonitor>(), Fast(TimeSpan.FromMilliseconds(100)));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Dispatcher(factory, monitor, new StagedHandler(cancellationToken => release.Task.WaitAsync(cancellationToken)));

        // 操作一直進行到定時紀錄出現為止，不靠固定的等待時間。
        var dispatched = Dispatch(dispatcher, CancellationToken.None);
        await WaitForAsync(provider, "action.still_running");
        release.SetResult();
        await dispatched;

        var entries = provider.Snapshot().ToList();
        var start = Assert.Single(entries, e => e.EventName == "action.start");
        var still = Assert.Single(entries, e => e.EventName == "action.still_running");
        Assert.Equal(start.CorrelationId, still.CorrelationId);
        Assert.Equal(MonitoredAction, still.Fields["action"]?.ToString());
        Assert.Equal("report_write", still.Fields["current_stage"]?.ToString());
        var end = entries.FindIndex(e => e.EventName == "action.end");
        var summary = entries.FindIndex(e => e.EventName == "action.summary");
        Assert.True(end >= 0 && summary > end, "action.summary 要寫在 action.end 之後");
        Assert.Equal(start.CorrelationId, entries[summary].CorrelationId);
    }

    /// <summary>
    /// 同步的 Cancel() 會在取消的執行緒上直接把等待中的操作跑完，監看模組最早登記的取消回呼可能還沒輪到就被移除；
    /// 兩種取消方式都要記得到取消的時間。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledAction_SummaryHasCancelTiming_AndCancelledEntryCarriesTheFailureStage(bool cancelAsync)
    {
        using var provider = new RingBufferLoggerProvider(10_000);
        using var factory = Factory(provider);
        using var monitor = new OperationMonitor(factory.CreateLogger<OperationMonitor>(), Fast(TimeSpan.FromHours(1)));
        var handler = new StagedHandler(cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken));
        var dispatcher = Dispatcher(factory, monitor, handler);
        using var cancellation = new CancellationTokenSource();

        var dispatched = Dispatch(dispatcher, cancellation.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // 計時器可能比要求的時間早一點到期，所以用和監看模組相同的時鐘量滿 300 毫秒才取消；操作開始得更早，下限一定成立。
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromMilliseconds(300))
        {
            await Task.Delay(10);
        }

        if (cancelAsync)
        {
            await cancellation.CancelAsync();
        }
        else
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatched);

        var entries = provider.Snapshot();
        var cancelled = Assert.Single(entries, e => e.EventName == "action.cancelled");
        Assert.Equal("report_write", cancelled.Fields["failure_stage"]?.ToString());
        var summary = Assert.Single(entries, e => e.EventName == "action.summary");
        Assert.Equal("Information", summary.Level);
        Assert.True(Convert.ToInt64(summary.Fields["cancel_requested_after_ms"]) >= 300);
        Assert.True(Convert.ToInt64(summary.Fields["cancel_latency_ms"]) >= 0);
        Assert.Equal(cancelled.CorrelationId, summary.CorrelationId);
    }

    [Fact]
    public async Task ActionCancelledBeforeItStarts_WritesNoSummary()
    {
        using var provider = new RingBufferLoggerProvider(10_000);
        using var support = new SupportRingBufferLoggerProvider(100);
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
            builder.AddProvider(support);
        });
        using var monitor = new OperationMonitor(factory.CreateLogger<OperationMonitor>(), Fast(TimeSpan.FromHours(1)));
        var dispatcher = Dispatcher(factory, monitor, new StagedHandler(cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Dispatch(dispatcher, new CancellationToken(canceled: true)));

        Assert.Contains(support.Snapshot(), e => e.EventName == "action.cancelled");
        Assert.DoesNotContain(support.Snapshot(), e => e.EventName == "action.summary");
        Assert.DoesNotContain(provider.Snapshot(), e => e.EventName == "action.summary");
    }

    [Fact]
    public async Task CriteriaExport_WritesProgressStagesAndSqlCountsIntoTheDiagnosticLog()
    {
        using var host = new HandlerTestHost(enableDevTools: true);
        var prepared = await ReportRevisionStateTests.PrepareAsync(host, "sqlite");

        await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new
        { validationResultId = prepared.ValidationResultId, revision = prepared.Revision }));

        var entries = host.DiagnosticLog!.Snapshot();
        var start = entries.Last(e => e.EventName == "action.start"
            && e.Fields["action"]?.ToString() == "export.criteriaSelectionReport");
        var mine = entries.Where(e => e.CorrelationId == start.CorrelationId).ToList();
        var progress = mine.Where(e => e.EventName == "export.progress").ToList();
        Assert.NotEmpty(progress);
        Assert.All(progress, e => Assert.Equal("criteriaSelectionReport", e.Fields["report_kind"]?.ToString()));
        Assert.Contains(mine, e => e.EventName == "action.stage_entered" && e.Fields["stage"]?.ToString() == "report_write");
        var summary = Assert.Single(mine, e => e.EventName == "action.summary");
        Assert.True(Convert.ToInt64(summary.Fields["sql_statements"]) > 0);
        Assert.Contains("report_write", summary.Fields["stage_path"]?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortActionWithoutSqlOrStages_WritesNoSummary()
    {
        using var host = new HandlerTestHost(enableDevTools: true);

        await host.DispatchAsync("system.ping");

        Assert.DoesNotContain(host.DiagnosticLog!.Snapshot(), e => e.EventName == "action.summary");
    }

    private static ILoggerFactory Factory(RingBufferLoggerProvider provider) => LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Trace);
        builder.AddProvider(provider);
    });

    private static ActionDispatcher Dispatcher(ILoggerFactory factory, OperationMonitor monitor, StagedHandler handler) => new(
        [handler],
        factory.CreateLogger<ActionDispatcher>(),
        new ProjectSession(),
        operationMonitor: monitor);

    private static async Task Dispatch(ActionDispatcher dispatcher, CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse("{}");
        await dispatcher.DispatchAsync(MonitoredAction, payload.RootElement, cancellationToken);
    }

    /// <summary>等到日誌出現某種紀錄；定時紀錄由背景的計時器寫，最多等 30 秒。</summary>
    private static async Task WaitForAsync(RingBufferLoggerProvider provider, string eventName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!provider.Snapshot().Any(e => e.EventName == eventName))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No {eventName} entry within 30 seconds.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>進入 report_write 步驟後等 <paramref name="wait"/> 結束；等待期間要求取消時照常丟出取消例外。</summary>
    private sealed class StagedHandler(Func<CancellationToken, Task> wait) : IApplicationActionHandler
    {
        public string Action => MonitoredAction;

        /// <summary>handler 已經進入 report_write 步驟。</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            SupportOperationStage.Enter("report_write");
            Entered.TrySetResult();
            await wait(cancellationToken);
            return new { done = true };
        }
    }
}
