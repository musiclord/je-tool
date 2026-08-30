using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyWorkbookHeaderCatalogTests
{
    [Fact]
    public void HeaderCatalog_IsClosedAndRetainsDynamicFieldInfoAndContinuationRules()
    {
        Assert.Equal(
            [3U],
            LegacyWorkbookHeaderCatalog.Rule(
                    LegacyReportKind.AccountMapping,
                    "AccountMapping")
                .FixedRows);
        Assert.Equal(
            3U,
            LegacyWorkbookHeaderCatalog.Rule(
                    LegacyReportKind.ValidationReport,
                    "自動化工具-檔案欄位資訊")
                .RepeatedHeaderPrototypeRow);
        Assert.Equal(
            [1U],
            LegacyWorkbookHeaderCatalog.Rule(
                    LegacyReportKind.ValidationReport,
                    "V_Report 6 (2)")
                .FixedRows);
        Assert.Equal(
            [11U],
            LegacyWorkbookHeaderCatalog.Rule(
                    LegacyReportKind.WorkingPaper,
                    "step4 符合高風險條件傳票 (續2)")
                .FixedRows);
        Assert.Equal(
            [1U],
            LegacyWorkbookHeaderCatalog.Rule(
                    LegacyReportKind.WorkingPaper,
                    "自動化工具-假期假日資訊 (續2)")
                .FixedRows);
        Assert.Throws<InvalidDataException>(() =>
            LegacyWorkbookHeaderCatalog.Rule(
                LegacyReportKind.WorkingPaper,
                "unknown-sheet"));
    }

    [Fact]
    public void HeaderCatalog_CoversTheExplicitCurrentSourceAndTemplateBaseSheetUnion()
    {
        var currentBaseSheets = new Dictionary<LegacyReportKind, string[]>
        {
            [LegacyReportKind.ValidationReport] =
            [
                "ValidationReport",
                "自動化工具-檔案欄位資訊",
                "完整性測試出現差異時之指引",
                "step1-3 完整性測試之差異說明",
                "V_Report 1",
                "V_Report 2",
                "V_Report 3",
                "V_Report 4",
                "V_Report 5",
                "V_Report 6",
                "V_Report 7",
            ],
            [LegacyReportKind.AccountMapping] = ["AccountMapping", "List"],
            [LegacyReportKind.InfReport] =
                ["INF Testing 可靠性測試", "可靠性樣本_所有欄位"],
            [LegacyReportKind.PrescreenReport] =
                ["Pre-screening_Report", "R1", "R2", "R3", "R4", "R5", "R6", "R7"],
            [LegacyReportKind.CriteriaSelectionReport] =
            [
                "Summary Inforamtion",
                "#Criteria Select 1",
                "#Criteria Select 2",
                "#Criteria Select 3",
                "#Criteria Select 4",
                "#Criteria Select 5",
                "#Criteria Select 6",
                "#Criteria Select 7",
                "#Criteria Select 8",
                "#Criteria Select 9",
                "#Criteria Select 10",
            ],
            [LegacyReportKind.WorkingPaper] =
            [
                "資料預先整理之說明",
                "JE WorkingPaper說明",
                "step1 完整性測試",
                "step1-1 借貸不平測試",
                "step1-2 分錄編製人員說明",
                "step1-3 完整性測試之差異說明",
                "step2 可靠性測試",
                "step3 高風險條件彙總",
                "step4 符合高風險條件傳票",
                "step4-1 符合高風險條件傳票明細",
                "step5 財務報表關帳後調整之分錄",
                "自動化工具-檔案欄位資訊",
                "自動化工具-假期假日資訊",
                "自動化工具-科目配對資訊",
            ],
        };

        Assert.Equal(
            48,
            currentBaseSheets.Sum(report => report.Value.Length));
        foreach (var (report, sheets) in currentBaseSheets)
        {
            Assert.All(
                sheets,
                sheet => Assert.NotNull(
                    LegacyWorkbookHeaderCatalog.Rule(report, sheet)));
        }

        var unsupportedPhysicalSheets = new[]
        {
            (LegacyReportKind.ValidationReport, "完整性測試出現差異時之指引 (2)"),
            (LegacyReportKind.ValidationReport, "V_Report (2)"),
            (LegacyReportKind.ValidationReport, "V_Report 8"),
            (LegacyReportKind.PrescreenReport, "R1 (續2)"),
            (LegacyReportKind.CriteriaSelectionReport, "#Criteria Select 11"),
            (LegacyReportKind.CriteriaSelectionReport, "#Criteria Select 1 (續2)"),
            (LegacyReportKind.WorkingPaper, "step2 可靠性測試 (續2)"),
            (LegacyReportKind.WorkingPaper, "step4-1 (續2)"),
            (LegacyReportKind.WorkingPaper, "step4 符合高風險條件傳票 (2)"),
            (LegacyReportKind.AccountMapping, "List (2)"),
        };
        Assert.All(
            unsupportedPhysicalSheets,
            item => Assert.Throws<InvalidDataException>(() =>
                LegacyWorkbookHeaderCatalog.Rule(
                    item.Item1,
                    item.Item2)));
    }

}
