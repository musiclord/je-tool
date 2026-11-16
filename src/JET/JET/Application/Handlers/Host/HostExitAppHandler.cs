using System.Text.Json;

namespace JET.Application;

/// <summary>
/// host.exitApp：要求關閉 JET 的視窗。
/// 這裡只關視窗，不儲存進度；前端要先呼叫 project.saveProgress 存好進度。
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
