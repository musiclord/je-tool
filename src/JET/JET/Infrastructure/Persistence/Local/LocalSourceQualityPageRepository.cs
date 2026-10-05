using System.Globalization;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// SQLite/DuckDB 共用的 current successful generation source-quality page。
/// target 保存全部 raw projection rows，所以 null post date 即使被 period 排除仍會列出；
/// join staging/source metadata 只為還原實際來源列號與來源標籤。
/// </summary>
public sealed class LocalSourceQualityPageRepository(ILocalProjectDatabase database)
    : ISourceQualityPageRepository
{
    public async Task<PageResult<SourceQualityFindingRow>> GetPageAsync(
        string projectId,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        return await SourceQualityPageReader.ReadAsync(connection, null, database.Dialect, string.Empty, request, cancellationToken);
    }
}