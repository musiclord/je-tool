using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class FilterSinglePurposeVoucherEquivalenceTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SeparateVoucherWitnesses_KeepTheFixedAnswersAndExplainTheVoucherScope(string provider)
    {
        const string seed = """
            INSERT INTO target_gl_entry(batch_id,source_row_number,document_number,line_item,post_date,
              account_code,document_description,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_effective) VALUES
            ('witness',1,'SPLIT','1','2025-01-01','A','X',10000,10000,0,'DEBIT',1),
            ('witness',2,'SPLIT','2','2025-01-01','B','Y',-5000,0,5000,'CREDIT',1),
            ('witness',3,'SPLIT','3','2025-01-01','C','X',-5000,0,5000,'CREDIT',1),
            ('witness',4,'MATCH','1','2025-01-01','A','X',10000,10000,0,'DEBIT',1),
            ('witness',5,'MATCH','2','2025-01-01','B','X',-10000,0,10000,'CREDIT',1),
            ('witness',6,'NO','1','2025-01-01','A','X',10000,10000,0,'DEBIT',1);
            """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed);
        const string anchor = """{"type":"fieldValue","field":"accNum","operator":"equals","value":"A"}""";
        const string account = """{"type":"fieldValue","field":"accNum","operator":"equals","value":"B"}""";
        const string description = """{"type":"fieldValue","field":"description","operator":"equals","value":"X"}""";
        var original = Parse($$"""{"matchScope":"sameVoucher","rules":[{{anchor}},{{account}},{{description}}]}""");
        var replacement = Parse($$"""{"rules":[{{anchor}},{"type":"voucher","quantifier":"any","rules":[{{account}}]},{"type":"voucher","quantifier":"any","rules":[{{description}}]}]}""");
        var context = new FilterRuleContext(100, null, "2025-01-01", "2025-12-31");
        foreach (var scenario in new[] { original, replacement })
        {
            var preview = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            Assert.Equal(["MATCH|1", "SPLIT|1"], preview.PreviewRows.Select(row => row.DocumentNumber + "|" + row.LineItem).Order());
            Assert.False(JetAuditProgram.UsesLegacyWholeVoucherRows(scenario));
            var page = await fixture.Vouchers!.ReadAsync(fixture.ProjectId, scenario, context, null, new PageRequest(null, 50), countWholeVoucherRows: false, CancellationToken.None);
            Assert.Equal([new FilterVoucherSummary("MATCH", "2025-01-01", 1, 2, 10000),
                new FilterVoucherSummary("SPLIT", "2025-01-01", 1, 3, 10000)], page.Vouchers);
            var detail = await fixture.Vouchers.ReadAsync(fixture.ProjectId, scenario, context, "SPLIT", new PageRequest(null, 50), countWholeVoucherRows: false, CancellationToken.None);
            Assert.Equal([("1", 10000L, true), ("2", -5000L, false), ("3", -5000L, false)],
                detail.Details.Select(row => (row.LineItem, row.AmountScaled, row.IsHit)));
            Assert.Equal([new FilterConditionPosition(1, 1)], detail.Details[0].PrimaryConditions);
            Assert.All(detail.Details.Skip(1), row => Assert.Empty(row.PrimaryConditions));
            if (ReferenceEquals(scenario, replacement))
            {
                Assert.All(detail.Details, row => Assert.Equal([new FilterConditionPosition(1, 2), new FilterConditionPosition(1, 3)], row.VoucherConditions));
                Assert.All(detail.Details, row => Assert.Empty(row.EvidenceConditions));
            }
            else
            {
                Assert.Equal([new FilterConditionPosition(1, 2)], detail.Details[1].EvidenceConditions);
                Assert.All(detail.Details, row => Assert.Empty(row.VoucherConditions));
            }
        }
    }

    private static FilterScenarioSpec Parse(string group) => FilterScenarioPayloadParser.Parse(
        JsonElement.Parse($$"""{"name":"合成佐證","rationale":"固定答案","groups":[{{group}}]}"""), 100);
}
