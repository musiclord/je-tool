using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 有效分錄預覽的欄序與人工或自動欄的轉換只定義一次；本機與 SQL Server 兩個實作都呼叫共用定義，
/// 避免其中一邊改了代碼另一邊沒跟上（SQL Server 測試平時不連線執行，差異不會被發現）。
/// </summary>
public sealed class DataPreviewColumnOwnershipTests
{
    private static readonly string[] PreviewRepositories =
    [
        "src/JET/JET/Infrastructure/Persistence/Local/LocalDataPreviewRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerDataPreviewRepository.cs",
    ];

    [Fact]
    public void GlPreviewRepositories_UseSharedManualAutoConversion()
    {
        foreach (var relativePath in PreviewRepositories)
        {
            var source = Read(relativePath);
            Assert.Contains("DataPreviewColumns.GlEntries", source, StringComparison.Ordinal);
            Assert.Contains("DataPreviewColumns.ManualAuto(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("\"manual\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("\"automatic\"", source, StringComparison.Ordinal);
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(
            TestRepositoryPaths.RepositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
