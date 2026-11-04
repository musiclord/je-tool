using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 第五步金額的「尾數連續多個 0」（裁定 2026-11-03-07）：一條條件取代改版前的子條件組
/// 「整數尾數符合 N 個 0 且金額絕對值大於或等於 1」，第四步預篩選規則「金額尾數連續 6 個 0」也改用同一個述詞。
/// 預期分錄逐筆手算：整數部分以 N 個 0 結尾，而且整數部分不是 0。新條件、改版前的子條件組與預篩選規則都要選到同一組分錄，
/// 預期值不由被測程式算出。全部資料都是程式生成的合成資料。
/// </summary>
public sealed class TrailingZeroRunConditionsTests
{
    private const string ExtraId = "rde.cccc1111cccc1111cccc1111cccc1111";

    // 金額以 scale 100 存放：Z01 0、Z02 0.50、Z03 1、Z04 10、Z05 -10.99、Z06 100、Z07 999,999、Z08 100,000、
    // Z09 1,000,000、Z10 -2,000,000.45、Z11 10,000,000.99、Z12 1,000,000,000,000。攸關資料元素的金額少了 Z12，當成空白。
    private const string Seed = """
        INSERT INTO gl(batch_id,source_row_number,voucher_number,gl_date,
            account_number,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded)
        VALUES ('zeros',1,'Z01','2025-03-01','1000',0,0,0,'DEBIT',0),
               ('zeros',2,'Z02','2025-03-01','1000',50,50,0,'DEBIT',0),
               ('zeros',3,'Z03','2025-03-01','1000',100,100,0,'DEBIT',0),
               ('zeros',4,'Z04','2025-03-01','1000',1000,1000,0,'DEBIT',0),
               ('zeros',5,'Z05','2025-03-01','1000',-1099,0,1099,'CREDIT',0),
               ('zeros',6,'Z06','2025-03-01','1000',10000,10000,0,'DEBIT',0),
               ('zeros',7,'Z07','2025-03-01','1000',99999900,99999900,0,'DEBIT',0),
               ('zeros',8,'Z08','2025-03-01','1000',10000000,10000000,0,'DEBIT',0),
               ('zeros',9,'Z09','2025-03-01','1000',100000000,100000000,0,'DEBIT',0),
               ('zeros',10,'Z10','2025-03-01','1000',-200000045,0,200000045,'CREDIT',0),
               ('zeros',11,'Z11','2025-03-01','1000',1000000099,1000000099,0,'DEBIT',0),
               ('zeros',12,'Z12','2025-03-01','1000',100000000000000,100000000000000,0,'DEBIT',0);
        INSERT INTO gl_rde_value(entry_id,field_id,value_type,amount_scaled)
        SELECT entry_id,'rde.cccc1111cccc1111cccc1111cccc1111','amount',amount_scaled FROM gl WHERE source_row_number <> 12;
        """;

    private static readonly string[] AllEntries =
        ["Z01", "Z02", "Z03", "Z04", "Z05", "Z06", "Z07", "Z08", "Z09", "Z10", "Z11", "Z12"];

    // 每個金額的整數部分結尾有幾個 0：Z04、Z05 一個，Z06 兩個，Z08 五個，Z09、Z10 六個，Z11 七個，Z12 十二個；
    // Z01、Z02 的整數部分是 0，Z03、Z07 沒有 0。
    private static readonly (int Zeros, string[] Hits)[] Expected =
    [
        (1, ["Z04", "Z05", "Z06", "Z08", "Z09", "Z10", "Z11", "Z12"]),
        (2, ["Z06", "Z08", "Z09", "Z10", "Z11", "Z12"]),
        (3, ["Z08", "Z09", "Z10", "Z11", "Z12"]),
        (5, ["Z08", "Z09", "Z10", "Z11", "Z12"]),
        (6, ["Z09", "Z10", "Z11", "Z12"]),
        (7, ["Z11", "Z12"]),
        (12, ["Z12"])
    ];

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task NewCondition_OldGroupAndPrescreenRule_SelectTheSameEntries(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        var context = new FilterConditionContext(100, null, "2025-01-01", "2025-12-31");
        async Task<string[]> Hits(string rule)
        {
            var scenario = FilterCompletenessTests.Scenario(rule);
            Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, true) { MoneyScale = 100 }));
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            return result.PreviewRows.Select(row => row.VoucherNumber).Order().ToArray();
        }

        foreach (var (zeros, hits) in Expected)
        {
            var count = zeros.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(hits, await Hits(ZeroRun("endsWithZeros", count)));
            Assert.Equal(AllEntries.Except(hits), await Hits(ZeroRun("notEndsWithZeros", count)));
            // 改版前第五步加入的子條件組，已儲存的情境照舊這樣讀與執行。
            var oldGroup = $$"""
                {"type":"group","conditions":[
                  {"type":"fieldValue","field":"amount","operator":"endsWithDigits","value":"{{new string('0', zeros)}}","amountBasis":"absolute"},
                  {"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"1","amountBasis":"absolute","join":"AND"}]}
                """;
            Assert.Equal(hits, await Hits(oldGroup));
            if (zeros == TrailingZeroThreshold.DefaultZerosThreshold)
                Assert.Equal(hits, await Hits("""{"type":"prescreen","prescreenKey":"trailingZeros"}"""));
        }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ExtraAmountField_IgnoresTheSignAndListsBlanksOnlyWhenAsked(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        var extra = new GlRdeFieldMetadata(ExtraId, "sourceMoney", "原幣金額", "amount");
        var context = new FilterConditionContext(100, null, "2025-01-01", "2025-12-31") { RdeFields = [extra] };
        (string Operator, bool IncludeBlank, string[] Hits)[] cases =
        [
            ("endsWithZeros", false, ["Z09", "Z10", "Z11"]),
            ("endsWithZeros", true, ["Z09", "Z10", "Z11", "Z12"]),
            ("notEndsWithZeros", false, ["Z01", "Z02", "Z03", "Z04", "Z05", "Z06", "Z07", "Z08"]),
            ("notEndsWithZeros", true, ["Z01", "Z02", "Z03", "Z04", "Z05", "Z06", "Z07", "Z08", "Z12"])
        ];
        foreach (var basis in new[] { "signed", "absolute" })
        foreach (var item in cases)
        {
            var rule = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "fieldValue", ["fieldId"] = ExtraId, ["operator"] = item.Operator, ["value"] = "6",
                ["amountBasis"] = basis, ["includeBlank"] = item.IncludeBlank
            });
            var scenario = FilterCompletenessTests.Scenario(rule);
            Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, true) { MoneyScale = 100, RdeFields = [extra] }));
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            Assert.Equal(item.Hits, result.PreviewRows.Select(row => row.VoucherNumber).Order());
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("13")]
    [InlineData("")]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("六")]
    public void ZeroCountOutsideOneToTwelve_IsRejectedWithTheScreenSentence(string value)
    {
        var scenario = FilterCompletenessTests.Scenario(ZeroRun("endsWithZeros", value));
        var errors = FilterScenarioValidator.Validate(scenario, new(false, true, true) { MoneyScale = 100 });
        // 和前端 filter-values.js problem() 同一句。
        Assert.Equal("第 1 組第 1 條：連續 0 的個數須為 1 到 12 的整數", Assert.Single(errors));
    }

    [Fact]
    public void AValueListBesideTheZeroCount_IsUnreadableContent()
    {
        var scenario = FilterCompletenessTests.Scenario(
            """{"type":"fieldValue","field":"amount","operator":"endsWithZeros","value":"6","values":["6"],"amountBasis":"absolute"}""");
        var errors = FilterScenarioValidator.Validate(scenario, new(false, true, true) { MoneyScale = 100 });
        Assert.Equal("第 1 組第 1 條：" + FilterScenarioCorruption.RuleMessage, Assert.Single(errors));
    }

    private static string ZeroRun(string op, string value) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["type"] = "fieldValue", ["field"] = "amount", ["operator"] = op, ["value"] = value, ["amountBasis"] = "absolute"
    });
}
