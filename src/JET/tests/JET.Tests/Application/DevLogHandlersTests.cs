using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// dev.log.export 的 Application 層驗收(TDD #6:NDJSON 每行可被 System.Text.Json 解析)。
/// 診斷日誌跨專案、dev-only;閘控(Release 不註冊)見 DevToolsGatingTests。
/// </summary>
public sealed class DevLogHandlersTests
{
    [Fact]
    public async Task DevLogExport_ReturnsNdjson_EachLineParsableByStj()
    {
        using var host = new HandlerTestHost(enableDevTools: true);

        await host.DispatchAsync("system.ping"); // 產生 action.start/end 診斷日誌

        var export = await host.DispatchAsync("dev.log.export");
        var ndjson = export.GetProperty("ndjson").GetString()!;
        var lines = ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line); // 每行皆為完整 JSON 物件（NDJSON）
            Assert.True(document.RootElement.TryGetProperty("eventName", out _));
        }

        Assert.Contains(lines, l => l.Contains("action.start")); // 含預期事件
    }
}
