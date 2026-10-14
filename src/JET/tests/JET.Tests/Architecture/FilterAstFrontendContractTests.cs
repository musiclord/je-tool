using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// Stage 5 filter AST frontend contract guards. The backend renderer and validator remain
/// authoritative; the runtime frontend only mirrors labels, edits wire data, and provides
/// accessible form guidance. All examples are synthetic.
/// 剩下的方法核對欄位值條件的標籤鏡像，以及第五步畫面固定的操作掛點。文字值清單的編輯與上限，
/// 2026-10-14 起改由 <c>tools/tests/frontend-journey-filter-saved.test.cjs</c> 的 journey 15 核對；
/// 上限與後端一致的鏡像在 <c>FilterBackendRuleMirrorFrontendTests.TextSetMaxValues_MatchesBackendTextSetLimit</c>。
/// 比對範圍（同一分錄列、同一傳票）的畫面行為與條件讀回句子，2026-10-14 起改由
/// <c>tools/tests/frontend-journey-filter-build.test.cjs</c>、<c>frontend-journey-readback.test.cjs</c> 與共用夾具
/// <c>tools/tests/fixtures/batch7-filter-readback.json</c> 核對；後端同一份夾具由 <c>Batch7FilterReadbackTests</c> 核對。
/// </summary>
public sealed class FilterAstFrontendContractTests
{
    [Fact]
    public void FilterCompleteness_ValueLabelsMirrorTheBackend()
    {
        var editor = ReadFrontend("js", "filter-values.js");
        var labels = Regex.Match(editor, @"var labels = \{(?<body>[\s\S]*?)\};").Groups["body"].Value;
        var found = Regex.Matches(labels, @"(?<key>\w+): '(?<label>[^']+)'" )
            .ToDictionary(match => match.Groups["key"].Value, match => match.Groups["label"].Value);
        Assert.Equal(FieldValueConditions.Labels.Count, found.Count);
        foreach (var expected in FieldValueConditions.Labels)
            Assert.Equal(expected.Value, found[expected.Key]);
    }

    [Theory]
    [InlineData("data-bind=\"scenario-name\"")]
    [InlineData("data-bind=\"scenario-rationale\"")]
    [InlineData("data-bind=\"scenario-notice\"")]
    [InlineData("data-bind=\"preview-pane-body\"")]
    [InlineData("data-action=\"add-rule\"")]
    [InlineData("data-action=\"add-set\"")]
    [InlineData("data-action=\"remove-rule\"")]
    [InlineData("data-action=\"preview-scenario\"")]
    [InlineData("data-action=\"save-scenario\"")]
    [InlineData("data-action=\"resave-scenarios\"")]
    [InlineData("data-action=\"toggle-scenario\"")]
    [InlineData("data-action=\"remove-scenario\"")]
    public void ExpandedFilterBuilder_PreservesStableRuntimeHooks(string hook)
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        Assert.Contains(hook, filter, StringComparison.Ordinal);
    }

    private static string ReadFrontend(params string[] relativeSegments)
    {
        var path = Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }
            .Concat(relativeSegments)
            .ToArray());
        Assert.True(File.Exists(path), $"找不到前端檔：{path}");
        return File.ReadAllText(path);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
    }
}
