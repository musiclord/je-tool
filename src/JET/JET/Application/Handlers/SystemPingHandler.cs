using System.Text.Json;

namespace JET.Application;

/// <summary>
/// system.ping：基本 host 通訊檢查（前端啟動 round-trip；取代已退役的 __jet.probe）。
/// devToolsEnabled 標示本組建是否註冊開發者工具（Debug 組建 true、Release 組建 false），
/// 前端據此決定是否顯示開發面板。
/// </summary>
public sealed class SystemPingHandler(bool devToolsEnabled) : IApplicationActionHandler
{
    public string Action => "system.ping";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        return Task.FromResult<object?>(new
        {
            message = "JET host bridge ok.",
            utcNow = DateTimeOffset.UtcNow,
            devToolsEnabled
        });
    }
}
