using System.Reflection;
using System.Text.RegularExpressions;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 文字長度與取樣數量的上限各只定義一次：400 字來自 <see cref="TextStorageLimits.TextUtf16CodeUnits"/>，
/// 欄位值比對一次最多幾個來自 MappingValueProfileHandler；前端用具名常數鏡像，不把數字寫在用到的地方
/// （技術債一輪，2026-10-22）。
/// </summary>
public sealed class TextLimitOwnershipTests
{
    [Fact]
    public void SearchTextLimit_ComesFromTextStorageLimits_AndTheFrontendMirrorsIt()
    {
        var handlers = ProductSources.Product("Application", "Handlers", "AccountMappingEditorHandlers.cs");
        Assert.Contains("TextStorageLimits.TextUtf16CodeUnits", handlers, StringComparison.Ordinal);
        Assert.DoesNotContain("> 400", handlers, StringComparison.Ordinal);

        var core = ProductSources.Frontend("ui-core.js");
        Assert.Equal(TextStorageLimits.TextUtf16CodeUnits, ProductSources.FrontendInt(core, "SEARCH_TEXT_MAX_LENGTH"));
        Assert.Contains("SEARCH_TEXT_MAX_LENGTH: SEARCH_TEXT_MAX_LENGTH", core, StringComparison.Ordinal);
        foreach (var source in new[] { ProductSources.Frontend("account-mapping-editor.js"), ProductSources.Frontend("filter-values.js") })
        {
            Assert.Contains("maxLength: Ui.SEARCH_TEXT_MAX_LENGTH", source, StringComparison.Ordinal);
            Assert.DoesNotContain("maxLength: 400", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LabelLengthLimits_ReferenceTextStorageLimits_AndTheFrontendMirrorsThem()
    {
        var rde = ProductSources.Product("Domain", "Contracts", "MappingMetadataContracts.cs");
        Assert.Contains("LabelUtf16CodeUnits = TextStorageLimits.TextUtf16CodeUnits;", rde, StringComparison.Ordinal);
        var taxonomy = ProductSources.Product("Domain", "Contracts", "AccountTaxonomyContracts.cs");
        Assert.Contains("MaxLabelLength = TextStorageLimits.TextUtf16CodeUnits;", taxonomy, StringComparison.Ordinal);

        var core = ProductSources.Frontend("ui-core.js");
        Assert.Equal(GlRdeStorageLimits.LabelUtf16CodeUnits, ProductSources.FrontendInt(core, "RDE_MAX_LABEL_LENGTH"));
        Assert.Equal(AccountTaxonomyInvariant.MaxLabelLength, ProductSources.FrontendInt(core, "TAXONOMY_MAX_LABEL_LENGTH"));
        // 註解曾寫成不存在的 GlMappingOptionRules；鏡像的是 GlRdeStorageLimits。
        Assert.DoesNotContain("GlMappingOptionRules", core, StringComparison.Ordinal);
        Assert.Contains("GlRdeStorageLimits", core, StringComparison.Ordinal);
    }

    [Fact]
    public void ValueProfileComparisonLimit_IsOneBackendConstant_MirroredByTheFrontend()
    {
        var max = Assert.IsType<int>(typeof(MappingValueProfileHandler)
            .GetField("MaxLimit", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!
            .GetRawConstantValue());
        Assert.Equal(100, max);

        var core = ProductSources.Frontend("ui-core.js");
        Assert.Equal(max, ProductSources.FrontendInt(core, "VALUE_PROFILE_MAX_VALUES"));
        Assert.Contains("VALUE_PROFILE_MAX_VALUES: VALUE_PROFILE_MAX_VALUES", core, StringComparison.Ordinal);

        var mapping = ProductSources.Frontend("steps", "mapping-step.js");
        Assert.Equal(3, Regex.Matches(mapping, @"Ui\.VALUE_PROFILE_MAX_VALUES\b").Count);
        Assert.DoesNotContain("slice(0, 100)", mapping, StringComparison.Ordinal);
        Assert.DoesNotContain("100 - anchors.length", mapping, StringComparison.Ordinal);
    }
}
