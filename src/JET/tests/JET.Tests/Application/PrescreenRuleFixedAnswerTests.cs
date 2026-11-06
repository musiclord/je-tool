using System.Text.Json;
using ClosedXML.Excel;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 預篩選規則照「一條規則一個定義」（裁定 2026-10-22-13），在 SQLite 與 DuckDB 各有一份固定答案。
/// 裁定 2026-10-29-27：第四步照舊版拿掉編製分錄較少的人員與使用較少的科目兩條規則，現在是十二條；
/// 第五步依審計目的的兩個統計條件仍預設 11 筆以下，下面照舊用欄位條件寫法核對它們的固定答案。
/// 同一個合成母體，第四步 prescreen.run、第五步「預篩選」分頁的條件，以及依審計目的條件與常用篩選條件卡片用欄位條件寫的版本，都要算出同一個數字；
/// 以前的等價測試只比對兩種寫法相等，這裡改成每種寫法都對照寫死的答案。
/// 第一次執行：收據 20261024-190913880-a45e823f31e745139f977c5c3481bdb3（這一則在改程式前就通過，固定答案不是由程式算出來的，見檔內的母體說明）。
/// </summary>
public sealed class PrescreenRuleFixedAnswerTests
{
    // 母體：18 張傳票 36 列，查核期間 2025 全年，期末財報準備日 2025-12-01，假日只有 2025-01-27（星期一），週末是週六與週日。
    // V01 2025-01-06（一）核准 2025-12-15：現金借 1,000,000／銷貨收入貸 1,000,000，摘要「調整 Alpha」，E01
    //     → 財報準備日起核准、摘要特定描述、金額尾數連續 0 各 2 列；現金是正常對方科目，不算未預期借貸組合。
    // V02 2025-01-04（六）核准 2025-01-05（日）：應收借 999／銷貨收入貸 999，「進貨」，E02 → 週末過帳、週末核准各 2 列。
    // V03 2025-01-08（三）核准 2025-01-27（假日）：費用借 100,000／應付費用貸 100,000，摘要空白與全空白，E03
    //     → 假日核准、空白摘要各 2 列；100,000 有五個 0，下面算出的 4 個 0 也符合，但不算尾數六個 0。
    // V04 2025-01-27（假日）核准 2025-01-28：費用借 500／銷貨收入貸 500，「服務收入」，zz（不在授權名單）
    //     → 假日過帳 2 列、非授權編製人員 2 列、未預期借貸組合 1 列（收入貸方，同張傳票只有費用借方）；
    //       第四步照舊版列整張命中傳票，未預期借貸組合是 V04 的 2 列。
    // V05 到 V16（12 張）2025-02-03（一）：薪資借 1／應付薪資貸 1，「一般」，E04 → E04 有 24 列、6101 與 2201 各 12 列，都不算較少使用。
    // V17 2025-02-04：現金借 3／應付費用貸 3，「雜項」，E05。V18 2025-02-05：現金借 2／應付費用貸 2，「未填人員」，建立人員空白。
    // 第五步「編製分錄較少的人員」（預設 11 筆以下，空白人員不列入）：E01、E02、E03、zz、E05 各 2 列 → 10 列。
    // 第五步「較少使用的科目」（預設 11 筆以下）：1101 三列、4101 三列、1170 一列、5101 兩列、2209 三列 → 12 列。
    // 編製人員數：E01 到 E05、zz，加上空白一組 → 7。
    // 尾數 0 的個數照 IDEA 版由 GL 算出（裁定 2026-11-05-25）：借方合計 1,000,000 + 999 + 100,000 + 500 + 12 + 3 + 2 = 1,101,516，
    // 除以 36 列，平均 30,597 是 5 位數，所以是 4 個 0；V01 的 1,000,000 與 V03 的 100,000 共 4 列符合。
    // 改版前固定 6 個 0，只有 V01 的 2 列，固定答案是 2。第一次失敗：Public 收據 20261105-084802022-bb026abaa79c492799abea536c518413。
    private static readonly Dictionary<string, long> RowTagAnswers = new(StringComparer.Ordinal)
    {
        ["prepPeriodApproval"] = 2,
        ["suspiciousKeywords"] = 2,
        ["unexpectedAccountPair"] = 1,
        ["trailingZeros"] = 4,
        ["weekendPosting"] = 2,
        ["weekendApproval"] = 2,
        ["holidayPosting"] = 2,
        ["holidayApproval"] = 2,
        ["blankDescription"] = 2,
        ["nonAuthorizedPreparer"] = 2
        // 第四步照舊版拿掉 rarePreparer 與 rareAccount（裁定 2026-10-29-27；第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad），
        // 兩者的固定答案 10 與 12 改由下面第五步統計條件的寫法核對，第四步的 rareAccountCount 5 跟著拿掉。
    };

    // 第四步卡片照舊版列整張命中傳票：未預期借貸組合是 V04 的 2 列；第五步的條件仍只直接命中收入貸方 1 列。
    // 原本第四步也是 1，第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad。
    private const long UnexpectedAccountPairWholeVoucherRows = 2;
    private const long RarePreparerStepFiveRows = 10;
    private const long RareAccountStepFiveRows = 12;
    private const long PreparerGroups = 7;
    private const int TrailingZeroDigits = 4;
    // 常用篩選條件「特定尾數」卡片預設 000000，仍是 6 個 0，只有 V01 的 2 列；由 GL 算出個數後它不再等於預篩選這條規則。
    private const long SixZeroRows = 2;

    // 和預篩選預設的 24 個摘要關鍵字相同（裁定 2026-11-05-43 拿掉「帳外」）；這組資料沒有含「帳外」的摘要，答案不變。
    private static readonly string[] SuspiciousKeywords =
    [
        "ADJ", "REV", "RECLASS", "SUSPENSE", "ERROR", "WRONG",
        "調整", "迴轉", "沖銷", "重分類", "避險", "重編", "錯誤", "計畫外", "預算外",
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
        }, prepStartDate: "2025-12-01", holidays: ["2025-01-27"], databaseProvider: provider, validateForDownstream: true);
        await ImportAccountMappingAsync(host);
        await ImportAuthorizedPreparersAsync(host);

        // 第四步：每條規則的計數。
        var prescreen = await host.DispatchAsync("prescreen.run");
        Expect("prescreen.run prepPeriodApproval", RowTagAnswers["prepPeriodApproval"], Count(prescreen, "prepPeriodApproval"));
        Expect("prescreen.run suspiciousKeywords", RowTagAnswers["suspiciousKeywords"], Count(prescreen, "suspiciousKeywords"));
        Expect("prescreen.run unexpectedAccountPair", UnexpectedAccountPairWholeVoucherRows, Count(prescreen, "unexpectedAccountPair"));
        Expect("prescreen.run trailingZeros", RowTagAnswers["trailingZeros"], Count(prescreen, "trailingZeros"));
        Expect("prescreen.run trailingZeros.zerosThreshold", TrailingZeroDigits,
            prescreen.GetProperty("trailingZeros").GetProperty("zerosThreshold").GetInt32());
        Expect("prescreen.run weekendPosting", RowTagAnswers["weekendPosting"], Side(prescreen, "weekendActivity", "postingCount"));
        Expect("prescreen.run weekendApproval", RowTagAnswers["weekendApproval"], Side(prescreen, "weekendActivity", "approvalCount"));
        Expect("prescreen.run holidayPosting", RowTagAnswers["holidayPosting"], Side(prescreen, "holidayActivity", "postingCount"));
        Expect("prescreen.run holidayApproval", RowTagAnswers["holidayApproval"], Side(prescreen, "holidayActivity", "approvalCount"));
        Expect("prescreen.run blankDescription", RowTagAnswers["blankDescription"], Count(prescreen, "blankDescription"));
        Expect("prescreen.run nonAuthorizedPreparer", RowTagAnswers["nonAuthorizedPreparer"], Count(prescreen, "nonAuthorizedPreparer"));
        Expect("prescreen.run preparerSummary.totalPreparerCount", PreparerGroups,
            prescreen.GetProperty("preparerSummary").GetProperty("totalPreparerCount").GetInt64());

        // 第五步「預篩選」分頁：同一條規則當成條件。
        foreach (var (key, expected) in RowTagAnswers)
        {
            Expect("filter.preview prescreen " + key, expected, await PreviewCountAsync(host, new { type = "prescreen", prescreenKey = key }));
        }

        // 依審計目的條件與常用篩選條件卡片用欄位條件寫的版本，以及較少使用人員與較少使用科目的統計條件寫法：答案相同。
        var endsWithSixZeros = new { type = "fieldValue", field = "amount", operator_ = "endsWithDigits", values = new[] { "000000" }, amountBasis = "absolute" };
        var forms = new (string Label, object Rule, long Expected)[]
        {
            ("摘要含特定字詞（依審計目的、特定摘要卡片）", Rule(new { type = "fieldValue", field = "description", operator_ = "contains", values = SuspiciousKeywords }), RowTagAnswers["suspiciousKeywords"]),
            ("空白摘要（依審計目的、空白摘要卡片）", Rule(new { type = "fieldValue", field = "description", operator_ = "isBlank" }), RowTagAnswers["blankDescription"]),
            ("金額尾數連續六個 0（特定尾數卡片）", Rule(endsWithSixZeros), SixZeroRows),
            ("金額尾數連續六個 0 加金額大於或等於 1（依審計目的）", new
            {
                type = "group",
                conditions = new object[] { Rule(endsWithSixZeros), Rule(new { type = "fieldValue", field = "amount", operator_ = "greaterThanOrEqual", value = "1", amountBasis = "absolute" }) }
            }, SixZeroRows),
            ("金額尾數連續多個 0，預設由 GL 算出的個數（裁定 2026-11-05-26）", Rule(new { type = "fieldValue", field = "amount", operator_ = "endsWithZeros", value = "4", amountBasis = "absolute" }), RowTagAnswers["trailingZeros"]),
            ("總帳日期在週末（依審計目的）", Rule(new { type = "fieldValue", field = "glDate", operator_ = "isWeekend" }), RowTagAnswers["weekendPosting"]),
            ("傳票核准日在週末", Rule(new { type = "fieldValue", field = "approvalDate", operator_ = "isWeekend" }), RowTagAnswers["weekendApproval"]),
            ("總帳日期在國定假日（依審計目的）", Rule(new { type = "fieldValue", field = "glDate", operator_ = "isHoliday" }), RowTagAnswers["holidayPosting"]),
            ("傳票核准日在國定假日", Rule(new { type = "fieldValue", field = "approvalDate", operator_ = "isHoliday" }), RowTagAnswers["holidayApproval"]),
            ("編製分錄較少的人員（統計條件）", new { type = "entityFrequency", field = "createdBy", countUnit = "entries", countOperator = "lessThanOrEqual", countFrom = 11 }, RarePreparerStepFiveRows),
            ("較少使用的科目（統計條件）", new { type = "entityFrequency", field = "accountNumber", countUnit = "entries", countOperator = "lessThanOrEqual", countFrom = 11 }, RareAccountStepFiveRows)
        };
        foreach (var form in forms)
        {
            Expect("filter.preview " + form.Label, form.Expected, await PreviewCountAsync(host, form.Rule));
        }
    }

    // 未預期借貸組合照舊版 IDEA R3（裁定 2026-10-29-29）：
    // W01 只有收入貸方 700 與應付費用貸方 300，沒有任何正金額分錄 → 第四步、第五步與報告都不挑中。
    // W02 費用借 1,000、收入貸 600、應付費用貸 300 與 100，缺正常對方科目但有正金額分錄 → 挑中；
    //     第五步直接命中的是收入貸方 1 列，第四步、第四步明細與報告列整張傳票 4 列。
    // W03 應收借 200／收入貸 200 有正常對方科目，不挑中。
    // W05 費用借 0／收入貸 50：0 元不算正金額，不挑中。
    // 改程式前的寫法挑中 W01、W02、W05 的收入貸方各 1 列。
    private static IEnumerable<object?[]> PositiveEntryRows()
    {
        yield return ["W01", "2025-03-03", "2025-03-03", "4101", "銷貨收入", "收入", "700.00", 0, "E01"];
        yield return ["W01", "2025-03-03", "2025-03-03", "2209", "應付費用", "收入", "300.00", 0, "E01"];
        yield return ["W02", "2025-03-04", "2025-03-04", "5101", "費用", "收入", "1000.00", 1, "E01"];
        yield return ["W02", "2025-03-04", "2025-03-04", "4101", "銷貨收入", "收入", "600.00", 0, "E01"];
        yield return ["W02", "2025-03-04", "2025-03-04", "2209", "應付費用", "收入", "300.00", 0, "E01"];
        yield return ["W02", "2025-03-04", "2025-03-04", "2209", "應付費用", "收入", "100.00", 0, "E01"];
        yield return ["W03", "2025-03-05", "2025-03-05", "1170", "應收帳款", "收入", "200.00", 1, "E01"];
        yield return ["W03", "2025-03-05", "2025-03-05", "4101", "銷貨收入", "收入", "200.00", 0, "E01"];
        yield return ["W05", "2025-03-06", "2025-03-06", "5101", "費用", "收入", "0.00", 1, "E01"];
        yield return ["W05", "2025-03-06", "2025-03-06", "4101", "銷貨收入", "收入", "50.00", 0, "E01"];
    }

    /// <summary>
    /// 未預期借貸組合要求傳票裡至少有一筆正金額分錄（舊版 idea-tool.bas 第 8380 到 8388 行、第 8434 到 8442 行），
    /// 第四步卡片、第四步明細與預篩選報告的 R3 列整張命中傳票（第 8526 到 8538 行）；第五步的條件仍只直接命中收入貸方。
    /// 第一次失敗：Focused 收據 20261030-162353256-42d10eb5b3cd4899b3b8a0bdf1ea4630。
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task UnexpectedAccountPair_NeedsAPositiveEntry_AndStepFourAndTheReportListTheWholeVoucher(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            builder.WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "建立人員");
            foreach (var row in PositiveEntryRows()) builder.AddRow(row);
        }, prepStartDate: "2025-12-01", databaseProvider: provider, validateForDownstream: true);
        await ImportAccountMappingAsync(host);

        // 第四步卡片：整張 W02 的 4 列，1 張傳票。
        var prescreen = await host.DispatchAsync("prescreen.run");
        Expect("prescreen.run unexpectedAccountPair", 4, Count(prescreen, "unexpectedAccountPair"));
        var period = prescreen.GetProperty("rulePeriod").GetProperty("rules").EnumerateArray()
            .Single(rule => rule.GetProperty("key").GetString() == "unexpectedAccountPair");
        Expect("rulePeriod unexpectedAccountPair hitLines", 4, period.GetProperty("hitLines").GetInt64());
        Expect("rulePeriod unexpectedAccountPair hitVouchers", 1, period.GetProperty("hitVouchers").GetInt64());

        // 第四步明細：和卡片同一批分錄。
        var page = await host.DispatchAsync("query.prescreenPage", JsonSerializer.Serialize(new { ruleKey = "unexpectedAccountPair", pageSize = 50 }));
        Expect("query.prescreenPage totalCount", 4, page.GetProperty("totalCount").GetInt64());
        Assert.All(page.GetProperty("rows").EnumerateArray(), row => Assert.Equal("W02", row.GetProperty("voucherNumber").GetString()));
        Expect("query.prescreenPage rows", 4, page.GetProperty("rows").GetArrayLength());

        // 第五步只用這一個條件：直接命中的仍是收入貸方 1 列，畫面的分錄筆數照現在的規則算整張傳票。
        var preview = await PreviewAsync(host, new { type = "prescreen", prescreenKey = "unexpectedAccountPair" });
        Expect("filter.preview count", 1, preview.GetProperty("count").GetInt64());
        Expect("filter.preview voucherCount", 1, preview.GetProperty("voucherCount").GetInt64());
        Expect("filter.preview summaryRowCount", 4, preview.GetProperty("summaryRowCount").GetInt64());
        Assert.True(preview.GetProperty("countsWholeVoucherRows").GetBoolean());
        var direct = Assert.Single(preview.GetProperty("previewRows").EnumerateArray());
        Assert.Equal("W02", direct.GetProperty("voucherNumber").GetString());
        Assert.Equal("4101", direct.GetProperty("accountNumber").GetString());

        // 預篩選報告：R3 工作表列整張傳票，摘要的傳票張數與明細筆數一致。
        var resultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString();
        var exported = await host.DispatchAsync("export.prescreenReport", JsonSerializer.Serialize(new { resultId }));
        Assert.True(exported.GetProperty("ok").GetBoolean());
        using var workbook = new XLWorkbook(exported.GetProperty("publishedReport").GetProperty("fullPath").GetString()!);
        var detail = workbook.Worksheet("R3").RowsUsed().Skip(1).ToArray();
        Expect("R3 工作表明細列", 4, detail.Length);
        Assert.All(detail, row => Assert.Contains(row.CellsUsed(), cell => cell.GetString() == "W02"));
        var summary = workbook.Worksheet("Pre-screening_Report");
        Expect("摘要 R3 傳票張數", 1, summary.Cell("E10").GetValue<long>());
        Expect("摘要 R3 明細筆數", 4, summary.Cell("F10").GetValue<long>());
        // 裁定 2026-11-05-44：D 欄照舊版原句，筆數看 E、F 欄。第一次失敗：Focused 收據 20261105-105749801-cc9ac6cac3934f2a954ac0e322d4c6f3。
        Assert.Equal("符合預篩選條件之明細項請詳 [R3] 工作表", summary.Cell("D10").GetString());
    }

    /// <summary>
    /// 第四步照舊版拿掉 11 筆以下的人員與科目（裁定 2026-10-29-27）：回應、規則期間分布與報告都沒有這兩條規則，
    /// 依筆數排序的兩份彙總照舊。第五步「依審計目的」的兩項仍預設 11 筆以下，由前端測試核對。
    /// 第一次失敗：Focused 收據 20261030-162353256-42d10eb5b3cd4899b3b8a0bdf1ea4630。
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task StepFour_NoLongerPicksPreparersOrAccountsWithElevenEntriesOrFewer(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            builder.WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "建立人員");
            foreach (var row in Rows()) builder.AddRow(row);
        }, prepStartDate: "2025-12-01", holidays: ["2025-01-27"], databaseProvider: provider, validateForDownstream: true);
        await ImportAccountMappingAsync(host);

        var prescreen = await host.DispatchAsync("prescreen.run");
        Assert.False(prescreen.TryGetProperty("rarePreparer", out _), "第四步回應不再有 rarePreparer");
        Assert.False(prescreen.TryGetProperty("rareAccount", out _), "第四步回應不再有 rareAccount");
        var rareAccounts = prescreen.GetProperty("rareAccounts");
        Assert.False(rareAccounts.TryGetProperty("rareAccountCount", out _), "較少使用之科目不再數 11 筆以下的科目");
        Expect("rareAccounts.distinctAccountCount", 7, rareAccounts.GetProperty("distinctAccountCount").GetInt64());
        Assert.Equal("1170", rareAccounts.GetProperty("accounts")[0].GetProperty("accountNumber").GetString());
        Expect("preparerSummary.totalPreparerCount", PreparerGroups,
            prescreen.GetProperty("preparerSummary").GetProperty("totalPreparerCount").GetInt64());
        var periodKeys = prescreen.GetProperty("rulePeriod").GetProperty("rules").EnumerateArray()
            .Select(rule => rule.GetProperty("key").GetString()).ToArray();
        Assert.DoesNotContain("rarePreparer", periodKeys);
        Assert.DoesNotContain("rareAccount", periodKeys);
        Assert.Equal(10, periodKeys.Length);

        var resultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString();
        var exported = await host.DispatchAsync("export.prescreenReport", JsonSerializer.Serialize(new { resultId }));
        Assert.True(exported.GetProperty("ok").GetBoolean());
        using var workbook = new XLWorkbook(exported.GetProperty("publishedReport").GetProperty("fullPath").GetString()!);
        Assert.True(workbook.Worksheets.Contains("R5"));
        Assert.True(workbook.Worksheets.Contains("R6"));
        Expect("R6 科目列", 7, workbook.Worksheet("R6").RowsUsed().Count() - 1);
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

    private static async Task<long> PreviewCountAsync(HandlerTestHost host, object rule) =>
        (await PreviewAsync(host, rule)).GetProperty("count").GetInt64();

    private static async Task<JsonElement> PreviewAsync(HandlerTestHost host, object rule)
    {
        var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new
        {
            scenario = new
            {
                name = "固定答案",
                rationale = "階段九的同一個數字",
                groups = new object[] { new { join = "AND", conditions = new[] { rule } } }
            }
        }));
        return preview.GetProperty("scenario");
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
