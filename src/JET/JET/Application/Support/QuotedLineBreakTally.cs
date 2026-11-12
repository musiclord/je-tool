using System.Runtime.CompilerServices;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// 一個來源裡用引號包住、內含換行的資料列筆數，以及前五個列號。文字檔裡這是合法寫法，所以不擋匯入；
/// 但引號放錯位置時，後面的列會被併進同一格。GL、TB 與科目配對檔的匯入共用這裡的統計與提醒句。
/// 列號是來源列號，也就是用 Excel 打開時的列號；
/// 支援日誌不寫檔名。
/// </summary>
internal sealed class QuotedLineBreakTally
{
    private const int ListedRowLimit = 5;
    private readonly List<int> firstRows = [];

    public int Count { get; private set; }

    public IReadOnlyList<int> FirstRows => firstRows;

    public void Observe(StagingRow row)
    {
        if (!row.HasQuotedLineBreak)
        {
            return;
        }

        Count++;
        if (firstRows.Count < ListedRowLimit)
        {
            firstRows.Add(row.SourceRowNumber);
        }
    }

    /// <summary>邊讀邊統計，資料列原樣往下傳。</summary>
    public async IAsyncEnumerable<StagingRow> Tally(
        IAsyncEnumerable<StagingRow> rows,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            Observe(row);
            yield return row;
        }
    }

    /// <summary>有這種資料時寫一筆支援日誌並回傳提醒句；沒有時回 null。</summary>
    public string? Warning(string fileName, int sourceNo, ILogger? logger)
    {
        if (Count == 0)
        {
            return null;
        }

        if (logger is not null)
        {
            ImportSourceDiagnostics.QuotedLineBreaks(logger, sourceNo, Count, string.Join(",", firstRows));
        }

        // 這句只有「，例如第…列」一種寫法，列號全部列出時也一樣。
        // 來源檔用「列」，所以寫「列數減少」。
        return $"「{fileName}」有 {CountText.Format(Count)} 列的欄位內含換行，例如第 {string.Join("、", firstRows)} 列。"
            + "引號位置錯誤時，後續資料會併入同一格而使列數減少，請以 Excel 開啟原檔核對。";
    }
}
