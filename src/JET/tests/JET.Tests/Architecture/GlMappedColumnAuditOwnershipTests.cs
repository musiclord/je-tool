using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// GL 必填文字欄整欄空白的偵測，和 TB 的 <c>TbMappedColumnAudit</c> 一樣由欄位目錄產生欄位清單與查詢，
/// 本機與 SQL Server 的 GL 倉儲都呼叫 <c>GlMappedColumnAudit.ReadAsync</c>，不各自手寫四個欄位（預篩選收斂計畫階段四，2026-10-23）。
/// 第一次失敗：Focused 收據 20261023-024529526-8734987986ba4bdbac60307db83f9dc1（共用的偵測還沒寫，找不到由欄位目錄產生的空白判定）。
/// 同一次執行裡，<c>CrossDatabaseConsistencyTests</c> 新增的整欄空白固定答案在 SQLite 與 DuckDB 都已通過，因為這一項只改寫法，不改提醒內容。
/// </summary>
public sealed class GlMappedColumnAuditOwnershipTests
{
    private static readonly string[][] Repositories =
    [
        ["Infrastructure", "Persistence", "Local", "LocalGlRepository.cs"],
        ["Infrastructure", "Persistence", "SqlServer", "SqlServerGlRepository.cs"]
    ];

    [Fact]
    public void GlBlankColumnProbe_IsBuiltFromTheFieldCatalog_AndSharedByBothRepositories()
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
            Assert.DoesNotContain("document_description IS NOT NULL", source, StringComparison.Ordinal);
        }
    }
}
