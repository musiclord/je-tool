using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyAppearanceRegistryTests
{
    [Fact]
    public void RegistryCountAndCrossScriptParity_AreDerivedFromOriginalSources()
    {
        var registry = LegacyAppearanceRegistry.Load();
        var scriptCommands = LegacyAppearanceRegistry.ExtractAppearanceCommands(
            LegacyAppearanceRegistry.ScriptRelativePath);
        var toolCommands = LegacyAppearanceRegistry.ExtractAppearanceCommands(
            LegacyAppearanceRegistry.ToolRelativePath);

        Assert.NotEmpty(scriptCommands);
        Assert.Equal(scriptCommands.Count, registry.Entries.Count);
        Assert.Equal(toolCommands.Count, registry.Entries.Count);
        Assert.Equal(
            scriptCommands.Select(command => command.CommandKey).Order(StringComparer.Ordinal),
            toolCommands.Select(command => command.CommandKey).Order(StringComparer.Ordinal));
        Assert.Equal(
            ProcedureDistribution(scriptCommands),
            ProcedureDistribution(toolCommands));
        Assert.Equal(
            ProcedureDistribution(scriptCommands),
            ProcedureDistribution(registry.Entries));
        Assert.Equal(
            scriptCommands.Select(CommandIdentity).Order(StringComparer.Ordinal),
            registry.Entries.Select(BasIdentity).Order(StringComparer.Ordinal));
        Assert.Equal(
            toolCommands.Select(CommandIdentity).Order(StringComparer.Ordinal),
            registry.Entries.Select(IsmIdentity).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RegistryCoordinatesAndNormalizedSemantics_MatchBothOriginalScripts()
    {
        var registry = LegacyAppearanceRegistry.Load();
        var scriptCommands = LegacyAppearanceRegistry.ExtractAppearanceCommands(
            LegacyAppearanceRegistry.ScriptRelativePath);
        var toolCommands = LegacyAppearanceRegistry.ExtractAppearanceCommands(
            LegacyAppearanceRegistry.ToolRelativePath);

        Assert.Equal(
            registry.Entries.Count,
            registry.Entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            registry.Entries.Count,
            registry.Entries
                .Select(entry => $"{entry.Procedure}|{entry.BasLine}")
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            registry.Entries.Count,
            registry.Entries
                .Select(entry => $"{entry.Procedure}|{entry.IsmLine}")
                .Distinct(StringComparer.Ordinal)
                .Count());

        foreach (var entry in registry.Entries)
        {
            var bas = Assert.Single(
                scriptCommands,
                command => command.Procedure == entry.Procedure && command.LineNumber == entry.BasLine);
            var tool = Assert.Single(
                toolCommands,
                command => command.Procedure == entry.Procedure && command.LineNumber == entry.IsmLine);

            Assert.Equal(entry.SourceText, bas.LineText);
            Assert.Equal(entry.SourceText, tool.LineText);
            Assert.Equal(entry.RangeExpression, bas.TargetExpression);
            Assert.Equal(entry.RangeExpression, tool.TargetExpression);
            Assert.Equal(entry.Property, bas.Property);
            Assert.Equal(entry.Property, tool.Property);
            Assert.Equal(entry.NormalizedValue, bas.NormalizedValue);
            Assert.Equal(entry.NormalizedValue, tool.NormalizedValue);
        }
    }

    [Fact]
    public void Registry_ConditionsAndJetDestinations_ArePinnedForLaterParityStages()
    {
        var registry = LegacyAppearanceRegistry.Load();

        AssertCondition(registry, "account-mapping source exists",
            "workingpaper-account-mapping-autofit",
            "workingpaper-account-mapping-header-bold",
            "workingpaper-account-mapping-header-font",
            "workingpaper-account-mapping-header-size");
        AssertCondition(registry, "always",
            "workingpaper-field-info-autofit",
            "workingpaper-field-info-gl-header-bold",
            "workingpaper-field-info-gl-header-fill",
            "workingpaper-field-info-gl-header-font",
            "workingpaper-field-info-gl-header-size",
            "workingpaper-field-info-tb-header-bold",
            "workingpaper-field-info-tb-header-fill",
            "workingpaper-field-info-tb-header-font",
            "workingpaper-field-info-tb-header-size",
            "workingpaper-field-info-version-bold",
            "workingpaper-field-info-version-color",
            "workingpaper-field-info-version-fill",
            "workingpaper-field-info-version-font",
            "workingpaper-field-info-version-size");
        AssertCondition(registry, "always after summary values are populated",
            "prescreen-summary-description-wrap");
        AssertCondition(registry, "always after the completeness detail ExportDatabase copy",
            "validation-completeness-detail-autofit");
        AssertCondition(registry, "approval-date result exists and its result count is greater than zero",
            "validation-summary-post-period-fill");
        AssertCondition(
            registry,
            "approval-date result exists, count is greater than zero, and the Taiwan over-10,000 detail gate does not skip export",
            "validation-post-period-detail-autofit");
        AssertCondition(registry, "at least one saved criteria-log row is being emitted",
            "criteria-summary-row-autofit");
        AssertCondition(
            registry,
            "blank-description count is greater than zero and the Taiwan over-10,000 detail gate does not skip export",
            "validation-blank-description-detail-autofit");
        AssertCondition(registry, "blank-description result count is greater than zero",
            "validation-summary-blank-description-fill");
        AssertCondition(registry, "completeness variance-account count is greater than zero",
            "validation-summary-completeness-fill",
            "workingpaper-step1-difference-bold",
            "workingpaper-step1-difference-font",
            "workingpaper-step1-difference-font-color",
            "workingpaper-step1-difference-size");
        AssertCondition(registry, "holiday source exists",
            "workingpaper-holiday-autofit",
            "workingpaper-holiday-header-bold",
            "workingpaper-holiday-header-font",
            "workingpaper-holiday-header-size");
        AssertCondition(registry, "make-up-day source exists",
            "workingpaper-makeup-autofit",
            "workingpaper-makeup-header-bold",
            "workingpaper-makeup-header-font",
            "workingpaper-makeup-header-size");
        AssertCondition(
            registry,
            "missing-account count is greater than zero and the Taiwan over-10,000 detail gate does not skip export",
            "validation-missing-account-detail-autofit");
        AssertCondition(registry, "missing-account result count is greater than zero",
            "validation-summary-missing-account-fill");
        AssertCondition(
            registry,
            "missing-voucher-number count is greater than zero and the Taiwan over-10,000 detail gate does not skip export",
            "validation-missing-voucher-detail-autofit");
        AssertCondition(registry, "missing-voucher-number result count is greater than zero",
            "validation-summary-missing-voucher-fill");
        AssertCondition(registry, "preparer-summary source file does not exist",
            "workingpaper-step1-preparer-warning-bold",
            "workingpaper-step1-preparer-warning-color",
            "workingpaper-step1-preparer-warning-font",
            "workingpaper-step1-preparer-warning-size");
        AssertCondition(
            registry,
            "source dataset exists, field is an eligible mapped character field, and p is nonzero",
            "inf-main-character-number-format");
        AssertCondition(registry, "Taiwan mode",
            "validation-field-info-autofit",
            "validation-field-info-gl-header-bold",
            "validation-field-info-gl-header-fill",
            "validation-field-info-gl-header-font",
            "validation-field-info-gl-header-size",
            "validation-field-info-tb-header-bold",
            "validation-field-info-tb-header-fill",
            "validation-field-info-tb-header-font",
            "validation-field-info-tb-header-size");
        AssertCondition(
            registry,
            "Taiwan mode and unbalanced-voucher detail row count is greater than zero and less than 10,000",
            "validation-summary-unbalanced-voucher-fill",
            "validation-unbalanced-voucher-detail-autofit");
        AssertCondition(registry, "the indexed criteria database exists and its row count is at most 1,000,000",
            "criteria-detail-autofit");
        AssertCondition(
            registry,
            "the indexed detail source exists and its row count is greater than zero and less than 10,000",
            "prescreen-detail-autofit");
        AssertCondition(registry, "unbalanced-voucher summary exists and has at least one row",
            "workingpaper-step1-balance-block-bold",
            "workingpaper-step1-balance-block-font",
            "workingpaper-step1-balance-block-size",
            "workingpaper-step1-balance-header-fill",
            "workingpaper-step1-balance-header-font-color",
            "workingpaper-step1-balance-message-font-color");
        AssertCondition(registry, "weekend source exists",
            "workingpaper-weekend-header-bold",
            "workingpaper-weekend-header-font",
            "workingpaper-weekend-header-size");

        var expectedDestinations = new Dictionary<string, string>(StringComparer.Ordinal);
        AddJetDestination(expectedDestinations,
            "CriteriaSelectionReport!dynamic criteria detail sheet:columns 1-20",
            "criteria-detail-autofit");
        AddJetDestination(expectedDestinations,
            "CriteriaSelectionReport!Summary Inforamtion:all rows",
            "criteria-summary-row-autofit");
        AddJetDestination(expectedDestinations,
            "INF_Report!INF Testing 可靠性測試:rows 53 onward in mapped columns",
            "inf-main-character-number-format");
        AddJetDestination(expectedDestinations,
            "Pre-screeningReport!dynamic legacy detail sheet:columns 1-20",
            "prescreen-detail-autofit");
        AddJetDestination(expectedDestinations,
            "Pre-screeningReport!Pre-screening_Report:column C",
            "prescreen-summary-description-wrap");
        AddJetDestination(expectedDestinations,
            "ValidationReport!自動化工具-檔案欄位資訊:A3:E3",
            "validation-field-info-tb-header-bold",
            "validation-field-info-tb-header-fill",
            "validation-field-info-tb-header-font",
            "validation-field-info-tb-header-size");
        AddJetDestination(expectedDestinations,
            "ValidationReport!自動化工具-檔案欄位資訊:columns A-E",
            "validation-field-info-autofit");
        AddJetDestination(expectedDestinations,
            "ValidationReport!自動化工具-檔案欄位資訊:dynamic GL header A:E",
            "validation-field-info-gl-header-bold",
            "validation-field-info-gl-header-fill",
            "validation-field-info-gl-header-font",
            "validation-field-info-gl-header-size");
        AddJetDestination(expectedDestinations, "ValidationReport!V_Report 1:columns 1-20",
            "validation-missing-account-detail-autofit");
        AddJetDestination(expectedDestinations, "ValidationReport!V_Report 2:columns 1-20",
            "validation-missing-voucher-detail-autofit");
        AddJetDestination(expectedDestinations, "ValidationReport!V_Report 3:columns 1-20",
            "validation-blank-description-detail-autofit");
        AddJetDestination(expectedDestinations, "ValidationReport!V_Report 4:columns 1-20",
            "validation-post-period-detail-autofit");
        AddJetDestination(expectedDestinations, "ValidationReport!V_Report 5:columns 1-20",
            "validation-completeness-detail-autofit");
        AddJetDestination(expectedDestinations, "ValidationReport!V_Report 6:columns 1-20",
            "validation-unbalanced-voucher-detail-autofit");
        AddJetDestination(expectedDestinations, "ValidationReport!ValidationReport:D20",
            "validation-summary-missing-account-fill");
        AddJetDestination(expectedDestinations, "ValidationReport!ValidationReport:D21",
            "validation-summary-missing-voucher-fill");
        AddJetDestination(expectedDestinations, "ValidationReport!ValidationReport:D22",
            "validation-summary-blank-description-fill");
        AddJetDestination(expectedDestinations, "ValidationReport!ValidationReport:D23",
            "validation-summary-post-period-fill");
        AddJetDestination(expectedDestinations, "ValidationReport!ValidationReport:D24",
            "validation-summary-completeness-fill");
        AddJetDestination(expectedDestinations, "ValidationReport!ValidationReport:D25",
            "validation-summary-unbalanced-voucher-fill");
        AddJetDestination(expectedDestinations,
            "WorkingPaper!自動化工具-科目配對資訊:columns A-C",
            "workingpaper-account-mapping-autofit");
        AddJetDestination(expectedDestinations,
            "WorkingPaper!自動化工具-科目配對資訊:header row",
            "workingpaper-account-mapping-header-bold",
            "workingpaper-account-mapping-header-font",
            "workingpaper-account-mapping-header-size");
        AddJetDestination(expectedDestinations,
            "WorkingPaper!自動化工具-假期假日資訊:columns A-C",
            "workingpaper-holiday-autofit",
            "workingpaper-makeup-autofit");
        AddJetDestination(expectedDestinations,
            "WorkingPaper!自動化工具-假期假日資訊:holiday header row",
            "workingpaper-holiday-header-bold",
            "workingpaper-holiday-header-font",
            "workingpaper-holiday-header-size");
        AddJetDestination(expectedDestinations,
            "WorkingPaper!自動化工具-假期假日資訊:make-up-day header row",
            "workingpaper-makeup-header-bold",
            "workingpaper-makeup-header-font",
            "workingpaper-makeup-header-size");
        AddJetDestination(expectedDestinations,
            "WorkingPaper!自動化工具-假期假日資訊:weekend header row",
            "workingpaper-weekend-header-bold",
            "workingpaper-weekend-header-font",
            "workingpaper-weekend-header-size");
        AddJetDestination(expectedDestinations, "WorkingPaper!自動化工具-檔案欄位資訊:A1",
            "workingpaper-field-info-version-bold",
            "workingpaper-field-info-version-color",
            "workingpaper-field-info-version-fill",
            "workingpaper-field-info-version-font",
            "workingpaper-field-info-version-size");
        AddJetDestination(expectedDestinations, "WorkingPaper!自動化工具-檔案欄位資訊:A3:E3",
            "workingpaper-field-info-tb-header-bold",
            "workingpaper-field-info-tb-header-fill",
            "workingpaper-field-info-tb-header-font",
            "workingpaper-field-info-tb-header-size");
        AddJetDestination(expectedDestinations, "WorkingPaper!自動化工具-檔案欄位資訊:columns A-E",
            "workingpaper-field-info-autofit");
        AddJetDestination(
            expectedDestinations,
            "WorkingPaper!自動化工具-檔案欄位資訊:dynamic GL header A:E",
            "workingpaper-field-info-gl-header-bold",
            "workingpaper-field-info-gl-header-fill",
            "workingpaper-field-info-gl-header-font",
            "workingpaper-field-info-gl-header-size");
        AddJetDestination(expectedDestinations, "WorkingPaper!step1 完整性測試:B15:B17",
            "workingpaper-step1-difference-bold",
            "workingpaper-step1-difference-font",
            "workingpaper-step1-difference-font-color",
            "workingpaper-step1-difference-size");
        AddJetDestination(expectedDestinations, "WorkingPaper!step1-1 借貸不平測試:B12:B16",
            "workingpaper-step1-balance-message-font-color");
        AddJetDestination(expectedDestinations, "WorkingPaper!step1-1 借貸不平測試:B12:F16",
            "workingpaper-step1-balance-block-bold",
            "workingpaper-step1-balance-block-font",
            "workingpaper-step1-balance-block-size");
        AddJetDestination(expectedDestinations, "WorkingPaper!step1-1 借貸不平測試:B16:F16",
            "workingpaper-step1-balance-header-fill",
            "workingpaper-step1-balance-header-font-color");
        AddJetDestination(expectedDestinations, "WorkingPaper!step1-2 分錄編製人員說明:B13",
            "workingpaper-step1-preparer-warning-bold",
            "workingpaper-step1-preparer-warning-color",
            "workingpaper-step1-preparer-warning-font",
            "workingpaper-step1-preparer-warning-size");

        Assert.Equal(
            expectedDestinations
                .Select(item => $"{item.Key}|{item.Value}")
                .Order(StringComparer.Ordinal),
            registry.Entries
                .Select(entry => $"{entry.Id}|{entry.JetOutput}")
                .Order(StringComparer.Ordinal));

        var dynamicWorksheets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prescreen-detail-autofit"] =
                "CriteriaWP(Z): R1, R2, R3, R4, R5, R6, A2, A3, A4, R7",
            ["criteria-detail-autofit"] = "#Criteria Select {i}"
        };
        Assert.All(registry.Entries, entry =>
        {
            Assert.NotNull(entry.JetOutput);
            Assert.True(
                entry.JetOutput.StartsWith($"{entry.Report}!", StringComparison.Ordinal),
                $"Registry entry {entry.Id} must keep its exact report mapping.");
            if (dynamicWorksheets.TryGetValue(entry.Id, out var expectedWorksheet))
            {
                Assert.Equal(expectedWorksheet, entry.Worksheet);
            }
            else
            {
                Assert.True(
                    entry.JetOutput.StartsWith(
                        $"{entry.Report}!{entry.Worksheet}:",
                        StringComparison.Ordinal),
                    $"Registry entry {entry.Id} must keep its exact worksheet mapping.");
            }
        });

        var expectedBases = new Dictionary<string, string>(StringComparer.Ordinal);
        AddExpectedValues(expectedBases,
            "all worksheet rows; command repeats once per saved criteria-log row",
            "criteria-summary-row-autofit");
        AddExpectedValues(expectedBases, "fixed cell",
            "validation-summary-blank-description-fill",
            "validation-summary-completeness-fill",
            "validation-summary-missing-account-fill",
            "validation-summary-missing-voucher-fill",
            "validation-summary-post-period-fill",
            "validation-summary-unbalanced-voucher-fill");
        AddExpectedValues(expectedBases, "fixed conditional explanation and header block",
            "workingpaper-step1-balance-block-bold",
            "workingpaper-step1-balance-block-font",
            "workingpaper-step1-balance-block-size");
        AddExpectedValues(expectedBases, "fixed conditional message and first header cell",
            "workingpaper-step1-balance-message-font-color");
        AddExpectedValues(expectedBases, "fixed conditional message cells",
            "workingpaper-step1-difference-bold",
            "workingpaper-step1-difference-font",
            "workingpaper-step1-difference-font-color",
            "workingpaper-step1-difference-size");
        AddExpectedValues(expectedBases, "fixed conditional table header",
            "workingpaper-step1-balance-header-fill",
            "workingpaper-step1-balance-header-font-color");
        AddExpectedValues(expectedBases, "fixed fallback warning cell",
            "workingpaper-step1-preparer-warning-bold",
            "workingpaper-step1-preparer-warning-color",
            "workingpaper-step1-preparer-warning-font",
            "workingpaper-step1-preparer-warning-size");
        AddExpectedValues(expectedBases, "fixed summary column C",
            "prescreen-summary-description-wrap");
        AddExpectedValues(expectedBases, "fixed TB header row",
            "validation-field-info-tb-header-bold",
            "validation-field-info-tb-header-fill",
            "validation-field-info-tb-header-font",
            "validation-field-info-tb-header-size",
            "workingpaper-field-info-tb-header-bold",
            "workingpaper-field-info-tb-header-fill",
            "workingpaper-field-info-tb-header-font",
            "workingpaper-field-info-tb-header-size");
        AddExpectedValues(expectedBases, "fixed version cell",
            "workingpaper-field-info-version-bold",
            "workingpaper-field-info-version-color",
            "workingpaper-field-info-version-fill",
            "workingpaper-field-info-version-font",
            "workingpaper-field-info-version-size");
        AddExpectedValues(expectedBases, "i = 1; P = 1..account-mapping field count",
            "workingpaper-account-mapping-header-bold",
            "workingpaper-account-mapping-header-font",
            "workingpaper-account-mapping-header-size");
        AddExpectedValues(expectedBases, "i = 1; P = 1..weekend field count",
            "workingpaper-weekend-header-bold",
            "workingpaper-weekend-header-font",
            "workingpaper-weekend-header-size");
        AddExpectedValues(expectedBases, "i = 1..10; j = 1..20", "criteria-detail-autofit");
        AddExpectedValues(expectedBases, "i = 1..20",
            "validation-blank-description-detail-autofit",
            "validation-completeness-detail-autofit",
            "validation-missing-account-detail-autofit",
            "validation-missing-voucher-detail-autofit",
            "validation-post-period-detail-autofit",
            "validation-unbalanced-voucher-detail-autofit");
        AddExpectedValues(
            expectedBases,
            "legacy i = TB field count + 6 after one spacer row and the GL title row; " +
            "JET frozen output header = TB field count + 7 after two spacer rows and the GL title row",
            "validation-field-info-gl-header-bold",
            "validation-field-info-gl-header-fill",
            "validation-field-info-gl-header-font",
            "validation-field-info-gl-header-size",
            "workingpaper-field-info-gl-header-bold",
            "workingpaper-field-info-gl-header-fill",
            "workingpaper-field-info-gl-header-font",
            "workingpaper-field-info-gl-header-size");
        AddExpectedValues(
            expectedBases,
            "i follows the holiday block; P = 1..make-up-day field count",
            "workingpaper-makeup-header-bold",
            "workingpaper-makeup-header-font",
            "workingpaper-makeup-header-size");
        AddExpectedValues(
            expectedBases,
            "i follows the weekend block (weekend row count + 3, or 3 when absent); P = 1..holiday field count",
            "workingpaper-holiday-header-bold",
            "workingpaper-holiday-header-font",
            "workingpaper-holiday-header-size");
        AddExpectedValues(expectedBases, "j = 1..3",
            "workingpaper-account-mapping-autofit",
            "workingpaper-holiday-autofit",
            "workingpaper-makeup-autofit");
        AddExpectedValues(expectedBases, "j = 1..5",
            "validation-field-info-autofit",
            "workingpaper-field-info-autofit");
        AddExpectedValues(
            expectedBases,
            "n = 52; k = 1..sample row count; p is the mapped output column",
            "inf-main-character-number-format");
        AddExpectedValues(expectedBases, "Z = 1..10; i = 1..20", "prescreen-detail-autofit");

        Assert.Equal(
            expectedBases.Select(item => $"{item.Key}|{item.Value}").Order(StringComparer.Ordinal),
            registry.Entries
                .Select(entry => $"{entry.Id}|{entry.DynamicRowBasis}")
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void LegacyReaders_UseUtf8WithoutBom()
    {
        var script = LegacyAppearanceRegistry.ReadScript(LegacyAppearanceRegistry.ScriptRelativePath);
        var tool = LegacyAppearanceRegistry.ReadScript(LegacyAppearanceRegistry.ToolRelativePath);

        Assert.Equal("UTF-8", script.EncodingName);
        Assert.False(script.HasBom);
        Assert.Equal("UTF-8", tool.EncodingName);
        Assert.False(tool.HasBom);
    }

    [Fact]
    public void Registry_ClassifiesEveryCommandAndPreservesLegacyColorDifferences()
    {
        var registry = LegacyAppearanceRegistry.Load();

        Assert.Equal(1, registry.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(registry.ExtractionMode));
        Assert.All(registry.Entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Id));
            Assert.False(string.IsNullOrWhiteSpace(entry.Procedure));
            Assert.False(string.IsNullOrWhiteSpace(entry.Report));
            Assert.False(string.IsNullOrWhiteSpace(entry.Worksheet));
            Assert.False(string.IsNullOrWhiteSpace(entry.RangeExpression));
            Assert.False(string.IsNullOrWhiteSpace(entry.DynamicRowBasis));
            Assert.False(string.IsNullOrWhiteSpace(entry.Property));
            Assert.False(string.IsNullOrWhiteSpace(entry.NormalizedValue));
            Assert.False(string.IsNullOrWhiteSpace(entry.Condition));
            Assert.True(entry.BasLine > 0);
            Assert.True(entry.IsmLine > 0);
            Assert.False(string.IsNullOrWhiteSpace(entry.SourceText));
            Assert.True(
                string.IsNullOrWhiteSpace(entry.JetOutput) ^
                string.IsNullOrWhiteSpace(entry.NoCorrespondenceReason),
                $"Registry entry {entry.Id} must have exactly one JET classification.");
        });

        Assert.Equal(
            "FFFF0000",
            Entry(registry, "workingpaper-step1-difference-font-color").NormalizedValue);
        Assert.Equal(
            "FFFFFFFF",
            Entry(registry, "workingpaper-step1-balance-header-font-color").NormalizedValue);
        Assert.Equal(
            "FF0066FF",
            Entry(registry, "workingpaper-step1-balance-header-fill").NormalizedValue);
        Assert.Equal(
            "FFF0F000",
            Entry(registry, "validation-field-info-tb-header-fill").NormalizedValue);
        Assert.Equal(
            "FFE6E600",
            Entry(registry, "validation-field-info-gl-header-fill").NormalizedValue);
        Assert.NotEqual(
            Entry(registry, "validation-field-info-tb-header-fill").NormalizedValue,
            Entry(registry, "validation-field-info-gl-header-fill").NormalizedValue);
    }

    [Fact]
    public void Registry_ListsEveryExportDatabaseBaseAppearanceAsScriptUnspecified()
    {
        var registry = LegacyAppearanceRegistry.Load();
        var scriptCalls = LegacyAppearanceRegistry.ExtractExportDatabaseCalls(
            LegacyAppearanceRegistry.ScriptRelativePath);
        var toolCalls = LegacyAppearanceRegistry.ExtractExportDatabaseCalls(
            LegacyAppearanceRegistry.ToolRelativePath);

        Assert.NotEmpty(registry.ScriptUnspecifiedSheets);
        Assert.Equal(
            registry.ScriptUnspecifiedSheets.Count,
            registry.ScriptUnspecifiedSheets
                .Select(item => $"{item.Report}|{item.Worksheet}")
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.All(registry.ScriptUnspecifiedSheets, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Report));
            Assert.False(string.IsNullOrWhiteSpace(item.Worksheet));
            Assert.False(string.IsNullOrWhiteSpace(item.SourceProcedure));
            Assert.False(string.IsNullOrWhiteSpace(item.JetOutput));
            Assert.False(string.IsNullOrWhiteSpace(item.Reason));
            Assert.Equal($"{item.Report}!{item.Worksheet}", item.JetOutput);
            Assert.Contains(
                scriptCalls,
                call => call.Procedure == item.SourceProcedure && call.LineNumber == item.BasLine);
            Assert.Contains(
                toolCalls,
                call => call.Procedure == item.SourceProcedure && call.LineNumber == item.IsmLine);
        });

        Assert.Equal(
            scriptCalls.Select(call => $"{call.Procedure}|{call.LineNumber}").Order(StringComparer.Ordinal),
            registry.ScriptUnspecifiedSheets
                .Select(item => $"{item.SourceProcedure}|{item.BasLine}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            toolCalls.Select(call => $"{call.Procedure}|{call.LineNumber}").Order(StringComparer.Ordinal),
            registry.ScriptUnspecifiedSheets
                .Select(item => $"{item.SourceProcedure}|{item.IsmLine}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));

        var expectedSheets = new[]
        {
            "CriteriaSelectionReport|#Criteria Select 1",
            "CriteriaSelectionReport|#Criteria Select 2",
            "CriteriaSelectionReport|#Criteria Select 3",
            "CriteriaSelectionReport|#Criteria Select 4",
            "CriteriaSelectionReport|#Criteria Select 5",
            "CriteriaSelectionReport|#Criteria Select 6",
            "CriteriaSelectionReport|#Criteria Select 7",
            "CriteriaSelectionReport|#Criteria Select 8",
            "CriteriaSelectionReport|#Criteria Select 9",
            "CriteriaSelectionReport|#Criteria Select 10",
            "INF_Report|可靠性樣本_所有欄位",
            "Pre-screeningReport|A2",
            "Pre-screeningReport|A3",
            "Pre-screeningReport|A4",
            "Pre-screeningReport|R1",
            "Pre-screeningReport|R2",
            "Pre-screeningReport|R3",
            "Pre-screeningReport|R4",
            "Pre-screeningReport|R5",
            "Pre-screeningReport|R6",
            "Pre-screeningReport|R7",
            "ValidationReport|V_Report 1",
            "ValidationReport|V_Report 2",
            "ValidationReport|V_Report 3",
            "ValidationReport|V_Report 4",
            "ValidationReport|V_Report 5",
            "ValidationReport|V_Report 6",
            "WorkingPaper|step4-1 符合高風險條件傳票明細"
        };
        Assert.Equal(
            expectedSheets.Order(StringComparer.Ordinal),
            registry.ScriptUnspecifiedSheets
                .Select(item => $"{item.Report}|{item.Worksheet}")
                .Order(StringComparer.Ordinal));
    }

    private static LegacyAppearanceRegistryEntry Entry(
        LegacyAppearanceRegistryDocument registry,
        string id) =>
        Assert.Single(registry.Entries, entry => entry.Id == id);

    private static string[] ProcedureDistribution(
        IEnumerable<LegacyAppearanceCommand> commands) =>
        commands
            .GroupBy(command => command.Procedure, StringComparer.Ordinal)
            .Select(group => $"{group.Key}:{group.Count()}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] ProcedureDistribution(
        IEnumerable<LegacyAppearanceRegistryEntry> entries) =>
        entries
            .GroupBy(entry => entry.Procedure, StringComparer.Ordinal)
            .Select(group => $"{group.Key}:{group.Count()}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string CommandIdentity(LegacyAppearanceCommand command) =>
        $"{command.Procedure}|{command.LineNumber}|{command.TargetExpression}|" +
        $"{command.Property}|{command.NormalizedValue}|{command.LineText}";

    private static string BasIdentity(LegacyAppearanceRegistryEntry entry) =>
        $"{entry.Procedure}|{entry.BasLine}|{entry.RangeExpression}|" +
        $"{entry.Property}|{entry.NormalizedValue}|{entry.SourceText}";

    private static string IsmIdentity(LegacyAppearanceRegistryEntry entry) =>
        $"{entry.Procedure}|{entry.IsmLine}|{entry.RangeExpression}|" +
        $"{entry.Property}|{entry.NormalizedValue}|{entry.SourceText}";

    private static void AssertCondition(
        LegacyAppearanceRegistryDocument registry,
        string condition,
        params string[] expectedIds) =>
        Assert.Equal(
            expectedIds.Order(StringComparer.Ordinal),
            registry.Entries
                .Where(entry => string.Equals(entry.Condition, condition, StringComparison.Ordinal))
                .Select(entry => entry.Id)
                .Order(StringComparer.Ordinal));

    private static void AddJetDestination(
        IDictionary<string, string> destinations,
        string output,
        params string[] entryIds)
    {
        foreach (var entryId in entryIds)
        {
            destinations.Add(entryId, output);
        }
    }

    private static void AddExpectedValues(
        IDictionary<string, string> values,
        string expectedValue,
        params string[] entryIds)
    {
        foreach (var entryId in entryIds)
        {
            values.Add(entryId, expectedValue);
        }
    }
}
