using System.Text.Json;

namespace JET.Application;

/// <summary>
/// host.exitApp：請求 host 關閉應用程式視窗。
/// 純 host capability（guide §12 Host）；進度保存由前端先行呼叫 project.saveProgress 完成。
/// </summary>
public sealed class HostExitAppHandler(IHostShell hostShell) : IApplicationActionHandler
{
    public string Action => "host.exitApp";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        hostShell.RequestExit();
        return Task.FromResult<object?>(new { ok = true });
    }
}
