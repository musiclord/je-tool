using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// GL 必填文字欄整欄空白的偵測，和 TB 的 <c>TbMappedColumnAudit</c> 一樣由欄位目錄產生欄位清單與查詢，
/// GL 倉儲呼叫 <c>GlMappedColumnAudit.ReadAsync</c>，不自己手寫四個欄位。這裡只檢查本機倉儲。
/// </summary>
public sealed class GlMappedColumnAuditOwnershipTests
{
    private static readonly string[][] Repositories =
    [
        ["Infrastructure", "Persistence", "Local", "LocalGlRepository.cs"]
    ];

    [Fact]
    public void GlBlankColumnProbe_IsBuiltFromTheFieldCatalog_AndUsedByTheGlRepository()
    {
        var audit = ProductSources.Product("Infrastructure", "Sql", "GlMappedColumnAudit.cs");
        Assert.Contains("JetFieldCatalog.GlFields", audit, StringComparison.Ordinal);
        Assert.Contains("SqlBlankText.IsNotBlank(", audit, StringComparison.Ordinal);
        Assert.Contains("internal static async Task<IReadOnlyList<string>> ReadAsync(", audit, StringComparison.Ordinal);

        foreach (var parts in Repositories)
        {
            var source = ProductSources.Product(parts);
            Assert.Contains("GlMappedColumnAudit.ReadAsync(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("emptyTextColumns", source, StringComparison.Ordinal);
            Assert.DoesNotContain("description IS NOT NULL", source, StringComparison.Ordinal);
        }
    }
}
