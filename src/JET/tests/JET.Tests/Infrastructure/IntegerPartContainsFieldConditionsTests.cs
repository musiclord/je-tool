using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 裁定 2026-11-05-46：金額與其他數字欄位補上舊版「特定數字篩選」的「整數部分包含」與「整數部分不包含」。
/// 只看絕對值的整數部分，忽略正負號及小數；多串數字任一包含就算符合。金額倍率 100，預期值逐列手算。
/// </summary>
public sealed class IntegerPartContainsFieldConditionsTests
{
    [Theory]
    [InlineData("sqlite")]
    public async Task IntegerPartContains_CoreAndExtraIgnoreSignAndDecimals(string provider)
    {
        const string extraId = "rde.cccccccccccccccccccccccccccccccc";
        // 整數部分：R1 12888、R2 888（-888.45）、R3 123（123.45）、R4 0（0.08）、R5 2800、R6 0。
        const string seed = """
            INSERT INTO gl(batch_id,source_row_number,voucher_number,gl_date,
                account_number,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded)
            VALUES ('digits',1,'R1','2025-03-01','1000',1288800,1288800,0,'DEBIT',0),
                   ('digits',2,'R2','2025-03-01','1000',-88845,0,88845,'CREDIT',0),
                   ('digits',3,'R3','2025-03-01','1000',12345,12345,0,'DEBIT',0),
                   ('digits',4,'R4','2025-03-01','1000',8,8,0,'DEBIT',0),
                   ('digits',5,'R5','2025-03-01','1000',280000,280000,0,'DEBIT',0),
                   ('digits',6,'R6','2025-03-01','1000',0,0,0,'DEBIT',0);
            INSERT INTO gl_rde_value(entry_id,field_id,value_type,amount_scaled)
            SELECT entry_id,'rde.cccccccccccccccccccccccccccccccc','amount',amount_scaled FROM gl WHERE source_row_number <> 6;
            """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed);
        var extra = new GlRdeFieldMetadata(extraId, "sourceMoney", "原幣金額", "amount");
        var context = new FilterConditionContext(100, null, "2025-01-01", "2025-12-31") { RdeFields = [extra] };
        var cases = new (string Operator, string Value, string[] Hits)[]
        {
            ("containsDigits", "888", ["R1", "R2"]),
            ("containsDigits", "28", ["R1", "R5"]),
            ("containsDigits", "0", ["R4", "R5", "R6"]),
            ("containsDigits", "888,123", ["R1", "R2", "R3"]),
            // 小數不比對：-888.45 與 123.45 的小數部分都含 45，整數部分不含。
            ("containsDigits", "45", []),
            ("notContainsDigits", "888", ["R3", "R4", "R5", "R6"])
        };
        foreach (var useExtra in new[] { false, true })
        foreach (var blank in new[] { false, true })
        foreach (var item in cases)
        {
            var payload = new Dictionary<string, object?> { ["type"] = "fieldValue", ["operator"] = item.Operator,
                ["value"] = item.Value, ["includeBlank"] = blank, ["amountBasis"] = useExtra ? "signed" : "absolute" };
            payload[useExtra ? "fieldId" : "field"] = useExtra ? extraId : "amount";
            var scenario = FilterCompletenessTests.Scenario(JsonSerializer.Serialize(payload));
            Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, true) { MoneyScale = 100, RdeFields = [extra] }));
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            // R6 沒有攸關資料元素的值，算空白：只有勾選「空白也符合」時列入。
            var expected = item.Hits.ToList();
            if (useExtra) { expected.Remove("R6"); if (blank) expected.Add("R6"); }
            Assert.Equal(expected.Order(), result.PreviewRows.Select(row => row.VoucherNumber).Order());
        }
    }

    [Theory]
    [InlineData("8a8", "數字「8a8」須為 1 到 12 位數字")]
    [InlineData("1234567890123", "數字「1234567890123」須為 1 到 12 位數字")]
    [InlineData("", "尚未輸入數字")]
    public void IntegerPartContains_RejectsValuesThatAreNotOneToTwelveDigits(string value, string reason)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "fieldValue", field = "amount", @operator = "containsDigits", value, includeBlank = false, amountBasis = "absolute"
        });
        var errors = FilterScenarioValidator.Validate(FilterCompletenessTests.Scenario(payload), new(false, true, true) { MoneyScale = 100 });
        Assert.Contains(errors, error => error.EndsWith(reason, StringComparison.Ordinal));
    }
}
