using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;
using static JET.Tests.Infrastructure.WorkingPaperWriterTestSupport;

namespace JET.Tests.Infrastructure;

/// <summary>底稿寫出器：step1 家族（完整性、借貸不平、編製人員、差異說明）與 step2 抽樣。</summary>
public sealed class WorkingPaperWriterStep1ToStep2Tests
{
    // ================= step2 可靠性(借貸代號、帶號金額、欄寬與樣本順序);最後一則核對 step1 表頭的 A3 =================

    [Fact]
    public async Task Step2_SampleRows_LegacyDirectionAndSignedAmount_MatchInfSampleRecount()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        var validate = await host.DispatchAsync("validate.run");
        var sampleSize = validate.GetProperty("infSamplingTest").GetProperty("sampleSize").GetInt64();
        Assert.Equal(59, sampleSize);

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(Step2Sheet);

        // 資料第 53 列起;A 樣本序號為列序 1..N(emitter 自累計),B 傳票號連續至首空。
        var docs = ReadColumnFrom(sheet, "B", 53);
        Assert.Equal(sampleSize, docs.Count);
        Assert.Equal("1", sheet.Cell("A53").GetString()); // A 欄樣本序號自 1 起
        Assert.Equal(sampleSize, (long)sheet.Cell($"A{52 + (int)sampleSize}").GetDouble());
        Assert.True(sheet.Cell("A112").IsEmpty());
        Assert.Single(sheet.DataValidations.GetAllInRange(sheet.Cell("S111").AsRange().RangeAddress));
        Assert.Empty(sheet.DataValidations.GetAllInRange(sheet.Cell("S112").AsRange().RangeAddress));

        // E/F 依範本為原始借貸代號與帶號金額；取首樣本 entry 獨立 recount。
        var firstEntryId = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT g.entry_id FROM inf_sample s " +
            "JOIN gl g ON g.entry_id = s.entry_id " +
            "WHERE s.result_id = (SELECT result_id FROM validation_result " +
            "                  ORDER BY generated_utc DESC, result_id DESC LIMIT 1) " +
            "ORDER BY g.voucher_number, g.entry_id LIMIT 1;");
        var expectedDebit = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT debit_amount_scaled FROM gl WHERE entry_id=@id;", ("@id", firstEntryId));
        var expectedCredit = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT credit_amount_scaled FROM gl WHERE entry_id=@id;", ("@id", firstEntryId));

        // 首樣本在第 53 列。樣本照舊版依傳票號碼排序、同一張傳票照原本的列順序，上面取首樣本的查詢用同一個順序。
        Assert.Equal(expectedCredit > 0 ? "0" : "1", sheet.Cell("E53").GetString());
        Assert.Equal(
            (double)(expectedDebit - expectedCredit) / MoneyScale,
            sheet.Cell("F53").GetDouble());
        AssertSafeAmountColumn(sheet, 6);
        AssertSingleLine(sheet.Cell("F53"));

        // Demo 未配對來源模組與傳票核准人員，T 說明手填留空。
        // 照舊版只在配對了傳票核准人員時把 L51 改成「傳票核准人員」，沒配對時保留範本原字，L 欄沒有資料。
        Assert.True(sheet.Cell("J53").IsEmpty());
        Assert.Equal("其他有用在高風險條件的RDE欄位\n(若有，請自行新增修改)", sheet.Cell("L51").GetString());
        Assert.True(sheet.Cell("L53").IsEmpty());
        Assert.True(sheet.Cell("T53").IsEmpty());
    }

    [Fact]
    public async Task Step2_LateSampleLongText_UsesFullDataWidthsAndSingleLineStyles()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        var validate = await host.DispatchAsync("validate.run");
        Assert.Equal(
            59,
            validate.GetProperty("infSamplingTest").GetProperty("sampleSize").GetInt64());
        var context = await CurrentContextForAsync(host, ctx.ProjectId);
        var longVoucherNumber = "ZZZ-SAMPLE-" + new string('8', 68);
        var longAccountNumber = "ZZZ-ACCOUNT-" + new string('A', 56);
        var longAccountName = string.Concat(Enumerable.Repeat("晚頁樣本科目名稱", 9));
        var longPreparer = string.Concat(Enumerable.Repeat("晚頁編製人員", 8));
        var cappedDescription = new string('界', 140);
        var longApprover = "APPROVER-" + new string('Z', 58);

        await ExecuteProjectSqlAsync(
            host,
            ctx.ProjectId,
            """
            UPDATE gl
            SET voucher_number = @voucherNumber,
                account_number = @accountNumber,
                account_name = @accountName,
                created_by = @preparer,
                approved_by = @approver,
                description = @description
            WHERE entry_id = (
                SELECT MAX(entry_id)
                FROM inf_sample
                WHERE result_id = @resultId
            );
            """,
            ("@voucherNumber", longVoucherNumber),
            ("@accountNumber", longAccountNumber),
            ("@accountName", longAccountName),
            ("@preparer", longPreparer),
            ("@approver", longApprover),
            ("@description", cappedDescription),
            ("@resultId", context.ValidationResultId));

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(Step2Sheet);
        var lateRow = FindRowByColumnValue(sheet, "B", longVoucherNumber, 53);
        Assert.Equal(111, lateRow);
        Assert.Equal(longAccountName, sheet.Cell(lateRow, 4).GetString());
        Assert.Equal(cappedDescription, sheet.Cell(lateRow, 11).GetString());
        AssertFullDataWidth(sheet, 2, longVoucherNumber);
        AssertFullDataWidth(sheet, 3, longAccountNumber);
        AssertFullDataWidth(sheet, 4, longAccountName);
        AssertFullDataWidth(sheet, 9, longPreparer);
        AssertFullDataWidth(sheet, 11, cappedDescription);
        AssertFullDataWidth(sheet, 12, longApprover);
        AssertSingleLine(sheet.Cell(lateRow, 2));
        AssertSingleLine(sheet.Cell(lateRow, 4));
        AssertSingleLine(sheet.Cell(lateRow, 11));
        AssertSingleLine(sheet.Cell(lateRow, 20));
    }

    // 資料可靠性樣本照舊版依傳票號碼排序，同一張傳票照原本的列順序；INF 報告與底稿 Step 2 都照這個順序。
    // 舊版在 IDEA 抽樣後依「傳票號碼_JE」升冪排序。
    // 依分錄順序第 k 筆（從 0 起）的樣本，傳票號碼改成 V(200 - k / 2)、摘要寫 k，所以傳票號碼的順序和分錄順序相反，
    // 而且每兩筆同一張傳票。兩份輸出都要依傳票號碼升冪，同一張傳票裡分錄順序在前的先列。
    // 傳票號碼空白的樣本照舊版排在最前面，所以第 10、30 筆改成沒有傳票號碼，第 20 筆只有空白字元，
    // 這三筆依分錄順序排在最前面。
    [Fact]
    public async Task InfReportAndStep2_ListSamplesByVoucherNumber_KeepingTheRowOrderWithinAVoucher()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        var validate = await host.DispatchAsync("validate.run");
        var sampleSize = checked((int)validate.GetProperty("infSamplingTest").GetProperty("sampleSize").GetInt64());
        Assert.Equal(59, sampleSize);
        var resultId = validate.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var context = await CurrentContextForAsync(host, ctx.ProjectId);
        Assert.Equal(resultId, context.ValidationResultId);
        await ExecuteProjectSqlAsync(
            host,
            ctx.ProjectId,
            """
            UPDATE gl
            SET voucher_number = CASE ranked.position
                    WHEN 10 THEN NULL
                    WHEN 30 THEN NULL
                    WHEN 20 THEN '   '
                    ELSE printf('V%03d', 200 - ranked.position / 2)
                END,
                description = printf('ORDER-%02d', ranked.position)
            FROM (
                SELECT entry_id, ROW_NUMBER() OVER (ORDER BY entry_id) - 1 AS position
                FROM inf_sample
                WHERE result_id = @resultId
            ) AS ranked
            WHERE gl.entry_id = ranked.entry_id;
            """,
            ("@resultId", resultId));

        int[] blank = [10, 20, 30];
        string Key(int position) =>
            (blank.Contains(position) ? string.Empty : $"V{200 - position / 2:000}") + $"|ORDER-{position:00}";
        // 空白的三筆在最前面；其餘的傳票號碼隨 k / 2 變小，所以依 k / 2 由大到小、同一張傳票裡 k 由小到大。
        var expected = blank
            .Concat(Enumerable.Range(0, sampleSize)
                .Where(position => !blank.Contains(position))
                .OrderByDescending(position => position / 2)
                .ThenBy(position => position))
            .Select(Key)
            .ToArray();
        // 前提：傳票號碼的順序和分錄順序不同，舊的順序過不了這個測試。
        Assert.NotEqual(Enumerable.Range(0, sampleSize).Select(Key).ToArray(), expected);

        using (var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId))
        {
            Assert.Equal(expected, SampleKeys(workbook.Worksheet(Step2Sheet), sampleSize));
        }

        var export = await host.DispatchAsync(
            "export.validationReports",
            JsonSerializer.Serialize(new { resultId }));
        var fileName = export.GetProperty("publishedReports").EnumerateArray()
            .Single(report => report.GetProperty("kind").GetString() == "infReport")
            .GetProperty("fileName").GetString()!;
        using var inf = new XLWorkbook(Path.Combine(host.ProjectsRoot, ctx.ProjectId, fileName));
        Assert.Equal(expected, SampleKeys(inf.Worksheet("INF Testing 可靠性測試"), sampleSize));

        // 兩份輸出的樣本都從第 53 列起，B 欄是傳票號碼、K 欄是傳票摘要；傳票號碼空白的列，B 欄不論寫成空字串或空白字元都算空白。
        static string[] SampleKeys(IXLWorksheet sheet, int count) =>
            Enumerable.Range(53, count)
                .Select(row => sheet.Cell(row, 2).GetString() is var voucher && string.IsNullOrWhiteSpace(voucher)
                    ? "|" + sheet.Cell(row, 11).GetString()
                    : voucher + "|" + sheet.Cell(row, 11).GetString())
                .ToArray();
    }

    [Fact]
    public async Task Step1Header_UsesLastAccountingPeriodStart_AndNeverSubstitutesPeriodEnd()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);

        await using var datedStream = new MemoryStream();
        await WriteWithCurrentPlanAsync(
            host,
            BuildWriter(host),
            datedStream,
            ContextFor(projectId) with { PrepStartDate = "2023-01-01" },
            CancellationToken.None);
        datedStream.Position = 0;
        using (var workbook = new XLWorkbook(datedStream))
        {
            var header = workbook.Worksheet(Step1Sheet).Cell("A3").GetString();
            Assert.Contains("20230101", header, StringComparison.Ordinal);
            Assert.DoesNotContain("20251231", header, StringComparison.Ordinal);
        }

        await using var missingStream = new MemoryStream();
        await WriteWithCurrentPlanAsync(
            host,
            BuildWriter(host),
            missingStream,
            ContextFor(projectId) with { PrepStartDate = null },
            CancellationToken.None);
        missingStream.Position = 0;
        using var missingWorkbook = new XLWorkbook(missingStream);
        Assert.EndsWith(
            "N/A",
            missingWorkbook.Worksheet(Step1Sheet).Cell("A3").GetString(),
            StringComparison.Ordinal);
    }

    // ================= step1 完整性(全科目);中間一則核對 step1 家族各表的欄寬 =================

    [Fact]
    public async Task Step1_ListsEveryAccount_IncludingZeroDiff()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step1Sheet);

        // 全科目 recount(含 diff=0):CompletenessDiffCte 同語意,不加 tb_s<>gl_s 過濾。
        var allAccounts = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "WITH gl_by_account AS (SELECT account_number, SUM(amount_scaled) s FROM gl GROUP BY account_number), " +
            "tb_by_account AS (SELECT account_number, SUM(change_amount_scaled) s FROM tb GROUP BY account_number) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_number FROM tb_by_account t LEFT JOIN gl_by_account g ON g.account_number=t.account_number " +
            " UNION ALL " +
            " SELECT g.account_number FROM gl_by_account g LEFT JOIN tb_by_account t ON t.account_number=g.account_number " +
            "   WHERE t.account_number IS NULL) x;");

        // 第 19 列為欄標,資料自第 20 列起。逐列讀科目編號(B 欄)直到空白。
        var codes = ReadColumnFrom(sheet, "B", 20);
        Assert.Equal(allAccounts, codes.Count);
        // 1101(diff=0)與 2201(diff≠0)都在 → 證實「全科目」(非僅差異)。
        Assert.Contains("1101", codes);
        Assert.Contains("2201", codes);

        // 2201 列:D=TB 變動(scaled→顯示=500)、E=GL 彙總(0)、F=差異(tb_s-gl_s=500)。
        var row2201 = FindRowByColumnValue(sheet, "B", "2201", 20);
        Assert.Equal(500d, sheet.Cell($"D{row2201}").GetDouble());
        Assert.Equal(0d, sheet.Cell($"E{row2201}").GetDouble());
        Assert.Equal(500d, sheet.Cell($"F{row2201}").GetDouble());
        Assert.All(Enumerable.Range(4, 3), column => AssertSafeAmountColumn(sheet, column));
        AssertSingleLine(sheet.Cell(row2201, 6));
    }

    [Fact]
    public async Task Step1Family_LateLongText_UsesFullDataWidthsAndSingleLineStyles()
    {
        using var host = new HandlerTestHost();
        var longAccountNumber = "ZZZ-" + new string('A', 72);
        var longAccountName = string.Concat(Enumerable.Repeat("晚頁長科目名稱", 10));
        var longVoucherNumber = "ZZZ-DOC-" + new string('9', 64);
        var longPreparer = "ZZZ-" + string.Concat(Enumerable.Repeat("晚頁編製人員", 8));
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns(
                    "傳票號碼",
                    "傳票日期",
                    "科目代號",
                    "科目名稱",
                    "摘要",
                    "建立人員",
                    "金額",
                    "借方旗標");
                gl.AddRow("A-BALANCED", "2025-03-01", "1000", "現金", "短值", "A-USER", 100m, 1);
                gl.AddRow("A-BALANCED", "2025-03-01", "1000", "現金", "短值", "A-USER", 100m, 0);
                gl.AddRow("B-UNBALANCED", "2025-03-02", "2000", "費用", "短值", "B-USER", 20m, 1);
                gl.AddRow("B-UNBALANCED", "2025-03-02", "2000", "費用", "短值", "B-USER", 10m, 0);
                gl.AddRow(
                    longVoucherNumber,
                    "2025-03-03",
                    longAccountNumber,
                    longAccountName,
                    "晚頁長值",
                    longPreparer,
                    300m,
                    1);
                gl.AddRow(
                    longVoucherNumber,
                    "2025-03-03",
                    longAccountNumber,
                    longAccountName,
                    "晚頁長值",
                    longPreparer,
                    100m,
                    0);
            },
            configureTb: tb =>
            {
                tb.AddRow("1000", "現金", 0m);
                tb.AddRow("2000", "費用", 10m);
                tb.AddRow(longAccountNumber, longAccountName, 700m);
            });

        using var workbook = await WriteAndReadAsync(host, projectId);

        var step1 = workbook.Worksheet(Step1Sheet);
        var step1Row = FindRowByColumnValue(step1, "B", longAccountNumber, 20);
        Assert.True(step1Row > 20, "晚頁長科目必須位於短值之後。");
        Assert.Equal(longAccountName, step1.Cell(step1Row, 3).GetString());
        AssertFullDataWidth(step1, 2, longAccountNumber);
        AssertFullDataWidth(step1, 3, longAccountName);
        AssertSingleLine(step1.Cell(step1Row, 2));
        AssertSingleLine(step1.Cell(step1Row, 3));

        var step11 = workbook.Worksheet(Step11Sheet);
        var step11Row = FindRowByColumnValue(step11, "B", longVoucherNumber, 15);
        Assert.True(step11Row > 15, "晚頁長傳票必須位於較短的不平傳票之後。");
        AssertFullDataWidth(step11, 2, longVoucherNumber);
        AssertSingleLine(step11.Cell(step11Row, 2));

        var step12 = workbook.Worksheet(Step12Sheet);
        var step12Row = FindRowByColumnValue(step12, "B", longPreparer, 12);
        Assert.True(step12Row > 12, "晚頁長編製者必須位於短值之後。");
        AssertFullDataWidth(step12, 2, longPreparer);
        AssertSingleLine(step12.Cell(step12Row, 2));
        AssertSingleLine(step12.Cell(step12Row, 8));

        var step13 = workbook.Worksheet(Step13Sheet);
        var step13Row = FindRowByColumnValue(step13, "B", longAccountNumber, 17);
        Assert.True(step13Row >= 17);
        Assert.Equal(longAccountName, step13.Cell(step13Row, 3).GetString());
        AssertFullDataWidth(step13, 2, longAccountNumber);
        AssertFullDataWidth(step13, 3, longAccountName);
        AssertSingleLine(step13.Cell(step13Row, 2));
        AssertSingleLine(step13.Cell(step13Row, 3));
        AssertSingleLine(step13.Cell(step13Row, 5));
    }

    [Fact]
    public async Task Step1_AccountDiff_MatchesIndependentRecount()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step1Sheet);
        var row2201 = FindRowByColumnValue(sheet, "B", "2201", 20);

        // 獨立 recount:2201 的 tb_s - gl_s(scaled),再除 MoneyScale 還原顯示值。
        var diffScaled = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COALESCE((SELECT SUM(change_amount_scaled) FROM tb WHERE account_number='2201'),0) " +
            "     - COALESCE((SELECT SUM(amount_scaled) FROM gl WHERE account_number='2201'),0);");

        Assert.Equal((double)diffScaled / MoneyScale, sheet.Cell($"F{row2201}").GetDouble());
    }

    // ================= step1-1 借貸不平(有不平傳票才列明細);另有 step1 與 step1-1 的結論文字、step1-3 的表頭說明 =================

    [Fact]
    public async Task Step11_UnbalancedPopulation_EmitsExceptionRow()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step11Sheet);

        // 照舊版 JET 的版面：B12 寫有不平的結論、B14 寫說明，
        // 第 16 列是五欄表頭，明細依傳票號碼與總帳日期彙總，貸方合計為負數，最後一欄留給審計員填理由。
        Assert.Equal(
            "基於上述程序，查核團隊發現有部分傳票借貸不平，但已取得足夠的查核證據，確認其理由尚屬合理。",
            sheet.Cell("B12").GetString());
        Assert.Equal("出現借貸不平之個別傳票說明：", sheet.Cell("B14").GetString());
        Assert.Equal(
            new[] { "傳票號碼", "總帳日期", "借方金額", "貸方金額", "借貸不平的理由" },
            Enumerable.Range(2, 5).Select(column => sheet.Cell(16, column).GetString()).ToArray());

        var jv3Row = FindRowByColumnValue(sheet, "B", "JV3", 17);
        Assert.Equal(17, jv3Row);
        Assert.Equal("2025-03-03", sheet.Cell(jv3Row, 3).GetString());
        Assert.Equal(300d, sheet.Cell(jv3Row, 4).GetDouble());
        Assert.Equal(-100d, sheet.Cell(jv3Row, 5).GetDouble());
        Assert.True(sheet.Cell(jv3Row, 6).IsEmpty());
        Assert.True(sheet.Cell(jv3Row + 1, 2).IsEmpty());
        Assert.All(new[] { 4, 5 }, column => AssertSafeAmountColumn(sheet, column));
        AssertSingleLine(sheet.Cell(jv3Row, 5));
    }

    [Fact]
    public async Task Step11_UnbalancedVoucherAcrossGlDates_ListsOneRowPerVoucherAndGlDate()
    {
        // 舊版 JET 先以傳票號碼判定不平，再依傳票號碼與總帳日期彙總借方與貸方。
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                gl.AddRow("JV1", "2025-03-01", "1101", "現金", "說明", "甲", "100.00", 1);
                gl.AddRow("JV1", "2025-03-01", "6101", "費用", "說明", "甲", "100.00", 0);
                gl.AddRow("JV9", "2025-03-03", "1101", "現金", "說明", "甲", "300.00", 1);
                gl.AddRow("JV9", "2025-03-03", "6101", "費用", "說明", "甲", "40.00", 0);
                gl.AddRow("JV9", "2025-03-04", "6101", "費用", "說明", "甲", "100.00", 0);
            },
            configureTb: tb =>
            {
                tb.AddRow("1101", "現金", 400);
                tb.AddRow("6101", "費用", -240);
            });
        using var workbook = await WriteAndReadAsync(host, projectId);
        var sheet = workbook.Worksheet(Step11Sheet);

        Assert.Equal(new[] { "JV9", "JV9" }, ReadColumnFrom(sheet, "B", 17));
        Assert.Equal("2025-03-03", sheet.Cell(17, 3).GetString());
        Assert.Equal(300d, sheet.Cell(17, 4).GetDouble());
        Assert.Equal(-40d, sheet.Cell(17, 5).GetDouble());
        Assert.Equal("2025-03-04", sheet.Cell(18, 3).GetString());
        Assert.Equal(0d, sheet.Cell(18, 4).GetDouble());
        Assert.Equal(-100d, sheet.Cell(18, 5).GetDouble());
    }

    [Fact]
    public async Task Step1AndStep11_Conclusions_FollowLegacyResultTexts()
    {
        // legacy 只在有差異科目時改寫 Step 1 的 B15、B17，只在有不平傳票時改寫 Step 1-1 的 B12。
        using var balancedHost = new HandlerTestHost();
        var balancedProject = await SetupBalancedAsync(balancedHost);
        using (var balanced = await WriteAndReadAsync(balancedHost, balancedProject))
        {
            Assert.Equal(
                "基於上述程序，查核團隊對於JE測試母體之完整性，已取得足夠的查核證據。",
                balanced.Worksheet(Step1Sheet).Cell("B15").GetString());
            Assert.Equal(
                "#針對試算表科目金額本期異動與會計分錄(JE)進行推滾比對之清單列示如下：(已確認差異數均為0，可確認其完整性)",
                balanced.Worksheet(Step1Sheet).Cell("B17").GetString());
            Assert.Equal(
                "基於上述程序，查核團隊已取得足夠的查核證據，確認無借貸不平之情形。",
                balanced.Worksheet(Step11Sheet).Cell("B12").GetString());
            Assert.True(balanced.Worksheet(Step11Sheet).Cell("B14").IsEmpty());
            Assert.True(balanced.Worksheet(Step11Sheet).Cell("B16").IsEmpty());
        }

        using var unbalancedHost = new HandlerTestHost();
        var unbalancedProject = await SetupUnbalancedAsync(unbalancedHost);
        using var unbalanced = await WriteAndReadAsync(unbalancedHost, unbalancedProject);
        Assert.Equal(
            "基於上述程序，查核團隊對於JE測試母體之完整性，尚需於Step1-3說明以取得足夠的查核證據。",
            unbalanced.Worksheet(Step1Sheet).Cell("B15").GetString());
        Assert.Equal(
            "#針對試算表科目金額本期異動與會計分錄(JE)進行推滾比對之清單列示如下：(有部分科目之差異數不為0，請於step1-3說明其理由，以確認JE母體的完整性)",
            unbalanced.Worksheet(Step1Sheet).Cell("B17").GetString());
    }

    [Fact]
    public async Task Step13_Header_KeepsTemplateReconciliationGuidanceLine()
    {
        // 範本 F16 有兩行；legacy 與資料驗證報告都保留第二行說明（ReportExportTests 已鎖住後者）。
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        Assert.Equal(
            "說明如何進行調節以降低該差異\n"
            + "(可參考Validation Report中的指引，於IDEA篩選JE測試母體檔之相關科目傳票明細來確認調節內容。)",
            workbook.Worksheet(Step13Sheet).Cell("F16").GetString());
    }

    [Fact]
    public async Task Step11_UnbalancedPopulation_UsesLegacyRow16HeaderStylesAndPlainDataRows()
    {
        // 舊版 JET 的表頭在第 16 列並套藍底白字，
        // 資料自第 17 列起，不能被套成表頭樣式；理由欄要讓審計員在保護工作表中仍可填寫。
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        var outputBytes = await WriteToBytesAsync(host, projectId);
        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            "WorkingPaper.xlsx");

        using var template = SpreadsheetDocument.Open(templatePath, false);
        using var output = SpreadsheetDocument.Open(
            new MemoryStream(outputBytes, writable: false),
            false);
        var templateSheet = WorksheetFor(template, Step11Sheet);
        var outputSheet = WorksheetFor(output, Step11Sheet);

        foreach (var reference in new[] { "B16", "C16", "D16", "E16", "F16" })
        {
            var templateFormat = ResolveCellFormat(
                template.WorkbookPart!,
                CellFor(templateSheet, reference));
            var outputFormat = ResolveCellFormat(
                output.WorkbookPart!,
                CellFor(outputSheet, reference));
            Assert.Equal(
                templateFormat.NumberFormatId?.Value,
                outputFormat.NumberFormatId?.Value);
            Assert.Equal(
                templateFormat.ApplyNumberFormat?.Value,
                outputFormat.ApplyNumberFormat?.Value);
            Assert.Equal(templateFormat.BorderId?.Value, outputFormat.BorderId?.Value);
            Assert.Equal(templateFormat.ApplyBorder?.Value, outputFormat.ApplyBorder?.Value);
            Assert.Equal(templateFormat.Alignment?.OuterXml, outputFormat.Alignment?.OuterXml);
            Assert.Equal(
                templateFormat.ApplyAlignment?.Value,
                outputFormat.ApplyAlignment?.Value);
            // 照舊版 JET 只鎖 B12:E 到資料末列；
            // 表頭 B16:E16 鎖定、F16 可填。
            Assert.Equal(reference != "F16", outputFormat.Protection?.Locked?.Value ?? true);
            Assert.Equal("FF0066FF", FillArgb(output.WorkbookPart!, outputFormat));
        }

        foreach (var reference in new[] { "B17", "C17", "D17", "E17" })
        {
            var format = ResolveCellFormat(
                output.WorkbookPart!,
                CellFor(outputSheet, reference));
            Assert.True(format.Protection?.Locked?.Value ?? true);
            Assert.False(format.Alignment?.WrapText?.Value ?? false);
            Assert.Equal(0U, format.Alignment?.Indent?.Value ?? 0U);
            Assert.False(format.Alignment?.ShrinkToFit?.Value ?? false);
            Assert.NotEqual("FF0066FF", FillArgb(output.WorkbookPart!, format));
        }

        AssertEditableCells(output.WorkbookPart!, Step11Sheet, ["F17"]);
        // 可以填的格子不加淡黃色底色。
        Assert.Null(FillArgb(output.WorkbookPart!, ResolveCellFormat(output.WorkbookPart!, CellFor(outputSheet, "F17"))));
    }

    [Fact]
    public async Task Step11_BalancedPopulation_NoExceptionRows_OnlyConclusion()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step11Sheet);

        // 平衡母體:結論文字在,但無任何例外傳票列(條件表不出)。
        Assert.Contains("結論", sheet.Cell("A12").GetString());
        var unbalanced = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COUNT(*) FROM (SELECT voucher_number FROM gl " +
            "GROUP BY voucher_number HAVING SUM(amount_scaled) <> 0) x;");
        Assert.Equal(0, unbalanced); // 母體前置確認:確實無不平傳票
        Assert.Equal(0, CountColumnFrom(sheet, "B", 13)); // 例外表欄標之後無資料列
    }

    // ================= step1-2 編製人員(全名單)=================

    [Fact]
    public async Task Step12_ListsEveryPreparer_WithCountsAndAmounts_ManualColumnsBlank()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step12Sheet);
        Assert.Equal("此處底稿係記錄分錄編製人員資訊", sheet.Cell("A4").GetString());

        // 名單筆數 == distinct created_by recount。第 11 列欄標,資料自第 12 列起。
        var distinct = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(DISTINCT created_by) FROM gl;");
        var preparers = ReadColumnFrom(sheet, "B", 12);
        Assert.Equal(distinct, preparers.Count);
        Assert.Contains("甲", preparers);
        Assert.Contains("乙", preparers);

        // 甲列:D=傳票數、E=金額彙總(借方 scaled→顯示);獨立 recount。
        var jiaRow = FindRowByColumnValue(sheet, "B", "甲", 12);
        var jiaCount = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM gl WHERE created_by='甲';");
        var jiaDebit = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COALESCE(SUM(debit_amount_scaled),0) FROM gl WHERE created_by='甲';");
        Assert.Equal(jiaCount, (long)sheet.Cell($"D{jiaRow}").GetDouble());
        Assert.Equal((double)jiaDebit / MoneyScale, sheet.Cell($"E{jiaRow}").GetDouble());
        AssertSafeCountColumn(sheet, 4);
        AssertSafeAmountColumn(sheet, 5);
        AssertSingleLine(sheet.Cell(jiaRow, 5));

        // 手填欄(C 自動/人工、F 部門、G 職稱、H 說明)一律空白。
        Assert.True(sheet.Cell($"C{jiaRow}").IsEmpty());
        Assert.True(sheet.Cell($"F{jiaRow}").IsEmpty());
        Assert.True(sheet.Cell($"G{jiaRow}").IsEmpty());
        Assert.True(sheet.Cell($"H{jiaRow}").IsEmpty());
    }

    // 沒有配對傳票建立人員時照舊版不列名單，也不寫「（空白）」那一列，只在 B13 寫提示句。
    [Fact]
    public async Task Step12_MissingCreatedByMapping_WritesLegacyNoticeInsteadOfBlankPreparerRow()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupMissingPreparerMappingAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step12Sheet);
        Assert.True(sheet.Cell("B12").IsEmpty());
        Assert.Equal(
            "請注意，該測試作業於總帳資料欄位配對作業程序並挑選編制人員欄位。",
            sheet.Cell("B13").GetString());
        Assert.True(sheet.Cell("B13").Style.Font.Bold);
        Assert.DoesNotContain(
            Enumerable.Range(12, 30).Select(row => sheet.Cell(row, 2).GetString()),
            value => value == "（空白）");
    }

    // 配對了人工傳票欄位時，Step 1-2 照舊版依人工旗標分列，C 欄寫「人工建立分錄」或「自動拋轉分錄」。
    // 人員依分錄總數由多到少，同一位人員先列自動、再列人工。
    [Fact]
    public async Task Step12_ManualFlagMapped_SplitsEachPreparerByFlagAndNamesTheEntryType()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns(
                    "傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "人工傳票", "金額", "借方旗標");
                gl.AddRow("SYN-1", "2025-03-01", "SYN-D", "Synthetic debit", "Synthetic", "甲", "1", 100m, 1);
                gl.AddRow("SYN-1", "2025-03-01", "SYN-C", "Synthetic credit", "Synthetic", "甲", "1", 100m, 0);
                gl.AddRow("SYN-2", "2025-03-02", "SYN-D", "Synthetic debit", "Synthetic", "甲", "0", 50m, 1);
                gl.AddRow("SYN-2", "2025-03-02", "SYN-C", "Synthetic credit", "Synthetic", "甲", "0", 50m, 0);
                gl.AddRow("SYN-3", "2025-03-03", "SYN-D", "Synthetic debit", "Synthetic", "乙", "0", 20m, 1);
                gl.AddRow("SYN-3", "2025-03-03", "SYN-C", "Synthetic credit", "Synthetic", "乙", "0", 20m, 0);
            },
            validateForDownstream: true);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step12Sheet);
        Assert.Equal(
            new[] { "甲", "甲", "乙" },
            Enumerable.Range(12, 3).Select(row => sheet.Cell(row, "B").GetString()).ToArray());
        Assert.Equal(
            new[] { "自動拋轉分錄", "人工建立分錄", "自動拋轉分錄" },
            Enumerable.Range(12, 3).Select(row => sheet.Cell(row, "C").GetString()).ToArray());
        Assert.Equal(
            new[] { 2d, 2d, 2d },
            Enumerable.Range(12, 3).Select(row => sheet.Cell(row, "D").GetDouble()).ToArray());
        Assert.Equal(
            new[] { 50d, 100d, 20d },
            Enumerable.Range(12, 3).Select(row => sheet.Cell(row, "E").GetDouble()).ToArray());
        Assert.True(sheet.Cell("B15").IsEmpty());
    }

    // 受保護的 step1-2 只鎖到最後一位編製人員。舊版 JET 多鎖下面那一列，JET 留給審計員填。
    [Fact]
    public async Task Step12_LocksThroughTheLastPreparer_AndLeavesTheNextRowEditable()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        var bytes = await WriteToBytesAsync(host, projectId);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        using var document = OpenWorkingPaper(bytes);
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var worksheet = WorksheetFor(document, Step12Sheet);
        Assert.NotNull(worksheet.Elements<SheetProtection>().SingleOrDefault());

        // 甲、乙寫在第 12、13 列。第 14 列在範本裡沒有格子，能不能填由欄的預設樣式決定。
        var lastRow = 11 + ReadColumnFrom(workbook.Worksheet(Step12Sheet), "B", 12).Count;
        Assert.Equal(13, lastRow);
        Assert.All(
            new[] { "B", "C", "D", "E" },
            column => Assert.True(
                IsCellLocked(workbookPart, worksheet, $"{column}{lastRow}"),
                $"{column}{lastRow} 應鎖定。"));
        Assert.All(
            new[] { "B", "C", "D", "E" },
            column => Assert.False(
                IsCellLocked(workbookPart, worksheet, $"{column}{lastRow + 1}"),
                $"{column}{lastRow + 1} 應可填。"));
    }

    // ================= step1-3 差異說明(僅 diff≠0)=================

    [Fact]
    public async Task Step13_ContainsOnlyNonZeroDiffAccounts_ManualColumnsBlank()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step13Sheet);

        // 僅 diff≠0:2201 在、1101/3301(diff=0)不在。第 16 列欄標,資料自第 17 列起。
        var codes = ReadColumnFrom(sheet, "B", 17);
        var diffCount = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "WITH gl_by_account AS (SELECT account_number, SUM(amount_scaled) s FROM gl GROUP BY account_number), " +
            "tb_by_account AS (SELECT account_number, SUM(change_amount_scaled) s FROM tb GROUP BY account_number) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_number FROM tb_by_account t LEFT JOIN gl_by_account g ON g.account_number=t.account_number " +
            "   WHERE COALESCE(g.s,0) <> t.s " +
            " UNION ALL " +
            " SELECT g.account_number FROM gl_by_account g LEFT JOIN tb_by_account t ON t.account_number=g.account_number " +
            "   WHERE t.account_number IS NULL AND g.s <> 0) x;");

        Assert.Equal(diffCount, codes.Count);
        Assert.Contains("2201", codes);
        Assert.DoesNotContain("1101", codes);
        Assert.DoesNotContain("3301", codes);

        // 手填欄(E 原因、F 調節、G 調節後差異)空白;D 差異金額自動。
        var row2201 = FindRowByColumnValue(sheet, "B", "2201", 17);
        Assert.Equal(500d, sheet.Cell($"D{row2201}").GetDouble());
        AssertSafeAmountColumn(sheet, 4);
        AssertSingleLine(sheet.Cell(row2201, 4));
        Assert.True(sheet.Cell($"E{row2201}").IsEmpty());
        Assert.True(sheet.Cell($"F{row2201}").IsEmpty());
        Assert.True(sheet.Cell($"G{row2201}").IsEmpty());
    }

    // ================= step1-3 只在有差異科目時產生,以及鎖定範圍 =================

    [Fact]
    public async Task Step13_ExistsWhenDiffPresent_WithoutRemovedStep131()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheetNames = workbook.Worksheets.Select(ws => ws.Name).ToList();
        Assert.Contains(Step13Sheet, sheetNames);
        Assert.DoesNotContain(RemovedStep131Sheet, sheetNames);

        var diffCount = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "WITH gl_by_account AS (SELECT account_number, SUM(amount_scaled) s FROM gl GROUP BY account_number), " +
            "tb_by_account AS (SELECT account_number, SUM(change_amount_scaled) s FROM tb GROUP BY account_number) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_number FROM tb_by_account t LEFT JOIN gl_by_account g ON g.account_number=t.account_number " +
            "   WHERE COALESCE(g.s,0) <> t.s " +
            " UNION ALL " +
            " SELECT g.account_number FROM gl_by_account g LEFT JOIN tb_by_account t ON t.account_number=g.account_number " +
            "   WHERE t.account_number IS NULL AND g.s <> 0) x;");

        Assert.Equal(diffCount, ReadColumnFrom(workbook.Worksheet(Step13Sheet), "B", 17).Count);

    }

    [Fact]
    public async Task Step13Family_OmittedWhenNoDiff()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        // 全平衡母體(無 diff≠0 科目)→ step1-3 不出現；JET 不寫的 step1-3-1 也不出現。
        Assert.DoesNotContain(Step13Sheet, workbook.Worksheets.Select(ws => ws.Name));
        Assert.DoesNotContain(RemovedStep131Sheet, workbook.Worksheets.Select(ws => ws.Name));
    }

    // 受保護的 step1-3 只鎖到最後一個差異科目。舊版 JET 多鎖下面那一列，JET 留給審計員填。
    [Fact]
    public async Task Step13_LocksThroughTheLastDiffAccount_AndLeavesTheNextRowEditable()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        var bytes = await WriteToBytesAsync(host, projectId);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        using var document = OpenWorkingPaper(bytes);
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var worksheet = WorksheetFor(document, Step13Sheet);
        Assert.NotNull(worksheet.Elements<SheetProtection>().SingleOrDefault());

        // 唯一的差異科目 2201 寫在第 17 列。第 18 列在範本裡沒有格子，能不能填由欄的預設樣式決定。
        var lastRow = 16 + ReadColumnFrom(workbook.Worksheet(Step13Sheet), "B", 17).Count;
        Assert.Equal(17, lastRow);
        Assert.All(
            new[] { "B", "C", "D" },
            column => Assert.True(
                IsCellLocked(workbookPart, worksheet, $"{column}{lastRow}"),
                $"{column}{lastRow} 應鎖定。"));
        Assert.All(
            new[] { "B", "C", "D" },
            column => Assert.False(
                IsCellLocked(workbookPart, worksheet, $"{column}{lastRow + 1}"),
                $"{column}{lastRow + 1} 應可填。"));
    }
}
