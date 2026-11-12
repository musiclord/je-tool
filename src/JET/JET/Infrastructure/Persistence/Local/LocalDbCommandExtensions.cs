using System.Data;
using System.Data.Common;

namespace JET.Infrastructure;

/// <summary>
/// 本機資料庫倉儲（<c>Local*</c>）共用的參數輔助方法。<see cref="DbCommand"/> 與
/// <see cref="DbParameterCollection"/> 沒有 SQLite client 專屬的 AddWithValue 與
/// Add(name, SqliteType) 便利多載，這裡用 <see cref="DbCommand.CreateParameter"/> 提供同樣的寫法
/// （DbType.String 對應 Text、DbType.Int64 對應 Integer）。
/// 和 <c>GlRulePredicates</c> 的 NextParam 用同一套 CreateParameter 寫法。
/// </summary>
internal static class LocalDbCommandExtensions
{
    /// <summary>加一個具名參數並設值（null → <see cref="DBNull"/>），回傳該參數。</summary>
    public static DbParameter AddWithValue(this DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }

    /// <summary>加一個指定 <see cref="DbType"/> 的具名參數（值稍後以 <c>.Value</c> 設定），回傳該參數。</summary>
    public static DbParameter AddParameter(this DbCommand command, string name, DbType type)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        command.Parameters.Add(parameter);
        return parameter;
    }
}
