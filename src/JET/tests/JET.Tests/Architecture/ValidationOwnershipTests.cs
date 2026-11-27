using Xunit;

namespace JET.Tests.Architecture;

public sealed class ValidationOwnershipTests
{
    private static readonly System.Reflection.Assembly ProductionAssembly =
        typeof(global::JET.Domain.JetActionException).Assembly;

    [Fact]
    public void TypedValidationSurface_IsInternal()
    {
        // 清單裡的型別都須存在且維持 internal。
        var expected = new[]
        {
            "JET.Application.ValidationReportProjectionParser",
            "JET.AuditCore.IValidationFactsPort",
            "JET.AuditCore.ValidationControlTotalsFacts",
            "JET.AuditCore.ValidationFacts",
            "JET.AuditCore.ValidationPlan",
            "JET.AuditCore.ValidationRequest",
            "JET.AuditCore.ValidationResult",
            "JET.AuditCore.IValidationReportWriter",
            "JET.Domain.ValidationReportProjection"
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
    public void RunHandlerAndRepository_ArePublic(string name)
    {
        var type = ProductionAssembly.GetType(name);

        Assert.NotNull(type);
        Assert.True(type!.IsPublic);
    }
}
