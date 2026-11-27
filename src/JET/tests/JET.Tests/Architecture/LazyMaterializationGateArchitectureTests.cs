using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 可同時執行的查詢遇到結果是空的才補算：只讀的部分照常同時執行；補算要先取得和 dispatcher 共用的
/// 非阻塞獨占閘門 ActionExecutionGate，取不到時回 operation_in_progress。
/// </summary>
public sealed class LazyMaterializationGateArchitectureTests
{
    private static readonly string[] LazyQueryHandlerPaths =
    [
        Path.Combine("Application", "Handlers", "Query", "QueryFilterHitsPageHandler.cs"),
        Path.Combine("Application", "Handlers", "Query", "QueryHighRiskSummaryScenariosHandler.cs"),
        Path.Combine("Application", "Handlers", "Query", "QueryHighRiskSummaryVoucherPageHandler.cs"),
        Path.Combine("Application", "Handlers", "Query", "QueryHighRiskSummaryRowPageHandler.cs")
    ];

    [Fact]
    public void ExclusiveExecutionGate_IsSharedByDispatcherAndLazyMaterializationService()
    {
        var domainSources = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    Path.Combine(JetRoot(), "JET", "Domain"),
                    "*.cs",
                    SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
        var dispatcher = ReadProduct("Bridge", "ActionDispatcher.cs");
        var materializeService = ReadProduct(
            "Application", "Support", "FilterHitMaterializeService.cs");

        Assert.Contains("class ActionExecutionGate", domainSources, StringComparison.Ordinal);
        Assert.Contains("ActionExecutionGate", dispatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("_exclusiveRunning", dispatcher, StringComparison.Ordinal);
        Assert.Contains("ActionExecutionGate", materializeService, StringComparison.Ordinal);
        Assert.Contains("TryAcquire", materializeService, StringComparison.Ordinal);
    }

    [Fact]
    public void LazyConcurrentQueries_UseDedicatedGatedMaterializationEntryPoint()
    {
        var materializeService = ReadProduct(
            "Application", "Support", "FilterHitMaterializeService.cs");
        Assert.Contains("MaterializeForConcurrentQueryAsync", materializeService, StringComparison.Ordinal);

        foreach (var relativePath in LazyQueryHandlerPaths)
        {
            var source = ReadProduct(relativePath.Split(Path.DirectorySeparatorChar));
            Assert.DoesNotContain(".MaterializeAllAsync(", source, StringComparison.Ordinal);
            Assert.Contains(
                ".MaterializeForConcurrentQueryAsync(", source, StringComparison.Ordinal);
        }
    }

    private static string ReadProduct(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { JetRoot(), "JET" }.Concat(segments).ToArray()));

    private static string JetRoot()
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
