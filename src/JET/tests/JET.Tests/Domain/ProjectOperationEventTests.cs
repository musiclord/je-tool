using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class ProjectOperationEventTests
{
    [Theory]
    [InlineData(ProjectOperations.DataReimport, ProjectOperationTargetTypes.Dataset)]
    [InlineData(ProjectOperations.MappingRecommit, ProjectOperationTargetTypes.Mapping)]
    [InlineData(ProjectOperations.ReportPublish, ProjectOperationTargetTypes.ReportCatalog)]
    public void Create_AcceptsOnlyRegisteredOperationAndTargetTokens(string operation, string targetType)
    {
        var operationEvent = ProjectOperationEvent.Create(operation, targetType, "target", 1);

        Assert.Equal(operation, operationEvent.Operation);
        Assert.Equal(targetType, operationEvent.TargetType);
    }

    [Fact]
    public void Create_RejectsUnknownTokensAndNegativeCounts()
    {
        Assert.Throws<ArgumentException>(() =>
            ProjectOperationEvent.Create("unknown", ProjectOperationTargetTypes.Dataset, "gl", 1));
        Assert.Throws<ArgumentException>(() =>
            ProjectOperationEvent.Create(ProjectOperations.DataReimport, "unknown", "gl", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProjectOperationEvent.Create(
                ProjectOperations.DataReimport,
                ProjectOperationTargetTypes.Dataset,
                "gl",
                -1));
    }
}
