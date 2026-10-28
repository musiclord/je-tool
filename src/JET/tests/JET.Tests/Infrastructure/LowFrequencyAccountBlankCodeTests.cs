using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 預篩選收斂計畫階段九（使用者 2026-10-22 裁定低頻人員與低頻科目的三種寫法收成一個方法）：
/// 「使用較少的科目」改由統計條件同一個方法產生，科目編號去頭尾空白後分組，空白科目不算一個科目；
/// 和「編製分錄較少的人員」空白人員不列入同一個規則。以前預篩選的寫法把空字串的科目當成一個只出現一次的科目而命中。
/// 第一次失敗：收據 20261024-190913880-a45e823f31e745139f977c5c3481bdb3。
/// </summary>
public sealed class LowFrequencyAccountBlankCodeTests
{
    private const string Seed = """
        INSERT INTO target_gl_entry(batch_id,source_row_number,document_number,line_item,post_date,
            account_code,document_description,created_by,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_effective) VALUES
        ('blank',1,'RARE','1','2025-01-01','A','x','u',100,100,0,'DEBIT',1),
        ('blank',2,'PADDED','1','2025-01-01',' A ','x','u',100,100,0,'DEBIT',1),
        ('blank',3,'EMPTY','1','2025-01-01','','x','u',100,100,0,'DEBIT',1),
        ('blank',4,'SPACES','1','2025-01-01','   ','x','u',100,100,0,'DEBIT',1),
        ('blank',5,'MISSING','1','2025-01-01',NULL,'x','u',100,100,0,'DEBIT',1),
        ('blank',6,'B-01','1','2025-01-01','B','x','u',100,100,0,'DEBIT',1),
        ('blank',7,'B-02','1','2025-01-01','B','x','u',100,100,0,'DEBIT',1),
        ('blank',8,'B-03','1','2025-01-01','B','x','u',100,100,0,'DEBIT',1),
        ('blank',9,'B-04','1','2025-01-01','B','x','u',100,100,0,'DEBIT',1),
        ('blank',10,'B-05','1','2025-01-01','B','x','u',100,100,0,'DEBIT',1),
        ('blank',11,'B-06','1','2025-01-01','B','x','u',100,100,0,'DEBIT',1),
        ('blank',12,'B-07','1','2025-01-01','B','x','u',100,100,0,'DEBIT',1),
        ('blank',13,'B-08','1','2025-01-01','B','x','u',100,100,0,'DEBIT',1),
        ('blank',14,'B-09','1','2025-01-01','B','x','u',100,100,0,'DEBIT',1),
        ('blank',15,'B-10','1','2025-01-01','B','x','u',100,100,0,'DEBIT',1),
        ('blank',16,'B-11','1','2025-01-01','B','x','u',100,100,0,'DEBIT',1),
        ('blank',17,'B-12','1','2025-01-01','B','x','u',100,100,0,'DEBIT',1);
        """;

    // A 與「 A 」去空白後是同一個科目，兩列；B 有 12 列不算低頻；空字串、只有空白與 NULL 的科目編號都不算科目。
    private static readonly string[] Expected = ["PADDED", "RARE"];

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task BlankAccountCodes_AreNotRarelyUsedAccounts_InBothWritings(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        Assert.Equal(Expected, await Hits(fixture, """{"type":"prescreen","prescreenKey":"lowFrequencyAccount"}"""));
        Assert.Equal(Expected, await Hits(fixture,
            """{"type":"entityFrequency","field":"accNum","countUnit":"entries","countOperator":"lessThanOrEqual","countFrom":11}"""));
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
