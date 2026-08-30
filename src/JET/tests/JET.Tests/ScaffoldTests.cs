using System.Text.Json;
using JET.Application;
using JET.Bridge;
using JET.Tests.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JET.Tests;

/// <summary>
/// 最薄層的傳輸骨架測試：dispatcher 行為與正式的 system.ping round-trip
/// （provisional __jet.* 與 prototype 組態庫已退役——組態只持久化在 projects/{id}/）。
/// </summary>
public sealed class ScaffoldTests
{
    [Fact]
    public async Task SystemPing_ReturnsMessageAndUtcNow()
    {
        using var host = new HandlerTestHost();

        var data = await host.DispatchAsync("system.ping");

        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("message").GetString()));
        Assert.NotEqual(default, data.GetProperty("utcNow").GetDateTimeOffset());
    }

    [Fact]
    public async Task ActionDispatcher_UnknownAction_ThrowsKeyNotFoundException()
    {
        var dispatcher = new ActionDispatcher([], NullLogger<ActionDispatcher>.Instance, new ProjectSession());

        using var payload = JsonDocument.Parse("{}");
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            dispatcher.DispatchAsync("__jet.missing", payload.RootElement, CancellationToken.None));
    }
}
