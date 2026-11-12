using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 資料預覽 GL 與 GL(未納入測試) 的欄位、欄序、SQL 與讀值轉換只定義一次，資料預覽倉儲呼叫共用定義。
/// 資料預覽依配對列欄位，欄位清單收成一份欄位計畫：
/// 這裡檢查本機實作呼叫共用的查詢，而且不自己寫欄位識別。
/// </summary>
public sealed class DataPreviewColumnOwnershipTests
{
    private static readonly string[] PreviewRepositories =
    [
        "JET/Infrastructure/Persistence/Local/LocalDataPreviewRepository.cs",
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
                TestRepositoryPaths.SolutionRoot,
                "JET", "Infrastructure", "Persistence", "DataPreviewColumns.cs")),
            "GL 九欄的舊清單已收進欄位計畫，不再另外保留。");
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(
            TestRepositoryPaths.SolutionRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
