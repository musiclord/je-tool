using System.Text.Json.Nodes;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class GlobalExceptionFallbackLogTests
{
    [Fact]
    public void Write_ThreeGlobalSources_ProducesOneJsonLinePerFailure()
    {
        using var root = new TempProjectRoot();
        var log = new GlobalExceptionFallbackLog(root.Path);

        log.Write("Application.ThreadException", new InvalidOperationException("thread\nfailure"));
        log.Write("AppDomain.UnhandledException", new ApplicationException("domain failure"), isTerminating: true);
        log.Write("TaskScheduler.UnobservedTaskException", new AggregateException("task failure"));

        var lines = File.ReadAllLines(log.FilePath);
        var entries = lines.Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();

        Assert.Equal(3, lines.Length);
        Assert.Equal(
            [
                "Application.ThreadException",
                "AppDomain.UnhandledException",
                "TaskScheduler.UnobservedTaskException"
            ],
            entries.Select(entry => entry["Source"]!.GetValue<string>()));
        Assert.False(entries[0]["IsTerminating"]!.GetValue<bool>());
        Assert.True(entries[1]["IsTerminating"]!.GetValue<bool>());
        Assert.Equal("thread\nfailure", entries[0]["Message"]!.GetValue<string>());
    }

    // 低嚴重度事項 E4（使用者 2026-10-19 要求處理）：視窗接住 WebView2 初始化失敗後，先寫一筆到例外後盾再顯示訊息框；
    // 這個失敗不會成為未處理例外，不寫就查不到原因。視窗本身不在單元測試啟動，所以另外核對 Form1 的接住處。
    [Fact]
    public void WebViewInitializationFailure_IsRecordedBeforeTheMessageBox()
    {
        using var root = new TempProjectRoot();
        var log = new GlobalExceptionFallbackLog(root.Path);

        log.Write(Form1.WebViewInitializationFailureSource, new InvalidOperationException("synthetic webview failure"));

        var entry = JsonNode.Parse(Assert.Single(File.ReadAllLines(log.FilePath)))!.AsObject();
        Assert.Equal("Form1.WebViewInitialization", entry["Source"]!.GetValue<string>());
        Assert.False(entry["IsTerminating"]!.GetValue<bool>());

        var form = File.ReadAllText(Path.Combine(RepoRoot(), "JET", "Form1.cs"));
        var record = form.IndexOf("Program.FallbackLog?.Write(WebViewInitializationFailureSource, exception);", StringComparison.Ordinal);
        var message = form.IndexOf("WebViewInitializationFailureMessage,", StringComparison.Ordinal);
        Assert.True(record > 0 && message > record, "Form1 先寫紀錄，再顯示初始化失敗訊息。");
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "JET", "Program.cs"));
        Assert.Contains("FallbackLog = fallbackLog;", program, StringComparison.Ordinal);
    }

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
