using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 診斷日誌 ring buffer provider 的地基測試（直接對 provider,不經 LoggerFactory）。
/// oracle:規格手算的小型固定資料集。涵蓋 TDD #5（滿載覆寫最舊不爆 memory）、scope 捕捉、exception。
/// </summary>
public sealed class RingBufferLoggerProviderTests
{
    [Fact]
    public void RingBuffer_BeyondCapacity_OverwritesOldest_NoBlowup()
    {
        using var provider = new RingBufferLoggerProvider(capacity: 3);
        var logger = provider.CreateLogger("Test");

        for (var i = 1; i <= 5; i++)
        {
            logger.LogInformation("msg {N}", i);
        }

        var snapshot = provider.Snapshot();
        Assert.Equal(3, snapshot.Count);              // 不超過容量
        Assert.Equal("msg 3", snapshot[0].Message);   // 最舊保留 = 第 3 筆（1、2 被覆寫）
        Assert.Equal("msg 5", snapshot[^1].Message);  // 最新在末（舊→新）
    }

    [Fact]
    public void Log_WithBeginScope_CapturesCorrelationAndTransactionId()
    {
        using var provider = new RingBufferLoggerProvider(capacity: 10);
        var logger = provider.CreateLogger("Test");

        using (logger.BeginScope(new Dictionary<string, object?> { ["correlation_id"] = "C1" }))
        {
            logger.LogInformation("outer");
            using (logger.BeginScope(new Dictionary<string, object?> { ["transaction_id"] = "T1" }))
            {
                logger.LogInformation("inner");
            }
        }

        var snapshot = provider.Snapshot();
        var outer = snapshot.Single(e => e.Message == "outer");
        var inner = snapshot.Single(e => e.Message == "inner");

        Assert.Equal("C1", outer.CorrelationId);
        Assert.Null(outer.TransactionId);
        Assert.Equal("C1", inner.CorrelationId); // 外層 scope 仍在
        Assert.Equal("T1", inner.TransactionId); // 巢狀 scope
    }

    [Fact]
    public void Log_WithException_CapturesFullStackTraceAndInner()
    {
        using var provider = new RingBufferLoggerProvider(capacity: 10);
        var logger = provider.CreateLogger("Test");
        var exception = new InvalidOperationException("outer-msg", new ArgumentException("inner-msg"));

        logger.LogError(exception, "failed");

        var entry = provider.Snapshot().Single();
        Assert.NotNull(entry.Exception);
        Assert.Contains("InvalidOperationException", entry.Exception);
        Assert.Contains("outer-msg", entry.Exception);
        Assert.Contains("ArgumentException", entry.Exception); // inner exception
        Assert.Contains("inner-msg", entry.Exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RingBufferLoggerProvider_NonPositiveCapacity_KeepsLatestEntry(int capacity)
    {
        using var provider = new RingBufferLoggerProvider(capacity);
        var logger = provider.CreateLogger("Test");

        logger.LogInformation("first");
        logger.LogInformation("second");

        var entry = Assert.Single(provider.Snapshot());
        Assert.Equal("second", entry.Message);
    }


    [Fact]
    public void BeginScope_DisposedBeforeLog_DoesNotCaptureScope()
    {
        using var provider = new RingBufferLoggerProvider(capacity: 10);
        var logger = provider.CreateLogger("Test");
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["correlation_id"] = "C1",
            ["action"] = "validate.run",
        });

        scope?.Dispose();
        logger.LogInformation("after scope");

        var entry = Assert.Single(provider.Snapshot());
        Assert.Null(entry.CorrelationId);
        Assert.DoesNotContain("action", entry.Fields.Keys);
    }

    [Fact]
    public void Log_StructuredStateAndNamedEventId_CapturesEntryValues()
    {
        using var provider = new RingBufferLoggerProvider(capacity: 10);
        var logger = provider.CreateLogger("StructuredCategory");
        var state = new Dictionary<string, object?>
        {
            ["row_count"] = 12,
            ["duration_ms"] = 34,
            ["{OriginalFormat}"] = "ignored-template",
        };

        using (logger.BeginScope(new Dictionary<string, object?> { ["project_id"] = "P1", ["action"] = "import.gl.fromFile" }))
        {
            logger.Log(LogLevel.Warning, new EventId(17, "audit.event"), state, null, (currentState, _) => $"rows={currentState["row_count"]}");
        }

        var entry = Assert.Single(provider.Snapshot());
        Assert.Equal("Warning", entry.Level);
        Assert.Equal("StructuredCategory", entry.Category);
        Assert.Equal("audit.event", entry.EventName);
        Assert.Equal("rows=12", entry.Message);
        Assert.Equal("P1", entry.ProjectId);
        Assert.Equal("import.gl.fromFile", entry.Fields["action"]?.ToString());
        Assert.Equal(12, entry.Fields["row_count"]);
        Assert.Equal(34, entry.Fields["duration_ms"]);
        Assert.DoesNotContain("{OriginalFormat}", entry.Fields.Keys);
    }

    [Fact]
    public void Log_UnnamedEventId_UsesEmptyEventName()
    {
        using var provider = new RingBufferLoggerProvider(capacity: 10);
        var logger = provider.CreateLogger("Test");

        logger.Log(LogLevel.Debug, new EventId(99), "payload", null, (state, _) => state);

        var entry = Assert.Single(provider.Snapshot());
        Assert.Equal(string.Empty, entry.EventName);
        Assert.Equal("payload", entry.Message);
    }

}
