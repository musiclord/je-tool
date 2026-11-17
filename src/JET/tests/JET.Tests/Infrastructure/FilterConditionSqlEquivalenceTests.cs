using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 資料庫條件式要和 JET 實際篩選的結果一致：把參數換成值之後的條件直接拿去查合成案件資料庫，
/// 查到的分錄要和篩選本身命中的分錄相同。資料全部是程式生成的合成值。
/// </summary>
public sealed class FilterConditionSqlEquivalenceTests
{
    private const string RdeTextId = "rde.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly FilterConditionContext Context = new(100, null, "2025-01-01", "2025-12-31")
    { RdeFields = [new(RdeTextId, "Extra", "Synthetic text", "text")] };

    public static TheoryData<string, object> Rules() => new()
    {
        { "文字符合清單任一值", new { type = "fieldValue", field = "description", @operator = "in", values = new[] { " alpha FEE ", "beta" } } },
        { "文字不在清單中，空白也符合", new { type = "fieldValue", field = "description", @operator = "notIn", values = new[] { "alpha fee" }, includeBlank = true } },
        { "文字不在清單中，空白不列入", new { type = "fieldValue", field = "description", @operator = "notIn", values = new[] { "alpha fee" }, includeBlank = false } },
        { "文字包含任一文字，值裡有單引號與百分比", new { type = "fieldValue", field = "description", @operator = "contains", values = new[] { "o'brien", "100%" } } },
        { "文字不包含任何文字，空白也符合", new { type = "fieldValue", field = "description", @operator = "notContains", values = new[] { "fee" }, includeBlank = true } },
        { "文字開頭符合", new { type = "fieldValue", field = "description", @operator = "startsWith", values = new[] { "alpha" } } },
        { "金額絕對值大於或等於", new { type = "fieldValue", field = "amount", @operator = "greaterThanOrEqual", amountBasis = "absolute", value = "100" } },
        { "金額整數尾數符合", new { type = "fieldValue", field = "amount", @operator = "endsWithDigits", amountBasis = "absolute", values = new[] { "000" } } },
        { "日期介於", new { type = "fieldValue", field = "glDate", @operator = "between", from = "2025-03-01", to = "2025-03-31" } },
        { "日期在週末", new { type = "fieldValue", field = "glDate", @operator = "isWeekend" } },
        { "攸關資料元素文字包含", new { type = "fieldValue", fieldId = RdeTextId, @operator = "contains", values = new[] { "tag" } } },
        { "整張傳票任一分錄符合", new { type = "voucher", quantifier = "any", conditions = new object[] {
            new { type = "fieldValue", field = "description", @operator = "contains", values = new[] { "beta" } } } } },
        { "僅借方", new { type = "drCrOnly", drCr = "debit" } }
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public async Task RunnableCondition_SelectsTheSameEntriesAsTheFilter(string label, object rule)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync("sqlite", Seed);
        var expected = await VoucherConditionSqlSemanticsTests.HitsAsync(fixture, rule, Context);

        var condition = fixture.Vouchers!.BuildConditionSql(VoucherConditionSqlSemanticsTests.Scenario(rule), Context);
        var runnable = FilterConditionSqlRenderer.Runnable(condition);
        var actual = await fixture.QueryTextsAsync(
            "SELECT COALESCE(g.voucher_number, '<null>') || '|' || g.line_item " + runnable);

        Assert.True(expected.Length > 0, $"{label}：合成資料應該至少命中一筆，否則這個比對沒有意義。");
        Assert.Equal(expected, actual.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("@p", runnable, StringComparison.Ordinal);
    }

    private const string Seed = """
        INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,account_number,description,
            amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded) VALUES
        ('b9',1,'V1','1','2025-03-01','A',' Alpha fee ',10000,10000,0,'DEBIT',0),
        ('b9',2,'V1','2','2025-03-01','B','alpha fee',-10000,0,10000,'CREDIT',0),
        ('b9',3,'V2','1','2025-03-03','A',NULL,2500000,2500000,0,'DEBIT',0),
        ('b9',4,'V2','2','2025-03-03','B','   ',-2500000,0,2500000,'CREDIT',0),
        ('b9',5,'V3','1','2025-04-07','A','O''Brien rent 100%',300,300,0,'DEBIT',0),
        ('b9',6,'V3','2','2025-04-07','B','beta',-300,0,300,'CREDIT',0),
        ('b9',7,'V4','1','2025-03-01','A','alpha fee',10000,10000,0,'DEBIT',1);
        INSERT INTO gl_rde_value(entry_id,field_id,value_type,text_value)
        SELECT entry_id,'rde.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','text','Tag ' || voucher_number
        FROM gl WHERE voucher_number IN ('V1','V4');
        """;
}
