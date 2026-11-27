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
    [InlineData("accountMapping", DataPreviewDataset.AccountMapping)]
    [InlineData("authorizedPreparer", DataPreviewDataset.AuthorizedPreparer)]
    [InlineData("calendar", DataPreviewDataset.Calendar)]
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
    // 下面這些也不是資料集的 wire 名稱，一律不接受。
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
