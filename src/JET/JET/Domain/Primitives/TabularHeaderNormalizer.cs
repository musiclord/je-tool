namespace JET.Domain;

/// <summary>
/// 來源標頭正規化：trim、空白標頭 → COL_{columnNumber}、重複標頭加 _2/_3 字尾。
/// xlsx 與 csv reader 共用同一套規則（guide §3.1.1），確保 mapping 階段欄名一字不差；
/// staging row_json 的 key 也使用同一組名稱。
/// </summary>
public static class TabularHeaderNormalizer
{
    /// <summary>
    /// headers：來源欄位的 (欄位編號, 原始標頭)。xlsx 用實際 ColumnNumber、csv 用 1-based 序號。
    /// 回傳值與輸入順序一一對應。
    /// </summary>
    public static IReadOnlyList<string> Normalize(IReadOnlyList<(int ColumnNumber, string? RawName)> headers)
    {
        var allocatedNames = new HashSet<string>(StringComparer.Ordinal);
        var nextSuffixByBaseName = new Dictionary<string, int>(StringComparer.Ordinal);
        var normalized = new List<string>(headers.Count);

        foreach (var (columnNumber, rawName) in headers)
        {
            var name = (rawName ?? string.Empty).Trim();

            if (name.Length == 0)
            {
                name = $"COL_{columnNumber}";
            }

            if (!allocatedNames.Add(name))
            {
                var baseName = name;
                var suffix = nextSuffixByBaseName.GetValueOrDefault(baseName, 2);

                do
                {
                    name = $"{baseName}_{suffix}";
                    suffix++;
                }
                while (!allocatedNames.Add(name));

                nextSuffixByBaseName[baseName] = suffix;
            }

            normalized.Add(name);
        }

        return normalized;
    }

    /// <summary>
    /// 佔位欄名判定：恰為 Normalize 對空白標頭產生的正準形 COL_{數字}。
    /// 去重字尾產物（COL_3_2）與一般欄名都不是佔位欄。
    /// </summary>
    public static bool IsPlaceholder(string name)
    {
        const string prefix = "COL_";

        if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = prefix.Length; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 批次有效欄位收斂（guide §3.1.5）：具名標頭一律保留（具名空欄是 schema 聲明）；
    /// COL_{n} 佔位欄僅在串流中觀察到資料（observedKeys 含該名）才保留。
    /// 標頭之外的觀察 key（reader lazy 合成的範圍外佔位欄）附加在後：
    /// 佔位欄依欄號升冪，其餘依 ordinal 排序。provider 中立，各倉儲實作共用。
    /// </summary>
    public static IReadOnlyList<string> FinalizeBatchColumns(
        IReadOnlyList<string> headerColumns,
        IReadOnlyCollection<string> observedKeys)
    {
        var result = new List<string>(headerColumns.Count);

        foreach (var column in headerColumns)
        {
            if (!IsPlaceholder(column) || observedKeys.Contains(column))
            {
                result.Add(column);
            }
        }

        var headerSet = new HashSet<string>(headerColumns, StringComparer.Ordinal);
        var extras = observedKeys.Where(key => !headerSet.Contains(key)).ToList();

        result.AddRange(extras
            .Where(IsPlaceholder)
            .OrderBy(key => int.Parse(key["COL_".Length..], System.Globalization.CultureInfo.InvariantCulture)));
        result.AddRange(extras
            .Where(key => !IsPlaceholder(key))
            .Order(StringComparer.Ordinal));

        return result;
    }
}
