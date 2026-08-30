using System.Text.Json;

namespace JET.Bridge
{
    public interface IActionDispatcher
    {
        IReadOnlyCollection<string> SupportedActions { get; }

        Task<object?> DispatchAsync(string action, JsonElement payload, CancellationToken cancellationToken);
    }
}
