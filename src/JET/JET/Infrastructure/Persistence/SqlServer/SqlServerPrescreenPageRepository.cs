using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>SQL Server 的預篩選命中 keyset 分頁；語意鏡射本地 provider，列出哪些分錄同樣由 <see cref="LocalPrescreenPageRepository.HitRowsPredicate"/> 決定。</summary>
public sealed class SqlServerPrescreenPageRepository(SqlServerProjectDatabase database) : IPrescreenPageRepository
{
    private static readonly GlFilterWhereBuilder WhereBuilder =
        new(
            SqlServerDialect.Instance,
            new GlRulePredicates(SqlServerDialect.Instance, GlPopulationScopeSql.Predicate));

    public async Task<PageResult<PrescreenHitRow>> GetPageAsync(
        string projectId,
        string ruleKey,
        FilterConditionContext context,
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
            schemaPrefix,
            includePopulationParameters: false);
        plan.BindParametersTo(command);
        GlPopulationScopeSql.Plan(
            SqlServerDialect.Instance,
            context,
            "g").BindParametersTo(command);
        var paging = KeysetPaging.Plan(SqlServerDialect.Instance, request, ResultPageSorting.Prescreen);
        foreach (var parameter in paging.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);
        await using (var expand = database.CreateCommand(connection, projectId,
            "SELECT g.entry_id, g.voucher_number, g.line_item, g.gl_date, g.account_number, g.account_name, " +
            "       g.amount_scaled, g.dr_cr, g.description" + paging.SelectSuffix + " " +
            "FROM {s}.gl g " +
            $"WHERE {GlExcludedPopulation.SqlPredicate("g")} AND ({LocalPrescreenPageRepository.HitRowsPredicate(ruleKey, plan.Sql, schemaPrefix)})" + paging.Predicate + " " +
            paging.OrderBy + " " + SqlServerDialect.Instance.LimitClause("@pageSize") + ";"))
        {
            command.CommandText = expand.CommandText;
        }

        return await LocalPrescreenPageRepository.ReadPageAsync(command, request, paging, SqlServerDialect.Instance, cancellationToken);
    }

    public async Task<PrescreenHitCounts> GetCountsAsync(
        string projectId,
        string ruleKey,
        FilterConditionContext context,
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
            "SELECT COUNT_BIG(DISTINCT g.voucher_number), COUNT_BIG(*) " +
            "FROM {s}.gl g " +
            $"WHERE {GlExcludedPopulation.SqlPredicate("g")} AND ({LocalPrescreenPageRepository.HitRowsPredicate(ruleKey, plan.Sql, schemaPrefix)});"))
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
