using Xunit;

namespace JET.Tests.Architecture;

public sealed class ValidationOwnershipTests
{
    private static readonly System.Reflection.Assembly ProductionAssembly =
        typeof(global::JET.Domain.JetActionException).Assembly;

    [Fact]
    public void ValidationGenericExecutionSeams_AreRemovedAfterTypedCutover()
    {
        Assert.Null(ProductionAssembly.GetType("JET.Domain.IValidationRunRepository"));
        Assert.Null(ProductionAssembly.GetType("JET.Domain.ValidationRunInput"));
        Assert.Null(ProductionAssembly.GetType("JET.Domain.ValidationReportProjectionParser"));
        Assert.Null(ProductionAssembly.GetType("JET.Infrastructure.ValidationAuditRuntime"));
        Assert.Null(ProductionAssembly.GetType("JET.Infrastructure.ProviderRoutingValidationRunRepository"));
    }

    [Fact]
    public void TypedValidationSurface_IsInternal()
    {
        var expected = new[]
        {
            "JET.Application.ValidationReportProjectionParser",
            "JET.AuditCore.IValidationFactsPort",
            "JET.AuditCore.ProgramGraph",
            "JET.AuditCore.ProgramNode",
            "JET.AuditCore.ValidationControlTotalsFacts",
            "JET.AuditCore.ValidationFacts",
            "JET.AuditCore.ValidationPlan",
            "JET.AuditCore.ValidationRequest",
            "JET.AuditCore.ValidationResult",
            "JET.Domain.ITypedValidationReportWriter",
            "JET.Domain.ValidationReportProjection",
            "JET.Infrastructure.ProviderRoutingValidationFactsPort"
        };

        foreach (var name in expected)
        {
            var type = ProductionAssembly.GetType(name);
            Assert.NotNull(type);
            Assert.False(type!.IsPublic);
        }
    }

    [Theory]
    [InlineData("JET.Application.ValidateRunHandler")]
    [InlineData("JET.Infrastructure.LocalValidationRunRepository")]
    [InlineData("JET.Infrastructure.SqlServerValidationRunRepository")]
    public void ExistingProductionTypes_RemainPublicDuringTypedCutover(string name)
    {
        var type = ProductionAssembly.GetType(name);

        Assert.NotNull(type);
        Assert.True(type!.IsPublic);
    }
}
