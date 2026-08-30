using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>SQL Server 的預篩選命中 keyset 分頁；語意鏡射本地 provider。</summary>
public sealed class SqlServerPrescreenPageRepository(SqlServerProjectDatabase database) : IPrescreenPageRepository
{
    private static readonly GlFilterWhereBuilder WhereBuilder =
        new(
            SqlServerDialect.Instance,
            new GlRulePredicates(SqlServerDialect.Instance, GlPopulationScopeSql.Predicate));

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

        var schemaPrefix = SqlServerProjectSchema.QualifierFor(projectId);
        var plan = WhereBuilder.BuildPlan(
            LocalPrescreenPageRepository.PrescreenScenario(ruleKey),
            context,
            TrailingZeroThreshold.UnitModulus(TrailingZeroThreshold.DefaultZerosThreshold),
            schemaPrefix,
            includePopulationParameters: false);
        plan.BindParametersTo(command);
        GlPopulationScopeSql.Plan(
            SqlServerDialect.Instance,
            context,
            "g").BindParametersTo(command);
        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? "AND g.entry_id > @cursor" : string.Empty;
        if (hasCursor)
        {
            command.Parameters.AddWithValue("@cursor", long.Parse(cursorKey));
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);
        await using (var expand = database.CreateCommand(connection, projectId,
            "SELECT g.entry_id, g.document_number, g.line_item, g.post_date, g.account_code, g.account_name, " +
            "       g.amount_scaled, g.dr_cr, g.document_description " +
            "FROM {s}.target_gl_entry g " +
            $"WHERE {GlEffectivePopulation.SqlPredicate("g")} AND ({plan.Sql}) {keyset} " +
            "ORDER BY g.entry_id " + SqlServerDialect.Instance.LimitClause("@pageSize") + ";"))
        {
            command.CommandText = expand.CommandText;
        }

        return await LocalPrescreenPageRepository.ReadPageAsync(command, request, cancellationToken);
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

        var schemaPrefix = SqlServerProjectSchema.QualifierFor(projectId);
        var plan = WhereBuilder.BuildPlan(
            LocalPrescreenPageRepository.PrescreenScenario(ruleKey),
            context,
            TrailingZeroThreshold.UnitModulus(TrailingZeroThreshold.DefaultZerosThreshold),
            schemaPrefix,
            includePopulationParameters: false);
        plan.BindParametersTo(command);
        GlPopulationScopeSql.Plan(
            SqlServerDialect.Instance,
            context,
            "g").BindParametersTo(command);
        await using (var expand = database.CreateCommand(
            connection,
            projectId,
            "SELECT COUNT_BIG(DISTINCT g.document_number), COUNT_BIG(*) " +
            "FROM {s}.target_gl_entry g " +
            $"WHERE {GlEffectivePopulation.SqlPredicate("g")} AND ({plan.Sql});"))
        {
            command.CommandText = expand.CommandText;
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PrescreenHitCounts(0, 0);
        }

        return new PrescreenHitCounts(reader.GetInt64(0), reader.GetInt64(1));
    }
}
