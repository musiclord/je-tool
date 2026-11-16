using System.Globalization;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 非直接比對頁逐格比對的放行規則：每一項只在差異確實是裁定或舊版程式說明的那一種時放行，
/// 其他寫法照樣擋下。工作簿都是程式產生的合成資料。
/// </summary>
public sealed class PrivateCaseResultCellComparisonTests
{
    private const string SyntheticClient = "synthetic-client";

    // 驗證報告的貸方合計：舊版寫負數，JET 寫正數（裁定 2026-11-05-64 先不改）；只放行剛好是相反數的情形。
    [Theory]
    [InlineData("500", true)]
    [InlineData("400", false)]
    public void ValidationSummary_CreditTotal_AcceptsOnlyTheOppositeSign(string actualCredit, bool accepted)
    {
        using var files = new SyntheticPair(
            [("ValidationReport", [("E12", -500m)])],
            [("ValidationReport", [("E12", decimal.Parse(actualCredit, CultureInfo.InvariantCulture))])]);

        var sheet = Assert.Single(Compare("validation-report", files));

        AssertOutcome(sheet, accepted, PrivateCaseResultCellComparison.ValidationCreditTotalSign, "E12");
    }

    // 舊版超過 2,500 張傳票寫「Over 2,500」，JET 寫實際張數（裁定 2026-10-29-29）；JET 的張數必須真的超過 2,500。
    [Theory]
    [InlineData("criteria-selection-report", "Summary Inforamtion", "C6", 3000, true)]
    [InlineData("criteria-selection-report", "Summary Inforamtion", "C6", 2000, false)]
    [InlineData("working-paper", WorkingPaperSheetCatalog.Step3, "E20", 2501, true)]
    [InlineData("working-paper", WorkingPaperSheetCatalog.Step3, "E20", 2500, false)]
    public void VoucherCountOverTheLegacyLimit_AcceptsOnlyAnActualCountAboveTheLimit(
        string reportSlug,
        string sheetName,
        string address,
        int actualCount,
        bool accepted)
    {
        using var files = new SyntheticPair(
            [(sheetName, [(address, "Over 2,500")])],
            [(sheetName, [(address, (decimal)actualCount)])]);

        var sheet = Assert.Single(Compare(reportSlug, files));

        AssertOutcome(sheet, accepted, PrivateCaseResultCellComparison.ActualVoucherCount, address);
    }

    // 舊版程式在預篩選摘要第 17 列寫空白摘要（R7）那一列；這份舊版報告沒有這一列時，JET 補上的整列放行。
    // 舊版有寫這一列時，兩邊的值照樣比。
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PrescreenSummary_LegacyReportWithoutTheBlankDescriptionRow_AcceptsOnlyTheAppendedRow(
        bool legacyHasRow,
        bool accepted)
    {
        (string, object)[] legacyCells = legacyHasRow ? [("A8", "V"), ("A17", "N/A")] : [("A8", "V")];
        using var files = new SyntheticPair(
            [("Pre-screening_Report", legacyCells)],
            [("Pre-screening_Report", [("A8", "V"), ("A17", "V"), ("E17", 3m)])]);

        var sheet = Assert.Single(Compare("prescreen-report", files));

        if (accepted)
        {
            Assert.Empty(sheet.BlockingCells);
            Assert.Contains(sheet.AcceptedCells, static decision =>
                decision.DecisionId == PrivateCaseResultCellComparison.LegacyReportWithoutBlankDescriptionRow);
        }
        else
        {
            Assert.Contains(sheet.BlockingCells, static cell => cell.Address == "A17");
        }
    }

    // 驗證框架給 JET 的客戶名稱和舊版案件的名稱不同，名稱照輸入條件放行。底稿的「公司名稱 : 」固定文字和舊版相同；
    // 三份報告的「Client:」JET 後面空一格，舊版不空格（裁定 2026-11-16-07）。多兩個空白、固定文字拼法不同、
    // 名稱不是驗證框架給的那個，都擋下並標出差異的種類。
    [Theory]
    [InlineData("Client: synthetic-client", "")]
    [InlineData("Client:  synthetic-client", "fixed-text-different")]
    [InlineData("Clients: synthetic-client", "fixed-text-different")]
    [InlineData("Client: other-client", "different")]
    public void HeaderCells_ClientName_AcceptsTheHarnessNameAndOnlyTheRuledLabelSpace(string jetValue, string relation)
    {
        using var paper = new SyntheticPair(
            [(WorkingPaperSheetCatalog.Step1, [("A1", "公司名稱 : legacy-company")])],
            [(WorkingPaperSheetCatalog.Step1, [("A1", $"公司名稱 : {SyntheticClient}")])]);
        using var validation = new SyntheticPair(
            [("ValidationReport", [("E1", "Client:legacy-company")])],
            [("ValidationReport", [("E1", jetValue)])]);

        var paperSheet = Assert.Single(Compare("working-paper", paper));
        var validationSheet = Assert.Single(Compare("validation-report", validation));

        AssertOutcome(paperSheet, true, PrivateCaseResultCellComparison.HarnessClientName, "A1");
        AssertOutcome(validationSheet, relation.Length == 0, PrivateCaseResultCellComparison.ClientLabelSpace, "E1");
        if (relation.Length > 0)
        {
            var cell = Assert.Single(validationSheet.BlockingCells);
            Assert.Equal(("E1", relation), (cell.Address, cell.Relation));
        }
        var json = JsonSerializer.Serialize(validationSheet);
        Assert.DoesNotContain("legacy-company", json, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticClient, json, StringComparison.Ordinal);
        Assert.DoesNotContain("other-client", json, StringComparison.Ordinal);
    }

    // 三份報告的 Year End，以及底稿與資料驗證報告 step1-3 說明頁的「測試資料期間」：JET 的日期寫 yyyy-MM-dd，
    // 舊版寫 8 位數字（裁定 2026-11-16-07）。只放行同一天、舊版 8 位數字對 JET yyyy-MM-dd 的情形；
    // 日期不同、任一邊用其他寫法，或不是這幾格，照樣擋下並標出差異的種類。
    [Theory]
    [InlineData("validation-report", "ValidationReport", "E2", "Year End: 20251231", "Year End: 2025-12-31", "")]
    [InlineData("prescreen-report", "Pre-screening_Report", "E2", "Year End: 20251231", "Year End: 2025-12-31", "")]
    [InlineData("criteria-selection-report", "Summary Inforamtion", "C2", "Year End: 20251231", "Year End: 2025-12-31", "")]
    [InlineData("working-paper", WorkingPaperSheetCatalog.Cover, "A2",
        "測試資料期間 :  20250101 ~ 20251231", "測試資料期間 :  2025-01-01 ~ 2025-12-31", "")]
    [InlineData("working-paper", WorkingPaperSheetCatalog.Step13, "A2",
        "測試資料期間 :  20250101 ~ 20251231", "測試資料期間 :  2025-01-01 ~ 2025-12-31", "")]
    [InlineData("validation-report", WorkingPaperSheetCatalog.Step13, "A2",
        "測試資料期間 :  20250101 ~ 20251231", "測試資料期間 :  2025-01-01 ~ 2025-12-31", "")]
    [InlineData("validation-report", "ValidationReport", "E2", "Year End: 20251231", "Year End: 2025-12-30", "different")]
    [InlineData("validation-report", "ValidationReport", "E2", "Year End: 20251231", "Year End: 2025/12/31", "date-format")]
    [InlineData("validation-report", "ValidationReport", "E2", "Year End: 2025/12/31", "Year End: 2025-12-31", "date-format")]
    [InlineData("working-paper", WorkingPaperSheetCatalog.Cover, "A2",
        "測試資料期間 :  20250101 ~ 20251231", "測試資料期間 :  2025-01-01 ~ 2025-12-30", "different")]
    [InlineData("validation-report", "ValidationReport", "E3", "Year End: 20251231", "Year End: 2025-12-31", "date-format")]
    public void HeaderCells_ReportPeriodDates_AcceptOnlyTheSameDayWrittenAsIso(
        string reportSlug,
        string sheetName,
        string address,
        string legacyValue,
        string jetValue,
        string relation)
    {
        using var files = new SyntheticPair(
            [(sheetName, [(address, legacyValue)])],
            [(sheetName, [(address, jetValue)])]);

        var sheet = Assert.Single(Compare(reportSlug, files));

        AssertOutcome(sheet, relation.Length == 0, PrivateCaseResultCellComparison.ReportPeriodDateFormat, address);
        if (relation.Length > 0)
        {
            var cell = Assert.Single(sheet.BlockingCells);
            Assert.Equal((address, relation), (cell.Address, cell.Relation));
        }
    }

    // 固定文字只差空白，或只差空白與日期寫法時，照樣擋下，但分開標出差異的種類。
    [Fact]
    public void FixedText_OnlySpacingDiffers_IsBlockedAndMarkedAsSpacing()
    {
        using var files = new SyntheticPair(
            [(WorkingPaperSheetCatalog.Intro, [("A5", "期末關帳開始日 20250101 之後核准"), ("A6", "說明 文字"), ("A7", "說明文字")])],
            [(WorkingPaperSheetCatalog.Intro, [("A5", "期末關帳開始日2025-01-01之後核准"), ("A6", "說明文字"), ("A7", "另一段文字")])]);

        var sheet = Assert.Single(Compare("working-paper", files));

        Assert.Equal(
            [("A5", "spacing-and-date-format"), ("A6", "spacing"), ("A7", "different")],
            sheet.BlockingCells.Select(static cell => (cell.Address, cell.Relation)).ToArray());
    }

    // 失敗訊息只列前幾格；每張頁另外依欄與差異的種類計數，列很多的表也看得出差異全落在哪幾欄、是哪一種。
    [Fact]
    public void BlockingCells_AreCountedByColumnAndKindOfDifference()
    {
        using var files = new SyntheticPair(
            [(WorkingPaperSheetCatalog.Intro,
                [("B13", "V-1"), ("C13", "20250131"), ("B14", "V-2"), ("C14", "20250201"), ("E14", 10m)])],
            [(WorkingPaperSheetCatalog.Intro,
                [("B13", "V-9"), ("C13", "2025-01-31"), ("B14", "V-2"), ("C14", "2025-02-01"), ("E14", 12m)])]);

        var sheet = Assert.Single(Compare("working-paper", files));

        Assert.Equal(
            [("B", "different", 1), ("C", "date-format", 2), ("E", "actual-higher", 1)],
            sheet.BlockingByColumn.Select(static tally => (tally.Column, tally.Relation, tally.Count)).ToArray());
    }

    // 底稿範本依裁定改過的文字：只放行裁定寫的那一種改法。
    [Theory]
    [InlineData("cover-a6", true)]
    [InlineData("cover-a6-rewritten", false)]
    [InlineData("step3-b7", true)]
    [InlineData("step3-b7-replaced", false)]
    [InlineData("step4-a12", true)]
    [InlineData("step4-a12-other", false)]
    [InlineData("step12-a4", true)]
    [InlineData("step12-a4-other", false)]
    public void WorkingPaper_RuledTemplateText_AcceptsOnlyTheRuledChange(string scenario, bool accepted)
    {
        var (sheetName, address, legacy, jet, decision) = scenario switch
        {
            "cover-a6" => (WorkingPaperSheetCatalog.Cover, "A6", "請詳：legacy-document.docx", (string?)null,
                PrivateCaseResultCellComparison.CoverWithoutDocumentName),
            "cover-a6-rewritten" => (WorkingPaperSheetCatalog.Cover, "A6", "請詳：legacy-document.docx", "其他文字",
                PrivateCaseResultCellComparison.CoverWithoutDocumentName),
            "step3-b7" => (WorkingPaperSheetCatalog.Step3, "B7", "舊版原文", "舊版原文\nJET 的查核期間",
                PrivateCaseResultCellComparison.Step3LegacyTextFirst),
            "step3-b7-replaced" => (WorkingPaperSheetCatalog.Step3, "B7", "舊版原文", "JET 的查核期間",
                PrivateCaseResultCellComparison.Step3LegacyTextFirst),
            "step4-a12" => (WorkingPaperSheetCatalog.Step4, "A12", "舊版範本句",
                "舊版範本句；空白傳票號碼的命中分錄逐筆列出，不計傳票張數，金額為該筆借方金額。",
                PrivateCaseResultCellComparison.Step4BlankVoucherNote),
            "step4-a12-other" => (WorkingPaperSheetCatalog.Step4, "A12", "舊版範本句", "舊版範本句；其他說明。",
                PrivateCaseResultCellComparison.Step4BlankVoucherNote),
            "step12-a4" => (WorkingPaperSheetCatalog.Step12, "A4", "舊版說明", "此處底稿係記錄分錄編製人員資訊",
                PrivateCaseResultCellComparison.Step12Description),
            "step12-a4-other" => (WorkingPaperSheetCatalog.Step12, "A4", "舊版說明", "其他說明",
                PrivateCaseResultCellComparison.Step12Description),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        (string, object)[] jetCells = jet is null ? [("A1", "fixed")] : [("A1", "fixed"), (address, jet)];
        using var files = new SyntheticPair(
            [(sheetName, [("A1", "fixed"), (address, legacy)])],
            [(sheetName, jetCells)]);

        var sheet = Assert.Single(Compare("working-paper", files));

        AssertOutcome(sheet, accepted, decision, address);
    }

    // step1-2 的編製人員表：JET 和舊版的列順序不同，表內的列不照位置比，而是比兩邊有哪些列；值不同的列照樣擋下。
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Step12_PreparerTable_ComparesTheRowsRegardlessOfOrder(bool changeCount, bool matches)
    {
        using var files = new SyntheticPair(
            [(WorkingPaperSheetCatalog.Step12,
                [("A1", "fixed"), ("B12", "creator-a"), ("D12", "3"), ("E12", "100"), ("B13", "creator-b"), ("D13", "2"), ("E13", "50")])],
            [(WorkingPaperSheetCatalog.Step12,
                [("A1", "fixed"), ("B12", "creator-b"), ("D12", 2m), ("E12", 50m), ("B13", "creator-a"), ("D13", changeCount ? 4m : 3m), ("E13", 100m)])]);

        var sheet = Assert.Single(Compare("working-paper", files));

        Assert.Equal(matches, sheet.Matches);
        if (!matches)
        {
            Assert.Equal(
                ["row-only-in-expected", "row-only-in-actual"],
                sheet.BlockingCells.Select(static cell => cell.Relation).ToArray());
            var json = JsonSerializer.Serialize(sheet);
            Assert.DoesNotContain("creator-a", json, StringComparison.Ordinal);
        }
    }

    // step1-2 的編製人員表：JET 按分錄筆數降冪、再按編製者升冪，舊版照 IDEA 彙總的順序（裁定 2026-11-16-07）。
    // 兩邊的列相同、只差順序時記成放行項目；順序相同時不記；少了一列照樣擋下，也不記。
    [Theory]
    [InlineData("reordered", true, true)]
    [InlineData("same-order", true, false)]
    [InlineData("row-missing", false, false)]
    public void Step12_PreparerTable_RecordsTheRuledOrderOnlyWhenTheSameRowsAreReordered(
        string scenario,
        bool matches,
        bool recorded)
    {
        (string, object)[] legacyCells =
            [("A1", "fixed"), ("B12", "creator-a"), ("D12", 3m), ("E12", 100m), ("B13", "creator-b"), ("D13", 2m), ("E13", 50m)];
        (string, object)[] jetCells = scenario switch
        {
            "reordered" =>
                [("A1", "fixed"), ("B12", "creator-b"), ("D12", 2m), ("E12", 50m), ("B13", "creator-a"), ("D13", 3m), ("E13", 100m)],
            "same-order" => legacyCells,
            "row-missing" => [("A1", "fixed"), ("B12", "creator-a"), ("D12", 3m), ("E12", 100m)],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        using var files = new SyntheticPair(
            [(WorkingPaperSheetCatalog.Step12, legacyCells)],
            [(WorkingPaperSheetCatalog.Step12, jetCells)]);

        var sheet = Assert.Single(Compare("working-paper", files));

        Assert.Equal(matches, sheet.Matches);
        Assert.Equal(
            recorded,
            sheet.AcceptedCells.Any(static decision =>
                decision.DecisionId == PrivateCaseResultCellComparison.Step12PreparerOrder));
    }

    // 預篩選摘要 C8 財報準備開始日那一句：JET 照範本句子、日期寫 yyyy-MM-dd，舊版在日期前後各加一個空白、日期寫 8 位數字
    // （裁定 2026-11-16-07）。只放行去掉日期前後的空白、把日期換成同一種寫法之後整句相同的情形；
    // 日期不同、日期換成別的寫法、別處多了空白、少一個字或多一個字都擋下。
    [Theory]
    [InlineData("將篩選期末關帳開始日2025-01-01之後核准並入到查核年度總帳之分錄", true)]
    [InlineData("將篩選期末關帳開始日2025-01-02之後核准並入到查核年度總帳之分錄", false)]
    [InlineData("將篩選期末關帳開始日2025/01/01之後核准並入到查核年度總帳之分錄", false)]
    [InlineData("將篩選期末關帳開始日2025-01-01之後 核准並入到查核年度總帳之分錄", false)]
    [InlineData("將篩選期末關帳開始日2025-01-01之後核准並入查核年度總帳之分錄", false)]
    [InlineData("將篩選期末關帳開始日2025-01-01之後核准並入到查核年度總帳之分錄。", false)]
    public void PrescreenSummary_PrepStartSentence_AcceptsOnlyTheSpacingAndFormatOfTheSameDate(
        string jetSentence,
        bool accepted)
    {
        using var files = new SyntheticPair(
            [("Pre-screening_Report", [("C8", "將篩選期末關帳開始日 20250101 之後核准並入到查核年度總帳之分錄")])],
            [("Pre-screening_Report", [("C8", jetSentence)])]);

        var sheet = Assert.Single(Compare("prescreen-report", files));

        AssertOutcome(sheet, accepted, PrivateCaseResultCellComparison.PrescreenPrepStartSentence, "C8");
    }

    // 預篩選摘要第 12 列（R5）的 D 欄：舊版原句在欄位名稱外加【】，JET 寫的欄位名稱不加任何符號（裁定 2026-11-16-09）。
    // 只放行舊版去掉【】之後和 JET 一字不差的情形；欄位名稱不同、其他字不同、JET 也加了括號，或不在這一格，都擋下。
    [Theory]
    [InlineData("D12", "因於資料欄未配對時未配對編制人員欄位，故此預篩選程序未執行", true)]
    [InlineData("D12", "因於資料欄未配對時未配對編製人員欄位，故此預篩選程序未執行", false)]
    [InlineData("D12", "因於資料欄未配對時未配對編制人員欄位，此預篩選程序未執行", false)]
    [InlineData("D12", "因於資料欄未配對時未配對「編制人員」欄位，故此預篩選程序未執行", false)]
    [InlineData("D13", "因於資料欄未配對時未配對編制人員欄位，故此預篩選程序未執行", false)]
    public void PrescreenSummary_PreparerNotMappedSentence_AcceptsOnlyTheFieldNameWithoutBrackets(
        string address,
        string jetSentence,
        bool accepted)
    {
        using var files = new SyntheticPair(
            [("Pre-screening_Report", [(address, "因於資料欄未配對時未配對【編制人員】欄位，故此預篩選程序未執行")])],
            [("Pre-screening_Report", [(address, jetSentence)])]);

        var sheet = Assert.Single(Compare("prescreen-report", files));

        AssertOutcome(sheet, accepted, PrivateCaseResultCellComparison.FieldNameWithoutBrackets, address);
    }

    // 底稿 Step 4 從第 13 列起的總帳日期：JET 寫 yyyy-MM-dd，舊版寫 8 位數字（裁定 2026-11-16-07）。
    // 舊版存成數字或文字都一樣；只放行同一個日期，日期不同、日期後面多了時間，或不在總帳日期欄，都擋下。
    [Theory]
    [InlineData("C13", true, "2025-01-31", true)]
    [InlineData("C14", false, "2025-01-31", true)]
    [InlineData("C13", true, "2025-01-30", false)]
    [InlineData("C13", true, "2025-01-31 00:00:00", false)]
    [InlineData("D13", true, "2025-01-31", false)]
    public void Step4_GlDate_AcceptsOnlyTheSameDateWrittenAsIso(
        string address,
        bool legacyIsNumber,
        string jetDate,
        bool accepted)
    {
        object legacyDate = legacyIsNumber ? 20250131m : "20250131";
        using var files = new SyntheticPair(
            [(WorkingPaperSheetCatalog.Step4, [("A1", "fixed"), (address, legacyDate)])],
            [(WorkingPaperSheetCatalog.Step4, [("A1", "fixed"), (address, jetDate)])]);

        var sheet = Assert.Single(Compare("working-paper", files));

        AssertOutcome(sheet, accepted, PrivateCaseResultCellComparison.Step4GlDateFormat, address);
    }

    // 底稿 Step 3：JET 只寫有用到的情境代號，舊版留著範本在 B19 到 B28 印好的 C1 到 C10（裁定 2026-11-16-07）。
    // 只放行舊版那一列只有範本代號、條件理由與張數都空白的情形；舊版那一列有內容、代號不是範本那一格的代號時照樣擋下，
    // 有用到的情境每一格照樣比。
    [Theory]
    [InlineData("code-only", "")]
    [InlineData("legacy-row-has-reason", "B20,D20")]
    [InlineData("legacy-row-has-count", "B20,E20")]
    [InlineData("not-the-template-code", "B20")]
    [InlineData("used-row-count-changed", "E19")]
    public void Step3_UnusedTemplateCodes_AcceptsOnlyLegacyRowsWithJustTheCode(
        string scenario,
        string blockingAddresses)
    {
        (string, object)[] usedRow = [("A1", "fixed"), ("B19", "C1"), ("C19", "legacy-condition"), ("D19", "reason"), ("E19", 2m)];
        (string, object)[] legacyCells = scenario switch
        {
            "legacy-row-has-reason" => [.. usedRow, ("B20", "C2"), ("D20", "reason-2")],
            "legacy-row-has-count" => [.. usedRow, ("B20", "C2"), ("E20", 1m)],
            "not-the-template-code" => [.. usedRow, ("B20", "C5")],
            _ => [.. usedRow, ("B20", "C2")],
        };
        var jetCount = scenario == "used-row-count-changed" ? 3m : 2m;
        using var files = new SyntheticPair(
            [(WorkingPaperSheetCatalog.Step3, legacyCells)],
            [(WorkingPaperSheetCatalog.Step3,
                [("A1", "fixed"), ("B19", "C1"), ("C19", "jet-condition"), ("D19", "reason"), ("E19", jetCount)])]);

        var sheet = Assert.Single(Compare("working-paper", files));

        Assert.Equal(
            blockingAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries),
            sheet.BlockingCells.Select(static cell => cell.Address).ToArray());
        Assert.Equal(
            !blockingAddresses.Contains("B20", StringComparison.Ordinal),
            sheet.AcceptedCells.Any(static decision =>
                decision.DecisionId == PrivateCaseResultCellComparison.Step3UnusedScenarioCodes));
    }

    // 科目配對範本：舊版這份是審計員填過的工作檔，C 欄有審計員選的分類，JET 匯出的範本留空給審計員填；
    // A2 的說明照裁定 2026-11-12-34 改寫。A、B 欄的科目照樣比。
    [Fact]
    public void AccountMapping_AuditorCategories_AreAcceptedOnlyWhereJetLeavesThemBlank()
    {
        using var files = new SyntheticPair(
            [("AccountMapping",
                [("A2", "舊版說明"), ("A4", "AC-01"), ("C4", "Cash"), ("A5", "AC-02"), ("C5", "Cash")])],
            [("AccountMapping",
                [("A2", "請在 C 欄選擇標準科目分類；要新增分類，請在 List 工作表的清單最後加上名稱，原有的名稱不要更改；A、B 欄由系統產生，不應修改。"),
                    ("A4", "AC-01"), ("A5", "AC-03"), ("C5", "Other")])]);

        var sheet = Assert.Single(Compare("account-mapping", files));

        Assert.Equal(
            [("A5", "different"), ("C5", "different")],
            sheet.BlockingCells.Select(static cell => (cell.Address, cell.Relation)).ToArray());
        Assert.Equal(
            [PrivateCaseResultCellComparison.AccountMappingInstruction, PrivateCaseResultCellComparison.AuditorFilledCategory],
            sheet.AcceptedCells.Select(static decision => decision.DecisionId).ToArray());
    }

    // 沒有固定位置審計結果的頁不逐格比：抽樣頁的範本文字與隨機樣本、兩張欄位資訊頁、假期假日資訊與科目分類清單。
    [Theory]
    [InlineData("inf-report", "INF Testing 可靠性測試")]
    [InlineData("working-paper", WorkingPaperSheetCatalog.Step2)]
    [InlineData("working-paper", WorkingPaperSheetCatalog.FieldInfo)]
    [InlineData("validation-report", WorkingPaperSheetCatalog.FieldInfo)]
    [InlineData("working-paper", WorkingPaperSheetCatalog.CalendarInfo)]
    [InlineData("account-mapping", "List")]
    public void Compare_PagesWithoutFixedPositionResults_AreNotComparedCellByCell(string reportSlug, string sheetName)
    {
        using var files = new SyntheticPair(
            [(sheetName, [("A1", "legacy")])],
            [(sheetName, [("A1", "jet")])]);

        Assert.Empty(Compare(reportSlug, files));
    }

    private static IReadOnlyList<PrivateCaseResultSheetSummary> Compare(string reportSlug, SyntheticPair files) =>
        PrivateCaseResultCellComparison.Compare(
            reportSlug,
            files.ExpectedPath,
            files.ActualPath,
            contentMask: null,
            clientName: SyntheticClient);

    private static void AssertOutcome(
        PrivateCaseResultSheetSummary sheet,
        bool accepted,
        string decisionId,
        string address)
    {
        if (accepted)
        {
            Assert.Empty(sheet.BlockingCells);
            Assert.Contains(sheet.AcceptedCells, decision => decision.DecisionId == decisionId);
        }
        else
        {
            Assert.Contains(sheet.BlockingCells, cell => cell.Address == address);
            Assert.DoesNotContain(sheet.AcceptedCells, decision => decision.DecisionId == decisionId);
        }
    }

    private sealed class SyntheticPair : IDisposable
    {
        private readonly string _root;

        internal SyntheticPair(
            IReadOnlyList<(string Sheet, (string Address, object Value)[] Cells)> expected,
            IReadOnlyList<(string Sheet, (string Address, object Value)[] Cells)> actual)
        {
            _root = Path.Combine(Path.GetTempPath(), $"jet-result-cells-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            ExpectedPath = Path.Combine(_root, "expected.xlsx");
            ActualPath = Path.Combine(_root, "actual.xlsx");
            Write(ExpectedPath, expected);
            Write(ActualPath, actual);
        }

        internal string ExpectedPath { get; }

        internal string ActualPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static void Write(
            string path,
            IReadOnlyList<(string Sheet, (string Address, object Value)[] Cells)> sheets)
        {
            using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook(new Sheets());
            uint sheetId = 1;
            foreach (var (sheetName, cells) in sheets)
            {
                var rows = cells
                    .GroupBy(static cell => RowOf(cell.Address))
                    .OrderBy(static group => group.Key)
                    .Select(static group =>
                    {
                        var row = new Row { RowIndex = group.Key };
                        row.Append(group.Select(static cell => cell.Value switch
                        {
                            decimal number => new Cell
                            {
                                CellReference = cell.Address,
                                CellValue = new CellValue(number.ToString(CultureInfo.InvariantCulture)),
                            },
                            string text => new Cell
                            {
                                CellReference = cell.Address,
                                DataType = CellValues.InlineString,
                                InlineString = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve }),
                            },
                            _ => throw new ArgumentOutOfRangeException(nameof(cells)),
                        }));
                        return row;
                    })
                    .ToArray();
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                worksheetPart.Worksheet = new Worksheet(new SheetData(rows));
                worksheetPart.Worksheet.Save();
                workbookPart.Workbook.Sheets!.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = sheetId++,
                    Name = sheetName,
                });
            }
            workbookPart.Workbook.Save();
        }

        private static uint RowOf(string address) =>
            uint.Parse(address.AsSpan(address.IndexOfAny("0123456789".ToCharArray())), CultureInfo.InvariantCulture);
    }
}
