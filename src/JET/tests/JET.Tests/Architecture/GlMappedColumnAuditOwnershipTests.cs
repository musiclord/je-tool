using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// GL 必填文字欄整欄空白的偵測，和 TB 的 <c>TbMappedColumnAudit</c> 一樣由欄位目錄產生欄位清單與查詢，
/// GL 倉儲呼叫 <c>GlMappedColumnAudit.ReadAsync</c>，不自己手寫四個欄位。原本本機與 SQL Server 的 GL 倉儲都要檢查；
/// 只保留 SQLite 之後 SQL Server 的倉儲已刪除，只檢查本機倉儲，方法名稱跟著改（裁定 2026-11-06-08）。
/// 這次的第一次失敗：Focused 收據 20261106-084107421-33f747f01c134f3ab499d047fcc1c3ed。
/// 第一次失敗：Focused 收據 20261023-024529526-8734987986ba4bdbac60307db83f9dc1（共用的偵測還沒寫，找不到由欄位目錄產生的空白判定）。
/// 同一次執行裡，<c>CrossDatabaseConsistencyTests</c> 新增的整欄空白固定答案在 SQLite 與 DuckDB 都已通過，因為這一項只改寫法，不改提醒內容。
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
