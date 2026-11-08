using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Advanced-filter AST execution matrix over wholly synthetic rows.
/// SQLite, DuckDB, and gated SQL Server must agree on semantic row identity;
/// a voucher-witness group must return only first-rule anchors while later AND rules
/// may be satisfied by different rows of the same in-period voucher.
/// 這組情境原本用「同一傳票」比對範圍；這個寫法已拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回），
/// 照它的重建方式改成第一條留在組裡、其餘各條放進「整張傳票條件」並選「至少一筆符合」，固定答案不變。
/// 第一次失敗：Build 收據 20261030-080858642-d5c1719481744042bbfc70329c5fcceb（比對範圍的型別已不存在，無法編譯）。
/// </summary>
public sealed class AdvancedFilterAstProviderParityTests
{
    private const int ScenarioPosition = 1;

    private const string FixtureSql =
        """
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date,
             account_number, account_name, description, amount_scaled,
             debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('advanced-ast', 1,  'TXT-C-1', '1', '2025-01-10', 'C-1', 'Text', 'prefix alpha suffix', 1000, 1000, 0, 'DEBIT'),
            ('advanced-ast', 2,  'TXT-C-2', '1', '2025-01-11', 'C-2', 'Text', 'BETA',                1000, 1000, 0, 'DEBIT'),
            ('advanced-ast', 3,  'TXT-C-3', '1', '2025-01-12', 'C-3', 'Text', 'AL PHA',              1000, 1000, 0, 'DEBIT'),
            ('advanced-ast', 4,  'TXT-C-4', '1', '2025-01-13', 'C-4', 'Text', NULL,                  1000, 1000, 0, 'DEBIT'),
            ('advanced-ast', 5,  'TXT-C-5', '1', '2025-01-14', 'C-5', 'Text', 'gamma',               1000, 1000, 0, 'DEBIT'),

            ('advanced-ast', 6,  'TXT-E-1', '1', '2025-02-10', 'ACC-1',  'Exact', 'first',  1000, 1000, 0, 'DEBIT'),
            ('advanced-ast', 7,  'TXT-E-2', '1', '2025-02-11', 'B-2',    'Exact', 'second', 1000, 1000, 0, 'DEBIT'),
            ('advanced-ast', 8,  'TXT-E-3', '1', '2025-02-12', 'AC C-1', 'Exact', 'third',  1000, 1000, 0, 'DEBIT'),
            ('advanced-ast', 9,  'TXT-E-4', '1', '2025-02-13', NULL,     'Exact', 'null',   1000, 1000, 0, 'DEBIT'),
            ('advanced-ast', 10, 'TXT-E-5', '1', '2025-02-14', 'acc-1',  'Exact', 'case',   1000, 1000, 0, 'DEBIT'),

            ('advanced-ast', 11, 'SV-1', '1', '2025-03-01', 'SVANCHOR', 'Voucher', 'anchor',                 1000,  1000, 0, 'DEBIT'),
            ('advanced-ast', 12, 'SV-1', '2', '2025-03-01', 'INFO',     'Voucher', 'first evidence',        10000, 10000, 0, 'DEBIT'),
            ('advanced-ast', 13, 'SV-1', '3', '2025-03-01', 'INFO',     'Voucher', 'second EVIDENCE marker',20000, 20000, 0, 'DEBIT'),
            ('advanced-ast', 14, 'SV-1', '4', '2025-03-01', 'INFO',     'Voucher', 'threshold support',     50000, 50000, 0, 'DEBIT'),

            ('advanced-ast', 15, 'SV-2', '1', '2025-04-01', 'SVANCHOR', 'Voucher', 'anchor',                 1000,  1000, 0, 'DEBIT'),
            ('advanced-ast', 16, 'SV-2', '2', '2025-04-01', 'INFO',     'Voucher', 'evidence',              10000, 10000, 0, 'DEBIT'),
            ('advanced-ast', 17, 'SV-2', '3', '2025-04-01', 'INFO',     'Voucher', 'below threshold',       49999, 49999, 0, 'DEBIT'),

            ('advanced-ast', 18, 'SV-3', '1', '2025-05-01', 'SVANCHOR', 'Voucher', 'anchor',                 1000,  1000, 0, 'DEBIT'),
            ('advanced-ast', 19, 'SV-3', '2', '2024-12-31', 'INFO',     'Voucher', 'evidence outside period',10000,10000,0, 'DEBIT'),
            ('advanced-ast', 20, 'SV-3', '3', '2025-05-01', 'INFO',     'Voucher', 'threshold support',     60000, 60000, 0, 'DEBIT'),

            ('advanced-ast', 21, 'SV-4', '1', '2025-06-01', 'INFO',     'Voucher', 'evidence without anchor',10000,10000,0, 'DEBIT'),
            ('advanced-ast', 22, 'SV-4', '2', '2025-06-01', 'INFO',     'Voucher', 'threshold support',      70000,70000,0, 'DEBIT'),

            ('advanced-ast', 23, 'SV-5', '1', '2025-07-01', 'SVANCHOR', 'Voucher', 'first anchor',           1000,  1000, 0, 'DEBIT'),
            ('advanced-ast', 24, 'SV-5', '2', '2025-07-01', 'SVANCHOR', 'Voucher', 'second anchor',          2000,  2000, 0, 'DEBIT'),
            ('advanced-ast', 25, 'SV-5', '3', '2025-07-01', 'INFO',     'Voucher', 'evidence',              10000, 10000, 0, 'DEBIT'),
            ('advanced-ast', 26, 'SV-5', '4', '2025-07-01', 'INFO',     'Voucher', 'threshold support',     80000, 80000, 0, 'DEBIT');

        UPDATE gl
        SET is_excluded = CASE WHEN source_row_number = 19 THEN 1 ELSE 0 END;
        """;

    private static readonly FilterConditionContext Context = new(
        MoneyScale: 100,
        PrepStartDate: null,
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        PopulationScope: GlPopulationScope.AuditPeriod);

    private static readonly FilterScenarioSpec SameVoucherScenario = Scenario(
        "same-voucher-anchor",
        new FilterGroupSpec(
            FilterJoin.And,
            new FilterConditionSpec[]
            {
                TextSetRule(
                    field: "accountNumber",
                    mode: TextMatchMode.Exact,
                    values: ["SV ANCHOR"],
                    normalization: TextSetNormalization.RemoveAsciiSpaces),
                TextSetRule(
                    field: "description",
                    mode: TextMatchMode.Contains,
                    values: ["EVI DENCE"],
                    normalization: TextSetNormalization.RemoveAsciiSpaces),
                NumRangeLowerBound(50_000)
            }
            .Select((rule, index) => index == 0 ? rule : AnyEntryInVoucher(rule))
            .ToArray()));

    private static readonly IReadOnlyList<ScenarioCase> Cases =
    [
        new(
            "text-set-contains-preserve-default",
            Scenario(
                "text-set-preserve-default",
                new FilterGroupSpec(
                    FilterJoin.And,
                    [
                        TextSetRule(
                            field: "description",
                            mode: TextMatchMode.Contains,
                            values: ["  alpha  "])
                    ])),
            ExpectedIdentities: ["TXT-C-1|1"],
            ExpectedVoucherCount: 1),
        new(
            "text-set-exact-preserve-explicit",
            Scenario(
                "text-set-preserve-explicit",
                new FilterGroupSpec(
                    FilterJoin.And,
                    [
                        TextSetRule(
                            field: "accountNumber",
                            mode: TextMatchMode.Exact,
                            values: ["  acc-1  "],
                            normalization: TextSetNormalization.Preserve)
                    ])),
            ExpectedIdentities: ["TXT-E-1|1", "TXT-E-5|1"],
            ExpectedVoucherCount: 2),
        new(
            "text-set-contains-remove-spaces",
            Scenario(
                "text-set-contains",
                new FilterGroupSpec(
                    FilterJoin.And,
                    [
                        TextSetRule(
                            field: "description",
                            mode: TextMatchMode.Contains,
                            values: ["AL PHA", "be ta"],
                            normalization: TextSetNormalization.RemoveAsciiSpaces)
                    ])),
            ExpectedIdentities: ["TXT-C-1|1", "TXT-C-2|1"],
            ExpectedVoucherCount: 2),
        new(
            "text-set-exact-remove-spaces",
            Scenario(
                "text-set-exact",
                new FilterGroupSpec(
                    FilterJoin.And,
                    [
                        TextSetRule(
                            field: "accountNumber",
                            mode: TextMatchMode.Exact,
                            values: ["AC C-1", "B- 2"],
                            normalization: TextSetNormalization.RemoveAsciiSpaces)
                    ])),
            ExpectedIdentities: ["TXT-E-1|1", "TXT-E-2|1", "TXT-E-5|1"],
            ExpectedVoucherCount: 3),
        new(
            "same-voucher-anchor-and-independent-evidence",
            SameVoucherScenario,
            ExpectedIdentities: ["SV-1|1", "SV-5|1", "SV-5|2"],
            ExpectedVoucherCount: 2)
    ];

    [Fact]
    public async Task SameVoucher_LocalProvider_PreviewCommitAndLazyMaterializationPersistSameAnchorSet(
)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await ExecuteAsync(database.CreateConnection(projectId), FixtureSql);

        await AssertPreviewCommitAndLazyParityAsync(
            projectId,
            new LocalFilterHitRepository(database),
            new LocalFilterCommitRepository(database),
            new LocalFilterHitMaterializer(database),
            database.CreateConnection,
            schemaPrefix: string.Empty);
    }

    private static async Task<ScenarioResult[]> ExecuteCasesAsync(
        FilterPredicateProviderFixture fixture)
    {
        var results = new List<ScenarioResult>(Cases.Count);
        foreach (var scenarioCase in Cases)
        {
            var preview = await fixture.Repository.PreviewAsync(
                fixture.ProjectId,
                scenarioCase.Scenario,
                Context,
                CancellationToken.None);
            results.Add(new ScenarioResult(
                scenarioCase.Name,
                preview.Count,
                preview.VoucherCount,
                SemanticIdentities(preview)));
        }

        return results.ToArray();
    }

    private static void AssertMatchesOracle(IReadOnlyList<ScenarioResult> actual)
    {
        Assert.Equal(Cases.Count, actual.Count);
        for (var index = 0; index < Cases.Count; index++)
        {
            Assert.Equal(Cases[index].Name, actual[index].Name);
            Assert.Equal((long)Cases[index].ExpectedIdentities.Length, actual[index].Count);
            Assert.Equal(Cases[index].ExpectedVoucherCount, actual[index].VoucherCount);
            Assert.Equal(Cases[index].ExpectedIdentities, actual[index].Identities);
        }
    }

    private static void AssertProvidersEqual(
        IReadOnlyList<ScenarioResult> left,
        IReadOnlyList<ScenarioResult> right)
    {
        Assert.Equal(left.Count, right.Count);
        for (var index = 0; index < left.Count; index++)
        {
            Assert.Equal(left[index].Name, right[index].Name);
            Assert.Equal(left[index].Count, right[index].Count);
            Assert.Equal(left[index].VoucherCount, right[index].VoucherCount);
            Assert.Equal(left[index].Identities, right[index].Identities);
        }
    }

    private static async Task AssertPreviewCommitAndLazyParityAsync(
        string projectId,
        IFilterHitRepository previewRepository,
        IFilterCommitRepository commitRepository,
        IFilterHitMaterializer lazyMaterializer,
        Func<string, DbConnection> connectionFactory,
        string schemaPrefix)
    {
        var preview = await previewRepository.PreviewAsync(
            projectId,
            SameVoucherScenario,
            Context,
            CancellationToken.None);
        var previewIdentities = SemanticIdentities(preview);

        var definition = new SavedFilterScenario(
            ScenarioPosition,
            SameVoucherScenario.Name,
            SameVoucherScenario.Rationale,
            """
            {"name":"same-voucher-anchor","rationale":"synthetic provider parity","groups":[{"join":"AND","matchScope":"row","conditions":[{"join":"AND","type":"textSet","field":"accountNumber","values":["SV ANCHOR"],"mode":"exact","normalization":"removeAsciiSpaces"},{"join":"AND","type":"voucher","quantifier":"any","conditions":[{"join":"AND","type":"textSet","field":"description","values":["EVI DENCE"],"mode":"contains","normalization":"removeAsciiSpaces"}]},{"join":"AND","type":"voucher","quantifier":"any","conditions":[{"join":"AND","type":"numRange","field":"amount","from":"500"}]}]}]}
            """,
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero));
        await commitRepository.CommitAsync(
            projectId,
            [new FilterCommitItem(definition, SameVoucherScenario)],
            Context,
            CancellationToken.None);
        var committedIdentities = await ReadPersistedHitIdentitiesAsync(
            connectionFactory(projectId),
            schemaPrefix);

        await ExecuteAsync(
            connectionFactory(projectId),
            $"DELETE FROM {schemaPrefix}filter_hit;");
        await lazyMaterializer.MaterializeAsync(
            projectId,
            [new MaterializableScenario(ScenarioPosition, SameVoucherScenario)],
            Context,
            CancellationToken.None);
        var lazyIdentities = await ReadPersistedHitIdentitiesAsync(
            connectionFactory(projectId),
            schemaPrefix);

        var expected = Cases.Single(item => ReferenceEquals(item.Scenario, SameVoucherScenario))
            .ExpectedIdentities;
        Assert.Equal(expected, previewIdentities);
        Assert.Equal(expected, committedIdentities);
        Assert.Equal(expected, lazyIdentities);
        Assert.Equal(previewIdentities, committedIdentities);
        Assert.Equal(committedIdentities, lazyIdentities);
    }

    private static async Task<string[]> ReadPersistedHitIdentitiesAsync(
        DbConnection connection,
        string schemaPrefix)
    {
        var identities = new List<string>();
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT g.voucher_number, g.line_item "
                + $"FROM {schemaPrefix}filter_hit r "
                + $"JOIN {schemaPrefix}gl g ON g.entry_id = r.entry_id "
                + $"WHERE r.scenario_id = {ScenarioPosition} "
                + "ORDER BY g.voucher_number, g.line_item;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                identities.Add($"{reader.GetString(0)}|{reader.GetString(1)}");
            }
        }

        return identities.ToArray();
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

    private static string[] SemanticIdentities(FilterPreviewResult preview) =>
        preview.PreviewRows
            .Select(row => $"{row.VoucherNumber}|{row.LineItem}")
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();

    private static FilterScenarioSpec Scenario(string name, FilterGroupSpec group) =>
        new(name, "synthetic provider parity", [group]);

    private static FilterConditionSpec TextSetRule(
        string field,
        TextMatchMode mode,
        IReadOnlyList<string> values,
        TextSetNormalization? normalization = null)
    {
        var rule = new FilterConditionSpec(
            FilterJoin.And,
            FilterConditionType.TextSet,
            PrescreenKey: null,
            Field: field,
            Keywords: [],
            Mode: mode,
            FromDate: null,
            ToDate: null,
            FromAmountScaled: null,
            ToAmountScaled: null,
            DrCr: null,
            IsManual: null)
        {
            Values = values
        };

        return normalization.HasValue
            ? rule with { Normalization = normalization.Value }
            : rule;
    }

    // 整張傳票條件，量詞「至少一筆符合」，子條件只有這一條。
    private static FilterConditionSpec AnyEntryInVoucher(FilterConditionSpec rule) =>
        new(
            FilterJoin.And,
            FilterConditionType.Voucher,
            PrescreenKey: null,
            Field: null,
            Keywords: [],
            Mode: TextMatchMode.Contains,
            FromDate: null,
            ToDate: null,
            FromAmountScaled: null,
            ToAmountScaled: null,
            DrCr: null,
            IsManual: null)
        {
            Quantifier = "any",
            Conditions = [rule]
        };

    private static FilterConditionSpec NumRangeLowerBound(long fromAmountScaled) =>
        new(
            FilterJoin.And,
            FilterConditionType.NumRange,
            PrescreenKey: null,
            Field: "amount",
            Keywords: [],
            Mode: TextMatchMode.Contains,
            FromDate: null,
            ToDate: null,
            FromAmountScaled: fromAmountScaled,
            ToAmountScaled: null,
            DrCr: null,
            IsManual: null);

    private sealed record ScenarioCase(
        string Name,
        FilterScenarioSpec Scenario,
        string[] ExpectedIdentities,
        long ExpectedVoucherCount);

    private sealed record ScenarioResult(
        string Name,
        long Count,
        long VoucherCount,
        string[] Identities);
}
