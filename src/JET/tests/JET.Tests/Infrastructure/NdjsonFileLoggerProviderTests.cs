using System.Text.Json;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 診斷日誌檔案 sink(dev-only;與 ring buffer 並列):每筆 entry 以 NDJSON append 到指定目錄的檔案,
/// 讓 agent 跑完 app 後直接讀執行時日誌。oracle:讀回檔案逐行解析 + scope 捕捉(重用 ring buffer 同一轉換)。
/// </summary>
public sealed class NdjsonFileLoggerProviderTests
{
    [Fact]
    public void Log_WritesOneNdjsonLinePerEntry_InOrder()
    {
        using var root = new TempProjectRoot();
        using var provider = new NdjsonFileLoggerProvider(root.Path);
        var logger = provider.CreateLogger("Test");

        logger.LogInformation("hello {N}", 1);
        logger.LogInformation("world {N}", 2);

        var lines = File.ReadAllLines(provider.FilePath);
        Assert.Equal(2, lines.Length);
        Assert.Equal("hello 1", JsonDocument.Parse(lines[0]).RootElement.GetProperty("message").GetString());
        Assert.Equal("world 2", JsonDocument.Parse(lines[1]).RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void Log_WithBeginScope_CapturesCorrelationId()
    {
        using var root = new TempProjectRoot();
        using var provider = new NdjsonFileLoggerProvider(root.Path);
        var logger = provider.CreateLogger("Test");

        using (logger.BeginScope(new Dictionary<string, object?> { ["correlation_id"] = "C1" }))
        {
            logger.LogInformation("scoped");
        }

        var entry = JsonDocument.Parse(File.ReadAllLines(provider.FilePath).Single()).RootElement;
        Assert.Equal("C1", entry.GetProperty("correlationId").GetString());
    }

    [Fact]
    public void FilePath_IsInGivenDirectory_WithJetDevPrefixAndNdjsonExtension()
    {
        using var root = new TempProjectRoot();
        using var provider = new NdjsonFileLoggerProvider(root.Path);

        Assert.Equal(root.Path, Path.GetDirectoryName(provider.FilePath));
        Assert.StartsWith("jet-dev-", Path.GetFileName(provider.FilePath));
        Assert.EndsWith(".ndjson", provider.FilePath);
    }
}
