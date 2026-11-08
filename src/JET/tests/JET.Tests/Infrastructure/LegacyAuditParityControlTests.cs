using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyAuditParityControlTests
{
    [Theory]
    // 不指定資料庫時原本三種資料庫都跑，只保留 SQLite 之後只剩一種，預期從 3 改成 1（裁定 2026-11-06-08）；
    // 第一次失敗：Focused 收據 20261106-084027187-45312a25ca8043b8a0f4152099dca2a0。
    [InlineData(null, null, null, 1, "Export", false)]
    // 下面兩列原本由 DuckDB 與 SQL Server 測停點與保留輸出的解析，改由 SQLite 測，情境不變（裁定 2026-11-06-08）。
    [InlineData("sqlite", "validate", "1", 1, "Validate", true)]
    [InlineData("sqlite", "prescreen", "true", 1, "Prescreen", true)]
    public void Parse_ProvidesBoundedProviderCheckpointAndRetentionControls(
        string? provider,
        string? stopAfter,
        string? keepOutputs,
        int expectedProviderCount,
        string expectedCheckpoint,
        bool expectedRetention)
    {
        var selection = LegacyAuditParityRunSelection.Parse(provider, stopAfter, keepOutputs);

        Assert.Equal(expectedProviderCount, selection.Providers.Count);
        Assert.Equal(expectedCheckpoint, selection.StopAfter.ToString());
        Assert.Equal(expectedRetention, selection.KeepOutputs);
    }

    [Theory]
    [InlineData("oracle")]
    [InlineData("everything")]
    public void Parse_RejectsUnknownProviderWithoutFallingBackToFullRun(string value)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            LegacyAuditParityRunSelection.Parse(value, null, null));

        Assert.Contains("provider", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("import")]
    [InlineData("mapping")]
    [InlineData("validate")]
    [InlineData("prescreen")]
    [InlineData("filter")]
    [InlineData("export")]
    public void Parse_AcceptsEveryNamedCheckpoint(string value)
    {
        var selection = LegacyAuditParityRunSelection.Parse("sqlite", value, "0");

        Assert.Single(selection.Providers);
        Assert.False(selection.KeepOutputs);
    }
}
