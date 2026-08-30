using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class FilterAccountMappingEligibilityFrontendTests
{
    [Fact]
    public void RuleMetadata_DeclaresPerRuleAccountMappingRequirements()
    {
        var core = ReadFrontend("js", "ui-core.js");

        Assert.Matches(new Regex(@"value:\s*'accountPair'[^\r\n]*accountMappingRequirement:\s*'any'"), core);
        Assert.Matches(new Regex(@"value:\s*'specialAccountCategoryPair'[^\r\n]*accountMappingRequirement:\s*'any'"), core);
        Assert.Matches(new Regex(@"value:\s*'revenueDebitNearQuarterEnd'[^\r\n]*accountMappingRequirement:\s*'revenue'"), core);
        Assert.Matches(new Regex(@"value:\s*'manualRevenueEntry'[^\r\n]*accountMappingRequirement:\s*'revenue'"), core);
        Assert.Matches(new Regex(@"value:\s*'revenueWithoutNormalCounterpart'[^\r\n]*accountMappingRequirement:\s*'revenueAndCounterpart'"), core);
    }

    [Fact]
    public void AvailableTypes_EvaluateTargetContentFacts_NotImportPresence()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var start = filter.IndexOf("function availableRuleTypes()", StringComparison.Ordinal);
        var end = filter.IndexOf("\n  function availablePrescreenKeys()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var body = filter[start..end];

        Assert.Contains("accountMappingRequirement", body, StringComparison.Ordinal);
        Assert.Contains("hasAnyCategory", body, StringComparison.Ordinal);
        Assert.Contains("hasRevenue", body, StringComparison.Ordinal);
        Assert.Contains("hasCounterpart", body, StringComparison.Ordinal);
        Assert.DoesNotContain("requiresAccountMapping", body, StringComparison.Ordinal);
    }

    [Fact]
    public void KctDisabledNotes_DistinguishMissingImportAndMissingTargetContent()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        Assert.Contains("function accountMappingRequirementNote", filter, StringComparison.Ordinal);
        Assert.Contains("需先匯入科目配對", filter, StringComparison.Ordinal);
        Assert.Contains("科目配對需至少一筆非空白分類", filter, StringComparison.Ordinal);
        Assert.Contains("科目配對需包含 Revenue 分類", filter, StringComparison.Ordinal);
        Assert.Contains("科目配對需至少一個一般對方分類", filter, StringComparison.Ordinal);
        Assert.Contains("accountMappingRequirementNote(item.ref)", filter, StringComparison.Ordinal);

        var start = filter.IndexOf("function customPickerHtml()", StringComparison.Ordinal);
        var end = filter.IndexOf("\n  function ", start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var customPicker = filter[start..end];

        Assert.Contains("accountMappingRequirementNote(t.value)", customPicker, StringComparison.Ordinal);
        Assert.Contains("picker-card--disabled", customPicker, StringComparison.Ordinal);
        Assert.Contains("disabled aria-disabled=\"true\"", customPicker, StringComparison.Ordinal);
        Assert.Contains("picker-card__note", customPicker, StringComparison.Ordinal);
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }.Concat(segments).ToArray()));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
