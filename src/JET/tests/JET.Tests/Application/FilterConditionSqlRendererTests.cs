using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 資料庫條件式的文字：由實際送進資料庫的條件轉出，參數換成值，直接來自 GL 檔的欄位寫成 GL 檔的欄名，
/// JET 算出來的欄位與用到的資料表在下方說明。欄名與值都是合成的。
/// </summary>
public sealed class FilterConditionSqlRendererTests
{
    private const string RdeTextId = "rde.cccccccccccccccccccccccccccccccc";

    private static readonly FilterConditionContext Context = new(100, null, "2025-01-01", "2025-12-31")
    { RdeFields = [new(RdeTextId, "合成自訂欄", "自訂文字", "text")] };

    private static readonly Dictionary<string, string> Mapping = new(StringComparer.Ordinal)
    {
        ["voucherNumber"] = "傳票號碼", ["glDate"] = "過帳日", ["accountNumber"] = "科目代號",
        ["accountName"] = "科目名稱", ["description"] = "摘要", ["amount"] = "金額"
    };

    private static string Render(object rule, IReadOnlyDictionary<string, string>? mapping = null) =>
        FilterConditionSqlRenderer.Render(
            FilterVoucherPageReader.ConditionSql(SqliteDialect.Instance, "", VoucherConditionSqlSemanticsTests.Scenario(rule), Context),
            mapping ?? Mapping, Context.RdeFields, AccountCategoryBuiltIns.All, Context.MoneyScale);

    [Fact]
    public void TextNotInList_ShowsSourceColumnValuesAndBlankHandling()
    {
        var text = Render(new
        {
            type = "fieldValue", field = "description", @operator = "notIn",
            values = new[] { "甲部門費用報銷", "乙部門費用報銷" }, includeBlank = true
        });

        Assert.Equal(string.Join("\n",
            "FROM gl g",
            "WHERE g.is_excluded = 0",
            "  AND (((CASE",
            "          WHEN (([摘要] IS NULL OR TRIM([摘要]) = '')) THEN 1",
            "          WHEN (UPPER(TRIM([摘要])) NOT IN ('甲部門費用報銷', '乙部門費用報銷')) THEN 1",
            "          ELSE 0 END = 1)))",
            "",
            "-- gl：GL 的分錄，一筆一列。方括號裡是 GL 檔的欄名。",
            "-- is_excluded = 0：納入測試的分錄。",
            "-- TRIM：去掉頭尾的空白字元，全形空白、Tab 與換行也算。",
            "-- UPPER：英文字母轉成大寫，所以比較時不分大小寫。"), text);
    }

    [Fact]
    public void BlankNotIncluded_IsVisibleInTheCondition()
    {
        var text = Render(new
        {
            type = "fieldValue", field = "description", @operator = "notContains",
            values = new[] { "合成字" }, includeBlank = false
        });

        // 多個關鍵字用 OR 連接後整個加括號，所以「不包含」的 NOT 後面有兩層括號。
        // 空白怎麼處理自成一行：這裡是不列入。
        Assert.Contains("\n          WHEN (([摘要] IS NULL OR TRIM([摘要]) = '')) THEN 0\n", text, StringComparison.Ordinal);
        Assert.Contains("\n          WHEN (NOT ((instr(UPPER(COALESCE([摘要], '')), '合成字') > 0))) THEN 1\n          ELSE 0 END = 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesAreQuotedAndNeverRewritten()
    {
        var text = Render(new
        {
            type = "fieldValue", field = "description", @operator = "in",
            values = new[] { "o'brien @p9 g.description" }
        });

        Assert.Contains("IN ('O''BRIEN @P9 G.DESCRIPTION')", text, StringComparison.Ordinal);
        Assert.DoesNotContain("@p0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AmountAndDate_KeepComputedColumnsAndExplainThem()
    {
        var amount = Render(new
        {
            type = "fieldValue", field = "amount", @operator = "greaterThanOrEqual", amountBasis = "absolute", value = "100000"
        });
        Assert.Contains("ABS(g.amount_scaled) >= 10000000", amount, StringComparison.Ordinal);
        Assert.Contains("-- amount_scaled：分錄金額乘以 100 的整數，借方為正、貸方為負。", amount, StringComparison.Ordinal);

        var date = Render(new { type = "fieldValue", field = "glDate", @operator = "between", from = "2025-03-01", to = "2025-03-31" });
        Assert.Contains("[過帳日] >= '2025-03-01'", date, StringComparison.Ordinal);
        Assert.Contains("-- 日期：JET 讀入時整理成 yyyy-MM-dd 的文字再比較。", date, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnWithoutSourceColumn_KeepsJetNameAndSaysWhereItComesFrom()
    {
        var text = Render(new { type = "fieldValue", field = "approvalDate", @operator = "on", value = "2025-03-01" });

        Assert.Contains("g.approval_date", text, StringComparison.Ordinal);
        Assert.Contains("-- approval_date：傳票核准日。GL 檔沒有配對這一欄，JET 用總帳日期。", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WholeVoucherCondition_RenamesColumnsInsideTheSubquery()
    {
        var text = Render(new
        {
            type = "voucher", quantifier = "any",
            conditions = new object[] { new { type = "fieldValue", field = "accountNumber", @operator = "in", values = new[] { "1101" } } }
        });

        // 子查詢裡用 AND 連接的條件也各占一行，所以這裡把換行與縮排收成一個空白再比對。
        var oneLine = System.Text.RegularExpressions.Regex.Replace(text, @"\n +(AND|OR|WHEN|ELSE) ", " $1 ");
        Assert.Contains("[傳票號碼] IN (SELECT [傳票號碼] FROM gl g WHERE [傳票號碼] IS NOT NULL AND g.is_excluded = 0 AND (", oneLine, StringComparison.Ordinal);
        Assert.Contains("UPPER(TRIM([科目代號])) IN ('1101')", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RdeField_ExplainsWhichSourceColumnTheFieldIdMeans()
    {
        var text = Render(new { type = "fieldValue", fieldId = RdeTextId, @operator = "contains", values = new[] { "合成字" } });

        Assert.Contains($"v.field_id = '{RdeTextId}'", text, StringComparison.Ordinal);
        Assert.Contains("-- gl_rde_value：攸關資料元素的值，一筆分錄的一個欄位一列。", text, StringComparison.Ordinal);
        Assert.Contains($"-- field_id '{RdeTextId}'：GL 檔的 [合成自訂欄]。", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountCategoryCondition_ExplainsTablesAndCategoryIds()
    {
        var text = Render(new
        {
            type = "accountSide", categoryMode = "is", categoryIds = new[] { AccountCategoryBuiltIns.CashId }, categorySelection = "node"
        });

        Assert.Contains("m.account_number = [科目代號]", text, StringComparison.Ordinal);
        Assert.Contains("-- account_mapping：科目配對，每個會計科目編號配到的科目類別。", text, StringComparison.Ordinal);
        Assert.Contains($"-- 科目類別 '{AccountCategoryBuiltIns.CashId}'：Cash", text, StringComparison.Ordinal);
    }

    [Fact]
    public void JoinedConditions_BreakOntoIndentedLinesWithoutChangingAnythingElse()
    {
        // 多個條件用 AND、OR 連接時各占一行，縮排跟著括號深度；CASE 裡每個 WHEN 與 ELSE 各占一行，
        // 裡面的 OR、BETWEEN 的 AND 與值的內容不換行。
        var condition = new FilterConditionSql(
            "FROM gl g\nWHERE g.is_excluded = 0\n  AND (((CASE WHEN (g.is_manual = 1 OR g.is_manual = 0) THEN 1 ELSE 0 END = 1)"
            + " AND (g.entry_id BETWEEN @p0 AND @p1)) OR (g.dr_cr = @p2))",
            [new("@p0", 1L), new("@p1", 9L), new("@p2", "甲 AND 乙 OR (丙")]);

        var text = FilterConditionSqlRenderer.Render(condition, Mapping, Context.RdeFields, AccountCategoryBuiltIns.All, Context.MoneyScale);

        Assert.Equal(string.Join("\n",
            "FROM gl g",
            "WHERE g.is_excluded = 0",
            "  AND (((CASE",
            "          WHEN (g.is_manual = 1 OR g.is_manual = 0) THEN 1",
            "          ELSE 0 END = 1)",
            "      AND (g.entry_id BETWEEN 1 AND 9))",
            "    OR (g.dr_cr = '甲 AND 乙 OR (丙'))"), text[..text.IndexOf("\n\n-- ", StringComparison.Ordinal)]);
    }

    [Fact]
    public void SourceColumnNameWithBracket_IsEscaped()
    {
        var mapping = new Dictionary<string, string>(Mapping, StringComparer.Ordinal) { ["description"] = "摘要]備註" };
        var text = Render(new { type = "fieldValue", field = "description", @operator = "isBlank" }, mapping);

        Assert.Contains("[摘要]]備註] IS NULL", text, StringComparison.Ordinal);
    }
}
