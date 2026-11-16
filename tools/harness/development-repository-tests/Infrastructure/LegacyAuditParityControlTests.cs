using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyAuditParityControlTests
{
    [Theory]
    // 不指定資料庫時只跑 SQLite 一種，預期是 1。
    [InlineData(null, null, null, 1, "Export", false)]
    // 下面兩列用 SQLite 測 stopAfter 與 keepOutputs 的解析。
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
