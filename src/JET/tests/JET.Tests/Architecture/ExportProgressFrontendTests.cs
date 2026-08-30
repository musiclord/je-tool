using Xunit;

namespace JET.Tests.Architecture;

/// <summary>匯出進度只鏡射後端真實里程碑；不得從未知總量虛構百分比或完成。</summary>
public sealed class ExportProgressFrontendTests
{
    [Fact]
    public void ExportProgress_UsesAllContractFields_WithoutInventingPercentageOrSuccess()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "JET", "wwwroot", "js", "app.js"));
        var formatter = ExtractFunction(app, "formatExportProgress");

        foreach (var phase in new[]
        {
            "preparingData",
            "writingSheet",
            "finalizingWorkbook",
            "publishingArtifact"
        })
        {
            Assert.Contains(phase, formatter, StringComparison.Ordinal);
        }

        foreach (var field in new[]
        {
            "artifactKind",
            "phase",
            "sheetName",
            "sheetsCompleted",
            "rowsWritten",
            "elapsedMilliseconds"
        })
        {
            Assert.Contains(field, formatter, StringComparison.Ordinal);
        }

        Assert.DoesNotContain('%', formatter);
        Assert.DoesNotContain('％', formatter);
        Assert.DoesNotContain("百分", formatter, StringComparison.Ordinal);
        Assert.DoesNotContain("percent", formatter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style.width", formatter, StringComparison.Ordinal);
        Assert.DoesNotContain("rowsTotal", formatter, StringComparison.Ordinal);
        Assert.DoesNotContain("sheetsTotal", formatter, StringComparison.Ordinal);
        Assert.DoesNotContain("匯出成功", formatter, StringComparison.Ordinal);
        Assert.DoesNotContain("匯出完成", formatter, StringComparison.Ordinal);

        var subscriber = ExtractSubscription(app, "export.progress");
        Assert.Contains("formatExportProgress(progress)", subscriber, StringComparison.Ordinal);
        Assert.Contains("Store.setBusyDetail", subscriber, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Store.addMessage",
            subscriber,
            StringComparison.Ordinal);
    }

    private static string ExtractFunction(string source, string name)
    {
        var start = source.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {name} 函式。");
        return ExtractBalancedBlock(source, start);
    }

    private static string ExtractSubscription(string source, string eventName)
    {
        var start = source.IndexOf($"JetApi.on('{eventName}'", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {eventName} 訂閱。");
        return ExtractBalancedBlock(source, start);
    }

    private static string ExtractBalancedBlock(string source, int start)
    {
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

        throw new InvalidOperationException("找不到 JavaScript block 結尾。");
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
