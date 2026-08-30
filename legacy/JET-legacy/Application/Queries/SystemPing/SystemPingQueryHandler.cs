using JET.Application.Contracts;

namespace JET.Application.Queries.SystemPing
{
    public sealed class SystemPingQueryHandler
    {
        public Task<SystemPingDto> HandleAsync(SystemPingQuery query, CancellationToken cancellationToken)
        {
            var response = new SystemPingDto("pong", DateTimeOffset.UtcNow);
            return Task.FromResult(response);
        }
    }
}
