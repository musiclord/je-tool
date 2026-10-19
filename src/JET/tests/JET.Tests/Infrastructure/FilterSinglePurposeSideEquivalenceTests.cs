using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

// SQLite 與 DuckDB 每次 Public 都執行；SQL Server 版在 2026-10-19 依跨資料庫一致性計畫補上，只在 Provider 執行。
// 三種資料庫比對同一組固定答案。
public sealed class FilterSinglePurposeSideEquivalenceTests
{
    private const string Seed = """
        INSERT INTO target_gl_entry(batch_id,source_row_number,document_number,line_item,post_date,
            account_code,document_description,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_effective) VALUES
        ('side-purpose',1,'V1','1','2025-01-01','A','X',1000,1000,0,'DEBIT',1),
        ('side-purpose',2,'V1','2','2025-01-01','B','X',-1000,0,1000,'CREDIT',1),
        ('side-purpose',3,'V2','1','2025-01-01','B','X',1000,1000,0,'DEBIT',1),
        ('side-purpose',4,'V2','2','2025-01-01','A','Y',-1000,0,1000,'CREDIT',1),
        ('side-purpose',5,'V3','1','2025-01-01','A',NULL,0,0,0,'DEBIT',1),
        ('side-purpose',6,'V4','1','2025-01-01','B','X',-500,0,500,'CREDIT',1),
        ('side-purpose',7,'V5','1','2025-01-01','A','X',1000,1000,0,'DEBIT',1),
        ('side-purpose',8,'V5','2','2025-01-01','C','Y',-1000,0,1000,'CREDIT',1);
        INSERT INTO target_account_mapping(batch_id,source_row_number,account_code,standardized_category,category_id)
        VALUES ('side-map',1,'A','Cash','builtin.cash'),('side-map',2,'B','Revenue','builtin.revenue'),('side-map',3,'C','Others','builtin.others');
        """;

    // 原寫法與明示方向子條件先比對通過；省略重複欄位首次失敗：20261014-132201427-ac4139a67469477a8c109420eba6632b。
    [Theory]
    [InlineData("sqlite", false)]
    [InlineData("duckdb", false)]
    [InlineData("sqlite", true)]
    [InlineData("duckdb", true)]
    public async Task ExplicitSideGroups_KeepTheOriginalFixedEntryIdentities(string provider, bool omitRetiredScopes)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        await AssertExplicitSideGroupsAsync(fixture, omitRetiredScopes);
    }

    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitSideGroups_KeepTheOriginalFixedEntryIdentities_SqlServer(bool omitRetiredScopes)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateSqlServerAsync(Seed);
        await AssertExplicitSideGroupsAsync(fixture, omitRetiredScopes);
    }

    private static async Task AssertExplicitSideGroupsAsync(FilterPredicateProviderFixture fixture, bool omitRetiredScopes)
    {
        const string debit = """{"type":"drCrOnly","drCr":"debit"}""";
        const string credit = """{"type":"drCrOnly","drCr":"credit"}""";
        const string accountA = """{"type":"fieldValue","field":"accNum","operator":"in","values":["A"]}""";
        const string accountB = """{"type":"fieldValue","field":"accNum","operator":"in","values":["B"]}""";
        const string debitA = """{"type":"fieldValue","field":"accNum","operator":"in","values":["A"],"drCr":"debit"}""";
        const string creditB = """{"type":"fieldValue","field":"accNum","operator":"in","values":["B"],"drCr":"credit"}""";
        const string cash = """{"type":"accountSide","drCr":"any","categoryMode":"is","categoryIds":["builtin.cash"],"categorySelection":"subtree"}""";
        var cases = new (string Original, string Replacement, string[] Expected)[]
        {
            (debitA, debit + "," + accountA, ["V1|1", "V3|1", "V5|1"]),
            (debitA, $$"""{"type":"group","rules":[{{debit}},{{accountA}}]}""", ["V1|1", "V3|1", "V5|1"]),
            (debitA + "," + creditB.Replace("{", "{\"join\":\"OR\",", StringComparison.Ordinal),
             $$"""{"type":"group","rules":[{{debit}},{{accountA}}]},{"join":"OR","type":"group","rules":[{{credit}},{{accountB}}]}""",
             ["V1|1", "V1|2", "V3|1", "V4|1", "V5|1"]),
            (cash.Replace("\"any\"", "\"debit\"", StringComparison.Ordinal), debit + "," + cash, ["V1|1", "V3|1", "V5|1"]),
            (cash.Replace("\"any\"", "\"credit\"", StringComparison.Ordinal).Replace("\"is\"", "\"absent\"", StringComparison.Ordinal),
             $$"""{"type":"voucher","side":"all","quantifier":"none","rules":[{{credit}},{{cash}}]}""",
             ["V1|1", "V1|2", "V3|1", "V4|1", "V5|1", "V5|2"]),
            ($$"""{"type":"voucher","side":"debit","quantifier":"any","rules":[{{accountA}}]}""",
             $$"""{"type":"voucher","side":"all","quantifier":"any","rules":[{{debit}},{{accountA}}]}""",
             ["V1|1", "V1|2", "V3|1", "V5|1", "V5|2"]),
            ("""{"type":"fieldValue","field":"amount","operator":"lessThanOrEqual","value":"-10","amountBasis":"signed"}""",
             credit + "," + """{"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"10","amountBasis":"absolute"}""",
             ["V1|2", "V2|2", "V5|2"])
        };
        foreach (var item in cases)
        {
            Assert.Equal(item.Expected, await Hits(fixture, item.Original));
            var replacement = omitRetiredScopes
                ? item.Replacement.Replace("\"drCr\":\"any\",", "", StringComparison.Ordinal)
                    .Replace("\"side\":\"all\",", "", StringComparison.Ordinal)
                : item.Replacement;
            Assert.Equal(item.Expected, await Hits(fixture, replacement));
        }
    }

    private static async Task<string[]> Hits(FilterPredicateProviderFixture fixture, string rules)
    {
        var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse(
            $$"""{"name":"合成方向比對","rationale":"固定答案","groups":[{"rules":[{{rules}}]}]}"""), 100);
        var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario,
            new FilterRuleContext(100, null, "2025-01-01", "2025-12-31"), CancellationToken.None);
        return result.PreviewRows.Select(row => row.DocumentNumber + "|" + row.LineItem).Order().ToArray();
    }
}
