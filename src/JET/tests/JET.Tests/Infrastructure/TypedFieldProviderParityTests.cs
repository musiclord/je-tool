using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// typed dynamic rule（2026-08-14 契約凍結）的三 provider 執行矩陣（全合成資料）：
/// 完整 field×operator 命中集合、blank＝missing row（負向 operator 不納入 blank 的反例）、
/// text trim＋不分大小寫、date 邊界含端點、money signed／absolute 與 MoneyScale lexeme 等價、
/// in 去重與同一張傳票的 typed 佐證。SQLite、DuckDB 與 gated SQL Server 必須得到同一組
/// semantic row identity；另以 commit／lazy materialization 驗證 typed 命中落地與 preview 一致。
/// </summary>
public sealed class TypedFieldProviderParityTests
{
    private const string TextFieldId = "rde.aaaa0000aaaa0000aaaa0000aaaa0000";
    private const string DateFieldId = "rde.bbbb0000bbbb0000bbbb0000bbbb0000";
    private const string MoneyFieldId = "rde.cccc0000cccc0000cccc0000cccc0000";
    private const int ScenarioPosition = 1;

    private const string FixtureSql =
        $"""
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date,
             account_number, account_name, description, amount_scaled,
             debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('typed-rde', 1, 'T-1', '1', '2025-08-01', 'A-1', 'Typed', 'r1', 1000, 1000, 0, 'DEBIT'),
            ('typed-rde', 2, 'T-2', '1', '2025-08-01', 'A-2', 'Typed', 'r2', 1000, 1000, 0, 'DEBIT'),
            ('typed-rde', 3, 'T-3', '1', '2025-08-01', 'A-3', 'Typed', 'r3', 1000, 1000, 0, 'DEBIT'),
            ('typed-rde', 4, 'T-4', '1', '2025-08-01', 'A-4', 'Typed', 'blank row', 1000, 1000, 0, 'DEBIT'),
            ('typed-rde', 5, 'T-5', '1', '2025-08-01', 'A-5', 'Typed', 'r5', 1000, 1000, 0, 'DEBIT'),
            ('typed-rde', 6, 'T-6', '1', '2025-08-01', 'A-6', 'Typed', 'r6', 1000, 1000, 0, 'DEBIT'),
            ('typed-rde', 7, 'SV-1', '1', '2025-08-02', 'SV-A', 'Typed', 'anchor', 1000, 1000, 0, 'DEBIT'),
            ('typed-rde', 8, 'SV-1', '2', '2025-08-02', 'SV-B', 'Typed', 'evidence', 2000, 2000, 0, 'DEBIT');

        UPDATE gl SET is_excluded = 0;

        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{TextFieldId}', 'text', ' Alpha ', NULL, NULL FROM gl WHERE voucher_number = 'T-1';
        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{TextFieldId}', 'text', 'BETA', NULL, NULL FROM gl WHERE voucher_number = 'T-2';
        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{TextFieldId}', 'text', 'gamma value', NULL, NULL FROM gl WHERE voucher_number = 'T-3';
        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{TextFieldId}', 'text', 'ALPHA', NULL, NULL FROM gl WHERE voucher_number = 'T-5';
        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{TextFieldId}', 'text', 'delta', NULL, NULL FROM gl WHERE voucher_number = 'T-6';
        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{TextFieldId}', 'text', 'ANCHOR', NULL, NULL FROM gl WHERE voucher_number = 'SV-1' AND line_item = '1';
        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{TextFieldId}', 'text', 'EVIDENCE', NULL, NULL FROM gl WHERE voucher_number = 'SV-1' AND line_item = '2';

        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{DateFieldId}', 'date', NULL, '2025-01-01', NULL FROM gl WHERE voucher_number = 'T-1';
        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{DateFieldId}', 'date', NULL, '2025-06-30', NULL FROM gl WHERE voucher_number = 'T-2';
        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{DateFieldId}', 'date', NULL, '2025-07-01', NULL FROM gl WHERE voucher_number = 'T-3';
        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{DateFieldId}', 'date', NULL, '2025-12-31', NULL FROM gl WHERE voucher_number = 'T-5';

        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{MoneyFieldId}', 'amount', NULL, NULL, 10050 FROM gl WHERE voucher_number = 'T-1';
        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{MoneyFieldId}', 'amount', NULL, NULL, -10050 FROM gl WHERE voucher_number = 'T-2';
        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{MoneyFieldId}', 'amount', NULL, NULL, 0 FROM gl WHERE voucher_number = 'T-3';
        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{MoneyFieldId}', 'amount', NULL, NULL, 500 FROM gl WHERE voucher_number = 'T-5';
        INSERT INTO gl_rde_value (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
        SELECT entry_id, '{MoneyFieldId}', 'amount', NULL, NULL, 99999 FROM gl WHERE voucher_number = 'SV-1' AND line_item = '2';
        """;

    private static readonly FilterConditionContext Context = new(
        MoneyScale: 100,
        PrepStartDate: null,
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        PopulationScope: GlPopulationScope.AuditPeriod)
    {
        RdeFields =
        [
            new GlRdeFieldMetadata(TextFieldId, "備註欄", "備註", RdeFieldValueTypeNames.Text),
            new GlRdeFieldMetadata(DateFieldId, "審核日欄", "審核日", RdeFieldValueTypeNames.Date),
            new GlRdeFieldMetadata(MoneyFieldId, "稅額欄", "稅額", RdeFieldValueTypeNames.Amount)
        ]
    };

    // 原本用「同一傳票」比對範圍；這個寫法已拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回），
    // 照它的重建方式把佐證條件放進「整張傳票條件」並選「至少一筆符合」，固定答案不變。
    // 第一次失敗：Build 收據 20261030-080858642-d5c1719481744042bbfc70329c5fcceb（比對範圍的型別已不存在，無法編譯）。
    private static readonly FilterScenarioSpec SameVoucherScenario = new(
        "typed-same-voucher",
        "synthetic typed parity",
        [
            new FilterGroupSpec(
                FilterJoin.And,
                [
                    TypedRule(TextFieldId, "equals", value: "anchor "),
                    new FilterConditionSpec(FilterJoin.And, FilterConditionType.Voucher, null, null, [], TextMatchMode.Contains,
                        null, null, null, null, null, null)
                    {
                        Quantifier = "any",
                        Conditions = [TypedRule(MoneyFieldId, "greaterThan", value: "500", amountBasis: "signed")]
                    }
                ])
        ]);

    private static readonly IReadOnlyList<ScenarioCase> Cases =
    [
        // ── text：trim＋不分大小寫；blank 不命中任何比較 operator ───────────
        new("text-equals-trim-case", TypedRule(TextFieldId, "equals", value: " alpha "),
            ["T-1|1", "T-5|1"], 2),
        new("text-notEquals-excludes-blank", TypedRule(TextFieldId, "notEquals", value: "alpha"),
            ["SV-1|1", "SV-1|2", "T-2|1", "T-3|1", "T-6|1"], 4),
        new("text-contains", TypedRule(TextFieldId, "contains", value: "amma v"),
            ["T-3|1"], 1),
        new("text-notContains-excludes-blank", TypedRule(TextFieldId, "notContains", value: "a"),
            ["SV-1|2"], 1),
        new("text-in-dedupes", TypedRule(TextFieldId, "in", values: ["beta ", "GAMMA VALUE", "beta"]),
            ["T-2|1", "T-3|1"], 2),
        new("text-notIn-excludes-blank", TypedRule(TextFieldId, "notIn", values: ["alpha", "delta"]),
            ["SV-1|1", "SV-1|2", "T-2|1", "T-3|1"], 3),
        new("text-isBlank-means-missing-row", TypedRule(TextFieldId, "isBlank"),
            ["T-4|1"], 1),
        new("text-isNotBlank", TypedRule(TextFieldId, "isNotBlank"),
            ["SV-1|1", "SV-1|2", "T-1|1", "T-2|1", "T-3|1", "T-5|1", "T-6|1"], 6),

        // ── date：ISO 精確語意與含端點邊界；blank row 不命中比較 ────────────
        new("date-on", TypedRule(DateFieldId, "on", value: "2025-06-30"), ["T-2|1"], 1),
        new("date-before", TypedRule(DateFieldId, "before", value: "2025-06-30"), ["T-1|1"], 1),
        new("date-onOrBefore-boundary", TypedRule(DateFieldId, "onOrBefore", value: "2025-06-30"),
            ["T-1|1", "T-2|1"], 2),
        new("date-after", TypedRule(DateFieldId, "after", value: "2025-06-30"),
            ["T-3|1", "T-5|1"], 2),
        new("date-onOrAfter-boundary", TypedRule(DateFieldId, "onOrAfter", value: "2025-06-30"),
            ["T-2|1", "T-3|1", "T-5|1"], 3),
        new("date-between-inclusive", TypedRule(
                DateFieldId, "between", from: "2025-06-30", to: "2025-12-31"),
            ["T-2|1", "T-3|1", "T-5|1"], 3),
        new("date-isBlank", TypedRule(DateFieldId, "isBlank"),
            ["SV-1|1", "SV-1|2", "T-4|1", "T-6|1"], 3),
        new("date-isNotBlank", TypedRule(DateFieldId, "isNotBlank"),
            ["T-1|1", "T-2|1", "T-3|1", "T-5|1"], 4),

        // ── money：scaled integer 域、signed／absolute、lexeme 等價 ─────────
        new("money-equals-signed", TypedRule(
                MoneyFieldId, "equals", value: "100.50", amountBasis: "signed"),
            ["T-1|1"], 1),
        new("money-equals-absolute", TypedRule(
                MoneyFieldId, "equals", value: "100.5", amountBasis: "absolute"),
            ["T-1|1", "T-2|1"], 2),
        new("money-notEquals-absolute-excludes-blank", TypedRule(
                MoneyFieldId, "notEquals", value: "100.50", amountBasis: "absolute"),
            ["SV-1|2", "T-3|1", "T-5|1"], 3),
        new("money-greaterThan-signed", TypedRule(
                MoneyFieldId, "greaterThan", value: "0", amountBasis: "signed"),
            ["SV-1|2", "T-1|1", "T-5|1"], 3),
        new("money-greaterThanOrEqual-signed-zero", TypedRule(
                MoneyFieldId, "greaterThanOrEqual", value: "0", amountBasis: "signed"),
            ["SV-1|2", "T-1|1", "T-3|1", "T-5|1"], 4),
        new("money-lessThan-signed-negative", TypedRule(
                MoneyFieldId, "lessThan", value: "-100", amountBasis: "signed"),
            ["T-2|1"], 1),
        new("money-lessThanOrEqual-absolute", TypedRule(
                MoneyFieldId, "lessThanOrEqual", value: "5.00", amountBasis: "absolute"),
            ["T-3|1", "T-5|1"], 2),
        new("money-between-signed", TypedRule(
                MoneyFieldId, "between", from: "-100.50", to: "5", amountBasis: "signed"),
            ["T-2|1", "T-3|1", "T-5|1"], 3),
        new("money-between-absolute", TypedRule(
                MoneyFieldId, "between", from: "5", to: "100.50", amountBasis: "absolute"),
            ["T-1|1", "T-2|1", "T-5|1"], 3),
        new("money-isBlank", TypedRule(MoneyFieldId, "isBlank"),
            ["SV-1|1", "T-4|1", "T-6|1"], 3),
        new("money-isNotBlank", TypedRule(MoneyFieldId, "isNotBlank"),
            ["SV-1|2", "T-1|1", "T-2|1", "T-3|1", "T-5|1"], 5),

        // ── 同一張傳票：typed 錨點與整張傳票條件裡的 typed 佐證（不展開 evidence、不複製 anchor）──
        new("typed-same-voucher", SameVoucherScenario, ["SV-1|1"], 1)
    ];

    [Fact]
    public async Task TypedRules_SqliteAndDuckDb_ProduceIdenticalSemanticHitSets()
    {
        await using var sqlite = await FilterPredicateProviderFixture.CreateLocalAsync(
            ProjectDocument.DefaultDatabaseProvider,
            FixtureSql);
        await using var duckDb = await FilterPredicateProviderFixture.CreateLocalAsync(
            ProjectDocument.DuckDbDatabaseProvider,
            FixtureSql);

        var sqliteResults = await ExecuteCasesAsync(sqlite);
        var duckDbResults = await ExecuteCasesAsync(duckDb);

        AssertMatchesOracle(sqliteResults);
        AssertMatchesOracle(duckDbResults);
        AssertProvidersEqual(sqliteResults, duckDbResults);
    }

    [SqlServerFact]
    public async Task TypedRules_SqlServerMatchesLocalProviders()
    {
        await using var sqlite = await FilterPredicateProviderFixture.CreateLocalAsync(
            ProjectDocument.DefaultDatabaseProvider,
            FixtureSql);
        await using var sqlServer = await FilterPredicateProviderFixture.CreateSqlServerAsync(FixtureSql);

        var sqliteResults = await ExecuteCasesAsync(sqlite);
        var sqlServerResults = await ExecuteCasesAsync(sqlServer);

        AssertMatchesOracle(sqlServerResults);
        AssertProvidersEqual(sqliteResults, sqlServerResults);
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task TypedSameVoucher_LocalProvider_PreviewCommitAndLazyMaterializationAgree(
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
        await ExecuteAsync(database.CreateConnection(projectId), FixtureSql);

        var previewRepository = new LocalFilterHitRepository(database);
        var commitRepository = new LocalFilterCommitRepository(database);
        var lazyMaterializer = new LocalFilterHitMaterializer(database);

        var preview = await previewRepository.PreviewAsync(
            projectId, SameVoucherScenario, Context, CancellationToken.None);
        var previewIdentities = SemanticIdentities(preview);

        var definition = new SavedFilterScenario(
            ScenarioPosition,
            SameVoucherScenario.Name,
            SameVoucherScenario.Rationale,
            $$"""
            {"name":"typed-same-voucher","rationale":"synthetic typed parity","groups":[{"join":"AND","matchScope":"row","conditions":[{"join":"AND","type":"typed","fieldId":"{{TextFieldId}}","operator":"equals","value":"anchor "},{"join":"AND","type":"voucher","quantifier":"any","conditions":[{"join":"AND","type":"typed","fieldId":"{{MoneyFieldId}}","operator":"greaterThan","value":"500","amountBasis":"signed"}]}]}]}
            """,
            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));
        await commitRepository.CommitAsync(
            projectId,
            [new FilterCommitItem(definition, SameVoucherScenario)],
            Context,
            CancellationToken.None);
        var committedIdentities = await ReadPersistedHitIdentitiesAsync(
            database.CreateConnection(projectId));

        await ExecuteAsync(
            database.CreateConnection(projectId),
            "DELETE FROM filter_hit;");
        await lazyMaterializer.MaterializeAsync(
            projectId,
            [new MaterializableScenario(ScenarioPosition, SameVoucherScenario)],
            Context,
            CancellationToken.None);
        var lazyIdentities = await ReadPersistedHitIdentitiesAsync(
            database.CreateConnection(projectId));

        Assert.Equal(new[] { "SV-1|1" }, previewIdentities);
        Assert.Equal(previewIdentities, committedIdentities);
        Assert.Equal(previewIdentities, lazyIdentities);
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

    private static async Task<string[]> ReadPersistedHitIdentitiesAsync(DbConnection connection)
    {
        var identities = new List<string>();
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT g.voucher_number, g.line_item "
                + "FROM filter_hit r "
                + "JOIN gl g ON g.entry_id = r.entry_id "
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

    private static FilterScenarioSpec SingleRuleScenario(string name, FilterConditionSpec rule) =>
        new(name, "synthetic typed parity", [new FilterGroupSpec(FilterJoin.And, [rule])]);

    private static FilterConditionSpec TypedRule(
        string fieldId,
        string op,
        string? value = null,
        string? from = null,
        string? to = null,
        string[]? values = null,
        string? amountBasis = null) =>
        new(
            FilterJoin.And,
            FilterConditionType.TypedField,
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
            FieldId = fieldId,
            TypedOperator = op,
            TypedValue = value,
            TypedFrom = from,
            TypedTo = to,
            TypedValues = values,
            AmountBasis = amountBasis
        };

    private sealed record ScenarioCase
    {
        public ScenarioCase(
            string name,
            FilterConditionSpec rule,
            string[] expectedIdentities,
            long expectedVoucherCount)
            : this(name, SingleRuleScenario(name, rule), expectedIdentities, expectedVoucherCount)
        {
        }

        public ScenarioCase(
            string name,
            FilterScenarioSpec scenario,
            string[] expectedIdentities,
            long expectedVoucherCount)
        {
            Name = name;
            Scenario = scenario;
            ExpectedIdentities = expectedIdentities;
            ExpectedVoucherCount = expectedVoucherCount;
        }

        public string Name { get; }

        public FilterScenarioSpec Scenario { get; }

        public string[] ExpectedIdentities { get; }

        public long ExpectedVoucherCount { get; }
    }

    private sealed record ScenarioResult(
        string Name,
        long Count,
        long VoucherCount,
        string[] Identities);
}
