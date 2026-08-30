using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>多來源匯入的前端只送一個既有 action；部分成功狀態不得在 UI 重建。</summary>
public sealed class ImportBatchAtomicityFrontendTests
{
    [Fact]
    public void ConfirmWizard_SubmitsOneAtomicSourcesPayload_WithoutPartialSuccessLoop()
    {
        var source = ReadFrontend("js", "steps", "import-step.js");
        var confirmWizard = ExtractFunction(source, "confirmWizard");

        Assert.Contains("sources:", confirmWizard, StringComparison.Ordinal);
        Assert.Contains("items.map", confirmWizard, StringComparison.Ordinal);
        Assert.Contains("mode: wizard.mode", confirmWizard, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(confirmWizard, @"\binvoke\s*\(").Cast<Match>());

        Assert.DoesNotContain("items.reduce", confirmWizard, StringComparison.Ordinal);
        Assert.DoesNotContain("mode: index === 0", confirmWizard, StringComparison.Ordinal);
        Assert.DoesNotContain("lastResponse", confirmWizard, StringComparison.Ordinal);
        Assert.DoesNotContain("wizard.pending = wizard.pending.filter", confirmWizard, StringComparison.Ordinal);
        Assert.DoesNotContain("applyResponse(kind, lastResponse)", confirmWizard, StringComparison.Ordinal);
    }

    private static string ExtractFunction(string source, string name)
    {
        var start = source.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {name} 函式。");

        var depth = 0;
        var opened = false;
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
                opened = true;
            }
            else if (source[index] == '}' && opened && --depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"找不到 {name} 函式結尾。");
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
