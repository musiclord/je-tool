namespace JET.Domain;

/// <summary>
/// .NET <see cref="string.Trim()"/> 會去掉的空白字元，由目前 runtime 的 <see cref="char.IsWhiteSpace(char)"/> 列舉（BMP 範圍）。
/// 匯入時各格式的讀取器都用 .NET Trim 去頭尾空白；資料庫端判斷空白、比較文字與分組時也要去掉同一組字元，
/// SQLite、DuckDB 與 SQL Server 才會給出相同答案（2026-10-04 使用者裁定 C3）。各 SQL 方言用 <see cref="CodePoints"/>
/// 組出 TRIM 的字元清單，不自行猜測空白字元。
/// </summary>
public static class TextWhitespace
{
    /// <summary>所有空白字元的碼位，遞增排序。</summary>
    public static IReadOnlyList<int> CodePoints { get; } = BuildCodePoints();

    /// <summary>所有空白字元接成的字串，可直接當 TRIM 的第二個參數。</summary>
    public static string Characters { get; } = new(CodePoints.Select(codePoint => (char)codePoint).ToArray());

    private static int[] BuildCodePoints()
    {
        var codePoints = new List<int>();
        for (var codePoint = (int)char.MinValue; codePoint <= char.MaxValue; codePoint++)
        {
            if (char.IsWhiteSpace((char)codePoint))
            {
                codePoints.Add(codePoint);
            }
        }

        return codePoints.ToArray();
    }
}
