using System.Globalization;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed class SqlServerSourceQualityPageRepository(SqlServerProjectDatabase database)
    : ISourceQualityPageRepository
{
    public async Task<PageResult<SourceQualityFindingRow>> GetPageAsync(
        string projectId,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await SourceQualityPageReader.ReadAsync(connection, null, SqlServerDialect.Instance,
            SqlServerProjectSchema.QualifierFor(projectId), request, cancellationToken);
    }
}