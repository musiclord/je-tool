using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class FrontendEscapingTests
{
    [Fact]
    public void Esc_EscapesBothQuoteKinds_ForHtmlAttributeInterpolation()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var match = Regex.Match(
            core,
            @"function esc\(text\)\s*\{(?<body>[\s\S]*?)\n\s*\}");

        Assert.True(match.Success, "找不到 ui-core.js 的 esc(text) 逃逸原語。");
        var body = match.Groups["body"].Value;
        Assert.Contains(".replace(/\"/g, '&quot;')", body, StringComparison.Ordinal);
        Assert.Contains(".replace(/'/g, '&#39;')", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingColumnAttributes_UseEscForUntrustedPbcHeaders()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");

        Assert.Contains(
            "data-map-col=\"' + Ui.esc(col) +",
            mapping,
            StringComparison.Ordinal);
        Assert.Contains(
            "data-focus-key=\"map-grid-' + kind + '-' + Ui.esc(col) + '\">",
            mapping,
            StringComparison.Ordinal);
        Assert.Contains(
            "<option value=\"' + Ui.esc(col) + '\"'",
            mapping,
            StringComparison.Ordinal);
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
