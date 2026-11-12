using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 條件 AST → 參數化 SQL 的執行（無狀態：COUNT + COUNT DISTINCT + LIMIT 預覽）。
/// WHERE 組譯由 provider 中立的 <see cref="GlFilterWhereBuilder"/> 完成
/// （述詞單一事實來源 <see cref="GlRulePredicates"/> + 注入的 <see cref="ISqlDialect"/>）；
/// 本類只負責本地引擎連線與 SELECT 骨架。
/// 識別字只出自 GlFieldWhitelist 與片段常數；所有使用者值參數綁定。
/// 診斷日誌（dev-only）：每個 SELECT 走 <see cref="DiagnosticDb"/> 擴充方法記錄完整 SQL/參數。
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
        // 條件的 WHERE plan 只引用子查詢的 g；外層用另一個別名列出同傳票的全部分錄。
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
