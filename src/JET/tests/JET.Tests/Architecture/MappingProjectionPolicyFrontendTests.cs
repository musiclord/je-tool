using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 欄位配對步驟的標準化政策鏡像守衛（前端工作流整合）。後端 <c>mapping.commit.gl</c> 仍是
/// 權威驗證；本組只剩核准日模式的封閉選項鏡像。互斥引導、來源值分布的取得方式與送出形狀，
/// 2026-10-14 起改由 <c>tools/tests/frontend-journey-mapping.test.cjs</c> 在正式第三步畫面上核對。
/// </summary>
public sealed class MappingProjectionPolicyFrontendTests
{
    [Fact]
    public void ApprovalDateModes_MirrorTheClosedWireValues()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var modes = ExtractValueLabelMap(core, "GL_APPROVAL_DATE_MODES");

        Assert.Equal(
            new[] { "unmapped", "mapped", "sameAsPostDate" },
            modes.Keys.ToArray());
        Assert.All(modes.Values, label => Assert.False(string.IsNullOrWhiteSpace(label)));
    }

    private static Dictionary<string, string> ExtractValueLabelMap(string source, string arrayName)
    {
        var arrayMatch = Regex.Match(
            source,
            arrayName + @"\s*=\s*\[(?<body>.*?)\];",
            RegexOptions.Singleline);
        Assert.True(arrayMatch.Success, $"ui-core.js 內找不到 {arrayName} 陣列。");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
            arrayMatch.Groups["body"].Value,
            @"\{\s*value:\s*'(?<value>[^']+)'\s*,\s*label:\s*'(?<label>[^']+)'"))
        {
            result[match.Groups["value"].Value] = match.Groups["label"].Value;
        }

        Assert.NotEmpty(result);
        return result;
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

        return directory?.FullName ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
