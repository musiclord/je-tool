using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

// 整張傳票條件對固定答案與傳票明細說明的比對。
public sealed class FilterSinglePurposeVoucherEquivalenceTests
{
    private const string Seed = """
        INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,
          account_number,description,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded) VALUES
        ('witness',1,'SPLIT','1','2025-01-01','A','X',10000,10000,0,'DEBIT',0),
        ('witness',2,'SPLIT','2','2025-01-01','B','Y',-5000,0,5000,'CREDIT',0),
        ('witness',3,'SPLIT','3','2025-01-01','C','X',-5000,0,5000,'CREDIT',0),
        ('witness',4,'MATCH','1','2025-01-01','A','X',10000,10000,0,'DEBIT',0),
        ('witness',5,'MATCH','2','2025-01-01','B','X',-10000,0,10000,'CREDIT',0),
        ('witness',6,'NO','1','2025-01-01','A','X',10000,10000,0,'DEBIT',0);
        """;

    [Theory]
    [InlineData("sqlite")]
    public async Task SeparateVoucherWitnesses_KeepTheFixedAnswersAndExplainTheVoucherScope(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        await AssertSeparateVoucherWitnessesAsync(fixture);
    }

    private static async Task AssertSeparateVoucherWitnessesAsync(FilterPredicateProviderFixture fixture)
    {
        const string anchor = """{"type":"fieldValue","field":"accountNumber","operator":"equals","value":"A"}""";
        const string account = """{"type":"fieldValue","field":"accountNumber","operator":"equals","value":"B"}""";
        const string description = """{"type":"fieldValue","field":"description","operator":"equals","value":"X"}""";
        var replacement = Parse($$"""{"conditions":[{{anchor}},{"type":"voucher","quantifier":"any","conditions":[{{account}}]},{"type":"voucher","quantifier":"any","conditions":[{{description}}]}]}""");
        var context = new FilterConditionContext(100, null, "2025-01-01", "2025-12-31");
        foreach (var scenario in new[] { replacement })
        {
            var preview = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            Assert.Equal(["MATCH|1", "SPLIT|1"], preview.PreviewRows.Select(row => row.VoucherNumber + "|" + row.LineItem).Order());
            Assert.False(JetAuditProgram.UsesLegacyWholeVoucherRows(scenario));
            var page = await fixture.Vouchers!.ReadAsync(fixture.ProjectId, scenario, context, null, new PageRequest(null, 50), countWholeVoucherRows: false, CancellationToken.None);
            Assert.Equal([new FilterVoucherSummary("MATCH", "2025-01-01", 1, 2, 10000),
                new FilterVoucherSummary("SPLIT", "2025-01-01", 1, 3, 10000)], page.Vouchers);
            var detail = await fixture.Vouchers.ReadAsync(fixture.ProjectId, scenario, context, "SPLIT", new PageRequest(null, 50), countWholeVoucherRows: false, CancellationToken.None);
            Assert.Equal([("1", 10000L, true), ("2", -5000L, false), ("3", -5000L, false)],
                detail.Details.Select(row => (row.LineItem, row.AmountScaled, row.IsHit)));
            Assert.Equal([new FilterConditionPosition(1, 1)], detail.Details[0].PrimaryConditions);
            Assert.All(detail.Details.Skip(1), row => Assert.Empty(row.PrimaryConditions));
            Assert.All(detail.Details, row => Assert.Equal([new FilterConditionPosition(1, 2), new FilterConditionPosition(1, 3)], row.VoucherConditions));
        }
    }

    private static FilterScenarioSpec Parse(string group) => FilterScenarioPayloadParser.Parse(
        JsonElement.Parse($$"""{"name":"合成佐證","rationale":"固定答案","groups":[{{group}}]}"""), 100);
}
