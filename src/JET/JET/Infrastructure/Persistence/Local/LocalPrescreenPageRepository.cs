using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>本地 provider 的預篩選命中 keyset 分頁；述詞與 filter.preview 共用同一 builder。</summary>
public sealed class LocalPrescreenPageRepository(ILocalProjectDatabase database) : IPrescreenPageRepository
{
    private readonly GlFilterWhereBuilder _whereBuilder =
        new(
            database.Dialect,
            new GlRulePredicates(database.Dialect, GlPopulationScopeSql.Predicate));

    public async Task<PageResult<PrescreenHitRow>> GetPageAsync(
        string projectId,
        string ruleKey,
        FilterRuleContext context,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var plan = _whereBuilder.BuildPlan(
            PrescreenScenario(ruleKey),
            context,
            TrailingZeroThreshold.UnitModulus(TrailingZeroThreshold.DefaultZerosThreshold),
            includePopulationParameters: false);
        plan.BindParametersTo(command);
        GlPopulationScopeSql.Plan(database.Dialect, context, "g").BindParametersTo(command);
        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? "AND g.entry_id > @cursor" : string.Empty;
        if (hasCursor)
        {
            command.AddWithValue("@cursor", long.Parse(cursorKey));
        }

        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);
        command.CommandText =
            "SELECT g.entry_id, g.document_number, g.line_item, g.post_date, g.account_code, g.account_name, " +
            "       g.amount_scaled, g.dr_cr, g.document_description " +
            "FROM target_gl_entry g " +
            $"WHERE {GlEffectivePopulation.SqlPredicate("g")} AND ({plan.Sql}) {keyset} " +
            "ORDER BY g.entry_id " + database.Dialect.LimitClause("@pageSize") + ";";

        return await ReadPageAsync(command, request, cancellationToken);
    }

    public async Task<PrescreenHitCounts> GetCountsAsync(
        string projectId,
        string ruleKey,
        FilterRuleContext context,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var plan = _whereBuilder.BuildPlan(
            PrescreenScenario(ruleKey),
            context,
            TrailingZeroThreshold.UnitModulus(TrailingZeroThreshold.DefaultZerosThreshold),
            includePopulationParameters: false);
        plan.BindParametersTo(command);
        GlPopulationScopeSql.Plan(database.Dialect, context, "g").BindParametersTo(command);
        command.CommandText =
            "SELECT COUNT(DISTINCT g.document_number), COUNT(*) " +
            "FROM target_gl_entry g " +
            $"WHERE {GlEffectivePopulation.SqlPredicate("g")} AND ({plan.Sql});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PrescreenHitCounts(0, 0);
        }

        return new PrescreenHitCounts(
            Convert.ToInt64(reader.GetValue(0)),
            Convert.ToInt64(reader.GetValue(1)));
    }

    internal static FilterScenarioSpec PrescreenScenario(string ruleKey) => new(
        Name: string.Empty,
        Rationale: string.Empty,
        Groups:
        [
            new FilterGroupSpec(
                FilterJoin.And,
                [new FilterRuleSpec(
                    FilterJoin.And, FilterRuleType.Prescreen, ruleKey, null, [], TextMatchMode.Contains,
                    null, null, null, null, null, null)])
        ]);

    internal static async Task<PageResult<PrescreenHitRow>> ReadPageAsync(
        System.Data.Common.DbCommand command,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        var rows = new List<PrescreenHitRow>();
        long lastEntryId = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lastEntryId = reader.GetInt64(0);
            rows.Add(new PrescreenHitRow(
                lastEntryId,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        var hasMore = rows.Count > request.ClampedPageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var next = hasMore
            ? PageCursor.Encode(rows[^1].EntryId.ToString())
            : null;
        return new PageResult<PrescreenHitRow>(rows, next);
    }
}
