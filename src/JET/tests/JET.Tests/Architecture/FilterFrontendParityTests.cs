using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 日期欄位的前後端鏡像守衛。Domain 的 GL 欄位白名單是正本；前端 <c>ui-core.js</c> 的 <c>GL_FIELDS</c> 與
/// <c>FILTER_DATE_FIELDS</c> 只做顯示鏡像，日期欄位集合不得漂移。
/// 組內 AND 與 OR 混用的讀回句子改由 <c>tools/tests/frontend-journey-readback.test.cjs</c> 與共用夾具
/// <c>tools/tests/fixtures/batch7-filter-readback.json</c> 以行為核對（2026-10-14 刪除原本的字串比對方法）。
/// </summary>
public sealed class FilterFrontendParityTests
{
    [Fact]
    public void DateFields_MirrorBackendFilterAndMappingCatalogs()
    {
        var backendDateFields = GlMappingKeys.All
            .Where(key => GlFieldWhitelist.TryResolve(key, out var column)
                && column.Kind == GlFieldKind.Date)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        var uiCore = ReadFrontend("js", "ui-core.js");
        var frontendGlFields = ExtractObjectArrayKeys(uiCore, "GL_FIELDS");
        var frontendDateFields = ExtractStringArrayValues(uiCore, "FILTER_DATE_FIELDS");

        Assert.Empty(MissingFrom(backendDateFields, FilterConditionLabels.GlFields.Keys));
        Assert.Empty(MissingFrom(backendDateFields, frontendGlFields));
        Assert.Equal(backendDateFields, frontendDateFields.OrderBy(static key => key, StringComparer.Ordinal));
        // 使用者 2026-10-08 裁定移除傳票日期與回溯過帳：日期欄只剩總帳日期與傳票核准日，前後端都不再有 voucherDate。
        Assert.Equal(["docDate", "postDate"], backendDateFields);
        Assert.DoesNotContain("voucherDate", frontendGlFields);
        Assert.False(FilterConditionLabels.GlFields.ContainsKey("voucherDate"));
    }

    private static IReadOnlyCollection<string> ExtractObjectArrayKeys(
        string source,
        string arrayName)
    {
        var body = ExtractArrayBody(source, arrayName);
        var result = Regex.Matches(body, @"\bkey:\s*'(?<key>[^']+)'")
            .Cast<Match>()
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(result);
        return result;
    }

    private static IReadOnlyCollection<string> ExtractStringArrayValues(
        string source,
        string arrayName)
    {
        var body = ExtractArrayBody(source, arrayName);
        var result = Regex.Matches(body, @"'(?<value>[^']+)'")
            .Cast<Match>()
            .Select(match => match.Groups["value"].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(result);
        return result;
    }

    private static string ExtractArrayBody(string source, string arrayName)
    {
        var match = Regex.Match(
            source,
            arrayName + @"\s*=\s*\[(?<body>.*?)\];",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"ui-core.js 找不到 {arrayName} 陣列。");
        return match.Groups["body"].Value;
    }

    private static string[] MissingFrom(
        IEnumerable<string> expectedFields,
        IEnumerable<string> availableFields) =>
        expectedFields
            .Except(availableFields, StringComparer.Ordinal)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();

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
