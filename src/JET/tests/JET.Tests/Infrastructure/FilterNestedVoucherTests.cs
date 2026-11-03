using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

// First failure: 20261014-135805134-9060396119bc4e8f8dae2eecd29f1776. Mixed-purpose input is retired; keep independent hit, amount, and report assertions.

public sealed class FilterNestedVoucherTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ExistingApprovalDateAndCreditOnlyRevenueRulesStayUnchanged(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed + """
            INSERT INTO account_mapping(batch_id,source_row_number,account_number,standardized_category,category_id)
            VALUES ('mapping',1,'A','Cash','builtin.cash'),('mapping',2,'B','Revenue','builtin.revenue');
            UPDATE gl SET approval_date='2025-01-03' WHERE voucher_number='EMPTY';
            """);
        var context = new FilterConditionContext(100, "2025-01-02", "2025-01-01", "2025-12-31");
        // 裁定 2026-10-29-29 改成照舊版 IDEA R3：未預期借貸組合要求同傳票至少有一筆金額大於 0 的分錄。
        // EMPTY 只有一筆收入貸方，沒有正金額分錄，改為不命中（原本命中 EMPTY|1；第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad）。
        foreach (var (key, expected) in new[] { ("prepPeriodApproval", new[] { "EMPTY|1" }), ("unexpectedAccountPair", Array.Empty<string>()) })
        {
            var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse($$"""{"groups":[{"conditions":[{"type":"prescreen","prescreenKey":"{{key}}"}]}]}"""), 100);
            var preview = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            // Posting date is Jan 1; only the approval date exceeds Jan 2. EMPTY has no debit counterpart.
            Assert.Equal(expected, preview.PreviewRows.Select(row => row.VoucherNumber + "|" + row.LineItem));
        }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    // 原本用「同一傳票」比對範圍與欄位條件的分錄方向；這兩種寫法已拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回），
    // 照它們的重建方式改成借貸別子條件組與「整張傳票條件」的「至少一筆符合」：佐證分開放是各自可由不同分錄符合，
    // 放在同一個子條件組是要同一筆分錄同時符合，固定答案不變。第一次失敗：Focused 收據 20261030-081546753-58fab64421d94d9a9973dfa09cda1b98（舊寫法在組成 SQL 時被擋下）。
    public async Task SeparateAndCompoundVoucherWitnessesKeepDifferentMeanings(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        var context = new FilterConditionContext(100, null, "2025-01-01", "2025-12-31");
        const string anchor = """{"type":"group","conditions":[{"type":"drCrOnly","drCr":"debit"},{"type":"fieldValue","field":"accountNumber","operator":"equals","value":"A"}]}""";
        const string account = """{"type":"group","conditions":[{"type":"drCrOnly","drCr":"credit"},{"type":"fieldValue","field":"accountNumber","operator":"equals","value":"B"}]}""";
        const string description = """{"type":"fieldValue","field":"description","operator":"equals","value":"X"}""";
        static string AnyEntry(string rule) => $$"""{"type":"voucher","quantifier":"any","conditions":[{{rule}}]}""";
        foreach (var item in new[] { (Body: AnyEntry(account) + "," + AnyEntry(description), Expected: new[] { "MATCH|1", "MIX|1", "SPLIT|1" }),
            (Body: AnyEntry($$"""{"type":"group","conditions":[{{account}},{{description}}]}"""), Expected: new[] { "MATCH|1", "MIX|1" }) })
        {
            var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse($$"""{"groups":[{"conditions":[{{anchor}},{{item.Body}}]}]}"""), 100);
            var preview = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            Assert.Equal(item.Expected, preview.PreviewRows.Select(row => row.VoucherNumber + "|" + row.LineItem).Order());
        }
    }

    [Theory]
    [InlineData("{\"type\":\"voucher\",\"side\":\"debit\",\"quantifier\":\"all\",\"conditions\":[]}")]
    [InlineData("{\"type\":\"voucher\",\"side\":\"bad\",\"quantifier\":\"bad\",\"conditions\":[{\"type\":\"drCrOnly\",\"drCr\":\"debit\"}]}")]
    public void InvalidQuantifiersCannotSave(string rule)
    {
        var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse($$"""{"groups":[{"conditions":[{{rule}}]}]}"""), 100);
        Assert.NotEmpty(FilterScenarioValidator.Validate(scenario, new(false, true, true), forSave: false));
    }

    [Fact]
    public void ExcessiveDepthIsRejectedBeforeCompilation()
    {
        var rule = """{"type":"drCrOnly","drCr":"debit"}""";
        for (var i = 0; i < 10; i++) rule = $$"""{"type":"group","conditions":[{{rule}}]}""";
        Assert.Throws<JetActionException>(() => FilterScenarioPayloadParser.Parse(
            JsonElement.Parse($$"""{"groups":[{"conditions":[{{rule}}]}]}"""), 100));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("123")]
    public void InvalidSelectionShapeDoesNotFallBackToLegacyRole(string value)
    {
        Assert.Throws<JetActionException>(() => FilterScenarioPayloadParser.Parse(JsonElement.Parse($$"""
            {"groups":[{"conditions":[{"type":"accountSide","drCr":"debit","categoryMode":"is","categoryIds":["builtin.cash"],"categorySelection":{{value}}}]}]}
            """), 100));
    }

    private const string Seed = """
        INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,
            account_number,description,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded) VALUES
        ('nested',1,'SPLIT','1','2025-01-01','A','X',100,100,0,'DEBIT',0),
        ('nested',2,'SPLIT','2','2025-01-01','B','Y',-50,0,50,'CREDIT',0),
        ('nested',3,'SPLIT','3','2025-01-01','C','X',-50,0,50,'CREDIT',0),
        ('nested',4,'MATCH','1','2025-01-01','A','X',100,100,0,'DEBIT',0),
        ('nested',5,'MATCH','2','2025-01-01','B','X',-100,0,100,'CREDIT',0),
        ('nested',6,'EMPTY','1','2025-01-01','B','X',-100,0,100,'CREDIT',0),
        ('nested',7,'NULL','1','2025-01-01','A',NULL,100,100,0,'DEBIT',0),
        ('nested',8,'ZERO','1','2025-01-01','A','X',0,0,0,'DEBIT',0),
        ('nested',9,'OUT','1','2026-01-01','B','X',-100,0,100,'CREDIT',1),
        ('nested',10,'MIX','1','2025-01-01','A','X',100,100,0,'DEBIT',0),
        ('nested',11,'MIX','2','2025-01-01','B','X',-50,0,50,'CREDIT',0),
        ('nested',12,'MIX','3','2025-01-01','C','Y',-50,0,50,'CREDIT',0);
        """;

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CompoundWitnessAndQuantifiers_HaveIndependentAnswers(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        var context = new FilterConditionContext(100, null, "2025-01-01", "2025-12-31");
        const string account = """{"type":"fieldValue","field":"accountNumber","operator":"equals","value":"B"}""";
        const string text = """{"type":"fieldValue","field":"description","operator":"contains","value":"X"}""";
        var cases = new (string Rule, string[] Expected)[]
        {
            ($$"""{"type":"voucher","quantifier":"any","conditions":[{"type":"drCrOnly","drCr":"credit"},{{account}},{{text}}]}""", ["EMPTY|1", "MATCH|1", "MATCH|2", "MIX|1", "MIX|2", "MIX|3"]),
            ($$"""{"type":"group","conditions":[{"type":"voucher","quantifier":"any","conditions":[{"type":"drCrOnly","drCr":"credit"}]},{"type":"voucher","quantifier":"all","conditions":[{"type":"drCrOnly","drCr":"debit"},{"type":"group","join":"OR","conditions":[{{account}},{{text}}]}]}]}""", ["EMPTY|1", "MATCH|1", "MATCH|2"]),
            ($$"""{"type":"voucher","quantifier":"none","conditions":[{"type":"drCrOnly","drCr":"credit"},{{account}},{{text}}]}""", ["NULL|1", "SPLIT|1", "SPLIT|2", "SPLIT|3", "ZERO|1"]),
            ($$"""{"type":"group","conditions":[{"type":"voucher","quantifier":"any","conditions":[{"type":"drCrOnly","drCr":"debit"}]},{"type":"voucher","quantifier":"all","conditions":[{"type":"drCrOnly","drCr":"credit"},{"type":"group","join":"OR","conditions":[{{text}}]}]}]}""", ["MATCH|1", "MATCH|2", "MIX|1", "MIX|2", "MIX|3", "SPLIT|1", "SPLIT|2", "SPLIT|3", "ZERO|1"]),
            ($$"""{"type":"voucher","quantifier":"any","conditions":[{"type":"drCrOnly","drCr":"debit"},{{text}}]}""", ["MATCH|1", "MATCH|2", "MIX|1", "MIX|2", "MIX|3", "SPLIT|1", "SPLIT|2", "SPLIT|3", "ZERO|1"]),
            ($$"""{"type":"voucher","quantifier":"none","conditions":[{"type":"drCrOnly","drCr":"debit"},{{text}}]}""", ["EMPTY|1", "NULL|1"]),
            ($$"""{"type":"voucher","quantifier":"any","conditions":[{{text}}]}""", ["EMPTY|1", "MATCH|1", "MATCH|2", "MIX|1", "MIX|2", "MIX|3", "SPLIT|1", "SPLIT|2", "SPLIT|3", "ZERO|1"]),
            ($$"""{"type":"voucher","quantifier":"none","conditions":[{{text}}]}""", ["NULL|1"]),
            ($$"""{"type":"voucher","quantifier":"all","conditions":[{{text}}]}""", ["EMPTY|1", "MATCH|1", "MATCH|2", "ZERO|1"]),
            ($$"""{"type":"group","conditions":[{"type":"fieldValue","field":"accountNumber","operator":"equals","value":"A"},{"type":"group","conditions":[{{text}},{"join":"OR","type":"fieldValue","field":"description","operator":"isBlank"}]}]}""", ["MATCH|1", "MIX|1", "NULL|1", "SPLIT|1", "ZERO|1"])
        };
        foreach (var (rule, expected) in cases)
        {
            var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse($$"""{"name":"合成量詞","rationale":"固定答案","groups":[{"conditions":[{{rule}}]}]}"""), 100);
            // 欄位旗標拿掉後，不帶可用欄位清單就不檢查欄位，等同原本摘要旗標是 true。
            // 第一次失敗：收據 20261023-031856475-04f811a69e3346428de91091f0d02655（編譯失敗）。
            Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, true)));
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            Assert.Equal(expected, result.PreviewRows.Select(row => row.VoucherNumber + "|" + row.LineItem).Order().ToArray());
        }
    }
}
