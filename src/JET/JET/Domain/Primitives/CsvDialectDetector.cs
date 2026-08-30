namespace JET.Domain;

/// <summary>
/// CSV 分隔符偵測（guide §3.1.1）。純函式：輸入檔案開頭取樣文字，輸出分隔符。
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
    /// 引號狀態與分隔符無關，可一次掃描供全部候選共用；"" 跳脫恰為兩次 toggle，不影響狀態。
    /// </summary>
    private static List<Dictionary<char, int>> CountOutsideQuotesPerLine(string sampleText)
    {
        var lines = new List<Dictionary<char, int>>();
        var current = NewCounter();
        var lineHasContent = false;
        var inQuotes = false;

        foreach (var ch in sampleText)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                lineHasContent = true;
                continue;
            }

            if (!inQuotes && (ch == '\n' || ch == '\r'))
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

                continue;
            }

            // 走到這裡的字元（含引號內換行）都屬於目前邏輯列的內容。
            lineHasContent = true;

            if (!inQuotes && current.ContainsKey(ch))
            {
                current[ch]++;
            }
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
