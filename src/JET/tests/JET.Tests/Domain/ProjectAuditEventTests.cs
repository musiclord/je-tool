using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class ProjectAuditEventTests
{
    [Theory]
    [InlineData(ProjectAuditOperations.DataReimport, ProjectAuditTargetTypes.Dataset)]
    [InlineData(ProjectAuditOperations.MappingRecommit, ProjectAuditTargetTypes.Mapping)]
    [InlineData(ProjectAuditOperations.ReportPublish, ProjectAuditTargetTypes.ReportCatalog)]
    public void Create_AcceptsOnlyRegisteredOperationAndTargetTokens(string operation, string targetType)
    {
        var auditEvent = ProjectAuditEvent.Create(operation, targetType, "target", 1);

        Assert.Equal(operation, auditEvent.Operation);
        Assert.Equal(targetType, auditEvent.TargetType);
    }

    [Fact]
    public void Create_RejectsUnknownTokensAndNegativeCounts()
    {
        Assert.Throws<ArgumentException>(() =>
            ProjectAuditEvent.Create("unknown", ProjectAuditTargetTypes.Dataset, "gl", 1));
        Assert.Throws<ArgumentException>(() =>
            ProjectAuditEvent.Create(ProjectAuditOperations.DataReimport, "unknown", "gl", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProjectAuditEvent.Create(
                ProjectAuditOperations.DataReimport,
                ProjectAuditTargetTypes.Dataset,
                "gl",
                -1));
    }
}
