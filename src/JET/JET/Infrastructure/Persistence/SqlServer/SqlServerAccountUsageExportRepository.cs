using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed class SqlServerAccountUsageExportRepository(SqlServerProjectDatabase database) : IAccountUsageExportRepository
{
    public async Task<IReadOnlyList<AccountUsageExportRow>> FetchAllAsync(
        string projectId,
        string periodStart,
        string periodEnd,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = database.CreateCommand(
            connection,
            projectId,
            "SELECT COALESCE(account_code, ''), MAX(account_name), COUNT_BIG(*), " +
            "COALESCE(SUM(debit_amount_scaled), 0), COALESCE(SUM(credit_amount_scaled), 0) " +
            $"FROM {{s}}.target_gl_entry WHERE {GlEffectivePopulation.SqlPredicate()} " +
            "GROUP BY account_code ORDER BY COUNT_BIG(*), account_code;");
        return await LocalAccountUsageExportRepository.ReadAsync(command, cancellationToken);
    }
}
