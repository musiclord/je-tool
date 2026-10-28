using System.Data.Common;
using DuckDB.NET.Data;
using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

/// <summary>
/// SQL providers 共用的 .NET <see cref="string.Trim()"/> 字元集合。由目前 runtime 的
/// <see cref="char.IsWhiteSpace(char)"/> 建立，避免各 provider 自行猜測空白字元。
/// </summary>
internal static class MappingValueProfileNormalization
{
    internal const string FunctionName = "jet_mapping_ordinal_key";
    private static readonly Lock RegistrationLock = new();
    internal static string DotNetTrimCharacters => JET.Domain.TextWhitespace.Characters;

    /// <summary>
    /// 「讀取來源值」的來源欄勾了往下填補時，接在去空白之後的兩段 CTE（使用者 2026-10-28 裁定依勾選先填補再計數），
    /// 下一段改從 <c>filled</c> 讀值。<paramref name="source"/> 要有 source_no、row_number 與去空白後的 value，空白是 NULL。
    /// 每份來源內依列序數到目前為止有幾個非空白值，數字相同的一段只有第一列有值，取它就是往下填補的結果；
    /// 每份來源開頭那一段沒有值，仍是空白。和 <see cref="GlStagingFillDown"/> 同一規則：確認配對依 row_number 讀列、
    /// 換來源就重來，追加的來源列序接在後面，所以依 source_no 分段、row_number 排序得到同一份結果。
    /// </summary>
    internal static string FillDownCtes(string source) => $"""
        fill_down_runs AS (
            SELECT source_no, value,
                   COUNT(value) OVER (PARTITION BY source_no ORDER BY row_number
                                      ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS run_no
            FROM {source}
        ),
        filled AS (
            SELECT MAX(value) OVER (PARTITION BY source_no, run_no) AS value
            FROM fill_down_runs
        ),
        """;

    internal static void RegisterLocalFunction(DbConnection connection)
    {
        if (connection is SqliteConnection sqlite)
        {
            sqlite.CreateFunction<string?, string?>(FunctionName, MappingCodeIdentity.Key, isDeterministic: true);
            return;
        }
        if (connection is DuckDbConnectionAdapter { Inner: DuckDBConnection duck })
        {
            // DuckDB 的同一資料庫可能有並行的唯讀連線；每個資料庫實例只註冊一次純函式，不保存來源值。
            lock (RegistrationLock)
            {
                using var exists = duck.CreateCommand();
                exists.CommandText = $"SELECT COUNT(*) FROM duckdb_functions() WHERE function_name = '{FunctionName}';";
                if (Convert.ToInt64(exists.ExecuteScalar()) == 0)
                    duck.RegisterScalarFunction<string, string?>(FunctionName, MappingCodeIdentity.Key);
            }
            return;
        }
        throw new InvalidOperationException("Mapping value profile requires a supported local database connection.");
    }
}
