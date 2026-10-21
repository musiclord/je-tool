using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 2026-10-20 使用者選擇在測試清理加上查出持有者的診斷：刪不掉暫存根目錄時照樣失敗，
/// 訊息要列出還留著的檔案與開著它的程序；這裡由測試程序自己開著一個合成鎖檔。
/// </summary>
public sealed class TempProjectRootTests
{
    [Fact]
    public void Dispose_FileStillOpen_FailsAndNamesTheHolder()
    {
        var root = new TempProjectRoot();
        Directory.CreateDirectory(root.Path);
        var lockPath = Path.Combine(root.Path, ".report-artifacts-synthetic.lock");
        try
        {
            using (new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                var exception = Assert.Throws<IOException>(root.Dispose);

                var original = Assert.IsType<IOException>(exception.InnerException);
                Assert.StartsWith(original.Message, exception.Message, StringComparison.Ordinal);
                var diagnostics = exception.Message[original.Message.Length..];
                Assert.Contains(".report-artifacts-synthetic.lock：0 位元組", diagnostics, StringComparison.Ordinal);
                // 同一時間 Defender 之類的程序也可能開著這個新檔，所以只要求列出測試程序本身，不要求它排第一個。
                Assert.Contains("開著它的程序：", diagnostics, StringComparison.Ordinal);
                Assert.Contains($"測試程序本身 pid {Environment.ProcessId}", diagnostics, StringComparison.Ordinal);
                Assert.DoesNotContain(Path.GetTempPath(), diagnostics, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            root.Dispose();
        }

        Assert.False(Directory.Exists(root.Path));
    }
}
