using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 裁定 2026-10-22-14 把較少使用人員與較少使用科目的三種寫法收成一個方法：
/// 「使用較少的科目」改由統計條件同一個方法產生，科目編號去頭尾空白後分組，空白科目不算一個科目；
/// 和「編製分錄較少的人員」空白人員不列入同一個規則。以前預篩選的寫法把空字串的科目當成一個只出現一次的科目而命中。
/// 第一次失敗：收據 20261024-190913880-a45e823f31e745139f977c5c3481bdb3。
/// 第四步照舊版拿掉編製分錄較少的人員與使用較少的科目（裁定 2026-10-29-27），預篩選的 rareAccount 寫法已不存在，只留第五步統計條件的寫法；
/// 原本兩種寫法各核對一次，第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad。
/// </summary>
public sealed class RareAccountBlankCodeTests
{
    private const string Seed = """
        INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,
            account_number,description,created_by,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded) VALUES
        ('blank',1,'RARE','1','2025-01-01','A','x','u',100,100,0,'DEBIT',0),
        ('blank',2,'PADDED','1','2025-01-01',' A ','x','u',100,100,0,'DEBIT',0),
        ('blank',3,'EMPTY','1','2025-01-01','','x','u',100,100,0,'DEBIT',0),
        ('blank',4,'SPACES','1','2025-01-01','   ','x','u',100,100,0,'DEBIT',0),
        ('blank',5,'MISSING','1','2025-01-01',NULL,'x','u',100,100,0,'DEBIT',0),
        ('blank',6,'B-01','1','2025-01-01','B','x','u',100,100,0,'DEBIT',0),
        ('blank',7,'B-02','1','2025-01-01','B','x','u',100,100,0,'DEBIT',0),
        ('blank',8,'B-03','1','2025-01-01','B','x','u',100,100,0,'DEBIT',0),
        ('blank',9,'B-04','1','2025-01-01','B','x','u',100,100,0,'DEBIT',0),
        ('blank',10,'B-05','1','2025-01-01','B','x','u',100,100,0,'DEBIT',0),
        ('blank',11,'B-06','1','2025-01-01','B','x','u',100,100,0,'DEBIT',0),
        ('blank',12,'B-07','1','2025-01-01','B','x','u',100,100,0,'DEBIT',0),
        ('blank',13,'B-08','1','2025-01-01','B','x','u',100,100,0,'DEBIT',0),
        ('blank',14,'B-09','1','2025-01-01','B','x','u',100,100,0,'DEBIT',0),
        ('blank',15,'B-10','1','2025-01-01','B','x','u',100,100,0,'DEBIT',0),
        ('blank',16,'B-11','1','2025-01-01','B','x','u',100,100,0,'DEBIT',0),
        ('blank',17,'B-12','1','2025-01-01','B','x','u',100,100,0,'DEBIT',0);
        """;

    // A 與「 A 」去空白後是同一個科目，兩列；B 有 12 列不算較少使用；空字串、只有空白與 NULL 的科目編號都不算科目。
    private static readonly string[] Expected = ["PADDED", "RARE"];

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task BlankAccountNumbers_AreNotRareAccounts(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        Assert.Equal(Expected, await Hits(fixture,
            """{"type":"entityFrequency","field":"accountNumber","countUnit":"entries","countOperator":"lessThanOrEqual","countFrom":11}"""));
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
