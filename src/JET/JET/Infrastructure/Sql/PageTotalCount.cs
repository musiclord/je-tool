using System.Data.Common;
using System.Globalization;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>以同一份查詢母體計算總列數；保留搜尋條件，不包含游標及每頁上限。請求沒有要求總數（第一頁以外）時回 null。</summary>
internal static class PageTotalCount
{
    public static Task<long?> ReadKeysetAsync(DbCommand pageCommand, PageRequest request,
        KeysetPagePlan paging, ISqlDialect dialect, CancellationToken cancellationToken, string cte = "")
    {
        if (!request.IncludeTotalCount) return Task.FromResult<long?>(null);
        // 僅移除本程式產生的固定分頁尾段，不解析任意 SQL。
        var suffix = paging.Predicate + " " + paging.OrderBy + " " + dialect.LimitClause("@pageSize");
        var sql = pageCommand.CommandText.TrimEnd(';');
        if (!sql.StartsWith(cte, StringComparison.Ordinal) || !sql.EndsWith(suffix, StringComparison.Ordinal))
            throw new InvalidOperationException("分頁 SQL 與計數範圍不一致。");
        var population = sql[cte.Length..^suffix.Length] + paging.SearchPredicate;
        var count = dialect is SqlServerDialect ? "COUNT_BIG(*)" : "COUNT(*)";
        return ReadAsync(pageCommand, request, cte + $" SELECT {count} FROM ({population}) page_count;", cancellationToken);
    }

    public static async Task<long?> ReadAsync(DbCommand pageCommand, PageRequest request,
        string countSql, CancellationToken cancellationToken)
    {
        if (!request.IncludeTotalCount) return null;
        await using var command = pageCommand.Connection!.CreateCommand();
        command.Transaction = pageCommand.Transaction;
        command.CommandTimeout = pageCommand.CommandTimeout;
        command.CommandText = countSql;
        foreach (DbParameter source in pageCommand.Parameters)
        {
            if (source.ParameterName is "@pageSize" or "@limit" or "@cursor" or "@afterId"
                or KeysetPaging.KeyCursorParameter or KeysetPaging.SortCursorParameter) continue;
            var parameter = command.CreateParameter();
            parameter.ParameterName = source.ParameterName;
            parameter.DbType = source.DbType;
            parameter.Value = source.Value;
            command.Parameters.Add(parameter);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }
}
