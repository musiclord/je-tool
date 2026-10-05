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
        // 2026-10-02 資料庫分流簡化：分流用的 ProviderRoutingValidationFactsPort 隨分流層刪除，
        // 從下一個測試的 internal 清單移出，改在這裡確認它不再存在。第一次失敗收據：20261002-120525875-ce1f07f5d912434b86ce8a6eef43759e。
        Assert.Null(ProductionAssembly.GetType("JET.Infrastructure.ProviderRoutingValidationFactsPort"));
    }

    [Fact]
    public void TypedValidationSurface_IsInternal()
    {
        // 2026-10-02 起 ProgramGraph 與 ProgramNode 隨只供測試使用的程式外殼一起刪除，因此從清單移除；
        // 其餘型別仍須存在且維持 internal。
        var expected = new[]
        {
            "JET.Application.ValidationReportProjectionParser",
            "JET.AuditCore.IValidationFactsPort",
            "JET.AuditCore.ValidationControlTotalsFacts",
            "JET.AuditCore.ValidationFacts",
            "JET.AuditCore.ValidationPlan",
            "JET.AuditCore.ValidationRequest",
            "JET.AuditCore.ValidationResult",
            "JET.Domain.ITypedValidationReportWriter",
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
    [InlineData("JET.Infrastructure.SqlServerValidationRunRepository")]
    public void ExistingProductionTypes_RemainPublicDuringTypedCutover(string name)
    {
        var type = ProductionAssembly.GetType(name);

        Assert.NotNull(type);
        Assert.True(type!.IsPublic);
    }
}
