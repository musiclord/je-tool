using System.Globalization;
using System.Text;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 處理紀錄的檔案：每個案件 GL 與 TB 各一份，放在案件資料夾，檔名是「案件名稱_GL處理紀錄.txt」與「案件名稱_TB處理紀錄.txt」。
/// 和舊版的歷程紀錄一樣用 UTF-8 加 BOM、Windows 換行，記事本與 Excel 都能直接開；檔案只往後加，不改寫已經寫下的內容。
/// </summary>
internal static class ProcessingLogFile
{
    internal const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private const string Rule = "------------------------------------------------------------";
    private const string HeaderRule = "============================================================";
    private const string NewLine = "\r\n";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    // 另一個程式短暫開著檔案（例如防毒軟體掃描）時稍候重試；總共等不到半秒就當成這次寫不進去。
    private static readonly int[] RetryDelaysMilliseconds = [20, 80, 240];

    internal static string FileName(string projectId, ProcessingLogTarget target) =>
        $"{ProjectFileNames.SafePrefix(projectId)}_{Dataset(target)}處理紀錄.txt";

    /// <summary>一筆紀錄的文字：空一行、分隔線、步驟名稱與時間和人員、分隔線，接著每一行內容。</summary>
    internal static string EntryText(ProcessingLogEntry entry, DateTimeOffset localTime, string operatorName)
    {
        var text = new StringBuilder();
        text.Append(NewLine);
        text.Append(Rule).Append(NewLine);
        text.Append(entry.Title).Append("    ").Append(Time(localTime)).Append("    ").Append(operatorName).Append(NewLine);
        text.Append(Rule).Append(NewLine);
        foreach (var line in entry.Lines)
        {
            text.Append(line.ReplaceLineEndings(NewLine)).Append(NewLine);
        }

        return text.ToString();
    }

    /// <summary>
    /// 把幾筆紀錄依序加在檔案最後；檔案還不存在時先寫開頭的說明。案件資料夾不存在或是連結時不寫，
    /// 不會在別處建立檔案，也不會把已刪除的案件資料夾建回來。寫不進去時丟出例外，由呼叫端保留這幾筆、下次再寫。
    /// </summary>
    internal static async Task AppendAsync(
        string caseDirectory,
        string projectId,
        ProcessingLogTarget target,
        IReadOnlyList<string> entries,
        DateTimeOffset localTime)
    {
        if (!Directory.Exists(caseDirectory) || IsLinked(caseDirectory))
        {
            throw new DirectoryNotFoundException("案件資料夾不存在或是連結。");
        }

        var path = Path.Combine(caseDirectory, FileName(projectId, target));
        if (File.Exists(path) && IsLinked(path))
        {
            throw new IOException("處理紀錄檔是連結。");
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                var content = new StringBuilder();
                if (stream.Length == 0)
                {
                    await stream.WriteAsync(Bom);
                    content.Append(Header(projectId, target, localTime));
                }

                stream.Seek(0, SeekOrigin.End);
                foreach (var entry in entries)
                {
                    content.Append(entry);
                }

                await stream.WriteAsync(Utf8WithoutBom.GetBytes(content.ToString()));
                await stream.FlushAsync();
                return;
            }
            catch (IOException) when (attempt < RetryDelaysMilliseconds.Length)
            {
                await Task.Delay(RetryDelaysMilliseconds[attempt]);
            }
        }
    }

    private static string Header(string projectId, ProcessingLogTarget target, DateTimeOffset localTime) =>
        HeaderRule + NewLine
        + $"JET {Dataset(target)} 處理紀錄" + NewLine
        + $"案件：{projectId}" + NewLine
        + HeaderRule + NewLine
        + $"JET 依時間順序記下這個案件 {Dataset(target)} 的每一次處理：做了什麼、誰做的、用了哪些條件，以及處理後的筆數。" + NewLine
        + $"紀錄從 {Time(localTime)} 開始，在這之前做過的處理沒有記在這裡。" + NewLine;

    private static string Dataset(ProcessingLogTarget target) => target == ProcessingLogTarget.Gl ? "GL" : "TB";

    private static string Time(DateTimeOffset localTime) => localTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static bool IsLinked(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
