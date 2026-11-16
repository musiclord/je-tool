using System.Text.Json;

namespace JET.Application;

/// <summary>
/// system.ping：確認前端和 C# 之間的通道是通的；前端啟動時送一次，收到回應就算通。
/// devToolsEnabled 表示這個組建有沒有註冊開發者工具，Debug 組建是 true，Release 組建是 false；
/// 前端依它決定要不要顯示開發面板。
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
