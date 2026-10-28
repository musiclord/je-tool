using System.Text.Json;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 預篩選收斂計畫階段九（使用者 2026-10-22 裁定「一條規則一個定義」）：十四條預篩選規則在 SQLite 與 DuckDB 各有一份固定答案。
/// 同一個合成母體，第四步 prescreen.run、第五步「預篩選」分頁的條件，以及常用查核條件與 KCT 卡用欄位條件寫的版本，都要算出同一個數字；
/// 以前的等價測試只比對兩種寫法相等，這裡改成每種寫法都對照寫死的答案。
/// 第一次執行：收據 20261024-190913880-a45e823f31e745139f977c5c3481bdb3（這一則在改程式前就通過，固定答案不是由程式算出來的，見檔內的母體說明）。
/// </summary>
public sealed class PrescreenRuleFixedAnswerTests
{
    // 母體：18 張傳票 36 列，查核期間 2025 全年，期末財報準備日 2025-12-01，假日只有 2025-01-27（星期一），週末是週六與週日。
    // V01 2025-01-06（一）核准 2025-12-15：現金借 1,000,000／銷貨收入貸 1,000,000，摘要「調整 Alpha」，E01
    //     → 財報準備日起核准、摘要特定描述、尾數六個 0 各 2 列；現金是正常對方科目，不算未預期借貸組合。
    // V02 2025-01-04（六）核准 2025-01-05（日）：應收借 999／銷貨收入貸 999，「進貨」，E02 → 週末過帳、週末核准各 2 列。
    // V03 2025-01-08（三）核准 2025-01-27（假日）：費用借 100,000／應付費用貸 100,000，摘要空白與全空白，E03
    //     → 假日核准、空白摘要各 2 列；100,000 只有五個 0，不算尾數六個 0。
    // V04 2025-01-27（假日）核准 2025-01-28：費用借 500／銷貨收入貸 500，「服務收入」，zz（不在授權名單）
    //     → 假日過帳 2 列、非授權編製人員 2 列、未預期借貸組合 1 列（收入貸方，同張傳票只有費用借方）。
    // V05 到 V16（12 張）2025-02-03（一）：薪資借 1／應付薪資貸 1，「一般」，E04 → E04 有 24 列、6101 與 2201 各 12 列，都不算低頻。
    // V17 2025-02-04：現金借 3／應付費用貸 3，「雜項」，E05。V18 2025-02-05：現金借 2／應付費用貸 2，「未填人員」，建立人員空白。
    // 低頻人員（11 筆以下，空白人員不列入）：E01、E02、E03、zz、E05 各 2 列 → 10 列。
    // 低頻科目（11 筆以下）：1101 三列、4101 三列、1170 一列、5101 兩列、2209 三列 → 12 列，5 個科目。
    // 編製人員數：E01 到 E05、zz，加上空白一組 → 7。
    private static readonly Dictionary<string, long> RowTagAnswers = new(StringComparer.Ordinal)
    {
        ["postPeriodApproval"] = 2,
        ["suspiciousKeywords"] = 2,
        ["unexpectedAccountPair"] = 1,
        ["trailingZeros"] = 2,
        ["weekendPosting"] = 2,
        ["weekendApproval"] = 2,
        ["holidayPosting"] = 2,
        ["holidayApproval"] = 2,
        ["blankDescription"] = 2,
        ["nonAuthorizedPreparer"] = 2,
        ["lowFrequencyPreparer"] = 10,
        ["lowFrequencyAccount"] = 12
    };

    private const long LowFrequencyAccounts = 5;
    private const long PreparerGroups = 7;

    private static readonly string[] SuspiciousKeywords =
    [
        "ADJ", "REV", "RECLASS", "SUSPENSE", "ERROR", "WRONG",
        "調整", "迴轉", "沖銷", "重分類", "避險", "重編", "錯誤", "計畫外", "預算外", "帳外",
        "调整", "回转", "冲销", "重分类", "避险", "重编", "错误", "计画外", "预算外"
    ];

    private static IEnumerable<object?[]> Rows()
    {
        yield return ["V01", "2025-01-06", "2025-12-15", "1101", "現金", "調整 Alpha", "1000000.00", 1, "E01"];
        yield return ["V01", "2025-01-06", "2025-12-15", "4101", "銷貨收入", "調整 Alpha", "1000000.00", 0, "E01"];
        yield return ["V02", "2025-01-04", "2025-01-05", "1170", "應收帳款", "進貨", "999.00", 1, "E02"];
        yield return ["V02", "2025-01-04", "2025-01-05", "4101", "銷貨收入", "進貨", "999.00", 0, "E02"];
        yield return ["V03", "2025-01-08", "2025-01-27", "5101", "費用", "", "100000.00", 1, "E03"];
        yield return ["V03", "2025-01-08", "2025-01-27", "2209", "應付費用", "   ", "100000.00", 0, "E03"];
        yield return ["V04", "2025-01-27", "2025-01-28", "5101", "費用", "服務收入", "500.00", 1, "zz"];
        yield return ["V04", "2025-01-27", "2025-01-28", "4101", "銷貨收入", "服務收入", "500.00", 0, "zz"];
        for (var i = 5; i <= 16; i++)
        {
            var doc = $"V{i:00}";
            yield return [doc, "2025-02-03", "2025-02-03", "6101", "薪資", "一般", "1.00", 1, "E04"];
            yield return [doc, "2025-02-03", "2025-02-03", "2201", "應付薪資", "一般", "1.00", 0, "E04"];
        }
        yield return ["V17", "2025-02-04", "2025-02-04", "1101", "現金", "雜項", "3.00", 1, "E05"];
        yield return ["V17", "2025-02-04", "2025-02-04", "2209", "應付費用", "雜項", "3.00", 0, "E05"];
        yield return ["V18", "2025-02-05", "2025-02-05", "1101", "現金", "未填人員", "2.00", 1, ""];
        yield return ["V18", "2025-02-05", "2025-02-05", "2209", "應付費用", "未填人員", "2.00", 0, ""];
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task EveryRule_HitsTheFixedAnswer_FromStepFourStepFiveAndTheFieldConditionForms(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            builder.WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "建立人員");
            foreach (var row in Rows()) builder.AddRow(row);
        }, lastPeriodStart: "2025-12-01", holidays: ["2025-01-27"], databaseProvider: provider, validateForDownstream: true);
        await ImportAccountMappingAsync(host);
        await ImportAuthorizedPreparersAsync(host);

        // 第四步：每條規則的計數。
        var prescreen = await host.DispatchAsync("prescreen.run");
        Expect("prescreen.run postPeriodApproval", RowTagAnswers["postPeriodApproval"], Count(prescreen, "postPeriodApproval"));
        Expect("prescreen.run suspiciousKeywords", RowTagAnswers["suspiciousKeywords"], Count(prescreen, "suspiciousKeywords"));
        Expect("prescreen.run unexpectedAccountPair", RowTagAnswers["unexpectedAccountPair"], Count(prescreen, "unexpectedAccountPair"));
        Expect("prescreen.run trailingZeros", RowTagAnswers["trailingZeros"], Count(prescreen, "trailingZeros"));
        Expect("prescreen.run weekendPosting", RowTagAnswers["weekendPosting"], Side(prescreen, "weekendActivity", "postingCount"));
        Expect("prescreen.run weekendApproval", RowTagAnswers["weekendApproval"], Side(prescreen, "weekendActivity", "approvalCount"));
        Expect("prescreen.run holidayPosting", RowTagAnswers["holidayPosting"], Side(prescreen, "holidayActivity", "postingCount"));
        Expect("prescreen.run holidayApproval", RowTagAnswers["holidayApproval"], Side(prescreen, "holidayActivity", "approvalCount"));
        Expect("prescreen.run blankDescription", RowTagAnswers["blankDescription"], Count(prescreen, "blankDescription"));
        Expect("prescreen.run nonAuthorizedPreparer", RowTagAnswers["nonAuthorizedPreparer"], Count(prescreen, "nonAuthorizedPreparer"));
        Expect("prescreen.run lowFrequencyPreparer", RowTagAnswers["lowFrequencyPreparer"], Count(prescreen, "lowFrequencyPreparer"));
        Expect("prescreen.run lowFrequencyAccount", RowTagAnswers["lowFrequencyAccount"], Count(prescreen, "lowFrequencyAccount"));
        Expect("prescreen.run rareAccounts.lowFrequencyAccountCount", LowFrequencyAccounts,
            prescreen.GetProperty("rareAccounts").GetProperty("lowFrequencyAccountCount").GetInt64());
        Expect("prescreen.run creatorSummary.totalPreparerCount", PreparerGroups,
            prescreen.GetProperty("creatorSummary").GetProperty("totalPreparerCount").GetInt64());

        // 第五步「預篩選」分頁：同一條規則當成條件。
        foreach (var (key, expected) in RowTagAnswers)
        {
            Expect("filter.preview prescreen " + key, expected, await PreviewCountAsync(host, new { type = "prescreen", prescreenKey = key }));
        }

        // 常用查核條件與 KCT 卡用欄位條件寫的版本，以及低頻人員與低頻科目的統計條件寫法：答案相同。
        var endsWithSixZeros = new { type = "fieldValue", field = "amount", operator_ = "endsWithDigits", values = new[] { "000000" }, amountBasis = "absolute" };
        var forms = new (string Label, object Rule, long Expected)[]
        {
            ("摘要含特定字詞（常用查核條件、KCT F）", Rule(new { type = "fieldValue", field = "description", operator_ = "contains", values = SuspiciousKeywords }), RowTagAnswers["suspiciousKeywords"]),
            ("空白摘要（常用查核條件、KCT G）", Rule(new { type = "fieldValue", field = "description", operator_ = "isBlank" }), RowTagAnswers["blankDescription"]),
            ("金額尾數連續六個 0（KCT H）", Rule(endsWithSixZeros), RowTagAnswers["trailingZeros"]),
            ("金額尾數連續六個 0 加金額大於或等於 1（常用查核條件）", new
            {
                type = "group",
                rules = new object[] { Rule(endsWithSixZeros), Rule(new { type = "fieldValue", field = "amount", operator_ = "greaterThanOrEqual", value = "1", amountBasis = "absolute" }) }
            }, RowTagAnswers["trailingZeros"]),
            ("總帳日期在週末（常用查核條件）", Rule(new { type = "fieldValue", field = "postDate", operator_ = "isWeekend" }), RowTagAnswers["weekendPosting"]),
            ("傳票核准日在週末", Rule(new { type = "fieldValue", field = "docDate", operator_ = "isWeekend" }), RowTagAnswers["weekendApproval"]),
            ("總帳日期在國定假日（常用查核條件）", Rule(new { type = "fieldValue", field = "postDate", operator_ = "isHoliday" }), RowTagAnswers["holidayPosting"]),
            ("傳票核准日在國定假日", Rule(new { type = "fieldValue", field = "docDate", operator_ = "isHoliday" }), RowTagAnswers["holidayApproval"]),
            ("編製分錄較少的人員（統計條件）", new { type = "entityFrequency", field = "createBy", countUnit = "entries", countOperator = "lessThanOrEqual", countFrom = 11 }, RowTagAnswers["lowFrequencyPreparer"]),
            ("較少使用的科目（統計條件）", new { type = "entityFrequency", field = "accNum", countUnit = "entries", countOperator = "lessThanOrEqual", countFrom = 11 }, RowTagAnswers["lowFrequencyAccount"])
        };
        foreach (var form in forms)
        {
            Expect("filter.preview " + form.Label, form.Expected, await PreviewCountAsync(host, form.Rule));
        }
    }

    private static void Expect(string label, long expected, long actual) =>
        Assert.True(expected == actual, $"{label}：固定答案 {expected}，實際 {actual}");

    private static long Count(JsonElement prescreen, string key) =>
        prescreen.GetProperty(key).GetProperty("count").GetInt64();

    private static long Side(JsonElement prescreen, string activity, string side) =>
        prescreen.GetProperty(activity).GetProperty(side).GetInt64();

    /// <summary>匿名型別不能用 operator 當屬性名，寫成 operator_ 再換成 wire 的 operator。</summary>
    private static Dictionary<string, object?> Rule(object anonymous)
    {
        var rule = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in anonymous.GetType().GetProperties())
        {
            rule[property.Name == "operator_" ? "operator" : property.Name] = property.GetValue(anonymous);
        }
        return rule;
    }

    private static async Task<long> PreviewCountAsync(HandlerTestHost host, object rule)
    {
        var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new
        {
            scenario = new
            {
                name = "固定答案",
                rationale = "階段九的同一個數字",
                groups = new object[] { new { join = "AND", rules = new[] { rule } } }
            }
        }));
        return preview.GetProperty("scenario").GetProperty("count").GetInt64();
    }

    private static async Task ImportAccountMappingAsync(HandlerTestHost host)
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "科目代號"; sheet.Cell(1, 2).Value = "科目名稱"; sheet.Cell(1, 3).Value = "標準化分類";
            var rows = new (string Code, string Name, string Category)[]
            {
                ("1101", "現金", "Cash"), ("1170", "應收帳款", "Receivables"), ("4101", "銷貨收入", "Revenue"),
                ("5101", "費用", ""), ("2209", "應付費用", ""), ("6101", "薪資", ""), ("2201", "應付薪資", "")
            };
            for (var i = 0; i < rows.Length; i++)
            {
                sheet.Cell(i + 2, 1).Value = rows[i].Code; sheet.Cell(i + 2, 2).Value = rows[i].Name; sheet.Cell(i + 2, 3).Value = rows[i].Category;
            }
        });
        try { await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path })); }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    private static async Task ImportAuthorizedPreparersAsync(HandlerTestHost host)
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "姓名"; sheet.Cell(1, 2).Value = "員工代碼";
            var codes = new[] { "E01", "E02", "E03", "E04", "E05" };
            for (var i = 0; i < codes.Length; i++) { sheet.Cell(i + 2, 1).Value = "合成人員"; sheet.Cell(i + 2, 2).Value = codes[i]; }
        });
        try
        {
            await host.DispatchAsync("import.authorizedPreparer.fromFile",
                JsonSerializer.Serialize(new { filePath = path, sourceColumn = "員工代碼" }));
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }
}
