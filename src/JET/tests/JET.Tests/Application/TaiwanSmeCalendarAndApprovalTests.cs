using Xunit;
using static JET.Tests.Application.TaiwanSmeScenarioSupport;

namespace JET.Tests.Application;

/// <summary>
/// 台灣 2025 年行事曆與財報準備日：週末、國定假日、春節連假、2025-02-08 補班日，
/// 以及核准日在財報準備日當日或之後的分錄。假日清單只匯入平日的國定假日，週末交給週末判斷；
/// 預期值依 docs/jet-guide.md 第 5、6 節（非營業日 = 週末或假日，再排除補班日）手算。
/// </summary>
public sealed class TaiwanSmeCalendarAndApprovalTests
{
    // 2025 年落在平日的國定假日：元旦、春節（1/27 調整放假至 1/31）、和平紀念日、兒童節與清明節、勞動節、
    // 端午節補假、中秋節、國慶日。春節連假的 1/25、1/26、2/1、2/2 是週末，由週末判斷涵蓋。
    private static readonly string[] Holidays2025 =
    [
        "2025-01-01", "2025-01-27", "2025-01-28", "2025-01-29", "2025-01-30", "2025-01-31",
        "2025-02-28", "2025-04-03", "2025-04-04", "2025-05-01", "2025-05-30", "2025-10-06", "2025-10-10"
    ];

    // 1/27 調整放假的補班日是 2025-02-08（星期六）。
    private static readonly string[] MakeupDays2025 = ["2025-02-08"];

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Taiwan2025Calendar_WeekendHolidayLunarNewYearAndMakeupSaturday(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, b =>
        {
            b.WithColumns("傳票號碼", "傳票項次", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標");
            void Voucher(string doc, string postDate, string approvalDate, string description)
            {
                b.AddRow(doc, "1", postDate, approvalDate, "6112", "租金支出", description, 1000, 1);
                b.AddRow(doc, "2", postDate, approvalDate, "1102", "銀行存款", description, 1000, 0);
            }

            Voucher("C1", "2025-01-02", "2025-01-02", "一般工作日");          // 星期四，工作日
            Voucher("C2", "2025-01-01", "2025-01-02", "元旦過帳");            // 星期三，國定假日
            Voucher("C3", "2025-01-25", "2025-02-03", "春節連假首日過帳");     // 星期六，春節連假
            Voucher("C4", "2025-01-29", "2025-01-29", "大年初一過帳及核准");   // 星期三，國定假日
            Voucher("C5", "2025-02-08", "2025-02-08", "補班日過帳及核准");     // 星期六，補班日
            Voucher("C6", "2025-02-10", "2025-02-09", "週日核准");            // 過帳星期一，核准星期日
            Voucher("C7", "2025-02-28", "2025-03-03", "和平紀念日過帳");       // 星期五，國定假日
            Voucher("C8", "2025-10-11", "2025-10-13", "國慶連假週六過帳");     // 星期六
            Voucher("C9", "2025-03-14", "2025-03-15", "週六核准");            // 過帳星期五，核准星期六
        }, holidays: Holidays2025, makeupDays: MakeupDays2025, databaseProvider: provider, validateForDownstream: true);

        await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var weekend = prescreen.GetProperty("weekendActivity");
        // 週末過帳依星期判斷，補班日仍算週末：C3（1/25）、C5（2/8）、C8（10/11），各兩列共 6 列。
        Assert.Equal(6, weekend.GetProperty("postingCount").GetInt64());
        // 週末核准：C5（2/8 星期六）、C6（2/9 星期日）、C9（3/15 星期六），共 6 列。
        Assert.Equal(6, weekend.GetProperty("approvalCount").GetInt64());
        var holiday = prescreen.GetProperty("holidayActivity");
        // 假日過帳：C2（元旦）、C4（初一）、C7（和平紀念日），共 6 列。
        Assert.Equal(6, holiday.GetProperty("postingCount").GetInt64());
        // 假日核准只有 C4（1/29），共 2 列。
        Assert.Equal(2, holiday.GetProperty("approvalCount").GetInt64());

        var kctI = Scenario("KCT I 非營業日", """
            [{"join":"AND","matchScope":"row","rules":[
              {"type":"group","rules":[{"type":"fieldValue","field":"postDate","operator":"isNonBusinessDay","join":"AND"}]}]}]
            """);
        var approvalNonBusiness = OneRule("核准日在非營業日",
            """{"type":"fieldValue","field":"docDate","operator":"isNonBusinessDay"}""");
        var makeup = OneRule("補班日過帳", """{"type":"fieldValue","field":"postDate","operator":"isMakeupDay"}""");
        var weekendPosting = OneRule("週末過帳", """{"type":"fieldValue","field":"postDate","operator":"isWeekend"}""");
        var lunarNewYear = Scenario("春節連假期間的非營業日過帳", """
            [{"join":"AND","matchScope":"row","rules":[
              {"type":"fieldValue","field":"postDate","operator":"between","from":"2025-01-25","to":"2025-02-02"},
              {"type":"fieldValue","field":"postDate","operator":"isNonBusinessDay","join":"AND"}]}]
            """);
        var overtimeException = Scenario("非營業日但排除核定加班日", """
            [{"join":"AND","matchScope":"row","rules":[
              {"type":"fieldValue","field":"postDate","operator":"isNonBusinessDay"},
              {"type":"fieldValue","field":"postDate","operator":"notIn","values":["2025-10-11"],"join":"AND"}]}]
            """);
        var hits = await CommitAndReadHitsAsync(host, kctI, approvalNonBusiness, makeup, weekendPosting, lunarNewYear, overtimeException);
        // KCT I：週末或假日再排除補班日 → C2、C3、C4、C7、C8；C5 是補班日，不算非營業日。
        Assert.Equal(Keys("C2:1,2;C3:1,2;C4:1,2;C7:1,2;C8:1,2"), hits[0]);
        // 核准日在非營業日：C4（初一）、C6（週日）、C9（週六）；C5 的 2/8 是補班日，不算。
        Assert.Equal(Keys("C4:1,2;C6:1,2;C9:1,2"), hits[1]);
        // 補班日過帳只有 C5。
        Assert.Equal(Keys("C5:1,2"), hits[2]);
        // 單看週末仍包含補班日：C3、C5、C8。
        Assert.Equal(Keys("C3:1,2;C5:1,2;C8:1,2"), hits[3]);
        // 春節連假 1/25 到 2/2 之間的非營業日過帳：C3（週六）、C4（初一）。
        Assert.Equal(Keys("C3:1,2;C4:1,2"), hits[4]);
        // 非營業日且不在核定加班日 10/11：KCT I 的五張去掉 C8。
        Assert.Equal(Keys("C2:1,2;C3:1,2;C4:1,2;C7:1,2"), hits[5]);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ApprovalOnOrAfterFinancialStatementPreparationDate_IncludesTheBoundaryDay(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, b =>
        {
            b.WithColumns("傳票號碼", "傳票項次", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標");
            void Voucher(string doc, string postDate, string? approvalDate, string description)
            {
                b.AddRow(doc, "1", postDate, approvalDate, "6112", "租金支出", description, 5000, 1);
                b.AddRow(doc, "2", postDate, approvalDate, "2171", "應付費用", description, 5000, 0);
            }

            Voucher("A0", "2025-01-02", "2024-12-31", "期初核准");             // 核准早於查核期間
            Voucher("A1", "2025-12-30", "2025-12-30", "年底正常核准");
            Voucher("A2", "2025-12-31", "2026-01-14", "準備日前一天核准");
            Voucher("A3", "2025-12-31", "2026-01-15", "準備日當天核准");
            Voucher("A4", "2025-12-31", "2026-02-10", "查核期間後補核准");
            Voucher("A5", "2025-11-28", null, "尚未核准");
            Voucher("A6", "2026-01-20", "2026-01-20", "次年度分錄");          // 總帳日期在期外，不在 GL
        }, lastPeriodStart: "2026-01-15", databaseProvider: provider, validateForDownstream: true);

        var validation = await host.DispatchAsync("validate.run");
        // 核准日不在查核期間：A0（2024-12-31）、A2、A3、A4（2026 年），各兩列共 8 列；A5 空白不計，A6 不在 GL。
        Assert.Equal(8, validation.GetProperty("nullRecordsTest").GetProperty("outOfRangeDateCount").GetInt64());
        var prescreen = await host.DispatchAsync("prescreen.run");
        // 財報準備日起核准：核准日 >= 2026-01-15，即 A3（當天）、A4，共 4 列；A2 早一天不算。
        Assert.Equal(4, prescreen.GetProperty("postPeriodApproval").GetProperty("count").GetInt64());

        var hits = await CommitAndReadHitsAsync(host,
            OneRule("財報準備日起核准", """{"type":"prescreen","prescreenKey":"postPeriodApproval"}"""),
            OneRule("核准日當日或以後", """{"type":"fieldValue","field":"docDate","operator":"onOrAfter","value":"2026-01-15"}"""),
            OneRule("核准日當日或以後含空白", """{"type":"fieldValue","field":"docDate","operator":"onOrAfter","value":"2026-01-15","includeBlank":true}"""));
        Assert.Equal(Keys("A3:1,2;A4:1,2"), hits[0]);
        // 欄位條件結果相同；核准日空白的 A5 預設不列入。
        Assert.Equal(Keys("A3:1,2;A4:1,2"), hits[1]);
        // 勾「空白也符合」後加入 A5。
        Assert.Equal(Keys("A3:1,2;A4:1,2;A5:1,2"), hits[2]);
    }
}
