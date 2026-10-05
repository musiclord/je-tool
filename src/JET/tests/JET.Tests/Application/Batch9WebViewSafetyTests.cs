using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9WebViewSafetyTests
{
    private static Type Host => typeof(global::JET.Form1);

    [Fact]
    public void BuildConfiguration_ControlsBrowserAccelerators_AndHostActuallyUsesThePolicy()
    {
        var property = Host.GetProperty("BrowserAcceleratorsEnabled", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(property);
#if DEBUG || JET_AGENT_GUI_TEST
        Assert.Equal(true, property.GetValue(null));
#else
        Assert.Equal(false, property.GetValue(null));
#endif
        Assert.Contains("settings.AreBrowserAcceleratorKeysEnabled = BrowserAcceleratorsEnabled;", Source());
    }

    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.BrowserProcessExited, true)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessExited, true)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive, false)]
    [InlineData(CoreWebView2ProcessFailedKind.GpuProcessExited, false)]
    [InlineData(CoreWebView2ProcessFailedKind.FrameRenderProcessExited, false)]
    public void MainProcessCrashes_RequireRestart_ButAutomaticallyRecoverableFailuresDoNot(CoreWebView2ProcessFailedKind kind, bool expected)
    {
        var method = Host.GetMethod("RequiresWebViewRestart", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.Equal(expected, method.Invoke(null, [kind]));
    }

    [Fact]
    public void InitializationAndCrashMessages_ExplainRecoveryWithoutRawExceptionText()
    {
        foreach (var name in new[] { "WebViewInitializationFailureMessage", "WebViewProcessFailureMessage" })
        {
            var field = Host.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var text = Assert.IsType<string>(field.GetRawConstantValue());
            Assert.Contains("重新開啟 JET", text);
            Assert.DoesNotContain("Exception", text);
        }
        var source = Source();
        Assert.Contains(".ProcessFailed += OnWebViewProcessFailed", source);
        Assert.Contains("CancellationRegistry.CancelAll()", source);
        Assert.DoesNotContain("\"JET WebView2 initialization failed\"", source);
        Assert.DoesNotContain(".Reload()", source);
    }

    private static string Source()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "JET.slnx"))) root = root.Parent;
        return File.ReadAllText(Path.Combine(root!.FullName, "JET", "Form1.cs"));
    }
}
