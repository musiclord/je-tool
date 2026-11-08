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
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date, approval_date,
             account_number, account_name, description, source_module, created_by, approved_by,
             is_manual, is_excluded, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('fixture', 1, 'UNBALANCED', '1', '2025-01-01', '2025-01-01',
             '1101', 'Cash', 'one', NULL, 'P', NULL, 1, 0, 100, 100, 0, 'DEBIT'),
            ('fixture', 2, 'UNBALANCED', '2', '2025-06-30', '2025-06-30',
             '1101', 'Cash', 'two', NULL, 'P', NULL, 1, 0, -20, 0, 20, 'CREDIT'),
            ('fixture', 3, 'UNBALANCED', '3', '2025-12-31', '2025-12-31',
             '1101', 'Cash', 'three', NULL, 'P', NULL, 1, 0, -30, 0, 30, 'CREDIT'),
            ('fixture', 4, 'BALANCED', '1', '2025-02-01', '2025-02-01',
             '1101', 'Cash', 'four', NULL, 'P', NULL, 1, 0, 80, 80, 0, 'DEBIT'),
            ('fixture', 5, 'BALANCED', '2', '2025-02-01', '2025-02-01',
             '1101', 'Cash', 'five', NULL, 'P', NULL, 1, 0, -80, 0, 80, 'CREDIT'),
            ('fixture', 6, 'PERIOD-SCOPE', '1', '2025-03-01', '2025-03-01',
             '1101', 'Cash', 'six', NULL, 'P', NULL, 1, 0, 50, 50, 0, 'DEBIT'),
            ('fixture', 7, 'PERIOD-SCOPE', '2', '2025-03-01', '2025-03-01',
             '1101', 'Cash', 'seven', NULL, 'P', NULL, 1, 0, -50, 0, 50, 'CREDIT'),
            ('fixture', 8, 'PERIOD-SCOPE', '3', '2026-01-01', '2026-01-01',
             '1101', 'Cash', 'outside', NULL, 'P', NULL, 1, 1, 999, 999, 0, 'DEBIT'),
            ('fixture', 9, 'OUTSIDE', '1', '2024-12-31', '2024-12-31',
             '1101', 'Cash', 'outside', NULL, 'P', NULL, 1, 1, 999, 999, 0, 'DEBIT');
        """;

    [Theory]
    [InlineData("sqlite")]
    public async Task LocalProviders_CountExactlyTheRowsReturnedByUnbalancedDetailPages(
        string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider switch
        {
            "sqlite" => new SqliteProjectDatabase(folder),
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
            new LocalUnbalancedVoucherPageRepository(database),
            projectId);

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
            BlankAccountNumberCount: 0,
            BlankVoucherNumberCount: 0,
            BlankDescriptionCount: 0,
            ApprovalDateNotInPeriodCount: 0));

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
        IUnbalancedVoucherPageRepository repository,
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
