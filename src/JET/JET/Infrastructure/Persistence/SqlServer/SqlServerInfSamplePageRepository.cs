using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// INF 抽樣行層明細 keyset 分頁(SQL Server,鏡像 <see cref="LocalInfSamplePageRepository"/>)。
/// 以 caller 指定 runId 限定樣本(共用參數化 <see cref="InfSamplePageSql.RunFilter"/>);
/// 排序鍵 entry_id ASC、游標展開布林式(@cursor 綁 long)、limit 由 <see cref="SqlServerDialect"/>
/// 出 OFFSET/FETCH(ORDER BY g.entry_id 已具備)。
/// </summary>
public sealed class SqlServerInfSamplePageRepository(SqlServerProjectDatabase database)
    : IInfSamplePageRepository
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    public async Task<PageResult<InfSampleRow>> GetPageAsync(
        string projectId, string runId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var paging = KeysetPaging.Plan(Dialect, request, ResultPageSorting.InfSample);

        await using var command = database.CreateCommand(connection, projectId,
            "SELECT g.document_number, g.account_code, g.account_name, " +
            "       g.debit_amount_scaled, g.credit_amount_scaled, " +
            "       g.post_date, g.approval_date, g.created_by, g.approved_by, g.document_description, g.entry_id" + paging.SelectSuffix + " " +
            "FROM {s}.result_inf_sampling_test_sample s " +
            "JOIN {s}.target_gl_entry g ON g.entry_id = s.entry_id " +
            "WHERE " + InfSamplePageSql.RunFilter +
            $" AND {GlEffectivePopulation.SqlPredicate("g")}" + paging.Predicate + " " +
            paging.OrderBy + " " + Dialect.LimitClause("@pageSize") + ";");
        foreach (var parameter in paging.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);
        command.Parameters.AddWithValue("@runId", runId);

        var buffer = new KeysetPageBuffer<InfSampleRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buffer.Add(
                new InfSampleRow(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetInt64(10)),
                paging.HasSort ? reader.GetValue(11) : null);
        }

        return buffer.ToPage(request, paging, static row => row.EntryId);
    }
}
