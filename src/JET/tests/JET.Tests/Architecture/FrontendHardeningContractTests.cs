using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 2026-07-14 本地收尾第三批的前端靜態接線守衛。這些測試只鎖原語、消費點與先後順序；
/// 真實慢回應與可見性仍由 Windows GUI handoff 驗收。
/// </summary>
public sealed class FrontendHardeningContractTests
{
    [Fact]
    public void NativeWindowClose_RoutesThroughFrontendDepartureAndSharedGateFallback()
    {
        var form = File.ReadAllText(Path.Combine(RepoRoot(), "JET", "Form1.cs"));
        var closing = ExtractCSharpMethod(form, "OnFormClosing");
        var frontendExit = ExtractCSharpMethod(form, "RequestFrontendExitAsync");
        var busyFallback = ExtractCSharpMethod(form, "NotifyBusyNativeExitAsync");

        AssertOrder(
            closing,
            "CloseReason.UserClosing",
            "e.Cancel = true",
            "RequestFrontendExitAsync()");
        Assert.Contains("window.JetUi.exitApp()", frontendExit, StringComparison.Ordinal);
        AssertOrder(
            closing,
            "TryAcquireShutdownExecutionLease",
            "_runtime?.Dispose()");
        Assert.Contains("NotifyBusyNativeExitAsync", closing, StringComparison.Ordinal);
        Assert.Contains("state.activeRequestId", busyFallback, StringComparison.Ordinal);
        Assert.Contains("setBusyDetail(message)", busyFallback, StringComparison.Ordinal);
        // 使用者 2026-10-13 勾選頁確認 22、23：關閉視窗時用同一句，不再寫「關閉視窗」
        // （第一次失敗：Focused 收據 20261013-141518189-803ef992f6924f6e8ee9076331128cc7）。
        Assert.Contains("作業還在進行，請等它完成後再試一次。", busyFallback, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeFrontend_DoesNotExposeTestCaseLoadersInAnyConfiguration()
    {
        var frontendRoot = Path.Combine(RepoRoot(), "JET", "wwwroot");
        var runtime = string.Join('\n',
            Directory.EnumerateFiles(frontendRoot, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) is ".html" or ".css" or ".js")
                .Select(File.ReadAllText));

        Assert.DoesNotContain("套用測試案件", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("data-action=\"mock-load\"", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("btn--mock", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("mockButtonHtml", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("bindMockButton", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("registerMockLoader", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseUiStrings_UseAuditorVocabulary()
    {
        var frontendRoot = Path.Combine(RepoRoot(), "JET", "wwwroot");
        var releaseSources = Directory.EnumerateFiles(frontendRoot, "*.js", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("dev-panel.js", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("dev-log-panel.js", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("jet-api.js", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText);
        var uiLiterals = string.Join('\n', releaseSources.Select(ExtractJavaScriptStringLiterals));

        var forbiddenDisplayPhrases = new[]
        {
            "標準化",
            "審計角色",
            "傳票量詞",
            "已投影",
            "後端權威",
            "Artifact ID",
            "kind 未提供",
            "目前 catalog",
            "wire 或前端 state",
            "資料庫 Provider",
            "操作使用者 ID",
            "WebView2 host",
            "Bridge 連線",
            "Host 未連線",
            "請 DBA",
            "逐行 tag",
            "step4：",
            "step4-1：",
            "AUTHORIZED_PREPARER（",
            "DATE_DIMENSION（",
            "（stale）",
            "（retention）"
        };

        Assert.All(forbiddenDisplayPhrases, phrase =>
            Assert.DoesNotContain(phrase, uiLiterals, StringComparison.Ordinal));

        foreach (var requiredDisplayPhrase in new[]
        {
            // Q8 confirmed-state wording; all other vocabulary assertions remain unchanged.
            // First failure: 20261004-091831760-a762b9ef96db4d61a4bb2bff7cdb0022.
            "已確認配對",
            "分類用途",
            // 使用者 2026-10-13 裁定改名（計畫「收尾四項：調查與裁定」）；第一次失敗：20261013-092449164-3847782f6c50488b86a0ac3856e606ca。
            "金額",
            "符合條件的分錄",
            "資料儲存方式",
            "應用程式連線",
        })
        {
            Assert.Contains(requiredDisplayPhrase, uiLiterals, StringComparison.Ordinal);
        }

        var index = ReadFrontend("index.html");
        Assert.DoesNotContain("system.whoAmI", VisibleHtml(index), StringComparison.Ordinal);
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        Assert.DoesNotContain(">key<", ExtractJavaScriptStringLiterals(mapping), StringComparison.Ordinal);
    }

    private static string ExtractJavaScriptStringLiterals(string source)
    {
        var withoutComments = Regex.Replace(
            source,
            @"/\*[\s\S]*?\*/|(?m)^\s*//.*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        return string.Join('\n', Regex.Matches(
                withoutComments,
                @"(['""])(?:\\.|(?!\1).)*\1",
                RegexOptions.Singleline | RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Value));
    }

    private static string VisibleHtml(string source)
        => Regex.Replace(
            source,
            @"<!--[\s\S]*?-->",
            string.Empty,
            RegexOptions.CultureInvariant);

    private static void AssertOrder(string source, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(current > previous, $"預期 '{marker}' 位於前一個離場步驟之後。\n{source}");
            previous = current;
        }
    }

    private static string ExtractCSharpMethod(string source, string name)
    {
        var start = source.IndexOf("void " + name + "(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {name} 方法。");

        var bodyStart = source.IndexOf('{', start);
        Assert.True(bodyStart >= 0, $"找不到 {name} 方法本體。");
        var depth = 0;
        for (var i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"找不到 {name} 方法結尾。");
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
