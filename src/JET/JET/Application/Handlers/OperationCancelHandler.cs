using System.Text.Json;

namespace JET.Application;

/// <summary>
/// operation.cancel：要求取消 requestId 指定的那個進行中的請求。這裡只送出取消的訊號，由那個請求自己在適當的地方停下來；
/// 回應的 requested 表示有沒有找到這個請求並送出訊號。
/// </summary>
public sealed class OperationCancelHandler(IOperationCancellationService cancellationService)
    : IApplicationActionHandler
{
    public string Action => "operation.cancel";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var requestId = PayloadReader.GetRequiredString(payload, "requestId");
        var requested = cancellationService.TryCancel(requestId);

        return Task.FromResult<object?>(new { requestId, requested });
    }
}
