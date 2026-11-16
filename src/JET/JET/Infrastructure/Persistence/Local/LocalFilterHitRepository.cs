using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 把篩選條件轉成參數化 SQL 並執行，用於預覽：命中筆數、命中傳票數，以及前 50 筆命中分錄。本類不保存任何狀態。
/// WHERE 由 <see cref="GlFilterWhereBuilder"/> 組出，它不分資料庫種類：每種條件的 SQL 寫法只定義在
/// <see cref="GlRulePredicates"/>，各資料庫不同的寫法由 <see cref="ISqlDialect"/> 提供。
/// 本類只負責開連線與 SELECT 的外框。
/// SQL 裡的欄位名稱只來自 GlFieldWhitelist 與固定的片段常數；審計員輸入的值一律用參數傳。
/// 每一句 SELECT 都經 <see cref="DiagnosticDb"/> 的擴充方法執行；記錄層級開到 Debug 時（開發用），診斷日誌會記下完整的 SQL 與參數。
/// </summary>
public sealed class LocalFilterHitRepository(ILocalProjectDatabase database, ILogger<LocalFilterHitRepository>? logger = null)
    : IFilterHitRepository
{
    private const int PreviewRowLimit = 50;

    private readonly string _provider = database.Dialect.ProviderName;

    private readonly GlFilterWhereBuilder WhereBuilder =
        new(
            database.Dialect,
            new GlRulePredicates(database.Dialect, GlPopulationScopeSql.Predicate));

    private readonly ILogger _log = logger ?? NullLogger<LocalFilterHitRepository>.Instance;

    public async Task<FilterPreviewResult> PreviewAsync(
        string projectId,
        FilterScenarioSpec scenario,
        FilterConditionContext context,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var count = await ExecuteScalarAsync(
            connection, scenario, context, "COUNT(*)", cancellationToken);
        var voucherCount = await ExecuteScalarAsync(
            connection, scenario, context, "COUNT(DISTINCT g.voucher_number)", cancellationToken);
        var previewRows = await ReadPreviewRowsAsync(
            connection, scenario, context, cancellationToken);

        return new FilterPreviewResult(count, voucherCount, previewRows);
    }

    public async Task<long> CountWholeVoucherRowsAsync(
        string projectId,
        FilterScenarioSpec scenario,
        FilterConditionContext context,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var plan = WhereBuilder.BuildPlan(scenario, context);
        plan.BindParametersTo(command);
        // 條件組出的 WHERE 只用到子查詢裡的別名 g；外層用另一個別名 whole_row 數出這些傳票的全部分錄。
        command.CommandText = "SELECT COUNT(*) FROM gl whole_row "
            + $"WHERE {GlPopulationScopeSql.Predicate(context, "whole_row")} AND whole_row.voucher_number IN ("
            + "SELECT g.voucher_number FROM gl g "
            + $"WHERE g.voucher_number IS NOT NULL AND {GlPopulationScopeSql.Predicate(context, "g")} AND ({plan.Sql}));";

        var result = await command.ExecuteScalarLoggedAsync(_log, _provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private async Task<long> ExecuteScalarAsync(
        DbConnection connection,
        FilterScenarioSpec scenario,
        FilterConditionContext context,
        string selectExpression,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var plan = WhereBuilder.BuildPlan(scenario, context);
        plan.BindParametersTo(command);
        command.CommandText = $"SELECT {selectExpression} FROM gl g "
            + $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} AND ({plan.Sql});";

        var result = await command.ExecuteScalarLoggedAsync(_log, _provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private async Task<IReadOnlyList<FilterPreviewRow>> ReadPreviewRowsAsync(
        DbConnection connection,
        FilterScenarioSpec scenario,
        FilterConditionContext context,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var plan = WhereBuilder.BuildPlan(scenario, context);
        plan.BindParametersTo(command);
        command.CommandText =
            $"""
            SELECT g.voucher_number, g.line_item, g.gl_date, g.account_number,
                   g.account_name, g.description, g.amount_scaled, g.dr_cr
            FROM gl g
            WHERE {GlPopulationScopeSql.Predicate(context, "g")} AND ({plan.Sql})
            ORDER BY g.entry_id
            LIMIT {PreviewRowLimit};
            """;

        var rows = new List<FilterPreviewRow>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
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
