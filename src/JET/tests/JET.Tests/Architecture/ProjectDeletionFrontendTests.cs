using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>Picker 刪案完成後要在 single-flight 解除後重載權威清單，不讓已刪列殘留。</summary>
public sealed class ProjectDeletionFrontendTests
{
    [Fact]
    public void PickerDelete_DeclaresSuccessOnlyRefreshInsideRunPipeline()
    {
        var app = ReadFrontend("js", "app.js");

        Assert.Matches(
            new Regex(
                @"Ui\.run\('刪除專案', function \(\) \{(?s:.*?)projectDelete\((?s:.*?)\}\s*,\s*\{\s*refresh:\s*Ui\.loadProjects,\s*refreshWhen:\s*'success'(?s:.*?)\}\);"),
            app);
        Assert.Contains("onError: function (message, error)", app, StringComparison.Ordinal);
        Assert.Contains("projectId: target.id", app, StringComparison.Ordinal);
        Assert.Contains("errorCode: error && error.code", app, StringComparison.Ordinal);
        Assert.Contains("correlationId: error && error.correlationId", app, StringComparison.Ordinal);

        var deleteRunStart = app.IndexOf("Ui.run('刪除專案'", StringComparison.Ordinal);
        Assert.True(deleteRunStart >= 0);
        var deleteRunEnd = app.IndexOf("\n        });", deleteRunStart, StringComparison.Ordinal);
        Assert.True(deleteRunEnd > deleteRunStart);
        var deleteRun = app[deleteRunStart..(deleteRunEnd + "\n        });".Length)];
        Assert.Matches(
            new Regex(
                @"\}\)\.then\(function \(\) \{\s*restoreProjectDeleteFocus\(target\);\s*\}\);"),
            deleteRun);
        Assert.DoesNotMatch(
            new Regex(@"\}\)\.then\(function \(\) \{(?s:.*?)(Ui\.loadProjects|projectDelete|Store\.)"),
            deleteRun);
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
