using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// KCT D/E/G/J ready-state predicate matrix. Every assertion executes the production
/// filter repository against a real SQLite, DuckDB, or gated SQL Server database.
/// The fixture is hand-calculated and locks exact row identity rather than count alone.
/// </summary>
public sealed class KctReadyPredicateProviderTests
{
    private const string ManualRevenue = "manualRevenueEntry";
    private const string SpecificPreparer = "specificPreparer";
    private const string BlankDescription = "blankDescription";
    private const string SamePreparerAndApprover = "preparerEqualsApprover";

    private const string FixtureSql =
        """
        INSERT INTO target_gl_entry
            (batch_id, source_row_number, document_number, line_item, post_date, approval_date,
             account_code, account_name, document_description, source_module, created_by, approved_by,
             is_manual, is_effective, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('kct', 1,  'D1', '1', '2025-03-01', '2025-03-02', '4101', 'Revenue', 'manual revenue', NULL, 'Clerk',  'Reviewer',   1, 1,  10000, 10000, 0,     'DEBIT'),
            ('kct', 2,  'D1', '2', '2025-03-01', '2025-03-02', '1101', 'Cash',    'counter row',    NULL, 'Clerk',  'Reviewer',   1, 1, -10000, 0,     10000, 'CREDIT'),

            ('kct', 3,  'E1', '1', '2025-04-01', '2025-04-02', '5101', 'Others',  'target creator', NULL, ' TARGET ', NULL,        0, 1,  20000, 20000, 0,     'DEBIT'),
            ('kct', 4,  'E1', '2', '2025-04-01', '2025-04-02', '1101', 'Cash',    'other creator',  NULL, 'Staff',    NULL,        0, 1, -20000, 0,     20000, 'CREDIT'),

            ('kct', 5,  'G1', '1', '2025-05-01', '2025-05-02', '5101', 'Others',  NULL,             NULL, 'G',        'Reviewer',  0, 1,  10100, 10100, 0,     'DEBIT'),
            ('kct', 6,  'G1', '2', '2025-05-01', '2025-05-02', '5101', 'Others',  '',               NULL, 'G',        'Reviewer',  0, 1,  20200, 20200, 0,     'DEBIT'),
            ('kct', 7,  'G1', '3', '2025-05-01', '2025-05-02', '5101', 'Others',  '   ',            NULL, 'G',        'Reviewer',  0, 1,  30300, 30300, 0,     'DEBIT'),
            ('kct', 8,  'G1', '4', '2025-05-01', '2025-05-02', '1101', 'Cash',    'not blank',      NULL, 'G',        'Reviewer',  0, 1, -60600, 0,     60600, 'CREDIT'),

            ('kct', 9,  'J1', '1', '2025-06-01', '2025-06-02', '5101', 'Others',  'same people',    NULL, ' Maker ',  'maker',     0, 1,  30000, 30000, 0,     'DEBIT'),
            ('kct', 10, 'J1', '2', '2025-06-01', '2025-06-02', '1101', 'Cash',    'different',      NULL, 'Maker',    'Controller',0, 1, -30000, 0,     30000, 'CREDIT');

        INSERT INTO target_account_mapping
            (batch_id, source_row_number, account_code, account_name, standardized_category)
        VALUES
            ('mapping', 1, '4101', 'Revenue', 'Revenue'),
            ('mapping', 2, '1101', 'Cash',    'Cash'),
            ('mapping', 3, '5101', 'Others',  'Others');
        """;

    private static readonly FilterRuleContext Context =
        new(100, "2025-12-31", "2025-01-01", "2025-12-31");

    [Theory]
    [InlineData("sqlite", ManualRevenue)]
    [InlineData("sqlite", SpecificPreparer)]
    [InlineData("sqlite", BlankDescription)]
    [InlineData("sqlite", SamePreparerAndApprover)]
    [InlineData("duckdb", ManualRevenue)]
    [InlineData("duckdb", SpecificPreparer)]
    [InlineData("duckdb", BlankDescription)]
    [InlineData("duckdb", SamePreparerAndApprover)]
    public async Task ReadyPredicate_LocalProvider_HitsExactRows(
        string provider,
        string condition)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, FixtureSql);

        await AssertConditionAsync(fixture, condition);
    }

    [SqlServerTheory]
    [InlineData(ManualRevenue)]
    [InlineData(SpecificPreparer)]
    [InlineData(BlankDescription)]
    [InlineData(SamePreparerAndApprover)]
    public async Task ReadyPredicate_SqlServer_HitsExactRows(string condition)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateSqlServerAsync(FixtureSql);

        await AssertConditionAsync(fixture, condition);
    }

    private static async Task AssertConditionAsync(
        FilterPredicateProviderFixture fixture,
        string condition)
    {
        var expectedRows = ExpectedRows(condition);
        var result = await fixture.Repository.PreviewAsync(
            fixture.ProjectId,
            SingleRule(Rule(condition)),
            Context,
            CancellationToken.None);
        var actualRows = result.PreviewRows
            .Select(row => $"{row.DocumentNumber}|{row.LineItem}")
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal((long)expectedRows.Length, result.Count);
        Assert.Equal(1, result.VoucherCount);
        Assert.Equal(expectedRows, actualRows);
    }

    private static FilterScenarioSpec SingleRule(FilterRuleSpec rule) =>
        new(
            "KCT ready predicate matrix",
            "hand-calculated row identity oracle",
            [new FilterGroupSpec(FilterJoin.And, [rule])]);

    private static FilterRuleSpec Rule(string condition)
    {
        var empty = new FilterRuleSpec(
            FilterJoin.And,
            FilterRuleType.PreparerEqualsApprover,
            null,
            null,
            [],
            TextMatchMode.Contains,
            null,
            null,
            null,
            null,
            null,
            null);

        return condition switch
        {
            ManualRevenue => empty with { Type = FilterRuleType.ManualRevenueEntry },
            SpecificPreparer => empty with
            {
                Type = FilterRuleType.Text,
                Field = "createBy",
                Keywords = ["target"],
                Mode = TextMatchMode.Exact
            },
            BlankDescription => empty with
            {
                Type = FilterRuleType.Prescreen,
                PrescreenKey = PrescreenRuleKeys.BlankDescription
            },
            SamePreparerAndApprover => empty,
            _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "Unknown KCT condition.")
        };
    }

    private static string[] ExpectedRows(string condition) => condition switch
    {
        ManualRevenue => ["D1|1"],
        SpecificPreparer => ["E1|1"],
        BlankDescription => ["G1|1", "G1|2", "G1|3"],
        SamePreparerAndApprover => ["J1|1"],
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "Unknown KCT condition.")
    };
}
