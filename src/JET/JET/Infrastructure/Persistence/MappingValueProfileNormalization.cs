using System.Data.Common;
using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

/// <summary>
/// 「讀取來源值」在 SQL 端共用的正規化：去空白的字元集合、往下填補的 CTE，以及產生代碼比對鍵的 SQLite 函式
/// （<see cref="FunctionName"/>，呼叫 <see cref="MappingCodeIdentity.Key"/>）。去空白的字元集合和 .NET
/// <see cref="string.Trim()"/> 相同，由目前 runtime 的 <see cref="char.IsWhiteSpace(char)"/> 建立，避免自行猜測空白字元。
/// </summary>
internal static class MappingValueProfileNormalization
{
    internal const string FunctionName = "jet_mapping_ordinal_key";
    internal static string DotNetTrimCharacters => JET.Domain.TextWhitespace.Characters;

    /// <summary>
    /// 「讀取來源值」的來源欄勾了往下填補時，接在去空白之後的三段 CTE，依勾選先填補再計數；最後一段 <c>filled</c>
    /// 是填補後的每個值與它的列數 value_count，空白是 NULL。<paramref name="source"/> 要有 source_no、row_number
    /// 與去空白後的 value，空白是 NULL。回傳的文字結尾沒有逗號。
    /// 每份來源內依列序數到目前為止有幾個非空白值，數字相同的一段只有第一列有值，整段填補後都是這個值；
    /// 每份來源開頭那一段沒有值，仍是空白。所以依來源與段號分組，取段內唯一的值與列數，再依值合計列數，
    /// 得到的次數和逐列填補後再計數相同。和 <see cref="GlStagingFillDown"/> 同一規則：確認配對依 row_number 讀列、
    /// 換來源就重來，追加的來源列序接在後面，所以依 source_no 分段、row_number 排序得到同一份結果。
    /// </summary>
    internal static string FillDownCtes(string source) => $"""
        fill_down_runs AS (
            SELECT source_no, value,
                   COUNT(value) OVER (PARTITION BY source_no ORDER BY row_number
                                      ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS run_no
            FROM {source}
        ),
        fill_down_groups AS MATERIALIZED (
            SELECT MAX(value) AS value, COUNT(*) AS row_count
            FROM fill_down_runs
            GROUP BY source_no, run_no
        ),
        filled AS (
            SELECT value, SUM(row_count) AS value_count
            FROM fill_down_groups
            GROUP BY value
        )
        """;

    internal static void RegisterLocalFunction(DbConnection connection)
    {
        if (connection is SqliteConnection sqlite)
        {
            sqlite.CreateFunction<string?, string?>(FunctionName, MappingCodeIdentity.Key, isDeterministic: true);
            return;
        }
        throw new InvalidOperationException("Mapping value profile requires a supported local database connection.");
    }
}
