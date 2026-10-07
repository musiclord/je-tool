using Xunit;

namespace JET.Tests.Application;

public sealed class WorkpaperCancellationFileDiscoveryTests
{
    // docs/action-contract-manifest.md: 底稿檔名含時間尾碼，取消檢查必須能找出此種殘留檔案。
    [Fact]
    public void CancellationFileDiscovery_FindsTimestampedWorkpaperInProjectSubdirectory()
    {
        var folder = Path.Combine(Path.GetTempPath(), "jet-cancel-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var output = Directory.CreateDirectory(Path.Combine(folder, "reports")).FullName;
            var path = Path.Combine(output, "Synthetic_20250101-20251231_WorkingPaper_20261007120000.xlsx");
            File.WriteAllText(path, "synthetic filename probe, not a workbook");
            Assert.Equal([path], WorkpaperExportTestSupport.FindWorkpapers(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
