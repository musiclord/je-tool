using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed class LocalAccountUsageExportRepository(ILocalProjectDatabase database) : IAccountUsageExportRepository
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
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COALESCE(account_code, ''), MAX(account_name), COUNT(*), " +
            "COALESCE(SUM(debit_amount_scaled), 0), COALESCE(SUM(credit_amount_scaled), 0) " +
            $"FROM target_gl_entry WHERE {GlEffectivePopulation.SqlPredicate()} " +
            "GROUP BY account_code ORDER BY COUNT(*), account_code;";
        return await ReadAsync(command, cancellationToken);
    }

    internal static async Task<IReadOnlyList<AccountUsageExportRow>> ReadAsync(
        System.Data.Common.DbCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<AccountUsageExportRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AccountUsageExportRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }
        return rows;
    }
}
