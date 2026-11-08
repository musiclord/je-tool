using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 資料預覽 GL 與 GL(未納入測試) 的欄位、欄序、SQL 與讀值轉換只定義一次，資料預覽倉儲呼叫共用定義。原本本機與 SQL Server
/// 兩個實作都要檢查；只保留 SQLite 之後 SQL Server 的實作已刪除，只檢查本機實作（裁定 2026-11-06-08），
/// 這次的第一次失敗：Focused 收據 20261106-084107421-33f747f01c134f3ab499d047fcc1c3ed。
/// 資料預覽依配對列欄位（裁定 2026-10-22-09），所以把原本三處寫死的欄位清單收成一份欄位計畫：
/// 原本檢查兩個實作呼叫 <c>DataPreviewColumns.Gl</c> 與 <c>DataPreviewColumns.ManualAuto(</c>，改成檢查它們呼叫共用的查詢，
/// 而且不再自己寫欄位識別。第一次失敗：Focused 收據 20261023-112004700-2e000bb010d54098b6c123ef7f06faf0。
/// </summary>
public sealed class DataPreviewColumnOwnershipTests
{
    private static readonly string[] PreviewRepositories =
    [
        "src/JET/JET/Infrastructure/Persistence/Local/LocalDataPreviewRepository.cs",
    ];

    [Fact]
    public void GlPreviewRepositories_BuildColumnsAndReadValuesThroughTheSharedQuery()
    {
        foreach (var relativePath in PreviewRepositories)
        {
            var source = Read(relativePath);
            Assert.Contains("DataPreviewGlQuery.", source, StringComparison.Ordinal);
            // 欄位識別、人工或自動分錄的代碼與分錄欄名都只寫在欄位計畫與共用查詢裡。
            foreach (var hardCoded in new[]
                     {
                         "\"voucherNumber\"", "\"exclusionReason\"", "\"postingStatus\"", "\"manualAuto\"",
                         "\"manual\"", "\"automatic\"", "description", "is_manual"
                     })
            {
                Assert.DoesNotContain(hardCoded, source, StringComparison.Ordinal);
            }
        }

        Assert.False(
            File.Exists(Path.Combine(
                TestRepositoryPaths.RepositoryRoot,
                "src", "JET", "JET", "Infrastructure", "Persistence", "DataPreviewColumns.cs")),
            "GL 九欄的舊清單已收進欄位計畫，不再另外保留。");
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(
            TestRepositoryPaths.RepositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
