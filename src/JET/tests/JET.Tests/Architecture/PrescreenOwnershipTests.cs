using Xunit;

namespace JET.Tests.Architecture;

public sealed class PrescreenOwnershipTests
{
    private static readonly System.Reflection.Assembly ProductionAssembly =
        typeof(global::JET.Domain.JetActionException).Assembly;

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
            "JET.AuditCore.IPrescreenReportWriter",
            "JET.Domain.PrescreenReportProjection",
            "JET.Domain.PrescreenReportRuleProjection",
            "JET.Infrastructure.PrescreenExecutionInput"
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
    public void RunHandlerAndRepository_ArePublic(string name)
    {
        var type = ProductionAssembly.GetType(name);

        Assert.NotNull(type);
        Assert.True(type!.IsPublic);
    }

    // 這裡核對 AuditCore 用到的 PrescreenRunResult 是 public 型別。
    [Theory]
    [InlineData("JET.Domain.PrescreenRunResult")]
    public void PublicCompatibilityContracts_RemainAvailable(string name)
    {
        var type = ProductionAssembly.GetType(name);

        Assert.NotNull(type);
        Assert.True(type!.IsPublic);
    }
}
