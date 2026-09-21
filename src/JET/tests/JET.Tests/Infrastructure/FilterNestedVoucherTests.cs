using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class FilterNestedVoucherTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ExistingApprovalDateAndCreditOnlyRevenueRulesStayUnchanged(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed + """
            INSERT INTO target_account_mapping(batch_id,source_row_number,account_code,standardized_category,category_id)
            VALUES ('mapping',1,'A','Cash','builtin.cash'),('mapping',2,'B','Revenue','builtin.revenue');
            UPDATE target_gl_entry SET approval_date='2025-01-03' WHERE document_number='EMPTY';
            """);
        var context = new FilterRuleContext(100, "2025-01-02", "2025-01-01", "2025-12-31");
        foreach (var key in new[] { "postPeriodApproval", "unexpectedAccountPair" })
        {
            var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse($$"""{"groups":[{"rules":[{"type":"prescreen","prescreenKey":"{{key}}"}]}]}"""), 100);
            var preview = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            // Posting date is Jan 1; only the approval date exceeds Jan 2. EMPTY has no debit counterpart.
            Assert.Equal(new[] { "EMPTY|1" }, preview.PreviewRows.Select(row => row.DocumentNumber + "|" + row.LineItem));
        }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task LegacyAnchorAndCompoundWitnessKeepDifferentMeanings(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        var context = new FilterRuleContext(100, null, "2025-01-01", "2025-12-31");
        const string anchor = """{"type":"fieldValue","field":"accNum","operator":"equals","value":"A","drCr":"debit"}""";
        const string account = """{"type":"fieldValue","field":"accNum","operator":"equals","value":"B","drCr":"credit"}""";
        const string description = """{"type":"fieldValue","field":"description","operator":"equals","value":"X"}""";
        foreach (var item in new[] { (Body: account + "," + description, Expected: new[] { "MATCH|1", "MIX|1", "SPLIT|1" }),
            (Body: $$"""{"type":"group","rules":[{{account}},{{description}}]}""", Expected: new[] { "MATCH|1", "MIX|1" }) })
        {
            var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse($$"""{"groups":[{"matchScope":"sameVoucher","rules":[{{anchor}},{{item.Body}}]}]}"""), 100);
            var preview = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            Assert.Equal(item.Expected, preview.PreviewRows.Select(row => row.DocumentNumber + "|" + row.LineItem).Order());
        }
    }

    [Theory]
    [InlineData("{\"type\":\"voucher\",\"side\":\"debit\",\"quantifier\":\"all\",\"rules\":[]}")]
    [InlineData("{\"type\":\"voucher\",\"side\":\"bad\",\"quantifier\":\"bad\",\"rules\":[{\"type\":\"drCrOnly\",\"drCr\":\"debit\"}]}")]
    public void InvalidQuantifiersCannotSave(string rule)
    {
        var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse($$"""{"groups":[{"rules":[{{rule}}]}]}"""), 100);
        Assert.NotEmpty(FilterScenarioValidator.Validate(scenario, new(false, true, true), forSave: false));
    }

    [Fact]
    public void ExcessiveDepthIsRejectedBeforeCompilation()
    {
        var rule = """{"type":"drCrOnly","drCr":"debit"}""";
        for (var i = 0; i < 10; i++) rule = $$"""{"type":"group","rules":[{{rule}}]}""";
        Assert.Throws<JetActionException>(() => FilterScenarioPayloadParser.Parse(
            JsonElement.Parse($$"""{"groups":[{"rules":[{{rule}}]}]}"""), 100));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("123")]
    public void InvalidSelectionShapeDoesNotFallBackToLegacyRole(string value)
    {
        Assert.Throws<JetActionException>(() => FilterScenarioPayloadParser.Parse(JsonElement.Parse($$"""
            {"groups":[{"rules":[{"type":"accountSide","drCr":"debit","categoryMode":"is","categoryIds":["builtin.cash"],"categorySelection":{{value}}}]}]}
            """), 100));
    }

    private const string Seed = """
        INSERT INTO target_gl_entry(batch_id,source_row_number,document_number,line_item,post_date,
            account_code,document_description,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_effective) VALUES
        ('nested',1,'SPLIT','1','2025-01-01','A','X',100,100,0,'DEBIT',1),
        ('nested',2,'SPLIT','2','2025-01-01','B','Y',-50,0,50,'CREDIT',1),
        ('nested',3,'SPLIT','3','2025-01-01','C','X',-50,0,50,'CREDIT',1),
        ('nested',4,'MATCH','1','2025-01-01','A','X',100,100,0,'DEBIT',1),
        ('nested',5,'MATCH','2','2025-01-01','B','X',-100,0,100,'CREDIT',1),
        ('nested',6,'EMPTY','1','2025-01-01','B','X',-100,0,100,'CREDIT',1),
        ('nested',7,'NULL','1','2025-01-01','A',NULL,100,100,0,'DEBIT',1),
        ('nested',8,'ZERO','1','2025-01-01','A','X',0,0,0,'DEBIT',1),
        ('nested',9,'OUT','1','2026-01-01','B','X',-100,0,100,'CREDIT',0),
        ('nested',10,'MIX','1','2025-01-01','A','X',100,100,0,'DEBIT',1),
        ('nested',11,'MIX','2','2025-01-01','B','X',-50,0,50,'CREDIT',1),
        ('nested',12,'MIX','3','2025-01-01','C','Y',-50,0,50,'CREDIT',1);
        """;

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CompoundWitnessAndQuantifiers_HaveIndependentAnswers(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        var context = new FilterRuleContext(100, null, "2025-01-01", "2025-12-31");
        const string account = """{"type":"fieldValue","field":"accNum","operator":"equals","value":"B"}""";
        const string text = """{"type":"fieldValue","field":"description","operator":"contains","value":"X"}""";
        var cases = new (string Rule, string[] Expected)[]
        {
            ($$"""{"type":"voucher","side":"credit","quantifier":"any","rules":[{{account}},{{text}}]}""", ["EMPTY|1", "MATCH|1", "MATCH|2", "MIX|1", "MIX|2", "MIX|3"]),
            ($$"""{"type":"voucher","side":"credit","quantifier":"all","rules":[{{account}},{{text}}]}""", ["EMPTY|1", "MATCH|1", "MATCH|2"]),
            ($$"""{"type":"voucher","side":"credit","quantifier":"none","rules":[{{account}},{{text}}]}""", ["NULL|1", "SPLIT|1", "SPLIT|2", "SPLIT|3", "ZERO|1"]),
            ($$"""{"type":"voucher","side":"debit","quantifier":"all","rules":[{{text}}]}""", ["MATCH|1", "MATCH|2", "MIX|1", "MIX|2", "MIX|3", "SPLIT|1", "SPLIT|2", "SPLIT|3", "ZERO|1"]),
            ($$"""{"type":"voucher","side":"debit","quantifier":"any","rules":[{{text}}]}""", ["MATCH|1", "MATCH|2", "MIX|1", "MIX|2", "MIX|3", "SPLIT|1", "SPLIT|2", "SPLIT|3", "ZERO|1"]),
            ($$"""{"type":"voucher","side":"debit","quantifier":"none","rules":[{{text}}]}""", ["EMPTY|1", "NULL|1"]),
            ($$"""{"type":"voucher","side":"all","quantifier":"any","rules":[{{text}}]}""", ["EMPTY|1", "MATCH|1", "MATCH|2", "MIX|1", "MIX|2", "MIX|3", "SPLIT|1", "SPLIT|2", "SPLIT|3", "ZERO|1"]),
            ($$"""{"type":"voucher","side":"all","quantifier":"none","rules":[{{text}}]}""", ["NULL|1"]),
            ($$"""{"type":"voucher","side":"all","quantifier":"all","rules":[{{text}}]}""", ["EMPTY|1", "MATCH|1", "MATCH|2", "ZERO|1"]),
            ($$"""{"type":"group","rules":[{"type":"fieldValue","field":"accNum","operator":"equals","value":"A"},{"type":"group","rules":[{{text}},{"join":"OR","type":"fieldValue","field":"description","operator":"isBlank"}]}]}""", ["MATCH|1", "MIX|1", "NULL|1", "SPLIT|1", "ZERO|1"])
        };
        foreach (var (rule, expected) in cases)
        {
            var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse($$"""{"name":"合成量詞","rationale":"固定答案","groups":[{"rules":[{{rule}}]}]}"""), 100);
            Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, true) { HasDescription = true }));
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            Assert.Equal(expected, result.PreviewRows.Select(row => row.DocumentNumber + "|" + row.LineItem).Order().ToArray());
        }
    }
}
