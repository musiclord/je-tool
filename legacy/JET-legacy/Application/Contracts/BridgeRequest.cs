using System.Text.Json;
using System.Text.Json.Serialization;

namespace JET.Application.Contracts
{
    public sealed record BridgeRequest(
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("payload")] JsonElement Payload);
}
