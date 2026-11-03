using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

// 第九節 G 的改寫前比對，只使用本檔合成資料。SQLite 與 DuckDB 每次 Public 都執行；SQL Server 版在
// 2026-10-19 為了核對三種資料庫結果一致而補上，只在 Provider 執行。三種資料庫比對同一組固定答案。
// 停用寫法已拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回），改寫前的寫法無法再解析，所以拿掉停用寫法那一半，只留現行寫法對固定答案的比對（停用寫法與現行寫法命中相同，最後一次在 Focused 收據 20261030-075358791-be8fbb69606344368d7a86a233582c62 通過）。
// 第一次失敗：Focused 收據 20261030-081546753-58fab64421d94d9a9973dfa09cda1b98（停用的型別讀不到）。
public sealed class FilterSinglePurposeEquivalenceTests
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

    // 固定答案原本逐列寫在本機版的 InlineData；2026-10-19 補 SQL Server 版時移到這裡，讓三種資料庫讀同一份，答案不變。
    private static readonly Dictionary<int, string[]> TrailingZeroHits = new()
    {
        [1] = ["HUNDRED", "MILLION", "NEG-TEN", "TEN", "TEN-FRACTION"],
        [2] = ["HUNDRED", "MILLION"],
        [6] = ["MILLION"]
    };

    [Theory]
    [InlineData("sqlite", 1)]
    [InlineData("duckdb", 1)]
    [InlineData("sqlite", 2)]
    [InlineData("duckdb", 2)]
    [InlineData("sqlite", 6)]
    [InlineData("duckdb", 6)]
    public async Task TrailingZeros_ReplacementMustKeepTheFixedHits(string provider, int digits)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        await AssertTrailingZerosAsync(fixture, digits);
    }

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public async Task TrailingZeros_ReplacementMustKeepTheFixedHits_SqlServer(int digits)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateSqlServerAsync(Seed);
        await AssertTrailingZerosAsync(fixture, digits);
    }

    private static async Task AssertTrailingZerosAsync(FilterPredicateProviderFixture fixture, int digits)
    {
        var expected = TrailingZeroHits[digits];
        var replacement = $$"""{"type":"fieldValue","field":"amount","operator":"endsWithDigits","values":["{{new string('0', digits)}}"],"amountBasis":"absolute"}""";
        // 裁定 2026-10-14-09 保留原結果，以子條件組排除主單位整數為零的金額。
        // 首次失敗：20261014-130735069-edd5b356c42049308e609d1b8da99b1a；固定答案不變。
        if (digits == 1)
            replacement = $$"""{"type":"group","combinator":"AND","conditions":[{{replacement}},{"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"1","amountBasis":"absolute"}]}""";
        // 所有改寫仍比對原本的固定答案。
        var actual = await Hits(fixture, replacement);
        Assert.True(expected.SequenceEqual(actual), $"固定答案：{string.Join(", ", expected)}；改寫結果：{string.Join(", ", actual)}");
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task BlankAndPeople_ReplacementKeepsFixedHits(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        await AssertBlankAndPeopleAsync(fixture);
    }

    [SqlServerFact]
    public async Task BlankAndPeople_ReplacementKeepsFixedHits_SqlServer()
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateSqlServerAsync(Seed);
        await AssertBlankAndPeopleAsync(fixture);
    }

    // 舊式關鍵字的三組比對只服務停用寫法，一起拿掉；空白摘要與人員兩組比的是兩種現行寫法，照舊比對。
    private static async Task AssertBlankAndPeopleAsync(FilterPredicateProviderFixture fixture)
    {
        var cases = new (string Original, string Replacement, string[] Expected)[]
        {
            ("""{"type":"prescreen","prescreenKey":"blankDescription"}""",
             """{"type":"fieldValue","field":"description","operator":"isBlank"}""", ["HUNDRED", "ZERO"]),
            ("""{"type":"text","field":"createdBy","mode":"exact","keywords":"USER"}""",
             """{"type":"fieldValue","field":"createdBy","operator":"in","values":["USER"]}""", ["SUB", "ZERO"])
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
            $$"""{"name":"合成規則比對","rationale":"固定答案","groups":[{"conditions":[{{rule}}]}]}"""), 100);
        var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario,
            new FilterConditionContext(100, null, "2025-01-01", "2025-12-31"), CancellationToken.None);
        return result.PreviewRows.Select(row => row.VoucherNumber).Order().ToArray();
    }
}
