using System.Text;

namespace JET.Infrastructure;

/// <summary>
/// 文字檔（.csv、.txt）的記錄讀取器，採一般 CSV 讀法（和 Excel、Python csv 相同）：
/// 欄位以引號開頭才進入引號模式，引號內的分隔符與換行不切欄、連續兩個引號代表一個引號；
/// 欄位中間出現的引號（例如英吋符號 3/4"）是一般字元，不會把後面的列併進來。
/// 引號開了沒有關到檔尾，表示檔案結構讀不通，拋出 <see cref="CsvStructureException"/> 並帶上第幾列。
/// 記錄編號從 1 起算，空白列也算一列，所以和檔案裡的列號對得上；引號內含換行的列仍算一筆記錄。
/// 解碼失敗由底層 <see cref="TextReader"/> 以 <see cref="DecoderFallbackException"/> 回報，不在這裡吞掉。
/// </summary>
internal sealed class CsvRecordReader(TextReader reader, char delimiter)
{
    private readonly StringBuilder field = new();

    /// <summary>最近一次 <see cref="ReadRecord"/> 讀到的記錄編號（1 起算）。</summary>
    public int RecordNumber { get; private set; }

    /// <summary>最近一次讀到的記錄裡，有沒有用引號包住、內含換行的欄位（V11：匯入完成時提醒筆數與列號）。</summary>
    public bool RecordSpansLines { get; private set; }

    /// <summary>讀下一筆記錄；到檔尾回 null。回傳的欄位是原始文字，沒有去空白。</summary>
    public List<string>? ReadRecord()
    {
        var c = reader.Read();
        if (c == -1)
        {
            return null;
        }

        RecordNumber++;
        RecordSpansLines = false;
        var fields = new List<string>();
        field.Clear();
        var atFieldStart = true;

        while (true)
        {
            if (c == -1)
            {
                fields.Add(field.ToString());
                return fields;
            }

            if (atFieldStart && c == '"')
            {
                ReadQuotedPart();
                atFieldStart = false;
                c = reader.Read();
                continue;
            }

            atFieldStart = false;

            if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                atFieldStart = true;
                c = reader.Read();
                continue;
            }

            if (c == '\r')
            {
                if (reader.Peek() == '\n')
                {
                    reader.Read();
                }

                fields.Add(field.ToString());
                return fields;
            }

            if (c == '\n')
            {
                fields.Add(field.ToString());
                return fields;
            }

            field.Append((char)c);
            c = reader.Read();
        }
    }

    /// <summary>已讀掉開頭引號；讀到成對的結尾引號為止，結尾引號後面的字元交回呼叫端照一般字元處理（和 Excel 相同）。</summary>
    private void ReadQuotedPart()
    {
        var startedAt = RecordNumber;
        while (true)
        {
            var c = reader.Read();
            if (c == -1)
            {
                throw new CsvStructureException(startedAt);
            }

            if (c == '"')
            {
                if (reader.Peek() == '"')
                {
                    reader.Read();
                    field.Append('"');
                    continue;
                }

                return;
            }

            if (c is '\n' or '\r')
            {
                RecordSpansLines = true;
            }

            field.Append((char)c);
        }
    }

    /// <summary>整筆記錄只有空白字元時為真；這種列不算標頭也不算資料列。</summary>
    public static bool IsBlank(List<string> fields)
    {
        foreach (var value in fields)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>文字檔的引號沒有成對、讀不出欄位結構。<see cref="RecordNumber"/> 是開引號那筆記錄的編號（1 起算）。</summary>
internal sealed class CsvStructureException(int recordNumber)
    : FormatException($"文字檔第 {recordNumber} 列開始的引號沒有成對。")
{
    public int RecordNumber { get; } = recordNumber;
}
