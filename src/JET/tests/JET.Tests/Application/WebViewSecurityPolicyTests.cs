using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Application;

public sealed class WebViewSecurityPolicyTests
{
    [Theory]
    [InlineData("https://app.jet.local/", true)]
    [InlineData("https://APP.JET.LOCAL/index.html?tab=1#summary", true)]
    [InlineData("https://app.jet.local:443/index.html", true)]
    [InlineData("http://app.jet.local/index.html", false)]
    [InlineData("https://app.jet.local.evil.example/index.html", false)]
    [InlineData("https://evil.example/", false)]
    [InlineData("file:///C:/temp/index.html", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a uri", false)]
    public void NavigationAllowlist_OnlyAcceptsAppVirtualHost(string uri, bool expected)
    {
        var policy = typeof(Form1).GetMethod(
            "IsAppNavigationAllowed",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(policy);

        var actual = Assert.IsType<bool>(policy.Invoke(null, [uri]));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WebViewInitialization_InstallsNavigationPopupAndSettingsFences()
    {
        var source = ReadProductionSource("Form1.cs");

        Assert.Contains("NavigationStarting +=", source, StringComparison.Ordinal);
        Assert.Contains("NewWindowRequested +=", source, StringComparison.Ordinal);
        Assert.Contains("args.Handled = true;", source, StringComparison.Ordinal);
        Assert.Contains("settings.AreHostObjectsAllowed = false;", source, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"#if DEBUG \|\| JET_AGENT_GUI_TEST(?s:.*?)settings\.AreDevToolsEnabled = true;(?s:.*?)settings\.AreDefaultContextMenusEnabled = true;(?s:.*?)#else(?s:.*?)settings\.AreDevToolsEnabled = false;(?s:.*?)settings\.AreDefaultContextMenusEnabled = false;(?s:.*?)#endif",
                RegexOptions.CultureInvariant),
            source);
    }

    private static string ReadProductionSource(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "JET" }.Concat(segments).ToArray()));

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
