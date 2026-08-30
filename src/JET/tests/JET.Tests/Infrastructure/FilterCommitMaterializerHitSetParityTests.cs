using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// filter.commit 與空命中結果的惰性補算必須以同一情境產生完全相同的 entry_id 集合。
/// 每個 provider 都走真實 commit repository 與 materializer，SQL Server 沿用 availability gate。
/// </summary>
public sealed class FilterCommitMaterializerHitSetParityTests
{
    private const int ScenarioPosition = 1;

    private static readonly FilterRuleContext Context = new(
        ProjectDocument.DefaultMoneyScale,
        LastPeriodStart: null,
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        PopulationScope: GlPopulationScope.AuditPeriod);

    private static readonly FilterScenarioSpec Scenario = new(
        "hit-set-parity",
        "commit and lazy materialization must agree",
        [
            new FilterGroupSpec(
                FilterJoin.And,
                [
                    new FilterRuleSpec(
                        FilterJoin.And,
                        FilterRuleType.Text,
                        PrescreenKey: null,
                        Field: "description",
                        Keywords: ["needle"],
                        Mode: TextMatchMode.Contains,
                        FromDate: null,
                        ToDate: null,
                        FromAmountScaled: null,
                        ToAmountScaled: null,
                        DrCr: null,
                        IsManual: null),
                    new FilterRuleSpec(
                        FilterJoin.And,
                        FilterRuleType.DrCrOnly,
                        PrescreenKey: null,
                        Field: null,
                        Keywords: [],
                        Mode: TextMatchMode.Contains,
                        FromDate: null,
                        ToDate: null,
                        FromAmountScaled: null,
                        ToAmountScaled: null,
                        DrCr: "debit",
                        IsManual: null)
                ])
        ]);

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task CommitAndLazyMaterializer_LocalProvider_SameScenario_PersistIdenticalEntryIdSet(
        string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await AssertEquivalentHitSetsAsync(
            projectId,
            new LocalFilterCommitRepository(database),
            new LocalFilterRunMaterializer(database),
            database.CreateConnection,
            schemaPrefix: string.Empty);
    }

    [SqlServerFact]
    public async Task CommitAndLazyMaterializer_SqlServer_SameScenario_PersistIdenticalEntryIdSet()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());

        await AssertEquivalentHitSetsAsync(
            project.ProjectId,
            new SqlServerFilterCommitRepository(project.Database),
            new SqlServerFilterRunMaterializer(project.Database),
            project.Database.CreateConnection,
            SqlServerProjectSchema.QualifierFor(project.ProjectId));
    }

    private static async Task AssertEquivalentHitSetsAsync(
        string projectId,
        IFilterCommitRepository commitRepository,
        IFilterRunMaterializer lazyMaterializer,
        Func<string, DbConnection> connectionFactory,
        string schemaPrefix)
    {
        await SeedPopulationAsync(connectionFactory(projectId), schemaPrefix);
        var expected = await ReadEntryIdsAsync(
            connectionFactory(projectId),
            $"SELECT entry_id FROM {schemaPrefix}target_gl_entry "
            + "WHERE source_row_number IN (1, 5) ORDER BY entry_id;");

        var definition = new SavedFilterScenario(
            ScenarioPosition,
            Scenario.Name,
            Scenario.Rationale,
            """
            {"name":"hit-set-parity","rationale":"commit and lazy materialization must agree","groups":[{"join":"AND","rules":[{"join":"AND","type":"text","field":"description","keywords":["needle"],"mode":"contains"},{"join":"AND","type":"drCrOnly","drCr":"debit"}]}]}
            """,
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero));
        await commitRepository.CommitAsync(
            projectId,
            [new FilterCommitItem(definition, Scenario)],
            Context,
            CancellationToken.None);

        var committed = await ReadHitEntryIdsAsync(
            connectionFactory(projectId),
            schemaPrefix);
        Assert.Equal(expected, committed);

        await ExecuteAsync(
            connectionFactory(projectId),
            $"DELETE FROM {schemaPrefix}result_filter_run;");
        await lazyMaterializer.MaterializeAsync(
            projectId,
            [new MaterializableScenario(ScenarioPosition, Scenario)],
            Context,
            CancellationToken.None);

        var lazilyMaterialized = await ReadHitEntryIdsAsync(
            connectionFactory(projectId),
            schemaPrefix);
        Assert.Equal(committed, lazilyMaterialized);
        Assert.Equal(expected, lazilyMaterialized);
    }

    private static async Task SeedPopulationAsync(DbConnection connection, string schemaPrefix) =>
        await ExecuteAsync(
            connection,
            $"""
            INSERT INTO {schemaPrefix}target_gl_entry
                (batch_id, source_row_number, document_number, line_item, post_date,
                 account_code, account_name, document_description, amount_scaled,
                 debit_amount_scaled, credit_amount_scaled, dr_cr)
            VALUES
                ('filter-hit-parity', 1, 'DOC-1', '1', '2025-03-01',
                 '1101', 'Cash', 'needle alpha', 10000, 10000, 0, 'DEBIT'),
                ('filter-hit-parity', 2, 'DOC-2', '1', '2025-03-02',
                 '1101', 'Cash', 'other text', 20000, 20000, 0, 'DEBIT'),
                ('filter-hit-parity', 3, 'DOC-3', '1', '2025-03-03',
                 '4101', 'Revenue', 'needle credit', -30000, 0, 30000, 'CREDIT'),
                ('filter-hit-parity', 4, 'DOC-4', '1', '2024-12-31',
                 '1101', 'Cash', 'needle outside period', 40000, 40000, 0, 'DEBIT'),
                ('filter-hit-parity', 5, 'DOC-5', '1', '2025-11-30',
                 '1101', 'Cash', 'needle beta', 50000, 50000, 0, 'DEBIT');

            UPDATE {schemaPrefix}target_gl_entry
            SET is_effective = CASE WHEN source_row_number = 4 THEN 0 ELSE 1 END;
            """);

    private static Task<long[]> ReadHitEntryIdsAsync(
        DbConnection connection,
        string schemaPrefix) =>
        ReadEntryIdsAsync(
            connection,
            $"SELECT entry_id FROM {schemaPrefix}result_filter_run "
            + $"WHERE scenario_position = {ScenarioPosition} ORDER BY entry_id;");

    private static async Task<long[]> ReadEntryIdsAsync(DbConnection connection, string sql)
    {
        var entryIds = new List<long>();
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entryIds.Add(Convert.ToInt64(reader.GetValue(0)));
            }
        }

        return entryIds.ToArray();
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }
}
