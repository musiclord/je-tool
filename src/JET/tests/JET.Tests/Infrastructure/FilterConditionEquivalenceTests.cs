using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

// 用本檔的合成資料，核對同一個用途的不同條件寫法都命中同一組固定答案。
public sealed class FilterConditionEquivalenceTests
{
    private const string Seed = """
        INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,
            account_number,description,created_by,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded) VALUES
        ('purpose',1,'ZERO','1','2025-01-01','A',NULL,' user ',0,0,0,'DEBIT',0),
        ('purpose',2,'SUB','1','2025-01-01','A','abc','USER',50,50,0,'DEBIT',0),
        ('purpose',3,'NEG-SUB','1','2025-01-01','A','ABC','other',-50,0,50,'CREDIT',0),
        ('purpose',4,'TEN','1','2025-01-01','A','  x abc  ','other',1000,1000,0,'DEBIT',0),
        ('purpose',5,'NEG-TEN','1','2025-01-01','A','a%b','other',-1000,0,1000,'CREDIT',0),
        ('purpose',6,'TEN-FRACTION','1','2025-01-01','A','a_b','other',1001,1001,0,'DEBIT',0),
        ('purpose',7,'ONE','1','2025-01-01','A','a[b','other',100,100,0,'DEBIT',0),
        ('purpose',8,'HUNDRED','1','2025-01-01','A','   ','other',10000,10000,0,'DEBIT',0),
        ('purpose',9,'MILLION','1','2025-01-01','A','調整分錄','other',100000025,100000025,0,'DEBIT',0),
        ('purpose',10,'USER-SUFFIX','1','2025-01-01','A','一般分錄','USER2',100,100,0,'DEBIT',0);
        """;

    // 固定答案集中寫在這裡。
    private static readonly Dictionary<int, string[]> TrailingZeroHits = new()
    {
        [1] = ["HUNDRED", "MILLION", "NEG-TEN", "TEN", "TEN-FRACTION"],
        [2] = ["HUNDRED", "MILLION"],
        [6] = ["MILLION"]
    };

    [Theory]
    [InlineData("sqlite", 1)]
    [InlineData("sqlite", 2)]
    [InlineData("sqlite", 6)]
    public async Task TrailingZeros_AllFormsHitTheFixedAnswers(string provider, int digits)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        await AssertTrailingZerosAsync(fixture, digits);
    }

    private static async Task AssertTrailingZerosAsync(FilterPredicateProviderFixture fixture, int digits)
    {
        var expected = TrailingZeroHits[digits];
        var replacement = $$"""{"type":"fieldValue","field":"amount","operator":"endsWithDigits","values":["{{new string('0', digits)}}"],"amountBasis":"absolute"}""";
        // 以子條件組排除主單位整數為零的金額。
        if (digits == 1)
            replacement = $$"""{"type":"group","combinator":"AND","conditions":[{{replacement}},{"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"1","amountBasis":"absolute"}]}""";
        // 每一種寫法都要命中上面的固定答案。
        var actual = await Hits(fixture, replacement);
        Assert.True(expected.SequenceEqual(actual), $"固定答案：{string.Join(", ", expected)}；改寫結果：{string.Join(", ", actual)}");
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task BlankAndPeople_AllFormsHitTheFixedAnswers(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        await AssertBlankAndPeopleAsync(fixture);
    }

    // 空白摘要與建立人員兩組，兩種寫法都要命中同一組固定答案：空白摘要比對預篩選條件與欄位值條件，
    // 建立人員比對「等於」與「符合清單任一值」。
    private static async Task AssertBlankAndPeopleAsync(FilterPredicateProviderFixture fixture)
    {
        var cases = new (string Original, string Replacement, string[] Expected)[]
        {
            ("""{"type":"prescreen","prescreenKey":"blankDescription"}""",
             """{"type":"fieldValue","field":"description","operator":"isBlank"}""", ["HUNDRED", "ZERO"]),
            ("""{"type":"fieldValue","field":"createdBy","operator":"equals","value":"USER"}""",
             """{"type":"fieldValue","field":"createdBy","operator":"in","values":["USER"]}""", ["SUB", "ZERO"])
        };
        foreach (var item in cases)
        {
            Assert.Equal(item.Expected, await Hits(fixture, item.Original));
            Assert.Equal(item.Expected, await Hits(fixture, item.Replacement));
        }
    }

    private static async Task<string?[]> Hits(FilterPredicateProviderFixture fixture, string rule)
    {
        var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse(
            $$"""{"name":"合成規則比對","rationale":"固定答案","groups":[{"conditions":[{{rule}}]}]}"""), 100);
        var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario,
            new FilterConditionContext(100, null, "2025-01-01", "2025-12-31"), CancellationToken.None);
        return result.PreviewRows.Select(row => row.VoucherNumber).Order().ToArray();
    }
}
