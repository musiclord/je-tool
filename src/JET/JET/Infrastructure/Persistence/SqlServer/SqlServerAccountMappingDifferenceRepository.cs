using JET.Domain;

namespace JET.Infrastructure;

public sealed class SqlServerAccountMappingDifferenceRepository(SqlServerProjectDatabase database) : IAccountMappingDifferenceRepository
{
    public async Task<AccountMappingDifferenceCounts> CountAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        return await AccountMappingDifferenceQuery.CountAsync(connection, SqlServerDialect.Instance,
            SqlServerProjectSchema.QualifierFor(projectId), cancellationToken);
    }

    public async Task<AccountMappingDifferencePage> GetPageAsync(
        string projectId, string kind, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        return await AccountMappingDifferenceQuery.GetPageAsync(connection, SqlServerDialect.Instance,
            SqlServerProjectSchema.QualifierFor(projectId), kind, request, cancellationToken);
    }
}
