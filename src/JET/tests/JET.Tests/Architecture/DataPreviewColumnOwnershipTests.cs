using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 資料預覽 GL 與 GL(未納入測試) 的欄位、欄序、SQL 與讀值轉換只定義一次；本機與 SQL Server 兩個實作都呼叫共用定義，
/// 避免其中一邊改了另一邊沒跟上（SQL Server 測試平時不連線執行，差異不會被發現）。
/// 預篩選收斂計畫階段五（使用者 2026-10-22 裁定依配對列欄位）把原本三處寫死的欄位清單收成一份欄位計畫：
/// 原本檢查兩個實作呼叫 <c>DataPreviewColumns.Gl</c> 與 <c>DataPreviewColumns.ManualAuto(</c>，改成檢查它們呼叫共用的查詢，
/// 而且不再自己寫欄位識別。第一次失敗：Focused 收據 20261023-112004700-2e000bb010d54098b6c123ef7f06faf0。
/// </summary>
public sealed class DataPreviewColumnOwnershipTests
{
    private static readonly string[] PreviewRepositories =
    [
        "src/JET/JET/Infrastructure/Persistence/Local/LocalDataPreviewRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerDataPreviewRepository.cs",
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
                         "\"documentNumber\"", "\"exclusionReason\"", "\"postingStatus\"", "\"manualAuto\"",
                         "\"manual\"", "\"automatic\"", "document_description", "is_manual"
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
