namespace JET.Domain;

/// <summary>
/// CSV 分隔符偵測。純函式：輸入檔案開頭取樣文字，輸出分隔符。
/// 規則：候選 [, \t ; |]，以引號感知方式統計每個邏輯列中引號外的出現次數，
/// 取「標頭列出現次數 > 0 且各列次數一致」者；並列依固定優先序 , &gt; \t &gt; ; &gt; |。
/// 全部不合格 → 取標頭列出現次數最高者；仍為 0 → null（單欄檔，合法）。
/// </summary>
public static class CsvDialectDetector
{
    public static readonly IReadOnlyList<char> CandidateDelimiters = [',', '\t', ';', '|'];

    private const int MaxSampledLines = 20;

    public static char? DetectDelimiter(string sampleText)
    {
        var counts = CountOutsideQuotesPerLine(sampleText);
        if (counts.Count == 0)
        {
            return null;
        }

        // 一致性優先：標頭列有出現，且取樣的每一列次數都相同（欄數固定的特徵）。
        foreach (var candidate in CandidateDelimiters)
        {
            var headerCount = counts[0][candidate];
            if (headerCount == 0)
            {
                continue;
            }

            var consistent = counts.All(line => line[candidate] == headerCount);
            if (consistent)
            {
                return candidate;
            }
        }

        // 髒資料 fallback：取標頭列出現次數最高者（並列依候選優先序）。
        char? best = null;
        var bestCount = 0;
        foreach (var candidate in CandidateDelimiters)
        {
            if (counts[0][candidate] > bestCount)
            {
                best = candidate;
                bestCount = counts[0][candidate];
            }
        }

        return best;
    }

    /// <summary>
    /// 取樣前 MaxSampledLines 個非空邏輯列，統計各候選在引號外的出現次數。
    /// 引號規則和文字檔讀取器相同：只有欄位開頭的引號會進入引號模式，欄位中間的引號（例如英吋符號）是一般字元；
    /// 引號模式裡連續兩個引號是跳脫。分隔符還沒決定，所以「前一個字元是任一候選分隔符或行首」都當成欄位開頭。
    /// </summary>
    private static List<Dictionary<char, int>> CountOutsideQuotesPerLine(string sampleText)
    {
        var lines = new List<Dictionary<char, int>>();
        var current = NewCounter();
        var lineHasContent = false;
        var inQuotes = false;
        var atFieldStart = true;

        for (var index = 0; index < sampleText.Length; index++)
        {
            var ch = sampleText[index];

            if (inQuotes)
            {
                // 引號內的分隔符與換行都屬於目前邏輯列的內容。
                lineHasContent = true;
                if (ch == '"')
                {
                    if (index + 1 < sampleText.Length && sampleText[index + 1] == '"')
                    {
                        index++;
                        continue;
                    }

                    inQuotes = false;
                }

                continue;
            }

            if (ch == '"' && atFieldStart)
            {
                inQuotes = true;
                lineHasContent = true;
                atFieldStart = false;
                continue;
            }

            if (ch == '\n' || ch == '\r')
            {
                if (lineHasContent)
                {
                    lines.Add(current);
                    if (lines.Count >= MaxSampledLines)
                    {
                        return lines;
                    }

                    current = NewCounter();
                    lineHasContent = false;
                }

                atFieldStart = true;
                continue;
            }

            lineHasContent = true;

            if (current.ContainsKey(ch))
            {
                current[ch]++;
                atFieldStart = true;
                continue;
            }

            atFieldStart = false;
        }

        if (lineHasContent)
        {
            lines.Add(current);
        }

        return lines;
    }

    private static Dictionary<char, int> NewCounter()
    {
        return CandidateDelimiters.ToDictionary(c => c, _ => 0);
    }
}
