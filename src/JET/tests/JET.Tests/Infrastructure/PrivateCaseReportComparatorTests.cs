using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseReportComparatorTests
{
    [Fact]
    public void K5_ManualAutoDisplayDecision_DoesNotAcceptUnlocalizedRowDifferences()
    {
        var decision = new PrivateCaseExplicitContentDecision("step41-manual-auto",
            LegacyAuditParityContentDimension.RowValues, PrivateCaseReportDifferencePolicy.ApprovedManualAutoDisplay);
        var difference = new LegacyAuditParityContentDifference("working-paper",
            "step4-1 符合高風險條件傳票明細", LegacyAuditParityContentDimension.RowValues, 1);
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveContent("working-paper", difference, [decision]));
    }

    [Theory]
    [InlineData("ManualEquivalent", true, true)]
    [InlineData("ManualEquivalent", false, false)]
    [InlineData("ManualSwapped", true, false)]
    [InlineData("ManualEmpty", true, false)]
    public void ManualFlagNormalizationRequiresExactApprovedValues(string difference, bool approved, bool passed)
    {
        using var fixture = new SyntheticFixture(Enum.Parse<SyntheticDifference>(difference));
        var decisions = approved ? new[] { new PrivateCaseExplicitContentDecision("step41-manual-auto",
            LegacyAuditParityContentDimension.RowValues, PrivateCaseReportDifferencePolicy.ApprovedManualAutoDisplay) } : [];
        var result = PrivateCaseReportComparator.Compare(fixture.Pairs, PrivateCaseAcceptancePolicy.Current, decisions);
        Assert.Equal(passed, result.Passed);
        var paper = Assert.Single(result.Reports, r => r.Kind == LegacyReportKind.WorkingPaper);
        Assert.Equal(passed, paper.ContentMatches);
    }

    // 空白位置移動：舊端第 2 筆空白、第 3 筆為 0；JET 端第 2 筆自動、第 3 筆空白。只看有值的儲存格兩邊都是「人工、自動」，
    // 保留空白位置後才看得出旗標落在不同傳票上。
    [Fact]
    public void ManualFlagNormalizationCatchesMovedBlankPositions()
    {
        using var fixture = new SyntheticFixture(SyntheticDifference.ManualBlankMoved);
        var result = PrivateCaseReportComparator.Compare(fixture.Pairs, PrivateCaseAcceptancePolicy.Current, [ManualAutoDecision]);
        var paper = Assert.Single(result.Reports, r => r.Kind == LegacyReportKind.WorkingPaper);
        Assert.False(paper.ContentMatches);
        Assert.False(result.Passed);
    }

    // 正規化只改 Step 4-1 的人工旗標欄：同頁其他 1、0 欄，以及其他工作表同名欄的 1、0 都保持原值，不產生任何差異。
    [Theory]
    [InlineData("ManualOtherColumnUntouched")]
    [InlineData("ManualOtherSheetUntouched")]
    public void ManualFlagNormalizationOnlyRewritesTheStep41ManualColumn(string difference)
    {
        using var fixture = new SyntheticFixture(Enum.Parse<SyntheticDifference>(difference));
        var result = PrivateCaseReportComparator.Compare(fixture.Pairs, PrivateCaseAcceptancePolicy.Current, [ManualAutoDecision]);
        var paper = Assert.Single(result.Reports, r => r.Kind == LegacyReportKind.WorkingPaper);
        Assert.Equal(0, paper.ContentDifferenceCount);
        Assert.True(result.Passed);
    }

    private static PrivateCaseExplicitContentDecision ManualAutoDecision => new("step41-manual-auto",
        LegacyAuditParityContentDimension.RowValues, PrivateCaseReportDifferencePolicy.ApprovedManualAutoDisplay);

    [Fact]
    public void Compare_DifferentInfMembersAndJetMetadata_AreNotTreatedAsReportDifferences()
    {
        using var fixture = new SyntheticFixture(SyntheticDifference.InfRandomContentOnly);

        var result = PrivateCaseReportComparator.Compare(
            fixture.Pairs,
            PrivateCaseAcceptancePolicy.Current,
            []);

        Assert.True(result.Passed);
        Assert.Equal(6, result.ReportCount);
        Assert.Equal(0, result.FailedReportCount);
        Assert.All(result.Reports, static report => Assert.True(report.Passed));

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(fixture.Root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic-random-member", json, StringComparison.Ordinal);
        Assert.DoesNotContain("JET_Metadata", json, StringComparison.Ordinal);
        Assert.DoesNotContain("INF Testing", json, StringComparison.Ordinal);
        Assert.StartsWith("private case report comparison", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_UnmaskedCellValueChange_FailsContentComparison()
    {
        using var fixture = new SyntheticFixture(SyntheticDifference.UnmaskedContent);

        var result = PrivateCaseReportComparator.Compare(
            fixture.Pairs,
            PrivateCaseAcceptancePolicy.Current,
            []);

        var report = Assert.Single(result.Reports, item =>
            item.Kind == LegacyReportKind.ValidationReport);
        Assert.False(result.Passed);
        Assert.False(report.ContentMatches);
        Assert.True(report.AppearanceMatches);
        Assert.True(report.ContentDifferenceCount > 0);
        Assert.True(report.ContentDifferenceMagnitude > 0);
        Assert.True(report.BlockingContentDifferenceCount > 0);
        Assert.True(report.BlockingContentDifferenceMagnitude > 0);
        Assert.NotEmpty(report.BlockingContentDifferenceLocations);
        Assert.Contains(
            report.BlockingContentColumnDetails,
            static detail => detail.ColumnRole == "source-or-derived-column");
        Assert.All(
            report.BlockingContentDifferenceLocations,
            static difference => Assert.True(difference.ScopePosition >= 0));
    }

    [Fact]
    public void Compare_ColumnWidthChange_IsComparedAndClassifiedAsDynamicWidth()
    {
        using var fixture = new SyntheticFixture(SyntheticDifference.AcceptedColumnWidth);

        var result = PrivateCaseReportComparator.Compare(
            fixture.Pairs,
            PrivateCaseAcceptancePolicy.Current,
            []);

        var report = Assert.Single(result.Reports, item =>
            item.Kind == LegacyReportKind.ValidationReport);
        Assert.True(result.Passed);
        Assert.True(report.ContentMatches);
        Assert.True(report.AppearanceMatches);
        Assert.True(report.AppearanceDifferenceCount > 0);
        Assert.Equal(0, report.UnclassifiedAppearanceDifferenceCount);
        Assert.Contains(
            report.AppearanceDifferenceDetails,
            static detail => detail.SheetPosition == 1
                && detail.ScopeKind == "column"
                && detail.Property == "width");
        Assert.Contains(
            report.AcceptedAppearanceDecisions,
            static decision => decision.DecisionId
                == PrivateCaseReportDifferencePolicy.DynamicSafeWidth);
    }

    [Fact]
    public void Compare_UnknownDirectPageAppearanceDifference_RemainsBlocking()
    {
        using var fixture = new SyntheticFixture(SyntheticDifference.UnsupportedAppearance);

        var result = PrivateCaseReportComparator.Compare(
            fixture.Pairs,
            PrivateCaseAcceptancePolicy.Current,
            []);

        var report = Assert.Single(result.Reports, item =>
            item.Kind == LegacyReportKind.ValidationReport);
        Assert.False(result.Passed);
        Assert.True(report.ContentMatches);
        Assert.False(report.AppearanceMatches);
        Assert.True(report.UnclassifiedAppearanceDifferenceCount > 0);
        Assert.Contains(
            report.UnclassifiedAppearanceDifferenceDetails,
            static detail => detail.SheetPosition == 1
                && detail.ScopeKind == "row"
                && detail.Property == "height");
    }

    [Fact]
    public void DifferencePolicy_ExplicitDecision_AppliesOnlyToItsDeclaredTarget()
    {
        var decision = new PrivateCaseExplicitContentDecision(
            "validation-v5",
            LegacyAuditParityContentDimension.RowValues,
            PrivateCaseReportDifferencePolicy.ApprovedCompletenessTotals);
        var target = new LegacyAuditParityContentDifference(
            "validation-report",
            "V_Report 5",
            LegacyAuditParityContentDimension.RowValues,
            1);
        var other = target with { SheetName = "V_Report 3" };

        Assert.True(PrivateCaseReportDifferencePolicy.IsSupportedExplicitContentDecision(decision));
        Assert.Equal(
            PrivateCaseReportDifferencePolicy.ApprovedCompletenessTotals,
            PrivateCaseReportDifferencePolicy.ResolveContent(
                "validation-report",
                target,
                [decision]));
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveContent(
            "validation-report",
            other,
            [decision]));
    }

    [Fact]
    public void DifferencePolicy_CompletenessReadableRows_OnlyAcceptsTheApprovedHeightAndSheet()
    {
        var group = new SpreadsheetAppearanceDifferenceGroup("V_Report 5", "row", "height", "12.5", "18", 1, "V_Report 5!row:1");
        Assert.Equal(PrivateCaseReportDifferencePolicy.ReadableCompletenessRows,
            PrivateCaseReportDifferencePolicy.ResolveAppearance("validation-report", group, [group]));
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveAppearance("validation-report", group with { ActualValue = "24" }, [group]));
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveAppearance("validation-report", group with { SheetName = "V_Report 4" }, [group]));
    }

    [Theory]
    [InlineData("scenario-1-tag")]
    [InlineData("scenario-5-tag")]
    [InlineData("scenario-10-tag")]
    public void DifferencePolicy_SafeColumnRole_RecognizesOnlyPublicScenarioTags(
        string expectedRole)
    {
        var position = expectedRole.Split('-')[1];
        var bytes = System.Text.Encoding.UTF8.GetBytes($"C{position}_TAG");
        var key = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(bytes).AsSpan(0, 16));

        Assert.Equal(expectedRole, PrivateCaseReportDifferencePolicy.SafeColumnRole(key));
        Assert.Equal(
            "source-or-derived-column",
            PrivateCaseReportDifferencePolicy.SafeColumnRole(new string('0', 32)));
    }

    [Fact]
    public void Compare_ScenarioTagDifference_ReportsOnlyKnownLiteralCounts()
    {
        using var fixture = new SyntheticFixture(SyntheticDifference.ScenarioTagContent);

        var result = PrivateCaseReportComparator.Compare(
            fixture.Pairs,
            PrivateCaseAcceptancePolicy.Current,
            []);

        var report = Assert.Single(result.Reports, item =>
            item.Kind == LegacyReportKind.WorkingPaper);
        var detail = Assert.Single(report.BlockingContentColumnDetails, item =>
            item.ColumnRole == "scenario-1-tag");
        var values = Assert.IsType<PrivateCaseScenarioTagValueSummary>(detail.ScenarioTagValues);
        Assert.Equal((1, 1, 0), (
            values.ExpectedYesCount,
            values.ExpectedNoCount,
            values.ExpectedOtherCount));
        Assert.Equal((1, 0, 0), (
            values.ActualYesCount,
            values.ActualNoCount,
            values.ActualOtherCount));
    }

    [Theory]
    [InlineData("numberFormat.code", "\"private-format-token\"0", "numeric")]
    [InlineData("font.name", "private-font-token", "other")]
    [InlineData("alignment.vertical", "center", "center")]
    public void SafeAppearanceValue_DoesNotReturnUnboundedWorkbookText(
        string property,
        string value,
        string expected)
    {
        Assert.Equal(expected, PrivateCaseReportComparator.SafeAppearanceValue(property, value));
    }

    [Fact]
    public void Compare_MalformedWorkbook_FailsWithoutEchoingAPath()
    {
        using var fixture = new SyntheticFixture(SyntheticDifference.MalformedActual);

        var error = Assert.Throws<PrivateCaseReportComparisonException>(() =>
            PrivateCaseReportComparator.Compare(
                fixture.Pairs,
                PrivateCaseAcceptancePolicy.Current,
                []));

        Assert.Equal(PrivateCaseReportComparisonFailure.WorkbookUnavailable, error.Failure);
        Assert.DoesNotContain(fixture.Root, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("generated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private enum SyntheticDifference
    {
        InfRandomContentOnly,
        UnmaskedContent,
        AcceptedColumnWidth,
        UnsupportedAppearance,
        ScenarioTagContent,
        MalformedActual,
        ManualEquivalent,
        ManualSwapped,
        ManualEmpty,
        ManualBlankMoved,
        ManualOtherColumnUntouched,
        ManualOtherSheetUntouched,
    }

    private sealed class SyntheticFixture : IDisposable
    {
        private readonly PrivateCaseInputWorkspace _workspace;

        internal SyntheticFixture(SyntheticDifference difference)
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"jet-private-report-comparison-{Guid.NewGuid():N}");
            var sourceRoot = System.IO.Path.Combine(Root, "source");
            var generatedRoot = System.IO.Path.Combine(Root, "generated");
            var ownedRoot = System.IO.Path.Combine(Root, "owned");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(generatedRoot);
            Directory.CreateDirectory(ownedRoot);

            var expectedFiles = new Dictionary<LegacyReportKind, string>();
            var artifacts = new List<LegacyAuditParityJourneyArtifact>();
            foreach (var (kind, index) in Enum.GetValues<LegacyReportKind>().Select(
                         static (kind, index) => (kind, index)))
            {
                var expectedName = $"reference-{index + 1:000}.xlsx";
                var actualName = $"generated-{index + 1:000}.xlsx";
                var expectedPath = System.IO.Path.Combine(sourceRoot, expectedName);
                var actualPath = System.IO.Path.Combine(generatedRoot, actualName);
                WriteReport(expectedPath, kind, isActual: false, difference);
                if (difference == SyntheticDifference.MalformedActual
                    && kind == LegacyReportKind.ValidationReport)
                {
                    File.WriteAllBytes(actualPath, [80, 75, 3, 4, 0]);
                }
                else
                {
                    WriteReport(actualPath, kind, isActual: true, difference);
                }

                expectedFiles.Add(kind, expectedName);
                artifacts.Add(new LegacyAuditParityJourneyArtifact(kind, actualName, actualPath));
            }

            _workspace = PrivateCaseInputWorkspace.Create(ownedRoot);
            var expected = PrivateCaseExpectedReportSet.Create(
                PrivateCaseExpectedReportSetTests.CreateManifest(expectedFiles),
                sourceRoot,
                _workspace);
            var actual = PrivateCaseActualReportSet.Create(
                artifacts,
                generatedRoot,
                _workspace);
            Pairs = PrivateCaseReportPairSet.Create(expected, actual);
        }

        internal string Root { get; }

        internal PrivateCaseReportPairSet Pairs { get; }

        public void Dispose()
        {
            _workspace.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private static void WriteReport(
        string path,
        LegacyReportKind kind,
        bool isActual,
        SyntheticDifference difference)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        var sheets = workbookPart.Workbook.Sheets!;
        uint sheetId = 1;

        if (kind == LegacyReportKind.InfReport)
        {
            AddSheet(
                workbookPart,
                sheets,
                ref sheetId,
                "INF Testing 可靠性測試",
                [
                    Row(1, InlineCell("A1", "fixed-title")),
                    Row(
                        53,
                        NumberCell("A53", "1"),
                        InlineCell(
                            "B53",
                            isActual
                                ? "synthetic-random-member-actual"
                                : "synthetic-random-member-expected")),
                ]);
            AddSheet(
                workbookPart,
                sheets,
                ref sheetId,
                "可靠性樣本_所有欄位",
                [
                    Row(1, InlineCell("A1", "field-name")),
                    Row(
                        2,
                        InlineCell(
                            "A2",
                            isActual
                                ? "synthetic-random-member-actual"
                                : "synthetic-random-member-expected")),
                ]);
        }
        else if (kind == LegacyReportKind.WorkingPaper && difference is SyntheticDifference.ManualEquivalent
            or SyntheticDifference.ManualSwapped or SyntheticDifference.ManualEmpty)
        {
            var first = difference == SyntheticDifference.ManualSwapped ? "自動" : "人工";
            var second = difference == SyntheticDifference.ManualSwapped ? "人工" : "自動";
            if (difference == SyntheticDifference.ManualEmpty) first = second = "";
            AddSheet(workbookPart, sheets, ref sheetId, "step4-1 符合高風險條件傳票明細",
            [
                Row(5, InlineCell("A5", "source-field"), InlineCell("B5", "人工傳票否_JE_S")),
                Row(6, InlineCell("A6", "row-1"), isActual ? InlineCell("B6", first) : NumberCell("B6", "1")),
                Row(7, InlineCell("A7", "row-2"), isActual ? InlineCell("B7", second) : NumberCell("B7", "0")),
                Row(8, InlineCell("A8", "row-3"))
            ]);
        }
        else if (kind == LegacyReportKind.WorkingPaper && difference == SyntheticDifference.ManualBlankMoved)
        {
            AddSheet(workbookPart, sheets, ref sheetId, "step4-1 符合高風險條件傳票明細",
            [
                Row(5, InlineCell("A5", "source-field"), InlineCell("B5", "人工傳票否_JE_S")),
                Row(6, InlineCell("A6", "row-1"), isActual ? InlineCell("B6", "人工") : NumberCell("B6", "1")),
                isActual ? Row(7, InlineCell("A7", "row-2"), InlineCell("B7", "自動")) : Row(7, InlineCell("A7", "row-2")),
                isActual ? Row(8, InlineCell("A8", "row-3")) : Row(8, InlineCell("A8", "row-3"), NumberCell("B8", "0")),
            ]);
        }
        else if (kind == LegacyReportKind.WorkingPaper && difference == SyntheticDifference.ManualOtherColumnUntouched)
        {
            AddSheet(workbookPart, sheets, ref sheetId, "step4-1 符合高風險條件傳票明細",
            [
                Row(5, InlineCell("A5", "source-field"), InlineCell("B5", "人工傳票否_JE_S"), InlineCell("C5", "other-flag")),
                Row(6, InlineCell("A6", "row-1"), isActual ? InlineCell("B6", "人工") : NumberCell("B6", "1"), NumberCell("C6", "1")),
                Row(7, InlineCell("A7", "row-2"), isActual ? InlineCell("B7", "自動") : NumberCell("B7", "0"), NumberCell("C7", "0")),
            ]);
        }
        else if (kind == LegacyReportKind.WorkingPaper && difference == SyntheticDifference.ManualOtherSheetUntouched)
        {
            AddSheet(workbookPart, sheets, ref sheetId, "step4-1 符合高風險條件傳票明細",
            [
                Row(5, InlineCell("A5", "source-field"), InlineCell("B5", "人工傳票否_JE_S")),
                Row(6, InlineCell("A6", "row-1"), isActual ? InlineCell("B6", "人工") : NumberCell("B6", "1")),
            ]);
            AddSheet(workbookPart, sheets, ref sheetId, "step1-2 synthetic",
            [
                Row(5, InlineCell("A5", "source-field"), InlineCell("B5", "人工傳票否_JE_S")),
                Row(6, InlineCell("A6", "row-1"), NumberCell("B6", "1")),
                Row(7, InlineCell("A7", "row-2"), NumberCell("B7", "0")),
            ]);
        }
        else if (kind == LegacyReportKind.WorkingPaper
            && difference == SyntheticDifference.ScenarioTagContent)
        {
            AddSheet(
                workbookPart,
                sheets,
                ref sheetId,
                "step4-1 符合高風險條件傳票明細",
                [
                    Row(
                        5,
                        InlineCell("A5", "source-field"),
                        InlineCell("B5", "C1_TAG")),
                    Row(
                        6,
                        InlineCell("A6", "row-1"),
                        InlineCell("B6", "Y")),
                    isActual
                        ? Row(7, InlineCell("A7", "row-2"))
                        : Row(
                            7,
                            InlineCell("A7", "row-2"),
                            InlineCell("B7", "N")),
                ]);
        }
        else
        {
            var value = difference == SyntheticDifference.UnmaskedContent
                && isActual
                && kind == LegacyReportKind.ValidationReport
                    ? "changed-fixed-content"
                    : "fixed-content";
            var isValidation = kind == LegacyReportKind.ValidationReport;
            AddSheet(
                workbookPart,
                sheets,
                ref sheetId,
                isValidation ? "V_Report 3" : "Report",
                isValidation
                    ? [
                        Row(1, InlineCell("A1", "field-name")),
                        Row(2, InlineCell("A2", value)),
                    ]
                    : [Row(1, InlineCell("A1", value))],
                wideFirstColumn: difference == SyntheticDifference.AcceptedColumnWidth
                    && isActual
                    && isValidation,
                tallFirstRow: difference == SyntheticDifference.UnsupportedAppearance
                    && isActual
                    && isValidation);
        }

        if (isActual && kind == LegacyReportKind.InfReport)
        {
            AddSheet(
                workbookPart,
                sheets,
                ref sheetId,
                ReportWorkbookMetadataFormat.WorksheetName,
                [Row(1, InlineCell("A1", "synthetic-random-member-metadata"))],
                wideFirstColumn: true);
        }

        workbookPart.Workbook.Save();
    }

    private static void AddSheet(
        WorkbookPart workbookPart,
        Sheets sheets,
        ref uint sheetId,
        string name,
        IReadOnlyList<Row> rows,
        bool wideFirstColumn = false,
        bool tallFirstRow = false)
    {
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        if (tallFirstRow)
        {
            rows[0].Height = 30D;
            rows[0].CustomHeight = true;
        }
        var sheetData = new SheetData(rows);
        worksheetPart.Worksheet = wideFirstColumn
            ? new Worksheet(
                new Columns(new Column
                {
                    Min = 1,
                    Max = 1,
                    Width = 25D,
                    CustomWidth = true,
                }),
                sheetData)
            : new Worksheet(sheetData);
        worksheetPart.Worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = sheetId,
            Name = name,
        });
        sheetId++;
    }

    private static Row Row(uint index, params Cell[] cells)
    {
        var row = new Row { RowIndex = index };
        row.Append(cells);
        return row;
    }

    private static Cell InlineCell(string reference, string value) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value)),
    };

    private static Cell NumberCell(string reference, string value) => new()
    {
        CellReference = reference,
        CellValue = new CellValue(value),
    };
}
