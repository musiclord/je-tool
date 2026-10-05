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
