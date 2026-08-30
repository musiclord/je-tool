using Xunit;

namespace JET.Tests.Architecture;

public sealed class PrescreenOwnershipTests
{
    private static readonly System.Reflection.Assembly ProductionAssembly =
        typeof(global::JET.Domain.JetActionException).Assembly;

    [Fact]
    public void PrescreenGenericExecutionSeams_AreRemovedAfterTypedCutover()
    {
        Assert.Null(ProductionAssembly.GetType("JET.AuditCore.IAuditRuntime"));
        Assert.Null(ProductionAssembly.GetType("JET.Infrastructure.PrescreenAuditRuntime"));
    }

    [Fact]
    public void TypedPrescreenSurface_IsInternal()
    {
        var expected = new[]
        {
            "JET.Application.PrescreenReportProjectionParser",
            "JET.AuditCore.IPrescreenFactsPort",
            "JET.AuditCore.PrescreenFacts",
            "JET.AuditCore.PrescreenPlan",
            "JET.AuditCore.PrescreenRequest",
            "JET.AuditCore.PrescreenResult",
            "JET.Domain.ITypedPrescreenReportWriter",
            "JET.Domain.PrescreenReportProjection",
            "JET.Domain.PrescreenReportRuleProjection",
            "JET.Infrastructure.PrescreenExecutionInput",
            "JET.Infrastructure.ProviderRoutingPrescreenFactsPort"
        };

        foreach (var name in expected)
        {
            var type = ProductionAssembly.GetType(name);
            Assert.NotNull(type);
            Assert.False(type!.IsPublic);
        }
    }

    [Theory]
    [InlineData("JET.Application.PrescreenRunHandler")]
    [InlineData("JET.Infrastructure.LocalPrescreenRunRepository")]
    [InlineData("JET.Infrastructure.ProviderRoutingPrescreenRunRepository")]
    [InlineData("JET.Infrastructure.SqlServerPrescreenRunRepository")]
    public void ExistingProductionTypes_RemainPublicDuringTypedCutover(string name)
    {
        var type = ProductionAssembly.GetType(name);

        Assert.NotNull(type);
        Assert.True(type!.IsPublic);
    }

    [Theory]
    [InlineData("JET.Domain.IPrescreenRunRepository")]
    [InlineData("JET.Domain.PrescreenRunInput")]
    [InlineData("JET.Domain.PrescreenRunResult")]
    public void PublicCompatibilityContracts_RemainAvailable(string name)
    {
        var type = ProductionAssembly.GetType(name);

        Assert.NotNull(type);
        Assert.True(type!.IsPublic);
    }
}
