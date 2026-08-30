using System.Text.Json;

namespace JET.Application;

public interface IApplicationActionHandler
{
    string Action { get; }

    Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken);
}
