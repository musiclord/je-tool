using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseReportComparatorTests
{
    [Fact]
    public void ManualAutoDisplayDecision_DoesNotAcceptUnlocalizedRowDifferences()
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
    public void ManualFlagNormalizationRequiresTheDecisionAndEquivalentValues(string difference, bool withDecision, bool passed)
    {
        using var fixture = new SyntheticFixture(Enum.Parse<SyntheticDifference>(difference));
        var decisions = withDecision ? new[] { new PrivateCaseExplicitContentDecision("step41-manual-auto",
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
    public void DifferencePolicy_FontScaledRowHeights_OnlyAcceptsTheExpectedHeightPerSheet()
    {
        // 10 點明細頁列高 15 點、12 點頁 18 點。
        var group = new SpreadsheetAppearanceDifferenceGroup("V_Report 5", "row", "height", "12.5", "15", 1, "V_Report 5!row:1");
        Assert.Equal(PrivateCaseReportDifferencePolicy.FontScaledRowHeights,
            PrivateCaseReportDifferencePolicy.ResolveAppearance("validation-report", group, [group]));
        Assert.Equal(PrivateCaseReportDifferencePolicy.FontScaledRowHeights,
            PrivateCaseReportDifferencePolicy.ResolveAppearance("prescreen-report", group with { SheetName = "R3" }, [group]));
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveAppearance("validation-report", group with { ActualValue = "18" }, [group]));
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveAppearance("validation-report", group with { ActualValue = "24" }, [group]));
        var sample = group with { SheetName = "可靠性樣本_所有欄位", ScopeKind = "sheetFormat", Property = "defaultRowHeight" };
        Assert.Equal(PrivateCaseReportDifferencePolicy.FontScaledRowHeights,
            PrivateCaseReportDifferencePolicy.ResolveAppearance("inf-report", sample with { ActualValue = "18" }, [sample]));
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveAppearance("inf-report", sample, [sample]));
    }

    // 外觀快照把等於 Excel 預設的 15 點工作表列高省略不記，所以 JET 寫的 15 點，
    // 在差異裡的實際值是「<default>」，不是 15。上面那則測試直接造出實際值 15 的差異、沒有經過快照，
    // 這裡讓兩份合成工作簿走真正的快照再判斷。
    [Fact]
    public void DifferencePolicy_FontScaledRowHeights_AcceptsTheCapturedFifteenPointSheetDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jet-row-height-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var legacy = Path.Combine(directory, "legacy.xlsx");
            var formal = Path.Combine(directory, "formal.xlsx");
            var other = Path.Combine(directory, "other.xlsx");
            WriteDefaultRowHeightWorkbook(legacy, "R3", 12.5D);
            WriteDefaultRowHeightWorkbook(formal, "R3", 15D);
            WriteDefaultRowHeightWorkbook(other, "R3", 16.5D);
            var expected = SpreadsheetAppearanceFingerprint.Capture(legacy);

            var formalGroups = expected.DescribeDifferenceGroups(SpreadsheetAppearanceFingerprint.Capture(formal));
            var formalHeight = Assert.Single(formalGroups);
            Assert.Equal(("sheetFormat", "defaultRowHeight"), (formalHeight.ScopeKind, formalHeight.Property));
            Assert.Equal(PrivateCaseReportDifferencePolicy.FontScaledRowHeights,
                PrivateCaseReportDifferencePolicy.ResolveAppearance("prescreen-report", formalHeight, formalGroups));

            var otherGroups = expected.DescribeDifferenceGroups(SpreadsheetAppearanceFingerprint.Capture(other));
            Assert.Null(PrivateCaseReportDifferencePolicy.ResolveAppearance("prescreen-report", Assert.Single(otherGroups), otherGroups));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // 私人案件清單標明這項決定時，只放行 step4-1 的工作表預設列高，而且 JET 那邊要是內嵌範本的值，舊版的值不限。
    // 沒有標明、JET 寫了別的值、其他屬性、驗證報告直接比對的頁，或清單寫了不支援的決定或範圍，都不放行。
    // 底稿的其他頁、step4-1 的接續工作表，以及其他報告裡同名的頁，照非直接比對的頁放行，不算這項決定。
    [Fact]
    public void AppearanceDecision_Step41TemplateRowHeight_OnlyAcceptsTheSheetDefaultThatMatchesTheTemplate()
    {
        var group = new SpreadsheetAppearanceDifferenceGroup(
            "step4-1 符合高風險條件傳票明細",
            "sheetFormat",
            "defaultRowHeight",
            "16.5",
            PrivateCaseReportDifferencePolicy.Step41TemplateDefaultRowHeight,
            1,
            "step4-1 符合高風險條件傳票明細!sheetFormat");
        PrivateCaseExplicitAppearanceDecision[] declared = [Step41TemplateRowHeightDecision];
        var unsupported = Step41TemplateRowHeightDecision with { DecisionId = "unreviewed-decision" };
        var wrongScope = Step41TemplateRowHeightDecision with { Scope = "step41-manual-auto" };

        Assert.True(PrivateCaseReportDifferencePolicy.IsSupportedExplicitAppearanceDecision(
            Step41TemplateRowHeightDecision));
        Assert.Equal(
            PrivateCaseReportDifferencePolicy.TemplateDefaultRowHeight,
            PrivateCaseReportDifferencePolicy.ResolveAppearance("working-paper", group, [group], declared));
        Assert.Equal(
            PrivateCaseReportDifferencePolicy.TemplateDefaultRowHeight,
            PrivateCaseReportDifferencePolicy.ResolveAppearance(
                "working-paper",
                group with { ExpectedValue = SpreadsheetAppearanceFingerprint.UnrecordedValue },
                [group],
                declared));
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveAppearance("working-paper", group, [group]));
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveAppearance(
            "working-paper",
            group with { ActualValue = "18" },
            [group],
            declared));
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveAppearance(
            "working-paper",
            group with { ActualValue = SpreadsheetAppearanceFingerprint.UnrecordedValue },
            [group],
            declared));
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveAppearance(
            "working-paper",
            group with { Property = "defaultColumnWidth" },
            [group],
            declared));
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveAppearance(
            "validation-report",
            group with { SheetName = "V_Report 3" },
            [group],
            declared));
        Assert.Equal(
            PrivateCaseReportDifferencePolicy.JetStandardNonDirectPage,
            PrivateCaseReportDifferencePolicy.ResolveAppearance(
                "working-paper",
                group with { SheetName = WorkingPaperSheetCatalog.Step4 },
                [group],
                declared));
        Assert.Equal(
            PrivateCaseReportDifferencePolicy.JetStandardNonDirectPage,
            PrivateCaseReportDifferencePolicy.ResolveAppearance(
                "working-paper",
                group with
                {
                    SheetName = ExcelWorksheetConstraints.ContinuationSheetName(WorkingPaperSheetCatalog.Step41, 2),
                },
                [group],
                declared));
        Assert.Equal(
            PrivateCaseReportDifferencePolicy.JetStandardNonDirectPage,
            PrivateCaseReportDifferencePolicy.ResolveAppearance("validation-report", group, [group], declared));
        Assert.False(PrivateCaseReportDifferencePolicy.IsSupportedExplicitAppearanceDecision(unsupported));
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveAppearance(
            "working-paper",
            group,
            [group],
            [unsupported]));
        Assert.False(PrivateCaseReportDifferencePolicy.IsSupportedExplicitAppearanceDecision(wrongScope));
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveAppearance(
            "working-paper",
            group,
            [group],
            [wrongScope]));
    }

    // 這項決定放行的值要和內嵌底稿範本 step4-1 的預設列高相同；範本改了，這裡就失敗，放行的值要重新決定。
    [Fact]
    public void AppearanceDecision_Step41TemplateRowHeight_IsTheValueInTheEmbeddedTemplate()
    {
        var template = SpreadsheetAppearanceFingerprint.Capture(Path.Combine(
            TestRepositoryPaths.SolutionRoot,
            "JET",
            "Templates",
            "WorkingPaper.xlsx"));

        Assert.Equal(
            PrivateCaseReportDifferencePolicy.Step41TemplateDefaultRowHeight,
            template.Value("step4-1 符合高風險條件傳票明細!sheetFormat", "defaultRowHeight"));
    }

    // 舊版底稿 step4-1 的預設列高和 JET 不同。私人案件清單標明這項決定，而且 JET 寫的是範本的值，底稿才算一致。
    [Theory]
    [InlineData("Step41TemplateRowHeight", false, false)]
    [InlineData("Step41TemplateRowHeight", true, true)]
    [InlineData("Step41OtherRowHeight", true, false)]
    public void Compare_Step41DefaultRowHeight_PassesOnlyWithTheDeclaredAppearanceDecision(
        string difference,
        bool withDecision,
        bool passed)
    {
        using var fixture = new SyntheticFixture(Enum.Parse<SyntheticDifference>(difference));
        PrivateCaseExplicitAppearanceDecision[] decisions = withDecision ? [Step41TemplateRowHeightDecision] : [];

        var result = PrivateCaseReportComparator.Compare(
            fixture.Pairs,
            PrivateCaseAcceptancePolicy.Current,
            [],
            decisions);

        var paper = Assert.Single(result.Reports, static report => report.Kind == LegacyReportKind.WorkingPaper);
        Assert.True(paper.ContentMatches);
        Assert.Equal(1, paper.AppearanceDifferenceCount);
        Assert.Equal(passed, paper.AppearanceMatches);
        Assert.Equal(passed, result.Passed);
        Assert.Equal(
            passed,
            paper.AcceptedAppearanceDecisions.Any(static decision =>
                decision.DecisionId == PrivateCaseReportDifferencePolicy.TemplateDefaultRowHeight));
    }

    // 比對程式只接受支援的外觀決定，同一項也不能標明兩次。
    [Fact]
    public void Compare_UnsupportedOrDuplicateAppearanceDecision_IsRejected()
    {
        using var fixture = new SyntheticFixture(SyntheticDifference.Step41TemplateRowHeight);
        var unsupported = Step41TemplateRowHeightDecision with { DecisionId = "unreviewed-decision" };

        var unsupportedError = Assert.Throws<PrivateCaseReportComparisonException>(() =>
            PrivateCaseReportComparator.Compare(
                fixture.Pairs,
                PrivateCaseAcceptancePolicy.Current,
                [],
                [unsupported]));
        var duplicateError = Assert.Throws<PrivateCaseReportComparisonException>(() =>
            PrivateCaseReportComparator.Compare(
                fixture.Pairs,
                PrivateCaseAcceptancePolicy.Current,
                [],
                [Step41TemplateRowHeightDecision, Step41TemplateRowHeightDecision]));

        Assert.Equal(PrivateCaseReportComparisonFailure.InvalidInput, unsupportedError.Failure);
        Assert.Equal(PrivateCaseReportComparisonFailure.InvalidInput, duplicateError.Failure);
    }

    private static PrivateCaseExplicitAppearanceDecision Step41TemplateRowHeightDecision => new(
        "step41-default-row-height",
        PrivateCaseReportDifferencePolicy.TemplateDefaultRowHeight);

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

    // 舊版把配對過的欄改名成 _JE 結尾，JET 照來源欄名寫（裁定 2026-11-05-01）。欄名不同仍放行，
    // 用私人案件清單的配對把兩邊的欄對起來後，值不同就算差異；對不起來的欄照舊處理，但在摘要裡計數。
    [Theory]
    [InlineData("RenamedRawColumnChanged", false)]
    [InlineData("RenamedRawColumnEqual", true)]
    public void Compare_RenamedRawColumn_ComparesTheValuesThroughTheManifestMapping(
        string difference,
        bool passed)
    {
        using var fixture = new SyntheticFixture(Enum.Parse<SyntheticDifference>(difference));

        var result = PrivateCaseReportComparator.Compare(
            fixture.Pairs,
            PrivateCaseAcceptancePolicy.Current,
            [],
            [],
            PrivateCaseReportColumnAlignment.CreateAliases(AlignmentGl));

        var report = Assert.Single(result.Reports, static item => item.Kind == LegacyReportKind.PrescreenReport);
        Assert.Equal(passed, report.ContentMatches);
        Assert.Equal(passed, result.Passed);
        var alignment = Assert.Single(report.ColumnAlignment);
        Assert.Equal((1, 1, 1), (
            alignment.AlignedColumnCount,
            alignment.UnalignedExpectedColumnCount,
            alignment.UnalignedActualColumnCount));
        Assert.Contains(
            report.AcceptedContentDecisions,
            static decision => decision.DecisionId == PrivateCaseReportDifferencePolicy.IdeaExportColumnCatalog);
        Assert.Equal(
            !passed,
            report.BlockingContentColumnDetails.Any(static detail => detail.ColumnRole == "field:voucherNumber"));

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("source-voucher", json, StringComparison.Ordinal);
        Assert.DoesNotContain("V-3", json, StringComparison.Ordinal);
    }

    // R5 的彙總欄：舊版用 IDEA 彙總的欄名，JET 用固定欄名；依角色對起來後，筆數不同就算差異。
    [Theory]
    [InlineData("R5AggregateChanged", false)]
    [InlineData("R5AggregateEqual", true)]
    public void Compare_PreparerSummary_ComparesTheSummaryColumnsByRole(string difference, bool passed)
    {
        using var fixture = new SyntheticFixture(Enum.Parse<SyntheticDifference>(difference));

        var result = PrivateCaseReportComparator.Compare(
            fixture.Pairs,
            PrivateCaseAcceptancePolicy.Current,
            [],
            [],
            PrivateCaseReportColumnAlignment.CreateAliases(AlignmentGl));

        var report = Assert.Single(result.Reports, static item => item.Kind == LegacyReportKind.PrescreenReport);
        Assert.Equal(passed, report.ContentMatches);
        var alignment = Assert.Single(report.ColumnAlignment);
        Assert.Equal((4, 0, 0), (
            alignment.AlignedColumnCount,
            alignment.UnalignedExpectedColumnCount,
            alignment.UnalignedActualColumnCount));
        Assert.Equal(
            !passed,
            report.BlockingContentColumnDetails.Any(static detail => detail.ColumnRole == "aggregate:entryCount"));
    }

    // 明細頁的總帳日期：舊版是 Excel 日期序列值，JET 照 PBC-GL 寫日期文字。對起來的日期欄比的是日期本身，
    // 儲存型態不同另由儲存格型態的比對處理；日期真的不同時照樣擋下。
    [Theory]
    [InlineData("RenamedRawDateSerial", true)]
    [InlineData("RenamedRawDateChanged", false)]
    public void Compare_RenamedDateColumn_ComparesTheDatesRatherThanTheCellStorage(string difference, bool passed)
    {
        using var fixture = new SyntheticFixture(Enum.Parse<SyntheticDifference>(difference));

        var result = PrivateCaseReportComparator.Compare(
            fixture.Pairs,
            PrivateCaseAcceptancePolicy.Current,
            [],
            [],
            PrivateCaseReportColumnAlignment.CreateAliases(AlignmentGl));

        var report = Assert.Single(result.Reports, static item => item.Kind == LegacyReportKind.PrescreenReport);
        Assert.Equal(passed, report.ContentMatches);
        Assert.Equal(passed, result.Passed);
        Assert.Equal(
            !passed,
            report.BlockingContentColumnDetails.Any(static detail => detail.ColumnRole == "field:glDate"));
    }

    // R5、R6 的貸方合計：舊版 IDEA 的貸方金額欄是負數，加總後寫負數，JET 寫正數（裁定 2026-11-16-07）。
    // 兩邊的列順序不同也一樣，每一列都只差貸方合計的正負號時放行。某一列的大小不同、借方合計或筆數不同，
    // 或兩列的貸方合計互換、整欄看起來仍是相反數但對不上列時，照樣擋下；互換時整欄仍標成只差正負號。
    [Theory]
    [InlineData("R5CreditSignFlipped", true, "")]
    [InlineData("R6CreditSignFlipped", true, "")]
    [InlineData("R5CreditMagnitudeChanged", false, "aggregate:creditTotal")]
    [InlineData("R5CreditFlippedDebitChanged", false, "aggregate:debitTotal")]
    [InlineData("R5CreditFlippedCountChanged", false, "aggregate:entryCount")]
    [InlineData("R5CreditSwappedBetweenRows", false, "aggregate:creditTotal")]
    public void Compare_PreparerSummaryCreditTotal_AcceptsOnlyAFlippedSignOnEveryRow(
        string difference,
        bool accepted,
        string blockingRole)
    {
        using var fixture = new SyntheticFixture(Enum.Parse<SyntheticDifference>(difference));

        var result = PrivateCaseReportComparator.Compare(
            fixture.Pairs,
            PrivateCaseAcceptancePolicy.Current,
            [],
            [],
            PrivateCaseReportColumnAlignment.CreateAliases(AlignmentGl));

        var report = Assert.Single(result.Reports, static item => item.Kind == LegacyReportKind.PrescreenReport);
        Assert.Equal(accepted, report.ContentMatches);
        Assert.Equal(
            accepted,
            report.AcceptedContentDecisions.Any(static decision =>
                decision.DecisionId == PrivateCaseReportDifferencePolicy.PrescreenCreditTotalSign));
        if (!accepted)
        {
            Assert.Contains(report.BlockingContentColumnDetails, detail => detail.ColumnRole == blockingRole);
        }
        if (difference == "R5CreditSwappedBetweenRows")
        {
            var detail = Assert.Single(report.BlockingContentColumnDetails);
            Assert.Equal(("aggregate:creditTotal", "sign-flipped"), (detail.ColumnRole, detail.ValueRelation));
        }

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("creator-a", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AC-01", json, StringComparison.Ordinal);
    }

    // step4-1 的建立人員：JET 寫標準欄名，舊版用清單宣告的另一個名稱；對起來後值不同就算差異。
    [Fact]
    public void Compare_Step41DeclaredLegacyName_ComparesTheCreatorValues()
    {
        using var fixture = new SyntheticFixture(SyntheticDifference.Step41DeclaredCreatorChanged);

        var result = PrivateCaseReportComparator.Compare(
            fixture.Pairs,
            PrivateCaseAcceptancePolicy.Current,
            [],
            [],
            PrivateCaseReportColumnAlignment.CreateAliases(AlignmentGl));

        var paper = Assert.Single(result.Reports, static item => item.Kind == LegacyReportKind.WorkingPaper);
        Assert.False(paper.ContentMatches);
        Assert.Equal(1, Assert.Single(paper.ColumnAlignment).AlignedColumnCount);
        Assert.Contains(paper.BlockingContentColumnDetails, static detail => detail.ColumnRole == "field:createdBy");
    }

    // 對照只從清單的 GL 配對與宣告的舊版欄名產生：明細頁對來源欄名，R5、R6 另對彙總欄，step4-1 只對宣告的名稱。
    // 借方與貸方金額在舊版保留來源欄名，不另對；V_Report 5 與可靠性樣本不在對照範圍。
    [Fact]
    public void CreateAliases_FollowsTheManifestMappingAndDeclaredLegacyNames()
    {
        var aliases = PrivateCaseReportColumnAlignment.CreateAliases(AlignmentGl);

        Assert.Contains(aliases, static alias => alias.ReportSlug == "prescreen-report"
            && alias.SheetName == "R1"
            && alias.JetHeader == "source-voucher"
            && alias.LegacyHeaders.SequenceEqual(["傳票號碼_JE"])
            && alias.Role == "field:voucherNumber");
        Assert.Contains(aliases, static alias => alias.ReportSlug == "criteria-selection-report"
            && alias.SheetName == "#Criteria Select 10"
            && alias.JetHeader == "source-creator"
            && alias.LegacyHeaders.SequenceEqual(["declared-creator", "傳票建立人員_JE"]));
        Assert.Contains(aliases, static alias => alias.ReportSlug == "validation-report"
            && alias.SheetName == "V_Report 4"
            && alias.JetHeader == "source-account-name"
            && alias.LegacyHeaders.SequenceEqual(["會計科目名稱_JE"]));
        Assert.Contains(aliases, static alias => alias.SheetName == "R5"
            && alias.JetHeader == "CREATED_BY"
            && alias.LegacyHeaders.SequenceEqual(["declared-creator", "傳票建立人員_JE"])
            && alias.Role == "field:createdBy");
        Assert.Contains(aliases, static alias => alias.SheetName == "R6"
            && alias.JetHeader == "source-account"
            && alias.LegacyHeaders.SequenceEqual(["會計科目編號_JE"]));
        Assert.Contains(aliases, static alias => alias.SheetName == "R6"
            && alias.JetHeader == "CREDIT_TOTAL"
            && alias.LegacyHeaders.SequenceEqual(["CREDIT_傳票金額_JE_T_SUM"])
            && alias.Role == "aggregate:creditTotal");
        Assert.Contains(aliases, static alias => alias.ReportSlug == "working-paper"
            && alias.JetHeader == "傳票建立人員_JE"
            && alias.LegacyHeaders.SequenceEqual(["declared-creator"]));
        Assert.DoesNotContain(aliases, static alias => alias.JetHeader is "source-debit" or "source-credit");
        Assert.DoesNotContain(aliases, static alias => alias.ReportSlug == "working-paper"
            && alias.JetHeader == "傳票號碼_JE");
        Assert.DoesNotContain(aliases, static alias => alias.SheetName is "V_Report 5" or "可靠性樣本_所有欄位");

        var json = JsonSerializer.Serialize(aliases);
        Assert.DoesNotContain("source-voucher", json, StringComparison.Ordinal);
        Assert.DoesNotContain("declared-creator", json, StringComparison.Ordinal);
    }

    // 驗證報告主表逐格比：驗證結果的筆數不同就不通過，回報只有儲存格位置與大小關係；
    // Prepared by、Prepared date 與來源檔名照裁定或驗證框架的輸入放行。
    [Theory]
    [InlineData("ValidationSummaryCountChanged", false)]
    [InlineData("ValidationSummaryAcceptedOnly", true)]
    public void Compare_ValidationSummary_ComparesTheResultCellsAndAcceptsOnlyTheListedCells(
        string difference,
        bool passed)
    {
        using var fixture = new SyntheticFixture(Enum.Parse<SyntheticDifference>(difference));

        var result = PrivateCaseReportComparator.Compare(
            fixture.Pairs,
            PrivateCaseAcceptancePolicy.Current,
            []);

        var report = Assert.Single(result.Reports, static item => item.Kind == LegacyReportKind.ValidationReport);
        Assert.Equal(passed, report.Passed);
        Assert.Equal(passed, result.Passed);
        var sheet = Assert.Single(report.ResultSheets);
        Assert.Equal(1, sheet.SheetPosition);
        Assert.Equal(
            [
                PrivateCaseResultCellComparison.HarnessInputFileName,
                PrivateCaseResultCellComparison.PreparedByCaseCreator,
                PrivateCaseResultCellComparison.PreparedDateExportDay,
            ],
            sheet.AcceptedCells.Select(static decision => decision.DecisionId));
        if (passed)
        {
            Assert.Empty(sheet.BlockingCells);
        }
        else
        {
            var cell = Assert.Single(sheet.BlockingCells);
            Assert.Equal(("D20", "number", "number", "actual-higher"), (
                cell.Address,
                cell.ExpectedKind,
                cell.ActualKind,
                cell.Relation));
        }

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("synthetic-legacy-preparer", json, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-source.xlsx", json, StringComparison.Ordinal);
    }

    // 條件篩選摘要：B 欄的條件說明照 JET 的寫法放行，同一列的傳票數與分錄數照樣比。
    [Fact]
    public void Compare_CriteriaSummary_AcceptsTheConditionTextButComparesTheCounts()
    {
        using var fixture = new SyntheticFixture(SyntheticDifference.CriteriaSummaryCountChanged);

        var result = PrivateCaseReportComparator.Compare(
            fixture.Pairs,
            PrivateCaseAcceptancePolicy.Current,
            []);

        var report = Assert.Single(result.Reports, static item => item.Kind == LegacyReportKind.CriteriaSelectionReport);
        Assert.False(report.Passed);
        var sheet = Assert.Single(report.ResultSheets);
        Assert.Equal("D5", Assert.Single(sheet.BlockingCells).Address);
        Assert.Contains(
            sheet.AcceptedCells,
            static decision => decision.DecisionId == PrivateCaseResultCellComparison.JetConditionText);
    }

    // 預篩選摘要：第 14 到 16 列的退役文字照範本留，其他列的命中數照樣比。
    [Fact]
    public void Compare_PrescreenSummary_AcceptsTheRetiredRowsButComparesTheHitCounts()
    {
        using var fixture = new SyntheticFixture(SyntheticDifference.PrescreenSummaryCountChanged);

        var result = PrivateCaseReportComparator.Compare(
            fixture.Pairs,
            PrivateCaseAcceptancePolicy.Current,
            []);

        var report = Assert.Single(result.Reports, static item => item.Kind == LegacyReportKind.PrescreenReport);
        Assert.False(report.Passed);
        var sheet = Assert.Single(report.ResultSheets);
        var cell = Assert.Single(sheet.BlockingCells);
        Assert.Equal(("E8", "actual-lower"), (cell.Address, cell.Relation));
        Assert.Contains(
            sheet.AcceptedCells,
            static decision => decision.DecisionId == PrivateCaseResultCellComparison.RetiredPrescreenRows);
    }

    // 條件篩選報告最多十個情境，第 7 到 10 個情境的明細頁和前六個一樣是直接比對頁，值不同不放行。
    [Theory]
    [InlineData("#Criteria Select 7")]
    [InlineData("#Criteria Select 10")]
    public void DifferencePolicy_EveryCriteriaSelectPage_IsADirectPage(string sheetName)
    {
        var rowValues = new LegacyAuditParityContentDifference(
            "criteria-selection-report",
            sheetName,
            LegacyAuditParityContentDimension.RowValues,
            1);
        var numberFormat = new SpreadsheetAppearanceDifferenceGroup(
            sheetName, "cell", "style.numberFormat.code", "General", "@", 1, $"{sheetName}!A2");

        Assert.True(PrivateCaseReportDifferencePolicy.IsDirectPhysicalPage("criteria-selection-report", sheetName));
        Assert.Null(PrivateCaseReportDifferencePolicy.ResolveContent("criteria-selection-report", rowValues, []));
        Assert.Equal(
            PrivateCaseReportDifferencePolicy.PersistedSourceFieldTypeAppearance,
            PrivateCaseReportDifferencePolicy.ResolveAppearance(
                "criteria-selection-report",
                numberFormat,
                [numberFormat]));
    }

    [Fact]
    public void Reader_TenthCriteriaSelectPage_BuildsADirectFamilyFromTheFirstRowHeader()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jet-criteria-family-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "criteria.xlsx");
            using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
            {
                var workbookPart = document.AddWorkbookPart();
                workbookPart.Workbook = new Workbook(new Sheets());
                uint sheetId = 1;
                AddSheet(
                    workbookPart,
                    workbookPart.Workbook.Sheets!,
                    ref sheetId,
                    "#Criteria Select 10",
                    [Row(1, InlineCell("A1", "field-name")), Row(2, InlineCell("A2", "value"))]);
                workbookPart.Workbook.Save();
            }

            var workbook = LegacyAuditParityNormalizedWorkbookReader.Read(
                "criteria-selection-report",
                path,
                excludeJetMetadataSheet: true);

            var family = Assert.Single(workbook.DirectFamilies);
            Assert.Equal(("#Criteria Select 10", 1), (family.BaseSheetName, family.DataRowCount));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PrivateCaseGlSettings AlignmentGl => new(
        [],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["voucherNumber"] = "source-voucher",
            ["glDate"] = "source-date",
            ["accountNumber"] = "source-account",
            ["accountName"] = "source-account-name",
            ["createdBy"] = "source-creator",
            ["debitAmount"] = "source-debit",
            ["creditAmount"] = "source-credit",
        },
        GlAmountMode.DualAmount)
    {
        LegacyFieldNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["createdBy"] = "declared-creator",
        },
    };

    private enum SyntheticDifference
    {
        RenamedRawColumnChanged,
        RenamedRawColumnEqual,
        R5AggregateChanged,
        R5AggregateEqual,
        R5CreditSignFlipped,
        R6CreditSignFlipped,
        R5CreditMagnitudeChanged,
        R5CreditFlippedDebitChanged,
        R5CreditFlippedCountChanged,
        R5CreditSwappedBetweenRows,
        RenamedRawDateSerial,
        RenamedRawDateChanged,
        Step41DeclaredCreatorChanged,
        ValidationSummaryCountChanged,
        ValidationSummaryAcceptedOnly,
        CriteriaSummaryCountChanged,
        PrescreenSummaryCountChanged,
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
        Step41TemplateRowHeight,
        Step41OtherRowHeight,
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
            var journeyReports = new List<LegacyAuditParityJourneyReport>();
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
                journeyReports.Add(new LegacyAuditParityJourneyReport(kind, actualName, actualPath));
            }

            _workspace = PrivateCaseInputWorkspace.Create(ownedRoot);
            var expected = PrivateCaseExpectedReportSet.Create(
                PrivateCaseExpectedReportSetTests.CreateManifest(expectedFiles),
                sourceRoot,
                _workspace);
            var actual = PrivateCaseActualReportSet.Create(
                journeyReports,
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
        else if (kind == LegacyReportKind.PrescreenReport
            && difference is SyntheticDifference.RenamedRawColumnChanged
                or SyntheticDifference.RenamedRawColumnEqual)
        {
            // 舊版只有改名後的欄與帶正負號的金額；JET 是來源欄名與借方金額，兩邊各有一欄對不起來。
            var secondVoucher = difference == SyntheticDifference.RenamedRawColumnChanged && isActual
                ? "V-3"
                : "V-2";
            AddSheet(
                workbookPart,
                sheets,
                ref sheetId,
                "R1",
                isActual
                    ? [
                        Row(1, InlineCell("A1", "source-voucher"), InlineCell("B1", "source-debit")),
                        Row(2, InlineCell("A2", "V-1"), NumberCell("B2", "100")),
                        Row(3, InlineCell("A3", secondVoucher)),
                    ]
                    : [
                        Row(1, InlineCell("A1", "傳票號碼_JE"), InlineCell("B1", "傳票金額_JE")),
                        Row(2, InlineCell("A2", "V-1"), NumberCell("B2", "100")),
                        Row(3, InlineCell("A3", secondVoucher), NumberCell("B3", "-100")),
                    ]);
        }
        else if (kind == LegacyReportKind.PrescreenReport
            && difference is SyntheticDifference.R5AggregateChanged
                or SyntheticDifference.R5AggregateEqual)
        {
            var entryCount = difference == SyntheticDifference.R5AggregateChanged && isActual ? "3" : "2";
            AddSheet(
                workbookPart,
                sheets,
                ref sheetId,
                "R5",
                isActual
                    ? [
                        Row(
                            1,
                            InlineCell("A1", "CREATED_BY"),
                            InlineCell("B1", "ENTRY_COUNT"),
                            InlineCell("C1", "DEBIT_TOTAL"),
                            InlineCell("D1", "CREDIT_TOTAL")),
                        Row(
                            2,
                            InlineCell("A2", "creator-a"),
                            NumberCell("B2", entryCount),
                            NumberCell("C2", "100"),
                            NumberCell("D2", "50")),
                    ]
                    : [
                        Row(
                            1,
                            InlineCell("A1", "declared-creator"),
                            InlineCell("B1", "NO_OF_RECS"),
                            InlineCell("C1", "DEBIT_傳票金額_JE_T_SUM"),
                            InlineCell("D1", "CREDIT_傳票金額_JE_T_SUM")),
                        Row(
                            2,
                            InlineCell("A2", "creator-a"),
                            NumberCell("B2", "2"),
                            NumberCell("C2", "100"),
                            NumberCell("D2", "50")),
                    ]);
        }
        else if (kind == LegacyReportKind.PrescreenReport
            && difference is SyntheticDifference.R5CreditSignFlipped
                or SyntheticDifference.R6CreditSignFlipped
                or SyntheticDifference.R5CreditMagnitudeChanged
                or SyntheticDifference.R5CreditFlippedDebitChanged
                or SyntheticDifference.R5CreditFlippedCountChanged
                or SyntheticDifference.R5CreditSwappedBetweenRows)
        {
            // 舊版 IDEA 的貸方合計是負數加總，JET 寫正數；JET 的列照筆數降冪，和舊版的列順序不同。
            // 每一列依序是彙總鍵、筆數、借方合計與貸方合計；R5 的彙總鍵是編製人員，R6 是科目編號。
            var isR6 = difference == SyntheticDifference.R6CreditSignFlipped;
            var first = isR6 ? "AC-01" : "creator-a";
            var second = isR6 ? "AC-02" : "creator-b";
            (string Key, string Count, string Debit, string Credit)[] values = isActual
                ? difference switch
                {
                    SyntheticDifference.R5CreditMagnitudeChanged => [(second, "3", "80", "70"), (first, "2", "100", "60")],
                    SyntheticDifference.R5CreditFlippedDebitChanged => [(second, "3", "80", "70"), (first, "2", "90", "50")],
                    SyntheticDifference.R5CreditFlippedCountChanged => [(second, "3", "80", "70"), (first, "4", "100", "50")],
                    SyntheticDifference.R5CreditSwappedBetweenRows => [(second, "3", "80", "50"), (first, "2", "100", "70")],
                    _ => [(second, "3", "80", "70"), (first, "2", "100", "50")],
                }
                : [(first, "2", "100", "-50"), (second, "3", "80", "-70")];
            string[] headers = (isR6, isActual) switch
            {
                (false, true) => ["CREATED_BY", "ENTRY_COUNT", "DEBIT_TOTAL", "CREDIT_TOTAL"],
                (false, false) => ["declared-creator", "NO_OF_RECS", "DEBIT_傳票金額_JE_T_SUM", "CREDIT_傳票金額_JE_T_SUM"],
                (true, true) => ["source-account", "ENTRY_COUNT", "DEBIT_TOTAL", "CREDIT_TOTAL"],
                (true, false) => ["會計科目編號_JE", "NO_OF_RECS", "DEBIT_傳票金額_JE_T_SUM", "CREDIT_傳票金額_JE_T_SUM"],
            };
            var rows = new List<Row>
            {
                Row(1, InlineCell("A1", headers[0]), InlineCell("B1", headers[1]), InlineCell("C1", headers[2]), InlineCell("D1", headers[3])),
            };
            for (var index = 0; index < values.Length; index++)
            {
                var rowNumber = (uint)(index + 2);
                rows.Add(Row(
                    rowNumber,
                    InlineCell($"A{rowNumber}", values[index].Key),
                    NumberCell($"B{rowNumber}", values[index].Count),
                    NumberCell($"C{rowNumber}", values[index].Debit),
                    NumberCell($"D{rowNumber}", values[index].Credit)));
            }
            AddSheet(workbookPart, sheets, ref sheetId, isR6 ? "R6" : "R5", rows);
        }
        else if (kind == LegacyReportKind.PrescreenReport
            && difference is SyntheticDifference.RenamedRawDateSerial
                or SyntheticDifference.RenamedRawDateChanged)
        {
            // 舊版的總帳日期是 Excel 序列值 45688（2025-01-31），JET 照來源寫日期文字。
            var secondDate = difference == SyntheticDifference.RenamedRawDateChanged ? "2025-02-02" : "2025-02-01";
            AddSheet(
                workbookPart,
                sheets,
                ref sheetId,
                "R1",
                isActual
                    ? [
                        Row(1, InlineCell("A1", "source-voucher"), InlineCell("B1", "source-date")),
                        Row(2, InlineCell("A2", "V-1"), InlineCell("B2", "2025-01-31")),
                        Row(3, InlineCell("A3", "V-2"), InlineCell("B3", secondDate)),
                    ]
                    : [
                        Row(1, InlineCell("A1", "傳票號碼_JE"), InlineCell("B1", "總帳日期_JE")),
                        Row(2, InlineCell("A2", "V-1"), NumberCell("B2", "45688")),
                        Row(3, InlineCell("A3", "V-2"), NumberCell("B3", "45689")),
                    ]);
        }
        else if (kind == LegacyReportKind.ValidationReport
            && difference is SyntheticDifference.ValidationSummaryCountChanged
                or SyntheticDifference.ValidationSummaryAcceptedOnly)
        {
            // 舊版與 JET 的編製人員、編製日期與來源檔名不同；第 20 列是一項驗證的筆數。
            var count = difference == SyntheticDifference.ValidationSummaryCountChanged && isActual ? "4" : "3";
            AddSheet(
                workbookPart,
                sheets,
                ref sheetId,
                "ValidationReport",
                [
                    Row(1, InlineCell("A1", "fixed-title")),
                    Row(4, InlineCell("E4", isActual ? "Prepared by: synthetic-principal" : "Prepared by: synthetic-legacy-preparer")),
                    Row(5, InlineCell("E5", isActual ? "Prepared date: 2026/11/16" : "Prepared date: 2025/01/31")),
                    Row(9, InlineCell("E9", isActual ? "input-001.xlsx" : "synthetic-source.xlsx")),
                    Row(20, InlineCell("A20", "fixed-label"), NumberCell("D20", count)),
                ]);
        }
        else if (kind == LegacyReportKind.CriteriaSelectionReport
            && difference == SyntheticDifference.CriteriaSummaryCountChanged)
        {
            AddSheet(
                workbookPart,
                sheets,
                ref sheetId,
                "Summary Inforamtion",
                [
                    Row(4, InlineCell("B4", "條件的內容")),
                    Row(
                        5,
                        InlineCell("A5", "Criteria Selection 1"),
                        InlineCell("B5", isActual ? "jet-condition" : "legacy-condition-log"),
                        NumberCell("C5", "2"),
                        NumberCell("D5", isActual ? "6" : "5")),
                ]);
        }
        else if (kind == LegacyReportKind.PrescreenReport
            && difference == SyntheticDifference.PrescreenSummaryCountChanged)
        {
            AddSheet(
                workbookPart,
                sheets,
                ref sheetId,
                "Pre-screening_Report",
                [
                    Row(8, InlineCell("B8", "R1"), NumberCell("E8", isActual ? "1" : "2")),
                    Row(14, InlineCell("B14", "A2"), InlineCell("D14", isActual ? "retired" : "legacy-result")),
                ]);
        }
        else if (kind == LegacyReportKind.WorkingPaper
            && difference == SyntheticDifference.Step41DeclaredCreatorChanged)
        {
            AddSheet(workbookPart, sheets, ref sheetId, "step4-1 符合高風險條件傳票明細",
            [
                Row(
                    5,
                    InlineCell("A5", "傳票號碼_JE"),
                    InlineCell("B5", isActual ? "傳票建立人員_JE" : "declared-creator")),
                Row(6, InlineCell("A6", "V-1"), InlineCell("B6", isActual ? "creator-b" : "creator-a")),
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
            && difference is SyntheticDifference.Step41TemplateRowHeight
                or SyntheticDifference.Step41OtherRowHeight)
        {
            // 舊版的預設列高是 16.5；JET 那邊寫範本的 17，或另一個值。
            var height = !isActual
                ? 16.5D
                : difference == SyntheticDifference.Step41TemplateRowHeight ? 17D : 18D;
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(
                new SheetFormatProperties { DefaultRowHeight = height, CustomHeight = true },
                new SheetData(
                    Row(5, InlineCell("A5", "source-field")),
                    Row(6, InlineCell("A6", "row-1"))));
            worksheetPart.Worksheet.Save();
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = sheetId,
                Name = "step4-1 符合高風險條件傳票明細",
            });
            sheetId++;
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

    private static void WriteDefaultRowHeightWorkbook(string path, string sheetName, double defaultRowHeight)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(
            new SheetFormatProperties { DefaultRowHeight = defaultRowHeight, CustomHeight = true },
            new SheetData(Row(1, InlineCell("A1", "synthetic"))));
        worksheetPart.Worksheet.Save();
        workbookPart.Workbook.AppendChild(new Sheets()).Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = sheetName,
        });
        workbookPart.Workbook.Save();
    }
}
