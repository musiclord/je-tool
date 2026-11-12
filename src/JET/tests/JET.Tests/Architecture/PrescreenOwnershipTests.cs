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

    /// <remarks>
    /// handler 直接拿到資料庫組裡的實作，沒有分流層。這裡確認
    /// ProviderRoutingPrescreenFactsPort 與 ProviderRoutingPrescreenRunRepository 不存在。
    /// </remarks>
    [Theory]
    [InlineData("JET.Infrastructure.ProviderRoutingPrescreenFactsPort")]
    public void ProviderRoutingPrescreenTypes_AreRemovedWithRoutingLayer(string name)
    {
        Assert.Null(ProductionAssembly.GetType(name));
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
    public void ExistingProductionTypes_RemainPublicDuringTypedCutover(string name)
    {
        var type = ProductionAssembly.GetType(name);

        Assert.NotNull(type);
        Assert.True(type!.IsPublic);
    }

    // IPrescreenRunRepository 與 PrescreenRunInput 不存在（由 CompatibilityPathOwnershipTests 確認），
    // 這裡只核對 AuditCore 仍在用的 PrescreenRunResult。
    [Theory]
    [InlineData("JET.Domain.PrescreenRunResult")]
    public void PublicCompatibilityContracts_RemainAvailable(string name)
    {
        var type = ProductionAssembly.GetType(name);

        Assert.NotNull(type);
        Assert.True(type!.IsPublic);
    }
}
