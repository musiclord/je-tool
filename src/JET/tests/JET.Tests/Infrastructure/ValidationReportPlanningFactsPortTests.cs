using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Validation workbook 的 unbalanced count 必須是回接本期 GL 後的 raw row count，
/// 且和 writer 實際使用的 page repository 保持同一母體。
/// </summary>
public sealed class ValidationReportPlanningFactsPortTests
{
    private const string FixtureSql =
        """
        INSERT INTO target_gl_entry
            (batch_id, source_row_number, document_number, line_item, post_date, approval_date,
             account_code, account_name, document_description, source_module, created_by, approved_by,
             is_manual, is_effective, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('fixture', 1, 'UNBALANCED', '1', '2025-01-01', '2025-01-01',
             '1101', 'Cash', 'one', NULL, 'P', NULL, 1, 1, 100, 100, 0, 'DEBIT'),
            ('fixture', 2, 'UNBALANCED', '2', '2025-06-30', '2025-06-30',
             '1101', 'Cash', 'two', NULL, 'P', NULL, 1, 1, -20, 0, 20, 'CREDIT'),
            ('fixture', 3, 'UNBALANCED', '3', '2025-12-31', '2025-12-31',
             '1101', 'Cash', 'three', NULL, 'P', NULL, 1, 1, -30, 0, 30, 'CREDIT'),
            ('fixture', 4, 'BALANCED', '1', '2025-02-01', '2025-02-01',
             '1101', 'Cash', 'four', NULL, 'P', NULL, 1, 1, 80, 80, 0, 'DEBIT'),
            ('fixture', 5, 'BALANCED', '2', '2025-02-01', '2025-02-01',
             '1101', 'Cash', 'five', NULL, 'P', NULL, 1, 1, -80, 0, 80, 'CREDIT'),
            ('fixture', 6, 'PERIOD-SCOPE', '1', '2025-03-01', '2025-03-01',
             '1101', 'Cash', 'six', NULL, 'P', NULL, 1, 1, 50, 50, 0, 'DEBIT'),
            ('fixture', 7, 'PERIOD-SCOPE', '2', '2025-03-01', '2025-03-01',
             '1101', 'Cash', 'seven', NULL, 'P', NULL, 1, 1, -50, 0, 50, 'CREDIT'),
            ('fixture', 8, 'PERIOD-SCOPE', '3', '2026-01-01', '2026-01-01',
             '1101', 'Cash', 'outside', NULL, 'P', NULL, 1, 0, 999, 999, 0, 'DEBIT'),
            ('fixture', 9, 'OUTSIDE', '1', '2024-12-31', '2024-12-31',
             '1101', 'Cash', 'outside', NULL, 'P', NULL, 1, 0, 999, 999, 0, 'DEBIT');
        """;

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task LocalProviders_CountExactlyTheRowsReturnedByUnbalancedDetailPages(
        string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider switch
        {
            "sqlite" => new SqliteProjectDatabase(folder),
            "duckdb" => new DuckDbProjectDatabase(folder),
            _ => throw new InvalidOperationException(provider)
        };
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await InsertLocalFixtureAsync(database, projectId);

        IValidationReportPlanningFactsPort port =
            new LocalValidationReportPlanningFactsPort(database);
        var facts = await port.ExecuteAsync(Plan(projectId), CancellationToken.None);
        var pageRows = await DrainAsync(
            new LocalUnbalancedGlEntryPageRepository(database),
            projectId);

        Assert.Equal(3, facts.UnbalancedDetailRowCount);
        Assert.Equal(facts.UnbalancedDetailRowCount, (long)pageRows.Count);
        Assert.Equal(pageRows.Count, pageRows.Distinct().Count());
    }

    [SqlServerFact]
    public async Task SqlServer_CountExactlyTheRowsReturnedByUnbalancedDetailPages()
    {
        await using var sql = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());
        await using (var connection = sql.Database.CreateConnection(sql.ProjectId))
        {
            await connection.OpenAsync();
            await using var command = sql.Database.CreateCommand(
                connection,
                sql.ProjectId,
                FixtureSql.Replace(
                    "INSERT INTO target_gl_entry",
                    "INSERT INTO {s}.target_gl_entry",
                    StringComparison.Ordinal));
            await command.ExecuteNonQueryAsync();
        }

        IValidationReportPlanningFactsPort port =
            new SqlServerValidationReportPlanningFactsPort(sql.Database);
        var facts = await port.ExecuteAsync(Plan(sql.ProjectId), CancellationToken.None);
        var pageRows = await DrainAsync(
            new SqlServerUnbalancedGlEntryPageRepository(sql.Database),
            sql.ProjectId);

        Assert.Equal(3, facts.UnbalancedDetailRowCount);
        Assert.Equal(facts.UnbalancedDetailRowCount, (long)pageRows.Count);
        Assert.Equal(pageRows.Count, pageRows.Distinct().Count());
    }

    private static ValidationReportPlan Plan(string projectId) =>
        JetAuditProgram.Plan(new ValidationReportRequest(
            ProjectId: projectId,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            CompletenessDiffAccountCount: 0,
            NullAccountCount: 0,
            NullDocumentCount: 0,
            NullDescriptionCount: 0,
            OutOfRangeDateCount: 0));

    private static async Task InsertLocalFixtureAsync(
        ILocalProjectDatabase database,
        string projectId)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = FixtureSql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<long>> DrainAsync(
        IUnbalancedGlEntryPageRepository repository,
        string projectId)
    {
        var rows = new List<long>();
        string? cursor = null;
        do
        {
            var page = await repository.GetEntryIdsPageAsync(
                projectId,
                "2025-01-01",
                "2025-12-31",
                new PageRequest(cursor, PageSize: 2),
                CancellationToken.None);
            rows.AddRange(page.Rows);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return rows;
    }
}
