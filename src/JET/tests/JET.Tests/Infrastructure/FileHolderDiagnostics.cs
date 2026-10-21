using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 測試清理刪不掉暫存資料夾時，列出還留著的項目與當下開著它們的程序，附進失敗訊息。
/// 2026-10-20 一次 Public 在清理時刪不掉報告鎖檔，事後查不出是誰開著；使用者同日選擇先加這個診斷，失敗判定不變。
/// 查詢用的開檔只要求讀取屬性，不受其他程序的共用模式限制，也不會被算進結果；查到本程序，就是測試程序自己還開著。
/// </summary>
internal static class FileHolderDiagnostics
{
    private const int MaxEntries = 50;
    private const int BufferSize = 64 * 1024;
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileProcessIdsUsingFileInformation = 47;

    public static string Describe(string root)
    {
        var report = new StringBuilder();
        report.Append(CultureInfo.InvariantCulture,
            $"清理診斷，{DateTime.Now:HH:mm:ss.fff} 查詢：暫存根目錄 {Path.GetFileName(root)} 刪不掉時還留著下列項目。");
        var entries = Entries(root);
        foreach (var entry in entries.Take(MaxEntries))
        {
            var isDirectory = Directory.Exists(entry);
            var holders = Holders(entry, out var error);
            // 資料夾沒有程序開著時不列，避免訊息被一路刪不掉的上層資料夾塞滿。
            if (isDirectory && error is null && holders.Count == 0)
            {
                continue;
            }

            var name = entry == root ? "暫存根目錄本身" : Path.GetRelativePath(root, entry);
            var facts = isDirectory ? "資料夾" : FileFacts(entry);
            var who = error ?? (holders.Count == 0 ? "查詢時沒有程序開著" : "開著它的程序：" + string.Join("、", holders));
            report.Append(CultureInfo.InvariantCulture, $"\n- {name}：{facts}；{who}");
        }

        if (entries.Count > MaxEntries)
        {
            report.Append(CultureInfo.InvariantCulture, $"\n- 另有 {entries.Count - MaxEntries} 個項目沒有列出。");
        }

        return report.ToString();
    }

    private static List<string> Entries(string root)
    {
        try
        {
            return [root, .. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [root];
        }
    }

    private static string FileFacts(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return string.Create(CultureInfo.InvariantCulture,
                $"{file.Length:N0} 位元組，建立 {file.CreationTime:HH:mm:ss.fff}，最後寫入 {file.LastWriteTime:HH:mm:ss.fff}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "讀不到檔案資訊";
        }
    }

    private static List<string> Holders(string path, out string? error)
    {
        using var handle = CreateFileW(path, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"無法查詢，Win32 錯誤 {Marshal.GetLastPInvokeError()}");
            return [];
        }

        var buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            var status = NtQueryInformationFile(handle, out _, buffer, BufferSize, FileProcessIdsUsingFileInformation);
            if (status != 0)
            {
                error = string.Create(CultureInfo.InvariantCulture, $"無法查詢，NTSTATUS 0x{status:X8}");
                return [];
            }

            error = null;
            var count = Marshal.ReadInt32(buffer);
            return Enumerable.Range(0, count)
                .Select(index => DescribeProcess((int)Marshal.ReadIntPtr(buffer, IntPtr.Size + index * IntPtr.Size)))
                .ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string DescribeProcess(int processId)
    {
        if (processId == Environment.ProcessId)
        {
            return string.Create(CultureInfo.InvariantCulture, $"測試程序本身 pid {processId}");
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return string.Create(CultureInfo.InvariantCulture, $"{process.ProcessName} pid {processId}");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return string.Create(CultureInfo.InvariantCulture, $"已結束的程序 pid {processId}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationFile(SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation, int length, int fileInformationClass);
}
