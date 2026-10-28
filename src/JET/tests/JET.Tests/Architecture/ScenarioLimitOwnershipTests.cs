using System.Reflection;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 已存情境上限只定義在 <see cref="FilterScenarioLimits.MaxSavedScenarios"/>。底稿第 4 與 4-1 頁把位置換成位元的 SQL、
/// 解回位置的方法、矩陣與暫存檔的範圍檢查、條件篩選報告的列範圍，以及「最多可儲存」的句子都引用它，不各自寫 10
/// （技術債一輪，使用者 2026-10-22 裁定全部收斂）。
/// </summary>
public sealed class ScenarioLimitOwnershipTests
{
    [Fact]
    public void PositionBitMask_IsBuiltOnceFromTheLimit()
    {
        Assert.NotNull(ProductSources.ProductType("JET.Infrastructure.ScenarioTagMask"));
        var mask = ProductSources.Product("Infrastructure", "Sql", "ScenarioTagMask.cs");
        Assert.Contains("FilterScenarioLimits.MaxSavedScenarios", mask, StringComparison.Ordinal);

        var stream = ProductSources.Product("Infrastructure", "Sql", "WorkpaperStep4StreamReader.cs");
        var session = ProductSources.Product("Infrastructure", "Sql", "WorkpaperStep41PreparedSession.cs");
        foreach (var source in new[] { stream, session })
        {
            Assert.Contains("ScenarioTagMask.", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WHEN 10 THEN 512", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DecodePositions(long tagMask)", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PositionRangeChecks_UseTheLimit()
    {
        var scope = ProductSources.Product("Infrastructure", "Sql", "TagMatrixScenarioSqlScope.cs");
        Assert.Contains("FilterScenarioLimits.MaxSavedScenarios", scope, StringComparison.Ordinal);
        Assert.DoesNotContain("> 10", scope, StringComparison.Ordinal);

        var spool = ProductSources.Product("Infrastructure", "Export", "WorkpaperWriter.Step2To41.cs");
        Assert.Contains("positionCount > FilterScenarioLimits.MaxSavedScenarios", spool, StringComparison.Ordinal);
        Assert.DoesNotContain("positionCount is < 0 or > 10", spool, StringComparison.Ordinal);
    }

    [Fact]
    public void CriteriaReportRows_DeriveFromTheLimit()
    {
        var criteria = ProductSources.Product("Infrastructure", "Export", "LegacyReportWriter.Criteria.cs");
        Assert.Contains("FilterScenarioLimits.MaxSavedScenarios", criteria, StringComparison.Ordinal);
        Assert.DoesNotContain("row <= 14", criteria, StringComparison.Ordinal);
        Assert.DoesNotContain("position <= 10", criteria, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Max(16u", criteria, StringComparison.Ordinal);
    }

    [Fact]
    public void LimitMessage_IsOneBackendSentence_MirroredByTheFrontend()
    {
        var message = typeof(FilterScenarioLimits).GetField("MaxSavedScenariosMessage", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(message);
        Assert.Equal("最多可儲存 10 個篩選情境，請先移除不需要的情境。", message!.GetValue(null));

        foreach (var source in new[]
                 {
                     ProductSources.Product("Application", "Handlers", "FilterHandlers.cs"),
                     ProductSources.Product("AuditCore", "FilterProgram.cs"),
                     ProductSources.Product("Application", "Support", "FilterScenarioBatchWriter.cs")
                 })
        {
            Assert.Contains("FilterScenarioLimits.MaxSavedScenariosMessage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("最多可儲存", source, StringComparison.Ordinal);
        }

        // 前端的句子由既有的 FilterBackendRuleMirrorFrontendTests 逐字核對；這裡確認它拼出來和後端那一句相同。
        var core = ProductSources.Frontend("ui-core.js");
        var frontendSentence = "最多可儲存 " + ProductSources.FrontendInt(core, "FILTER_MAX_SAVED_SCENARIOS") + " 個篩選情境，請先移除不需要的情境。";
        Assert.Equal(message.GetValue(null), frontendSentence);
    }
}
