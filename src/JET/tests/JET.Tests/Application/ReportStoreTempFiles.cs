using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 報告清單寫到一半時放在案件資料夾裡的檔案：暫存的報告、暫存的清單與原清單的備份。
/// 寫入完成、取消或失敗後還原成功，這三種都不能留下；備份只在還原失敗時才保留。
/// </summary>
internal static class ReportStoreTempFiles
{
    private static readonly string[] Prefixes = [".report-stage-", ".report-manifest-", ".report-backup-"];

    internal static void AssertNoneLeft(string projectDirectory)
    {
        foreach (var prefix in Prefixes)
        {
            Assert.Empty(Directory.GetFiles(projectDirectory, prefix + "*"));
        }
    }
}
