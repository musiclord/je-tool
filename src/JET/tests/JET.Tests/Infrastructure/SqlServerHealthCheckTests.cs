using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SqlServerHealthCheckTests
{
    [Fact]
    public async Task Probe_BadServer_ReturnsRedactedFailure()
    {
        var r = await SqlServerHealthCheck.ProbeAsync(
            "Server=nonexistent.invalid;Database=JET_DEV;Integrated Security=True;Connect Timeout=2;",
            default);
        Assert.False(r.Ok);
        Assert.Contains("JET_DEV", r.Message);                 // 含目標 DB
        Assert.Contains("nonexistent.invalid", r.Message);      // 含目標 server
        Assert.DoesNotContain("Password", r.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_PasswordConnString_NeverLeaksPassword()
    {
        var r = await SqlServerHealthCheck.ProbeAsync(
            "Server=nonexistent.invalid;Database=D;User ID=sa;Password=Sup3rSecret!;Connect Timeout=2;",
            default);
        Assert.False(r.Ok);
        Assert.DoesNotContain("Sup3rSecret", r.Message);
    }

    // --- DescribeAsync（system.databaseInfo 後端身分查詢）---
    // 等價分割：未設定（null / 空白 / 無 DataSource）、已設定但連不上。live 成功路徑依賴環境，
    // 故不在此單元測試斷言（由 app 執行時驗），這裡只鎖「未設定」與「連不上去敏」兩類。

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Integrated Security=True;Encrypt=True;")] // 有連線字串但無 Server → 視為未設定
    public async Task Describe_NoServerConfigured_ReturnsNotConfigured(string? connectionString)
    {
        var info = await SqlServerHealthCheck.DescribeAsync(connectionString, "JET_DEV", default);

        Assert.False(info.Configured);
        Assert.False(info.Reachable);
        Assert.Equal("", info.Server);
    }

    [Fact]
    public async Task Describe_BadServer_ConfiguredButUnreachable_NoLeak()
    {
        var info = await SqlServerHealthCheck.DescribeAsync(
            "Server=nonexistent.invalid;User ID=sa;Password=Sup3rSecret!;Connect Timeout=2;",
            "JET_DEV", default);

        Assert.True(info.Configured);
        Assert.False(info.Reachable);
        Assert.Equal("nonexistent.invalid", info.Server); // 目標 server 可揭露
        Assert.Equal("JET_DEV", info.Database);           // 單庫名取自參數、非連線字串
        // 去敏不變量：密碼絕不出現在任何欄位（含 Detail）。
        Assert.DoesNotContain("Sup3rSecret", info.Detail);
        Assert.DoesNotContain("Sup3rSecret", info.Server);
        Assert.DoesNotContain("Sup3rSecret", info.Database);
    }
}
