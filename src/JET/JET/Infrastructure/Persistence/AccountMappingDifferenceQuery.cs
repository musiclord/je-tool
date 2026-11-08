using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>「配對檔未列的科目」與空白範本共用 <see cref="AccountMappingPopulationQuery"/> 的可配對科目母體；科目編輯清單另含配對檔多出的科目。</summary>
internal static class AccountMappingDifferenceQuery
{
    private static string Cte(string prefix, ISqlDialect dialect) => AccountMappingPopulationQuery.Cte(prefix, dialect) + $$"""
        , mapping_only AS (
            SELECT m.account_number, m.account_name FROM {{prefix}}account_mapping m
            WHERE NOT EXISTS (SELECT 1 FROM population p WHERE p.account_number = m.account_number)
        ), unmapped AS (
            SELECT p.account_number, p.account_name FROM population p
            WHERE NOT EXISTS (SELECT 1 FROM {{prefix}}account_mapping m WHERE m.account_number = p.account_number)
        )
        """;

    public static async Task<AccountMappingDifferenceCounts> CountAsync(
        DbConnection connection, ISqlDialect dialect, string prefix, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Cte(prefix, dialect) + " SELECT (SELECT COUNT(*) FROM mapping_only), (SELECT COUNT(*) FROM unmapped);";
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
                throw new JetActionException(JetErrorCodes.InvalidPayload, "無法接續上一頁，請重新列出科目。");
            cursorCode = decoded[(kind.Length + 1)..];
        }
        await using var command = connection.CreateCommand();
        // 第一頁的總數用同一語句的純量子查詢，與 CountAsync 同寫法，另計一次科目母體；之後的頁不再計算。
        // SQLite 在同一語句讀同一快照。不用 COUNT(*) OVER ()：DuckDB 1.5.3 在這種分組加 NOT EXISTS 的形狀上
        // 多執行緒時偶發內部錯誤，並讓同一資料庫的其他連線一起失效；日後恢復 DuckDB 時仍要避開。
        command.CommandText = Cte(prefix, dialect) + $" SELECT account_number, account_name, "
            + (request.IncludeTotalCount ? $"(SELECT COUNT(*) FROM {table})" : "NULL") + $" AS total_count FROM {table} "
            + (hasCursor ? "WHERE account_number > @cursor " : "")
            + "ORDER BY account_number " + dialect.LimitClause("@pageSize") + ";";
        if (hasCursor) command.AddWithValue("@cursor", cursorCode);
        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);
        var rows = new List<AccountMappingBlankAccount>();
        long? totalCount = request.IncludeTotalCount ? 0 : null;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            if (request.IncludeTotalCount) totalCount = Convert.ToInt64(reader.GetValue(2));
        }
        var hasMore = rows.Count > request.ClampedPageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows, hasMore ? PageCursor.Encode(kind + "\n" + rows[^1].AccountNumber) : null, totalCount);
    }
}
