using JET.Domain;

namespace JET.Infrastructure;

public sealed class LocalAccountMappingDifferenceRepository(ILocalProjectDatabase database) : IAccountMappingDifferenceRepository
{
    public async Task<AccountMappingDifferenceCounts> CountAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        return await AccountMappingDifferenceQuery.CountAsync(connection, database.Dialect, "", cancellationToken);
    }

    public async Task<AccountMappingDifferencePage> GetPageAsync(
        string projectId, string kind, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        return await AccountMappingDifferenceQuery.GetPageAsync(connection, database.Dialect, "", kind, request, cancellationToken);
    }
}
