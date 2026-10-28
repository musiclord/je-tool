using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 「文字欄是空白」「文字欄不是空白」的 SQL 只寫在 AuditCore 的 SqlBlankText 一處，去空白的字元集合由方言決定；
/// 驗證、預篩選、篩選與科目配對的查詢都呼叫它，不各自手寫 IS NULL OR TRIM(x) = ''（技術債一輪，2026-10-22）。
/// 傳票號碼跨日重用的檢查也改用同一個判定，純空白的傳票號碼和空白傳票號碼類別一樣視為空白。
/// </summary>
public sealed class BlankTextPredicateOwnershipTests
{
    private static readonly string[][] Consumers =
    [
        ["AuditCore", "FilterCompilation.cs"],
        ["AuditCore", "GlRulePredicates.cs"],
        ["AuditCore", "GlRulePredicates.Frequency.cs"],
        ["AuditCore", "GlRulePredicates.Values.cs"],
        ["AuditCore", "ValidationProcedures.cs"],
        ["Infrastructure", "Persistence", "AccountMappingPopulationQuery.cs"],
        ["Infrastructure", "Sql", "TbMappedColumnAudit.cs"]
    ];

    [Fact]
    public void BlankPredicate_IsWrittenOnce_AndEveryConsumerCallsIt()
    {
        Assert.NotNull(ProductSources.ProductType("JET.AuditCore.SqlBlankText"));

        foreach (var parts in Consumers)
        {
            var source = ProductSources.Product(parts);
            // 預篩選收斂計畫階段九：欄位條件的空白判斷改呼叫 GlRulePredicates.BlankText（它呼叫 SqlBlankText），和第四步空白摘要同一個方法。
            // 第一次失敗：Focused 收據 20261024-195604731-a54ac30c8a994b46878d21f5b82967bf。
            Assert.True(source.Contains("SqlBlankText.", StringComparison.Ordinal) || source.Contains("BlankText(", StringComparison.Ordinal),
                string.Join("/", parts) + " 沒有呼叫共用的空白判定");
            Assert.DoesNotContain("IS NULL OR {dialect.Trim(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IS NULL OR {trim(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IS NOT NULL AND {dialect.Trim(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IS NOT NULL AND {{dialect.Trim(", source, StringComparison.Ordinal);
        }

        var procedures = ProductSources.Product("AuditCore", "ValidationProcedures.cs");
        Assert.DoesNotContain("document_number IS NOT NULL AND document_number <> ''", procedures, StringComparison.Ordinal);
    }
}
