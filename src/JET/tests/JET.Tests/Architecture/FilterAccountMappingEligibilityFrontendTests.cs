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
        Assert.Contains("需先完成科目配對", filter, StringComparison.Ordinal);
        Assert.Contains("科目配對需至少一筆非空白分類", filter, StringComparison.Ordinal);
        Assert.Contains("科目配對需包含 Revenue 分類", filter, StringComparison.Ordinal);
        Assert.Contains("科目配對需至少一個一般對方分類", filter, StringComparison.Ordinal);
        Assert.Contains("accountMappingRequirementNote(item.ref)", filter, StringComparison.Ordinal);

        // 2026-09-07 自訂篩選條件改版：十三張挑選卡改成每組一列三個家族入口（addRuleBarHtml），
        // 「科目分類」入口在案件沒有科目配對時停用並把同一句原因顯示在旁邊。守衛的意圖不變：
        // 自訂條件的停用原因與 KCT 卡用同一個 accountMappingRequirementNote，且停用對輔助工具可見。
        // 第一次失敗的證據：收據 20260907-135727110（customPickerHtml 已不含挑選卡）。
        // 09-08 科目入口改成原生選項，保留停用與可閱讀的原因；第一次失敗為 20260908-024320083。
        var start = filter.IndexOf("function addRuleBarHtml()", StringComparison.Ordinal);
        var end = filter.IndexOf("\n  function ", start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var addBar = filter[start..end];

        Assert.Contains("accountMappingRequirementNote('accountSide')", addBar, StringComparison.Ordinal);
        Assert.Contains("option('type:accountSide', '借方或貸方分類', accountNote)", addBar, StringComparison.Ordinal);
        Assert.Contains("(note ? ' disabled' : '')", addBar, StringComparison.Ordinal);
        Assert.Contains("Ui.esc(label + (note ? '（' + note + '）' : ''))", addBar, StringComparison.Ordinal);
        Assert.Contains("scenario-add__note", addBar, StringComparison.Ordinal);
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
