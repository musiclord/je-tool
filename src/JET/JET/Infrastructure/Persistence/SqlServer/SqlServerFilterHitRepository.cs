using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 條件 AST → 參數化 SQL 的 SQL Server 執行(對應 <see cref="LocalFilterHitRepository"/>)。
/// WHERE 組譯共用 provider 中立的 <see cref="GlFilterWhereBuilder"/>(述詞 + <see cref="SqlServerDialect"/>);
/// 本類只負責 SQL Server 連線與 SELECT 骨架。預覽 LIMIT 50 → TOP (50)。
/// 診斷日誌（dev-only）：每個 SELECT 走 <see cref="DiagnosticDb"/> 擴充方法記錄完整 SQL/參數。
/// </summary>
public sealed class SqlServerFilterHitRepository(SqlServerProjectDatabase database, ILogger<SqlServerFilterHitRepository>? logger = null)
    : IFilterHitRepository
{
    private const int PreviewRowLimit = 50;
    private const string Provider = "sqlServer";

    private static readonly GlFilterWhereBuilder WhereBuilder =
        new(
            SqlServerDialect.Instance,
            new GlRulePredicates(SqlServerDialect.Instance, GlPopulationScopeSql.Predicate));

    private readonly ILogger _log = logger ?? NullLogger<SqlServerFilterHitRepository>.Instance;

    public async Task<FilterPreviewResult> PreviewAsync(
        string projectId,
        FilterScenarioSpec scenario,
        FilterConditionContext context,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var count = await ExecuteScalarAsync(
            connection, projectId, scenario, context, "COUNT_BIG(*)", cancellationToken);
        var voucherCount = await ExecuteScalarAsync(
            connection, projectId, scenario, context, "COUNT_BIG(DISTINCT g.voucher_number)", cancellationToken);
        var previewRows = await ReadPreviewRowsAsync(
            connection, projectId, scenario, context, cancellationToken);

        return new FilterPreviewResult(count, voucherCount, previewRows);
    }

    public async Task<long> CountWholeVoucherRowsAsync(
        string projectId,
        FilterScenarioSpec scenario,
        FilterConditionContext context,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var plan = WhereBuilder.BuildPlan(
            scenario,
            context,
            SqlServerProjectSchema.QualifierFor(projectId));
        plan.BindParametersTo(command);
        // 條件的 WHERE plan 只引用子查詢的 g；外層用另一個別名列出同傳票的全部分錄。
        await using (var expand = database.CreateCommand(connection, projectId,
            "SELECT COUNT_BIG(*) FROM {s}.gl whole_row "
            + $"WHERE {GlPopulationScopeSql.Predicate(context, "whole_row")} AND whole_row.voucher_number IN ("
            + "SELECT g.voucher_number FROM {s}.gl g "
            + $"WHERE g.voucher_number IS NOT NULL AND {GlPopulationScopeSql.Predicate(context, "g")} AND ({plan.Sql}));"))
        {
            command.CommandText = expand.CommandText;
        }

        var result = await command.ExecuteScalarLoggedAsync(_log, Provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private async Task<long> ExecuteScalarAsync(
        SqlConnection connection,
        string projectId,
        FilterScenarioSpec scenario,
        FilterConditionContext context,
        string selectExpression,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var plan = WhereBuilder.BuildPlan(
            scenario,
            context,
            SqlServerProjectSchema.QualifierFor(projectId));
        plan.BindParametersTo(command);
        // {s} 由命令工廠收斂;WhereBuilder 需先綁到 command,故借工廠展開 token 後回填本命令。
        await using (var expand = database.CreateCommand(connection, projectId,
            $"SELECT {selectExpression} FROM {{s}}.gl g "
            + $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} AND ({plan.Sql});"))
        {
            command.CommandText = expand.CommandText;
        }

        var result = await command.ExecuteScalarLoggedAsync(_log, Provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private async Task<IReadOnlyList<FilterPreviewRow>> ReadPreviewRowsAsync(
        SqlConnection connection,
        string projectId,
        FilterScenarioSpec scenario,
        FilterConditionContext context,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var plan = WhereBuilder.BuildPlan(
            scenario,
            context,
            SqlServerProjectSchema.QualifierFor(projectId));
        plan.BindParametersTo(command);
        // {s} 由命令工廠收斂;WhereBuilder 需先綁到 command,故借工廠展開 token 後回填本命令。
        await using (var expand = database.CreateCommand(connection, projectId,
            $$"""
            SELECT TOP ({{PreviewRowLimit}})
                   g.voucher_number, g.line_item, g.gl_date, g.account_number,
                   g.account_name, g.description, g.amount_scaled, g.dr_cr
            FROM {s}.gl g
            WHERE {{GlPopulationScopeSql.Predicate(context, "g")}} AND ({{plan.Sql}})
            ORDER BY g.entry_id;
            """))
        {
            command.CommandText = expand.CommandText;
        }

        var rows = new List<FilterPreviewRow>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FilterPreviewRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6),
                reader.GetString(7)));
        }

        return rows;
    }
}
