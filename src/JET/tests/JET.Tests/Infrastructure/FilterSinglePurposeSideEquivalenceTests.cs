using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

// 現行寫法對固定答案的比對。
// drCr:any 與 side:all 這兩個屬性當成無法讀取的內容，所以這裡用省略它們的寫法。
public sealed class FilterSinglePurposeSideEquivalenceTests
{
    private const string Seed = """
        INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,
            account_number,description,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded) VALUES
        ('side-purpose',1,'V1','1','2025-01-01','A','X',1000,1000,0,'DEBIT',0),
        ('side-purpose',2,'V1','2','2025-01-01','B','X',-1000,0,1000,'CREDIT',0),
        ('side-purpose',3,'V2','1','2025-01-01','B','X',1000,1000,0,'DEBIT',0),
        ('side-purpose',4,'V2','2','2025-01-01','A','Y',-1000,0,1000,'CREDIT',0),
        ('side-purpose',5,'V3','1','2025-01-01','A',NULL,0,0,0,'DEBIT',0),
        ('side-purpose',6,'V4','1','2025-01-01','B','X',-500,0,500,'CREDIT',0),
        ('side-purpose',7,'V5','1','2025-01-01','A','X',1000,1000,0,'DEBIT',0),
        ('side-purpose',8,'V5','2','2025-01-01','C','Y',-1000,0,1000,'CREDIT',0);
        INSERT INTO account_mapping(batch_id,source_row_number,account_number,standardized_category,category_id)
        VALUES ('side-map',1,'A','Cash','builtin.cash'),('side-map',2,'B','Revenue','builtin.revenue'),('side-map',3,'C','Others','builtin.others');
        """;

    // 原寫法與明示方向子條件先比對通過；省略重複欄位
    [Theory]
    [InlineData("sqlite")]
    public async Task ExplicitSideGroups_KeepTheOriginalFixedEntryIdentities(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        await AssertExplicitSideGroupsAsync(fixture);
    }

    private static async Task AssertExplicitSideGroupsAsync(FilterPredicateProviderFixture fixture)
    {
        const string debit = """{"type":"drCrOnly","drCr":"debit"}""";
        const string credit = """{"type":"drCrOnly","drCr":"credit"}""";
        const string accountA = """{"type":"fieldValue","field":"accountNumber","operator":"in","values":["A"]}""";
        const string accountB = """{"type":"fieldValue","field":"accountNumber","operator":"in","values":["B"]}""";
        const string cash = """{"type":"accountSide","categoryMode":"is","categoryIds":["builtin.cash"],"categorySelection":"subtree"}""";
        var cases = new (string Replacement, string[] Expected)[]
        {
            (debit + "," + accountA, ["V1|1", "V3|1", "V5|1"]),
            ($$"""{"type":"group","conditions":[{{debit}},{{accountA}}]}""", ["V1|1", "V3|1", "V5|1"]),
            ($$"""{"type":"group","conditions":[{{debit}},{{accountA}}]},{"join":"OR","type":"group","conditions":[{{credit}},{{accountB}}]}""",
             ["V1|1", "V1|2", "V3|1", "V4|1", "V5|1"]),
            (debit + "," + cash, ["V1|1", "V3|1", "V5|1"]),
            ($$"""{"type":"voucher","quantifier":"none","conditions":[{{credit}},{{cash}}]}""",
             ["V1|1", "V1|2", "V3|1", "V4|1", "V5|1", "V5|2"]),
            ($$"""{"type":"voucher","quantifier":"any","conditions":[{{debit}},{{accountA}}]}""",
             ["V1|1", "V1|2", "V3|1", "V5|1", "V5|2"]),
            (credit + "," + """{"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"10","amountBasis":"absolute"}""",
             ["V1|2", "V2|2", "V5|2"])
        };
        foreach (var item in cases)
            Assert.Equal(item.Expected, await Hits(fixture, item.Replacement));
    }

    private static async Task<string[]> Hits(FilterPredicateProviderFixture fixture, string rules)
    {
        var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse(
            $$"""{"name":"合成方向比對","rationale":"固定答案","groups":[{"conditions":[{{rules}}]}]}"""), 100);
        var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario,
            new FilterConditionContext(100, null, "2025-01-01", "2025-12-31"), CancellationToken.None);
        return result.PreviewRows.Select(row => row.VoucherNumber + "|" + row.LineItem).Order().ToArray();
    }
}
