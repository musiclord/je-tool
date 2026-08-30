using JET.Domain.Enums;

namespace JET.Domain.Abstractions
{
    public interface IAppStateStore
    {
        Task<DatabaseStatus> GetStatusAsync(CancellationToken cancellationToken);
    }

    public sealed record DatabaseStatus(
        DatabaseProvider Provider,
        bool IsAvailable,
        string ConnectionTarget,
        string Mode);
}
