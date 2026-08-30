using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// ActionDispatcher 的診斷日誌（TDD #1 action 生命週期、#3 correlation 一致、#4 exception）。
/// oracle:dispatch 已知 action,斷言 ring buffer 內的 action.start/end/error 結構化欄位。
/// </summary>
public sealed class DispatcherDiagnosticsTests
{
    [Fact]
    public async Task Dispatch_LogsActionLifecycle_WithCorrelationAndDuration()
    {
        using var host = new HandlerTestHost(enableDevTools: true);

        await host.DispatchAsync("system.ping"); // 不需 active project、不打 DB

        var entries = host.DiagnosticLog!.Snapshot();
        var start = entries.Single(e => e.EventName == "action.start");
        var end = entries.Single(e => e.EventName == "action.end");

        Assert.Equal("system.ping", start.Fields["action"]?.ToString());
        Assert.NotNull(start.CorrelationId);
        Assert.Equal(start.CorrelationId, end.CorrelationId); // 同一 dispatch 共享 correlation_id
        Assert.Equal("ok", end.Fields["result_status"]?.ToString());
        Assert.True(Convert.ToInt64(end.Fields["duration_ms"]) >= 0);
    }

    [Fact]
    public async Task Dispatch_HandlerThrows_LogsActionErrorWithStackTrace()
    {
        using var host = new HandlerTestHost(enableDevTools: true);

        // log.append 無 active project → JetActionException(no_active_project)
        await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("log.append", """{ "text": "x" }"""));

        var error = host.DiagnosticLog!.Snapshot().Single(e => e.EventName == "action.error");
        Assert.Equal("log.append", error.Fields["action"]?.ToString());
        Assert.NotNull(error.CorrelationId);
        Assert.NotNull(error.Exception);
        Assert.Contains("JetActionException", error.Exception); // 完整例外（含 stack trace）
    }

    [Fact]
    public async Task Correlation_SharedAcrossDispatcherAndRepository()
    {
        using var host = new HandlerTestHost(enableDevTools: true);
        await host.DispatchAsync("project.create",
            """{ "projectCode":"C","entityName":"關聯案","operatorId":"smoke","periodStart":"2025-01-01","periodEnd":"2025-12-31" }""");

        // 真檔匯入(sqlite 預設 provider)→ 觸發 LocalImportRepository 的 sql.executed,應帶 import dispatch 的 correlation_id
        var csvPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
        await File.WriteAllTextAsync(csvPath, "科目,金額\n1001,100\n");
        try
        {
            await host.DispatchAsync("import.gl.fromFile",
                System.Text.Json.JsonSerializer.Serialize(new { filePath = csvPath, mode = "replace" }));
        }
        finally
        {
            File.Delete(csvPath);
        }

        var entries = host.DiagnosticLog!.Snapshot();
        var importStart = entries.Last(e => e.EventName == "action.start" && e.Fields["action"]?.ToString() == "import.gl.fromFile");
        var createStart = entries.First(e => e.EventName == "action.start" && e.Fields["action"]?.ToString() == "project.create");

        Assert.NotNull(importStart.CorrelationId);
        // 跨層:repo 的 SQL 帶 import dispatch 的 correlation_id（BeginScope 經 AsyncLocal 自動傳入子層）
        Assert.Contains(entries, e => e.EventName == "sql.executed" && e.CorrelationId == importStart.CorrelationId);
        // 不同 dispatch 不同 correlation_id
        Assert.NotEqual(createStart.CorrelationId, importStart.CorrelationId);
    }

    [Fact]
    public void ActionStart_LoggerEnabled_WritesStructuredInformationEvent()
    {
        using var provider = new JET.Infrastructure.RingBufferLoggerProvider(capacity: 10);
        using var factory = new Microsoft.Extensions.Logging.LoggerFactory([provider]);
        var logger = factory.CreateLogger("dispatcher-tests");

        JET.Bridge.DispatcherDiagnostics.ActionStart(logger, "system.ping");

        var entry = Assert.Single(provider.Snapshot());
        Assert.Equal("Information", entry.Level);
        Assert.Equal("dispatcher-tests", entry.Category);
        Assert.Equal("action.start", entry.EventName);
        Assert.Equal("action system.ping start", entry.Message);
        Assert.Equal("system.ping", entry.Fields["action"]?.ToString());
        Assert.False(entry.Fields.ContainsKey("result_status"));
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void ActionEnd_LoggerEnabled_WritesStatusAndDurationFields()
    {
        using var provider = new JET.Infrastructure.RingBufferLoggerProvider(capacity: 10);
        using var factory = new Microsoft.Extensions.Logging.LoggerFactory([provider]);
        var logger = factory.CreateLogger("dispatcher-tests");

        JET.Bridge.DispatcherDiagnostics.ActionEnd(logger, "filter.preview", "ok", 42);

        var entry = Assert.Single(provider.Snapshot());
        Assert.Equal("Information", entry.Level);
        Assert.Equal("dispatcher-tests", entry.Category);
        Assert.Equal("action.end", entry.EventName);
        Assert.Equal("action filter.preview ok in 42 ms", entry.Message);
        Assert.Equal("filter.preview", entry.Fields["action"]?.ToString());
        Assert.Equal("ok", entry.Fields["result_status"]?.ToString());
        Assert.Equal(42L, entry.Fields["duration_ms"]);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void ActionError_LoggerEnabled_WritesErrorEventWithExceptionAndDuration()
    {
        using var provider = new JET.Infrastructure.RingBufferLoggerProvider(capacity: 10);
        using var factory = new Microsoft.Extensions.Logging.LoggerFactory([provider]);
        var logger = factory.CreateLogger("dispatcher-tests");
        var exception = new InvalidOperationException("boom");

        JET.Bridge.DispatcherDiagnostics.ActionError(logger, "validate.run", 7, exception);

        var entry = Assert.Single(provider.Snapshot());
        Assert.Equal("Error", entry.Level);
        Assert.Equal("dispatcher-tests", entry.Category);
        Assert.Equal("action.error", entry.EventName);
        Assert.Equal("action validate.run failed in 7 ms", entry.Message);
        Assert.Equal("validate.run", entry.Fields["action"]?.ToString());
        Assert.Equal(7L, entry.Fields["duration_ms"]);
        Assert.False(entry.Fields.ContainsKey("result_status"));
        Assert.Contains("InvalidOperationException", entry.Exception);
        Assert.Contains("boom", entry.Exception);
    }


}
