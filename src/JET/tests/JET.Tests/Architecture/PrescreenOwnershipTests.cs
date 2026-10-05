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
    /// 2026-10-02 資料庫分流簡化：分流層刪除後，handler 直接拿到該種資料庫組裡的實作。原本要求存在的
    /// ProviderRoutingPrescreenFactsPort（須為 internal）與 ProviderRoutingPrescreenRunRepository（須為 public）
    /// 已刪除，從下面兩個清單移出，改在這裡確認它們不再存在。第一次失敗收據：20261002-120525875-ce1f07f5d912434b86ce8a6eef43759e。
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
            "JET.Domain.ITypedPrescreenReportWriter",
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
