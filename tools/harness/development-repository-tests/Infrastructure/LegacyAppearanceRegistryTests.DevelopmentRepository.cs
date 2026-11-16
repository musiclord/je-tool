using JET.Tests.TestInfrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed partial class LegacyAppearanceRegistryTests
{
    [Fact]
    [Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]
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
    [Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]
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
    [Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]
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
    [Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]
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
}
