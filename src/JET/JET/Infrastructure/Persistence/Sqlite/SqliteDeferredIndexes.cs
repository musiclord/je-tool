using System.Data.Common;

namespace JET.Infrastructure;

/// <summary>
/// <see cref="IDeferredIndexes"/> 的 SQLite 實作。
/// 逐列寫入時每一列都要插進每個索引；資料多時，寫完以後一次排序建索引比較快。
/// 建回時照資料庫結構表 <c>sqlite_master</c> 記下的定義原文，依原本建立的先後逐一執行，
/// 所以索引的名稱、定義文字與彼此的先後都和刪除前相同。
/// 案件資料庫不收集統計資料，查詢的執行計畫只看資料表與索引的定義和先後，因此也和刪除前相同。
/// </summary>
internal sealed class SqliteDeferredIndexes : IDeferredIndexes
{
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    private readonly IReadOnlyList<string> _definitions;

    private SqliteDeferredIndexes(DbConnection connection, DbTransaction transaction, IReadOnlyList<string> definitions)
    {
        _connection = connection;
        _transaction = transaction;
        _definitions = definitions;
    }

    /// <summary>在 <paramref name="transaction"/> 裡刪掉 <paramref name="table"/> 的索引，回傳之後用來建回這些索引的物件。</summary>
    public static async Task<SqliteDeferredIndexes> DropAsync(
        DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken)
    {
        var indexes = new List<(string Name, string Definition)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            // 定義是 NULL 的是 SQLite 替主鍵與 UNIQUE 自動建的索引，跟著資料表走，不能單獨刪除，也不必建回。
            read.CommandText =
                """
                SELECT name, sql
                FROM sqlite_master
                WHERE type = 'index' AND tbl_name = @table AND sql IS NOT NULL
                ORDER BY rowid;
                """;
            read.AddWithValue("@table", table);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                indexes.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (name, _) in indexes)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"DROP INDEX \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\";",
                cancellationToken);
        }

        return new SqliteDeferredIndexes(connection, transaction, [.. indexes.Select(static index => index.Definition)]);
    }

    public async Task RebuildAsync(CancellationToken cancellationToken)
    {
        foreach (var definition in _definitions)
        {
            await ExecuteAsync(_connection, _transaction, definition, cancellationToken);
        }
    }

    private static async Task ExecuteAsync(
        DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
