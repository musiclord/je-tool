using System.Data;
using System.Data.Common;

namespace JET.Infrastructure;

/// <summary>
/// 本地引擎家族（<c>Local*</c>）共用的參數樣板。<see cref="DbCommand"/>／
/// <see cref="DbParameterCollection"/> 沒有 SQLite 具體 client 專屬的 AddWithValue／
/// Add(name, SqliteType) 便利多載，此處以 provider 中立的 <see cref="DbCommand.CreateParameter"/>
/// 樣式提供等價版本（型別與值語意等同原 Sqlite 呼叫；DbType.String↔Text、DbType.Int64↔Integer）。
/// 與 <see cref="GlRulePredicates"/> 的 NextParam 為同一套 CreateParameter 慣用法。
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
