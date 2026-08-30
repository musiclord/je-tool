using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class UiRunRefreshPipelineTests
{
    [Fact]
    public void Run_RefreshesOnceOnlyAfterBusyClears_WithSuccessOrSettledGuard()
    {
        var source = ReadFrontend("js", "ui-core.js");
        var run = ExtractFunction(source, "run");

        Assert.Contains("function run(label, promiseFactory, options)", run, StringComparison.Ordinal);
        Assert.Contains("if (Store.getState().busy)", run, StringComparison.Ordinal);
        Assert.Contains("var operationSucceeded = false;", run, StringComparison.Ordinal);
        Assert.Contains("operationSucceeded = true;", run, StringComparison.Ordinal);
        Assert.Contains("options.refreshWhen === 'success'", run, StringComparison.Ordinal);
        Assert.Contains("options.refresh", run, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"\.finally\(function \(\) \{\s*Store\.setBusy\(false\);\s*\}\)\s*\.then\(function"),
            run);
        Assert.Matches(
            new Regex(@"Promise\.resolve\(\)\s*\.then\(options\.refresh\)\s*\.catch\(function \(error\)"),
            run);
        Assert.Single(Regex.Matches(run, @"\.then\(options\.refresh\)").Cast<Match>());
    }

    [Fact]
    public void Run_BusyEarlyReturn_ShowsFeedbackBeforeRefreshPipeline()
    {
        var run = ExtractFunction(ReadFrontend("js", "ui-core.js"), "run");

        var busyGuard = run.IndexOf("if (Store.getState().busy)", StringComparison.Ordinal);
        var feedback = run.IndexOf("Store.addMessage", busyGuard, StringComparison.Ordinal);
        var earlyReturn = run.IndexOf("return Promise.resolve();", busyGuard, StringComparison.Ordinal);
        var refresh = run.IndexOf("options.refresh", StringComparison.Ordinal);
        Assert.True(busyGuard >= 0 && feedback > busyGuard && earlyReturn > feedback && refresh > earlyReturn,
            "busy 再入必須先留下使用者可見訊息，再於任何 refresh 排程前返回。");
    }

    private static string ExtractFunction(string source, string name)
    {
        var start = source.IndexOf($"function {name}(", StringComparison.Ordinal);
        var next = source.IndexOf("\n  function ", start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0 && next > start, $"找不到 {name} 函式邊界。");
        return source[start..next];
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
