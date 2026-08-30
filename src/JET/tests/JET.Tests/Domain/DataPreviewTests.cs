using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class DataPreviewTests
{
    [Fact]
    public void TryParse_TbStagingWireName_ReturnsTbStaging()
    {
        var parsed = DataPreviewDatasetNames.TryParse("tbStaging", out var dataset);

        Assert.True(parsed);
        Assert.Equal(DataPreviewDataset.TbStaging, dataset);
    }

    [Theory]
    [InlineData("glStaging", DataPreviewDataset.GlStaging)]
    [InlineData("glEntries", DataPreviewDataset.GlEntries)]
    [InlineData("glExcludedEntries", DataPreviewDataset.GlExcludedEntries)]
    [InlineData("tbBalances", DataPreviewDataset.TbBalances)]
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
    [InlineData("TbStaging")]
    public void TryParse_UnknownWireName_ReturnsFalseAndDefault(string? wireName)
    {
        var parsed = DataPreviewDatasetNames.TryParse(wireName, out var dataset);

        Assert.False(parsed);
        Assert.Equal(default, dataset);
    }

}
