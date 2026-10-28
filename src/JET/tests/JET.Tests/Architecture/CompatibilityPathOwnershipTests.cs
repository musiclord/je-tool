using System.Reflection;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 只有測試或舊呼叫端在用的相容路徑已刪除，產品只留正式入口（使用者 2026-10-22 裁定「全部刪，測試改走正式入口」）：
/// 預篩選倉儲只服務 facts 介面；四種報告寫出器各只有一個正式方法；底稿只依計畫寫出；WorkpaperContext 沒有舊欄位；
/// 沒有空的稽核紀錄實作；只有測試在用的方法移到測試或刪除；前端沒有 setFilterPopulationScope。
/// </summary>
public sealed class CompatibilityPathOwnershipTests
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void PrescreenRepositories_OnlyServeTheFactsPort()
    {
        Assert.Null(ProductSources.ProductType("JET.Domain.IPrescreenRunRepository"));
        Assert.Null(ProductSources.ProductType("JET.Domain.PrescreenRunInput"));

        var input = ProductSources.ProductType("JET.Infrastructure.PrescreenExecutionInput");
        Assert.NotNull(input);
        Assert.Null(input!.GetMethod("FromCompatibility", AnyStatic));

        foreach (var name in new[] { "JET.Infrastructure.LocalPrescreenRunRepository", "JET.Infrastructure.SqlServerPrescreenRunRepository" })
        {
            var repository = ProductSources.ProductType(name);
            Assert.NotNull(repository);
            Assert.Null(repository!.GetMethod("RunAsync", AnyInstance));
            Assert.Null(repository.GetMethod("ToCompatibilityResult", AnyStatic));
        }
    }

    [Fact]
    public void ReportWriters_HaveOneFormalEntryEach()
    {
        foreach (var retired in new[]
                 {
                     "JET.Domain.ITypedValidationReportWriter",
                     "JET.Domain.ITypedPrescreenReportWriter",
                     "JET.Domain.ITypedCriteriaSelectionReportWriter",
                     "JET.AuditCore.IPlannedValidationReportWriter",
                     "JET.AuditCore.IPlannedPrescreenReportWriter",
                     "JET.AuditCore.IFormalPlannedValidationReportWriter",
                     "JET.AuditCore.IFormalPlannedPrescreenReportWriter",
                     "JET.Domain.IFormalInfReportWriter",
                     "JET.Domain.IFormalCriteriaSelectionReportWriter"
                 })
        {
            Assert.True(ProductSources.ProductType(retired) is null, retired + " 仍然存在。");
        }

        foreach (var name in new[]
                 {
                     "JET.AuditCore.IValidationReportWriter",
                     "JET.AuditCore.IPrescreenReportWriter",
                     "JET.Domain.IInfReportWriter",
                     "JET.Domain.ICriteriaSelectionReportWriter"
                 })
        {
            var writer = ProductSources.ProductType(name);
            Assert.True(writer is not null, name + " 不存在。");
            var method = Assert.Single(writer!.GetMethods());
            Assert.Equal("WriteAsync", method.Name);
        }

        var legacy = typeof(JET.Infrastructure.LegacyReportWriter);
        Assert.Null(legacy.GetMethod("CompatibilityPlan", AnyStatic));
        Assert.Null(legacy.GetMethod("CompatibilityPrescreenPlan", AnyStatic));

        var handlers = ProductSources.Product("Application", "Handlers", "ExportReportHandlers.cs");
        Assert.DoesNotContain(" as IPlanned", handlers, StringComparison.Ordinal);
        Assert.DoesNotContain(" as ITyped", handlers, StringComparison.Ordinal);
        Assert.DoesNotContain(" as IFormal", handlers, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteTypedAsync(", handlers, StringComparison.Ordinal);
        Assert.DoesNotContain("WritePlannedAsync(", handlers, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkpaperWriter_OnlyWritesFromAPlan()
    {
        Assert.Null(ProductSources.ProductType("JET.Domain.IWorkpaperWriter"));
        Assert.Null(typeof(JET.Infrastructure.WorkpaperWriter).GetMethod("EmitCompatibilityFieldInfoSheet", AnyInstance));
        Assert.Null(typeof(WorkpaperContext).GetProperty("ScenarioConditionLogic"));

        Assert.DoesNotContain("plan: null", ProductSources.Product("Infrastructure", "Export", "WorkpaperWriter.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sample.CreditScaled > 0 ? \"CREDIT\"",
            ProductSources.Product("Infrastructure", "Export", "WorkpaperWriter.Step2To41.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TestOnlyMembers_AreGone()
    {
        Assert.Null(ProductSources.ProductType("JET.Domain.NullProjectAuditLog"));
        Assert.Null(typeof(ValidationProcedures).GetField("PeriodBounds", AnyStatic));
        Assert.Null(typeof(ValidationProcedures).GetMethod("PeriodBoundsWith", AnyStatic));
        Assert.Null(typeof(JetAuditProgram).GetMethod("ComputeInfSamplingOrderingKey", AnyStatic));
        Assert.Null(typeof(ReportExportSupport).GetMethod("RequireRevision", AnyStatic));
        Assert.NotNull(typeof(ReportExportSupport).GetMethod("RequireRevisionState", AnyStatic));
        Assert.Null(typeof(JetFieldCatalog).GetMethod("FindGlMappingSlot", AnyStatic));
        Assert.Null(typeof(JetFieldCatalog).GetMethod("FindTbMappingSlot", AnyStatic));
        Assert.DoesNotContain("setFilterPopulationScope", ProductSources.Frontend("state.js"), StringComparison.Ordinal);
    }
}
