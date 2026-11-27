using System.Data.Common;
using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

/// <summary>
/// 首次發布之後才加進建表程式的表或欄位。較早建立的案件沒有它們，開啟時建立；欄位用 <see cref="AddColumnSql"/> 加上。
/// </summary>
internal sealed record ProjectSchemaAddition(string Table, string? Column, string? AddColumnSql)
{
    internal static ProjectSchemaAddition NewTable(string table) => new(table, null, null);

    internal static ProjectSchemaAddition NewColumn(string table, string column, string addColumnSql) =>
        new(table, column, addColumnSql);
}

/// <summary>
/// 案件資料庫的結構：一份建表程式，加上首次發布之後新增的表與欄位清單。JET 不讀資料庫裡的版本紀錄，只比對結構。
/// 已經建立的案件開啟時，和建表程式建出的表、欄位、索引與觸發條件比對：
/// 全部都在時不寫入任何東西；缺少索引、觸發條件或清單上的表與欄位時，在一個交易裡補上；
/// 缺少清單以外的表或欄位，表示是首次發布以前的 JET 建立的，不開啟，檔案保持原狀。
/// 案件裡多出建表程式沒有的表、欄位或索引時照常開啟，不讀也不改它們。
/// </summary>
internal sealed class ProjectSchema
{
    private readonly Lazy<Task<SchemaShape>> _reference;

    internal ProjectSchema(string createSql, IReadOnlyList<ProjectSchemaAddition> additions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(createSql);
        ArgumentNullException.ThrowIfNull(additions);
        CreateSql = createSql;
        Additions = additions;
        _reference = new Lazy<Task<SchemaShape>>(BuildReferenceAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>建表程式。每一句都要能在已有資料的案件上重複執行而不改變資料。</summary>
    internal string CreateSql { get; }

    /// <summary>首次發布之後新增的表與欄位，依加入的先後排列；補上時照這個順序執行。</summary>
    internal IReadOnlyList<ProjectSchemaAddition> Additions { get; }

    /// <summary>
    /// 只讀取、不寫入。結構齊全時回 false，需要補上索引、觸發條件或新增的表與欄位時回 true；
    /// 缺少不能補上的表或欄位時，用請審計員刪除後重建的訊息拒絕。
    /// </summary>
    internal async Task<bool> CheckAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var plan = await PlanAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
        return plan.RequiresCompletion;
    }

    /// <summary>
    /// 在一個交易裡補齊結構：先依清單加上缺少的欄位，再執行建表程式建立缺少的表、索引與觸發條件；
    /// 任何一句失敗，整個交易復原，資料庫保持原狀。取得交易後重新比對一次，另一條連線已經補好時不再執行。
    /// 磁碟已滿、檔案唯讀或損毀這類認得的資料庫錯誤，照一般的資料庫錯誤回報，使用者才知道要處理的是檔案本身；
    /// 另一個程式正在寫入而取不到交易，以及其他失敗，回報開啟失敗，請使用者關閉其他程式後再開啟。
    /// </summary>
    internal async Task CompleteAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var plan = await PlanAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (!plan.RequiresCompletion)
            {
                return;
            }

            foreach (var addition in plan.ColumnsToAdd)
            {
                await ExecuteAsync(connection, transaction, addition.AddColumnSql!, cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(connection, transaction, CreateSql, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not JetActionException)
        {
            var translated = LocalEngineErrors.TryTranslate(exception);
            if (translated is not null
                && translated.Code is not (JetErrorCodes.DatabaseBusy or JetErrorCodes.DatabaseLocked))
            {
                throw translated;
            }

            throw new JetActionException(
                JetErrorCodes.InvalidProjectSchema,
                ProjectFolderMessages.DatabaseUpgradeFailed,
                innerException: exception);
        }
    }

    private async Task<CompletionPlan> PlanAsync(
        DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        var expected = await _reference.Value.ConfigureAwait(false);
        var actual = await SchemaShape.ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var missingTable = false;
        var columnsToAdd = new HashSet<ProjectSchemaAddition>();
        foreach (var (table, columns) in expected.Tables)
        {
            if (!actual.Tables.TryGetValue(table, out var actualColumns))
            {
                if (!Additions.Any(addition => addition.Column is null && SchemaShape.SameName(addition.Table, table)))
                {
                    throw EarlierCase();
                }

                // 整張表由建表程式建立，表上的欄位不必另外加。
                missingTable = true;
                continue;
            }

            foreach (var column in columns.Where(column => !actualColumns.Contains(column)))
            {
                var addition = Additions.FirstOrDefault(addition =>
                    addition.Column is not null
                    && SchemaShape.SameName(addition.Table, table)
                    && SchemaShape.SameName(addition.Column, column));
                if (addition is null)
                {
                    throw EarlierCase();
                }

                columnsToAdd.Add(addition);
            }
        }

        var requiresCompletion = missingTable
            || columnsToAdd.Count > 0
            || !expected.Indexes.IsSubsetOf(actual.Indexes)
            || !expected.Triggers.IsSubsetOf(actual.Triggers);
        return new CompletionPlan(requiresCompletion, [.. Additions.Where(columnsToAdd.Contains)]);
    }

    private static JetActionException EarlierCase() =>
        new(JetErrorCodes.InvalidProjectSchema, ProjectFolderMessages.OldVersionProject());

    /// <summary>
    /// 在記憶體裡的空資料庫執行建表程式，取得現行結構；同一個程序只建一次。
    /// 清單上的每一項都要指向建表程式裡的表與欄位，欄位也要附上新增欄位的語句，寫錯時這裡就失敗。
    /// </summary>
    private async Task<SchemaShape> BuildReferenceAsync()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync().ConfigureAwait(false);
        await ExecuteAsync(connection, transaction: null, CreateSql, CancellationToken.None).ConfigureAwait(false);
        var shape = await SchemaShape.ReadAsync(connection, transaction: null, CancellationToken.None).ConfigureAwait(false);
        foreach (var addition in Additions)
        {
            if (!shape.Tables.TryGetValue(addition.Table, out var columns))
            {
                throw new InvalidOperationException($"Schema addition names table '{addition.Table}', which the create script does not create.");
            }

            if (addition.Column is not null
                && (!columns.Contains(addition.Column) || string.IsNullOrWhiteSpace(addition.AddColumnSql)))
            {
                throw new InvalidOperationException(
                    $"Schema addition '{addition.Table}.{addition.Column}' must name a column of the create script and give its ALTER TABLE statement.");
            }
        }

        return shape;
    }

    private static async Task ExecuteAsync(
        DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record CompletionPlan(bool RequiresCompletion, IReadOnlyList<ProjectSchemaAddition> ColumnsToAdd);

    /// <summary>一個資料庫有哪些表與欄位、索引與觸發條件。只看 main 裡的物件，SQLite 自己建的物件不算。名稱不分大小寫。</summary>
    private sealed record SchemaShape(
        IReadOnlyDictionary<string, HashSet<string>> Tables,
        HashSet<string> Indexes,
        HashSet<string> Triggers)
    {
        internal static bool SameName(string left, string right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        internal static async Task<SchemaShape> ReadAsync(
            DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
        {
            var tables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var triggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var objects = connection.CreateCommand())
            {
                objects.Transaction = transaction;
                objects.CommandText =
                    """
                    SELECT type, name FROM main.sqlite_master
                    WHERE type IN ('table', 'index', 'trigger') AND name NOT LIKE 'sqlite\_%' ESCAPE '\';
                    """;
                await using var reader = await objects.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var name = reader.GetString(1);
                    switch (reader.GetString(0))
                    {
                        case "table":
                            tables[name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            break;
                        case "index":
                            indexes.Add(name);
                            break;
                        default:
                            triggers.Add(name);
                            break;
                    }
                }
            }

            await using (var columns = connection.CreateCommand())
            {
                columns.Transaction = transaction;
                columns.CommandText =
                    """
                    SELECT m.name, c.name FROM main.sqlite_master AS m, pragma_table_info(m.name, 'main') AS c
                    WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite\_%' ESCAPE '\';
                    """;
                await using var reader = await columns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    tables[reader.GetString(0)].Add(reader.GetString(1));
                }
            }

            return new SchemaShape(tables, indexes, triggers);
        }
    }
}
