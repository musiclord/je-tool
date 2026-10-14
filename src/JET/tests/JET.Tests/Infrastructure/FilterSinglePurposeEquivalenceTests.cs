using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

// 第九節 G 的改寫前比對，只使用本檔合成資料與兩種本機資料庫。
public sealed class FilterSinglePurposeEquivalenceTests
{
    private const string Seed = """
        INSERT INTO target_gl_entry(batch_id,source_row_number,document_number,line_item,post_date,
            account_code,document_description,created_by,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_effective) VALUES
        ('purpose',1,'ZERO','1','2025-01-01','A',NULL,' user ',0,0,0,'DEBIT',1),
        ('purpose',2,'SUB','1','2025-01-01','A','abc','USER',50,50,0,'DEBIT',1),
        ('purpose',3,'NEG-SUB','1','2025-01-01','A','ABC','other',-50,0,50,'CREDIT',1),
        ('purpose',4,'TEN','1','2025-01-01','A','  x abc  ','other',1000,1000,0,'DEBIT',1),
        ('purpose',5,'NEG-TEN','1','2025-01-01','A','a%b','other',-1000,0,1000,'CREDIT',1),
        ('purpose',6,'TEN-FRACTION','1','2025-01-01','A','a_b','other',1001,1001,0,'DEBIT',1),
        ('purpose',7,'ONE','1','2025-01-01','A','a[b','other',100,100,0,'DEBIT',1),
        ('purpose',8,'HUNDRED','1','2025-01-01','A','   ','other',10000,10000,0,'DEBIT',1),
        ('purpose',9,'MILLION','1','2025-01-01','A','調整分錄','other',100000025,100000025,0,'DEBIT',1),
        ('purpose',10,'USER-SUFFIX','1','2025-01-01','A','一般分錄','USER2',100,100,0,'DEBIT',1);
        """;

    [Theory]
    [InlineData("sqlite", 1, new[] { "HUNDRED", "MILLION", "NEG-TEN", "TEN", "TEN-FRACTION" })]
    [InlineData("duckdb", 1, new[] { "HUNDRED", "MILLION", "NEG-TEN", "TEN", "TEN-FRACTION" })]
    [InlineData("sqlite", 2, new[] { "HUNDRED", "MILLION" })]
    [InlineData("duckdb", 2, new[] { "HUNDRED", "MILLION" })]
    [InlineData("sqlite", 6, new[] { "MILLION" })]
    [InlineData("duckdb", 6, new[] { "MILLION" })]
    public async Task TrailingZeros_ReplacementMustKeepTheFixedHits(string provider, int digits, string[] expected)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        var original = $$"""{"type":"customTrailingZeros","digits":{{digits}}}""";
        var replacement = $$"""{"type":"fieldValue","field":"amount","operator":"endsWithDigits","values":["{{new string('0', digits)}}"],"amountBasis":"absolute"}""";
        // 使用者 2026-10-14 裁定保留原結果，以子條件組排除主單位整數為零的金額。
        // 首次失敗：20261014-130735069-edd5b356c42049308e609d1b8da99b1a；固定答案不變。
        if (digits == 1)
            replacement = $$"""{"type":"group","combinator":"AND","rules":[{{replacement}},{"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"1","amountBasis":"absolute"}]}""";
        Assert.Equal(expected, await Hits(fixture, original));
        // 所有改寫仍比對原本的固定答案。
        var actual = await Hits(fixture, replacement);
        Assert.True(expected.SequenceEqual(actual), $"固定答案：{string.Join(", ", expected)}；改寫結果：{string.Join(", ", actual)}");
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task KeywordsBlankAndPeople_ReplacementKeepsFixedHits(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        var cases = new (string Original, string Replacement, string[] Expected)[]
        {
            ("""{"type":"customKeywords","keywords":" abc "}""",
             """{"type":"fieldValue","field":"description","operator":"contains","values":[" abc "]}""", ["NEG-SUB", "SUB", "TEN"]),
            ("""{"type":"customKeywords","keywords":"%,_,["}""",
             """{"type":"fieldValue","field":"description","operator":"contains","values":["%","_","["]}""", ["NEG-TEN", "ONE", "TEN-FRACTION"]),
            ("""{"type":"customKeywords","keywords":"調整"}""",
             """{"type":"fieldValue","field":"description","operator":"contains","values":["調整"]}""", ["MILLION"]),
            ("""{"type":"prescreen","prescreenKey":"blankDescription"}""",
             """{"type":"fieldValue","field":"description","operator":"isBlank"}""", ["HUNDRED", "ZERO"]),
            ("""{"type":"text","field":"createBy","mode":"exact","keywords":"USER"}""",
             """{"type":"fieldValue","field":"createBy","operator":"in","values":["USER"]}""", ["SUB", "ZERO"])
        };
        foreach (var item in cases)
        {
            Assert.Equal(item.Expected, await Hits(fixture, item.Original));
            Assert.Equal(item.Expected, await Hits(fixture, item.Replacement));
        }
    }

    private static async Task<string[]> Hits(FilterPredicateProviderFixture fixture, string rule)
    {
        var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse(
            $$"""{"name":"合成規則比對","rationale":"固定答案","groups":[{"rules":[{{rule}}]}]}"""), 100);
        var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario,
            new FilterRuleContext(100, null, "2025-01-01", "2025-12-31"), CancellationToken.None);
        return result.PreviewRows.Select(row => row.DocumentNumber).Order().ToArray();
    }
}
