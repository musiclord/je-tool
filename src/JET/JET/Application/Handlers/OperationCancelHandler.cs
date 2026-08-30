using System.Text.Json;

namespace JET.Application;

/// <summary>operation.cancel：依目標 requestId 發出 cooperative cancellation。</summary>
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
