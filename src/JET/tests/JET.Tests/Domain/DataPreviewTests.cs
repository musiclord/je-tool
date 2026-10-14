using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class DataPreviewTests
{
    [Fact]
    public void TryParse_PbcTbWireName_ReturnsPbcTb()
    {
        var parsed = DataPreviewDatasetNames.TryParse("pbcTb", out var dataset);

        Assert.True(parsed);
        Assert.Equal(DataPreviewDataset.PbcTb, dataset);
    }

    [Theory]
    [InlineData("pbcGl", DataPreviewDataset.PbcGl)]
    [InlineData("gl", DataPreviewDataset.Gl)]
    [InlineData("glExcluded", DataPreviewDataset.GlExcluded)]
    [InlineData("tb", DataPreviewDataset.Tb)]
    [InlineData("accountMappings", DataPreviewDataset.AccountMappings)]
    [InlineData("authorizedPreparers", DataPreviewDataset.AuthorizedPreparers)]
    [InlineData("dateDimension", DataPreviewDataset.DateDimension)]
    [InlineData("schemaOverview", DataPreviewDataset.SchemaOverview)]
    public void TryParse_KnownWireName_ReturnsExpectedDataset(string wireName, DataPreviewDataset expected)
    {
        var parsed = DataPreviewDatasetNames.TryParse(wireName, out var dataset);

        Assert.True(parsed);
        Assert.Equal(expected, dataset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tb_staging")]
    [InlineData("PbcTb")]
    // 使用者 2026-10-08 裁定資料名稱全系統統一；舊 wire key 不再接受。
    [InlineData("glStaging")]
    [InlineData("glEntries")]
    [InlineData("glExcludedEntries")]
    [InlineData("tbStaging")]
    [InlineData("tbBalances")]
    public void TryParse_UnknownWireName_ReturnsFalseAndDefault(string? wireName)
    {
        var parsed = DataPreviewDatasetNames.TryParse(wireName, out var dataset);

        Assert.False(parsed);
        Assert.Equal(default, dataset);
    }

}
