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
using static JET.Tests.Infrastructure.WorkpaperWriterTestSupport;

namespace JET.Tests.Infrastructure;

/// <summary>底稿寫出器：step1 家族（完整性、借貸不平、編製人員、差異說明）與 step2 抽樣。</summary>
public sealed class WorkpaperWriterStep1ToStep2Tests
{
    // ================= Task 4:step2 可靠性(legacy 借貸代號＋帶號金額)=================

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
            "SELECT g.entry_id FROM result_inf_sampling_test_sample s " +
            "JOIN target_gl_entry g ON g.entry_id = s.entry_id " +
            "WHERE s.run_id = (SELECT run_id FROM result_rule_run WHERE run_kind='validate' " +
            "                  ORDER BY generated_utc DESC, run_id DESC LIMIT 1) " +
            "ORDER BY g.entry_id LIMIT 1;");
        var expectedDebit = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT debit_amount_scaled FROM target_gl_entry WHERE entry_id=@id;", ("@id", firstEntryId));
        var expectedCredit = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT credit_amount_scaled FROM target_gl_entry WHERE entry_id=@id;", ("@id", firstEntryId));

        // 首樣本在第 53 列(INF 抽樣 ORDER BY entry_id 升冪,emitter 逐頁同序)。
        Assert.Equal(expectedCredit > 0 ? "0" : "1", sheet.Cell("E53").GetString());
        Assert.Equal(
            (double)(expectedDebit - expectedCredit) / MoneyScale,
            sheet.Cell("F53").GetDouble());
        AssertSafeAmountColumn(sheet, 6);
        AssertSingleLine(sheet.Cell("F53"));

        // Demo 未配對來源模組；L 欄採 current-required 核准人員欄標，T 說明手填留空。
        Assert.True(sheet.Cell("J53").IsEmpty());
        Assert.Equal("傳票核准人員", sheet.Cell("L51").GetString());
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
        var longDocumentNumber = "ZZZ-SAMPLE-" + new string('8', 68);
        var longAccountCode = "ZZZ-ACCOUNT-" + new string('A', 56);
        var longAccountName = string.Concat(Enumerable.Repeat("晚頁樣本科目名稱", 9));
        var longCreator = string.Concat(Enumerable.Repeat("晚頁編製人員", 8));
        var cappedDescription = new string('界', 140);
        var longApprover = "APPROVER-" + new string('Z', 58);

        await ExecuteProjectSqlAsync(
            host,
            ctx.ProjectId,
            """
            UPDATE target_gl_entry
            SET document_number = @documentNumber,
                account_code = @accountCode,
                account_name = @accountName,
                created_by = @creator,
                approved_by = @approver,
                document_description = @description
            WHERE entry_id = (
                SELECT MAX(entry_id)
                FROM result_inf_sampling_test_sample
                WHERE run_id = @runId
            );
            """,
            ("@documentNumber", longDocumentNumber),
            ("@accountCode", longAccountCode),
            ("@accountName", longAccountName),
            ("@creator", longCreator),
            ("@approver", longApprover),
            ("@description", cappedDescription),
            ("@runId", context.ValidationRunId));

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(Step2Sheet);
        var lateRow = FindRowByColumnValue(sheet, "B", longDocumentNumber, 53);
        Assert.Equal(111, lateRow);
        Assert.Equal(longAccountName, sheet.Cell(lateRow, 4).GetString());
        Assert.Equal(cappedDescription, sheet.Cell(lateRow, 11).GetString());
        AssertFullDataWidth(sheet, 2, longDocumentNumber);
        AssertFullDataWidth(sheet, 3, longAccountCode);
        AssertFullDataWidth(sheet, 4, longAccountName);
        AssertFullDataWidth(sheet, 9, longCreator);
        AssertFullDataWidth(sheet, 11, cappedDescription);
        AssertFullDataWidth(sheet, 12, longApprover);
        AssertSingleLine(sheet.Cell(lateRow, 2));
        AssertSingleLine(sheet.Cell(lateRow, 4));
        AssertSingleLine(sheet.Cell(lateRow, 11));
        AssertSingleLine(sheet.Cell(lateRow, 20));
    }

    [Fact]
    public async Task Step1Header_UsesLastAccountingPeriodStart_AndNeverSubstitutesPeriodEnd()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);

        await using var datedStream = new MemoryStream();
        await BuildWriter(host).WriteAsync(
            datedStream,
            ContextFor(projectId) with { LastPeriodStart = "2023-01-01" },
            CancellationToken.None);
        datedStream.Position = 0;
        using (var workbook = new XLWorkbook(datedStream))
        {
            var header = workbook.Worksheet(Step1Sheet).Cell("A3").GetString();
            Assert.Contains("20230101", header, StringComparison.Ordinal);
            Assert.DoesNotContain("20251231", header, StringComparison.Ordinal);
        }

        await using var missingStream = new MemoryStream();
        await BuildWriter(host).WriteAsync(
            missingStream,
            ContextFor(projectId) with { LastPeriodStart = null },
            CancellationToken.None);
        missingStream.Position = 0;
        using var missingWorkbook = new XLWorkbook(missingStream);
        Assert.EndsWith(
            "N/A",
            missingWorkbook.Worksheet(Step1Sheet).Cell("A3").GetString(),
            StringComparison.Ordinal);
    }

    // ================= Task 3:step1 完整性(全科目)=================

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
            "WITH gl AS (SELECT account_code, SUM(amount_scaled) s FROM target_gl_entry GROUP BY account_code), " +
            "tb AS (SELECT account_code, SUM(change_amount_scaled) s FROM target_tb_balance GROUP BY account_code) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_code FROM tb t LEFT JOIN gl ON gl.account_code=t.account_code " +
            " UNION ALL " +
            " SELECT g.account_code FROM gl g LEFT JOIN tb ON tb.account_code=g.account_code " +
            "   WHERE tb.account_code IS NULL) x;");

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
        var longAccountCode = "ZZZ-" + new string('A', 72);
        var longAccountName = string.Concat(Enumerable.Repeat("晚頁長科目名稱", 10));
        var longDocumentNumber = "ZZZ-DOC-" + new string('9', 64);
        var longCreator = "ZZZ-" + string.Concat(Enumerable.Repeat("晚頁編製人員", 8));
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
                    longDocumentNumber,
                    "2025-03-03",
                    longAccountCode,
                    longAccountName,
                    "晚頁長值",
                    longCreator,
                    300m,
                    1);
                gl.AddRow(
                    longDocumentNumber,
                    "2025-03-03",
                    longAccountCode,
                    longAccountName,
                    "晚頁長值",
                    longCreator,
                    100m,
                    0);
            },
            configureTb: tb =>
            {
                tb.AddRow("1000", "現金", 0m);
                tb.AddRow("2000", "費用", 10m);
                tb.AddRow(longAccountCode, longAccountName, 700m);
            });

        using var workbook = await WriteAndReadAsync(host, projectId);

        var step1 = workbook.Worksheet(Step1Sheet);
        var step1Row = FindRowByColumnValue(step1, "B", longAccountCode, 20);
        Assert.True(step1Row > 20, "晚頁長科目必須位於短值之後。");
        Assert.Equal(longAccountName, step1.Cell(step1Row, 3).GetString());
        AssertFullDataWidth(step1, 2, longAccountCode);
        AssertFullDataWidth(step1, 3, longAccountName);
        AssertSingleLine(step1.Cell(step1Row, 2));
        AssertSingleLine(step1.Cell(step1Row, 3));

        var step11 = workbook.Worksheet(Step11Sheet);
        var step11Row = FindRowByColumnValue(step11, "B", longDocumentNumber, 15);
        Assert.True(step11Row > 15, "晚頁長傳票必須位於較短的不平傳票之後。");
        AssertFullDataWidth(step11, 2, longDocumentNumber);
        AssertSingleLine(step11.Cell(step11Row, 2));

        var step12 = workbook.Worksheet(Step12Sheet);
        var step12Row = FindRowByColumnValue(step12, "B", longCreator, 12);
        Assert.True(step12Row > 12, "晚頁長編製者必須位於短值之後。");
        AssertFullDataWidth(step12, 2, longCreator);
        AssertSingleLine(step12.Cell(step12Row, 2));
        AssertSingleLine(step12.Cell(step12Row, 8));

        var step13 = workbook.Worksheet(Step13Sheet);
        var step13Row = FindRowByColumnValue(step13, "B", longAccountCode, 17);
        Assert.True(step13Row >= 17);
        Assert.Equal(longAccountName, step13.Cell(step13Row, 3).GetString());
        AssertFullDataWidth(step13, 2, longAccountCode);
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
            "SELECT COALESCE((SELECT SUM(change_amount_scaled) FROM target_tb_balance WHERE account_code='2201'),0) " +
            "     - COALESCE((SELECT SUM(amount_scaled) FROM target_gl_entry WHERE account_code='2201'),0);");

        Assert.Equal((double)diffScaled / MoneyScale, sheet.Cell($"F{row2201}").GetDouble());
    }

    // ================= Task 3:step1-1 借貸不平(條件例外表)=================

    [Fact]
    public async Task Step11_UnbalancedPopulation_EmitsExceptionRow()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step11Sheet);

        // 2026-10-02 使用者裁定照 legacy 版面（idea-tool.bas:10468-10476、idea-script.bas:9089-9097）。
        // 原本只確認 JV3 列存在、表頭在第 14 列且第三欄是借貸差額；legacy 在 B12 寫有不平的結論、B14 寫說明，
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
    public async Task Step11_UnbalancedVoucherAcrossPostDates_ListsOneRowPerVoucherAndPostDate()
    {
        // legacy 先以傳票號碼判定不平，再依傳票號碼與總帳日期彙總借方與貸方（idea-tool.bas:6618、6686-6691）。
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
        // 範本 F16 有兩行；legacy 與 Validation Report 都保留第二行說明（ReportArtifactExportTests 已鎖住後者）。
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
        // 原測試比對第 14 列表頭與範本格式。legacy 表頭在第 16 列並套藍底白字（idea-tool.bas:10472-10483），
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
            Assert.Equal(templateFormat.Protection?.OuterXml, outputFormat.Protection?.OuterXml);
            Assert.Equal(
                templateFormat.ApplyProtection?.Value,
                outputFormat.ApplyProtection?.Value);
            Assert.Equal("FF0066FF", FillArgb(output.WorkbookPart!, outputFormat));
        }

        foreach (var reference in new[] { "B17", "C17", "D17", "E17" })
        {
            var format = ResolveCellFormat(
                output.WorkbookPart!,
                CellFor(outputSheet, reference));
            Assert.False(format.Alignment?.WrapText?.Value ?? false);
            Assert.Equal(0U, format.Alignment?.Indent?.Value ?? 0U);
            Assert.False(format.Alignment?.ShrinkToFit?.Value ?? false);
            Assert.NotEqual("FF0066FF", FillArgb(output.WorkbookPart!, format));
        }

        AssertEditableCells(output.WorkbookPart!, Step11Sheet, ["F17"]);
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
            "SELECT COUNT(*) FROM (SELECT document_number FROM target_gl_entry " +
            "GROUP BY document_number HAVING SUM(amount_scaled) <> 0) x;");
        Assert.Equal(0, unbalanced); // 母體前置確認:確實無不平傳票
        Assert.Equal(0, CountColumnFrom(sheet, "B", 13)); // 例外表欄標之後無資料列
    }

    // ================= Task 3:step1-2 編製人員(全名單)=================

    [Fact]
    public async Task Step12_ListsEveryCreator_WithCountsAndAmounts_ManualColumnsBlank()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step12Sheet);

        // 名單筆數 == distinct created_by recount。第 11 列欄標,資料自第 12 列起。
        var distinct = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(DISTINCT created_by) FROM target_gl_entry;");
        var creators = ReadColumnFrom(sheet, "B", 12);
        Assert.Equal(distinct, creators.Count);
        Assert.Contains("甲", creators);
        Assert.Contains("乙", creators);

        // 甲列:D=傳票數、E=金額彙總(借方 scaled→顯示);獨立 recount。
        var jiaRow = FindRowByColumnValue(sheet, "B", "甲", 12);
        var jiaCount = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM target_gl_entry WHERE created_by='甲';");
        var jiaDebit = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COALESCE(SUM(debit_amount_scaled),0) FROM target_gl_entry WHERE created_by='甲';");
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

    // ================= Task 3:step1-3 差異說明(僅 diff≠0)=================

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
            "WITH gl AS (SELECT account_code, SUM(amount_scaled) s FROM target_gl_entry GROUP BY account_code), " +
            "tb AS (SELECT account_code, SUM(change_amount_scaled) s FROM target_tb_balance GROUP BY account_code) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_code FROM tb t LEFT JOIN gl ON gl.account_code=t.account_code " +
            "   WHERE COALESCE(gl.s,0) <> t.s " +
            " UNION ALL " +
            " SELECT g.account_code FROM gl g LEFT JOIN tb ON tb.account_code=g.account_code " +
            "   WHERE tb.account_code IS NULL AND g.s <> 0) x;");

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

    // ================= Task 3:step1-3 正準條件表 =================

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
            "WITH gl AS (SELECT account_code, SUM(amount_scaled) s FROM target_gl_entry GROUP BY account_code), " +
            "tb AS (SELECT account_code, SUM(change_amount_scaled) s FROM target_tb_balance GROUP BY account_code) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_code FROM tb t LEFT JOIN gl ON gl.account_code=t.account_code " +
            "   WHERE COALESCE(gl.s,0) <> t.s " +
            " UNION ALL " +
            " SELECT g.account_code FROM gl g LEFT JOIN tb ON tb.account_code=g.account_code " +
            "   WHERE tb.account_code IS NULL AND g.s <> 0) x;");

        Assert.Equal(diffCount, ReadColumnFrom(workbook.Worksheet(Step13Sheet), "B", 17).Count);

    }

    [Fact]
    public async Task Step13Family_OmittedWhenNoDiff()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        // 全平衡母體(無 diff≠0 科目)→ step1-3 不出現，已移除的工作表永不出現。
        Assert.DoesNotContain(Step13Sheet, workbook.Worksheets.Select(ws => ws.Name));
        Assert.DoesNotContain(RemovedStep131Sheet, workbook.Worksheets.Select(ws => ws.Name));
    }

}
