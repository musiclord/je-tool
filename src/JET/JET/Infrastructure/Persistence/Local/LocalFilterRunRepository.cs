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
public sealed class LocalFilterRunRepository(ILocalProjectDatabase database, ILogger<LocalFilterRunRepository>? logger = null)
    : IFilterRunRepository
{
    private const int PreviewRowLimit = 50;

    // 診斷 provider 標籤由方言注入（sqlite／duckdb），不再寫死。
    private readonly string _provider = database.Dialect.ProviderName;

    private readonly GlFilterWhereBuilder WhereBuilder =
        new(
            database.Dialect,
            new GlRulePredicates(database.Dialect, GlPopulationScopeSql.Predicate));

    private readonly ILogger _log = logger ?? NullLogger<LocalFilterRunRepository>.Instance;

    public async Task<FilterPreviewResult> PreviewAsync(
        string projectId,
        FilterScenarioSpec scenario,
        FilterRuleContext context,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // 連續零尾數條件的模數與 prescreen.run 同源（固定預設 Domain 門檻）。
        var zeroModulus = TrailingZeroThreshold.UnitModulus(
            TrailingZeroThreshold.DefaultZerosThreshold);

        var count = await ExecuteScalarAsync(
            connection, scenario, context, zeroModulus, "COUNT(*)", cancellationToken);
        var voucherCount = await ExecuteScalarAsync(
            connection, scenario, context, zeroModulus, "COUNT(DISTINCT g.document_number)", cancellationToken);
        var previewRows = await ReadPreviewRowsAsync(
            connection, scenario, context, zeroModulus, cancellationToken);

        return new FilterPreviewResult(count, voucherCount, previewRows);
    }

    private async Task<long> ExecuteScalarAsync(
        DbConnection connection,
        FilterScenarioSpec scenario,
        FilterRuleContext context,
        long zeroModulus,
        string selectExpression,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var plan = WhereBuilder.BuildPlan(scenario, context, zeroModulus);
        plan.BindParametersTo(command);
        command.CommandText = $"SELECT {selectExpression} FROM target_gl_entry g "
            + $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} AND ({plan.Sql});";

        var result = await command.ExecuteScalarLoggedAsync(_log, _provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private async Task<IReadOnlyList<FilterPreviewRow>> ReadPreviewRowsAsync(
        DbConnection connection,
        FilterScenarioSpec scenario,
        FilterRuleContext context,
        long zeroModulus,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var plan = WhereBuilder.BuildPlan(scenario, context, zeroModulus);
        plan.BindParametersTo(command);
        command.CommandText =
            $"""
            SELECT g.document_number, g.line_item, g.post_date, g.account_code,
                   g.account_name, g.document_description, g.amount_scaled, g.dr_cr
            FROM target_gl_entry g
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
