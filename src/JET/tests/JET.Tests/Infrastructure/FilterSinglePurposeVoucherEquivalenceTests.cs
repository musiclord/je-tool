using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

// SQLite 與 DuckDB 每次 Public 都執行；SQL Server 版在 2026-10-19 為了核對三種資料庫結果一致而補上，只在 Provider 執行。
// 三種資料庫比對同一組固定答案。
// 「同一傳票」比對範圍停用寫法已拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回），改寫前的寫法無法再解析，所以拿掉它那一半，只留整張傳票條件對固定答案與傳票明細說明的比對
// （停用寫法與現行寫法命中相同，最後一次在 Focused 收據 20261030-075358791-be8fbb69606344368d7a86a233582c62 通過）。第一次失敗：Focused 收據 20261030-081546753-58fab64421d94d9a9973dfa09cda1b98（舊的比對範圍改成無法讀取，原寫法沒有命中）。
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
            // 原本另斷言每列的佐證條件為空；舊的「同一傳票」寫法拿掉後，傳票明細不再有佐證條件這一欄，這則斷言跟著拿掉。
        }
    }

    private static FilterScenarioSpec Parse(string group) => FilterScenarioPayloadParser.Parse(
        JsonElement.Parse($$"""{"name":"合成佐證","rationale":"固定答案","groups":[{{group}}]}"""), 100);
}
