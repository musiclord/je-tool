using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 「文字欄是空白」「文字欄不是空白」的 SQL 只寫在 AuditCore 的 SqlBlankText 一處；
/// 驗證、預篩選、篩選與科目配對的查詢都呼叫它，不各自手寫 IS NULL OR TRIM(x) = ''，避免同一個判斷散在各處各寫一份。
/// 傳票號碼跨日重用的檢查也用同一個判定，純空白的傳票號碼和空白傳票號碼類別一樣視為空白。
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
            // 一條規則只有一個定義，所以欄位條件的空白判斷改呼叫 GlRulePredicates.BlankText（它呼叫 SqlBlankText），和第四步空白摘要同一個方法。
            Assert.True(source.Contains("SqlBlankText.", StringComparison.Ordinal) || source.Contains("BlankText(", StringComparison.Ordinal),
                string.Join("/", parts) + " 沒有呼叫共用的空白判定");
            Assert.DoesNotContain("IS NULL OR {dialect.Trim(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IS NULL OR {trim(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IS NOT NULL AND {dialect.Trim(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IS NOT NULL AND {{dialect.Trim(", source, StringComparison.Ordinal);
        }

        var procedures = ProductSources.Product("AuditCore", "ValidationProcedures.cs");
        Assert.DoesNotContain("voucher_number IS NOT NULL AND voucher_number <> ''", procedures, StringComparison.Ordinal);
    }
}
