using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>「配對檔未列的科目」與空白範本共用 <see cref="AccountMappingPopulationQuery"/> 的可配對科目母體；科目編輯清單另含配對檔多出的科目。</summary>
internal static class AccountMappingDifferenceQuery
{
    private static string Cte(string prefix, ISqlDialect dialect) => AccountMappingPopulationQuery.Cte(prefix, dialect) + $$"""
        , mapping_only AS (
            SELECT m.account_code, m.account_name FROM {{prefix}}target_account_mapping m
            WHERE NOT EXISTS (SELECT 1 FROM population p WHERE p.account_code = m.account_code)
        ), unmapped AS (
            SELECT p.account_code, p.account_name FROM population p
            WHERE NOT EXISTS (SELECT 1 FROM {{prefix}}target_account_mapping m WHERE m.account_code = p.account_code)
        )
        """;

    public static async Task<AccountMappingDifferenceCounts> CountAsync(
        DbConnection connection, ISqlDialect dialect, string prefix, CancellationToken ct)
    {
        var count = dialect is SqlServerDialect ? "COUNT_BIG(*)" : "COUNT(*)";
        await using var command = connection.CreateCommand();
        command.CommandText = Cte(prefix, dialect) + $" SELECT (SELECT {count} FROM mapping_only), (SELECT {count} FROM unmapped);";
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)));
    }

    public static async Task<AccountMappingDifferencePage> GetPageAsync(
        DbConnection connection, ISqlDialect dialect, string prefix, string kind, PageRequest request, CancellationToken ct)
    {
        var table = kind switch
        {
            AccountMappingDifferenceKinds.MappingOnly => "mapping_only",
            AccountMappingDifferenceKinds.Unmapped => "unmapped",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var hasCursor = !string.IsNullOrEmpty(request.Cursor);
        var cursorCode = "";
        if (hasCursor)
        {
            if (!PageCursor.TryDecode(request.Cursor, out var decoded) || !decoded.StartsWith(kind + "\n", StringComparison.Ordinal))
                throw new JetActionException(JetErrorCodes.InvalidPayload, "游標不屬於這份科目清單，請重新列出科目。");
            cursorCode = decoded[(kind.Length + 1)..];
        }
        await using var command = connection.CreateCommand();
        var count = dialect is SqlServerDialect ? "COUNT_BIG(*)" : "COUNT(*)";
        // 第一頁總數用同一語句的純量子查詢，與 CountAsync 同寫法，另計一次科目母體。
        // SQLite 與 DuckDB 在同一語句讀同一快照；SQL Server 預設隔離不保證此事，只經編譯。
        // 不用 COUNT(*) OVER ()：
        // DuckDB 1.5.3 在這種分組加 NOT EXISTS 的形狀上多執行緒時偶發內部錯誤，並讓同一資料庫的其他連線一起失效。
        command.CommandText = Cte(prefix, dialect) + $" SELECT account_code, account_name, "
            + (hasCursor ? "NULL" : $"(SELECT {count} FROM {table})") + $" AS total_count FROM {table} "
            + (hasCursor ? "WHERE account_code > @cursor " : "")
            + "ORDER BY account_code " + dialect.LimitClause("@pageSize") + ";";
        if (hasCursor) command.AddWithValue("@cursor", cursorCode);
        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);
        var rows = new List<AccountMappingBlankAccount>();
        long? totalCount = hasCursor ? null : 0;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            if (!hasCursor) totalCount = Convert.ToInt64(reader.GetValue(2));
        }
        var hasMore = rows.Count > request.ClampedPageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows, hasMore ? PageCursor.Encode(kind + "\n" + rows[^1].AccountCode) : null, totalCount);
    }
}
