using System.Data.Common;
using DuckDB.NET.Data;

namespace JET.Infrastructure;

/// <summary>
/// DuckDB 的 <see cref="IBulkRowWriter"/> 實作：走原生 Appender（繞過 SQL 解析／逐列計畫，快一數量級）。
/// DuckDB 1.5.3 本機實測確認：Appender 在顯式交易內可用、<c>Close()</c> 後同交易可見、
/// 與同連線開著的 staging reader 並存（Close 延至 <see cref="CompleteAsync"/>，reader 迴圈結束後才呼叫）。
/// </summary>
/// <remarks>
/// 欄位映射（建構時查一次 <c>pragma_table_info</c>，全列按資料表物理欄序 append）：
/// <list type="bullet">
/// <item>呼叫端提供的欄 → 依目標欄型別強制轉換值（BIGINT→long、INTEGER→int、VARCHAR→string、
///   BOOLEAN→bool；null／DBNull→AppendNullValue），落庫值與參數化路徑一致。</item>
/// <item>未提供且帶 <c>nextval</c> 預設的 auto-id 欄（entry_id／balance_id）→ 顯式供值
///   （<c>MAX(col)+1</c> 起編）：1.5.3 的 <c>AppendDefault</c> 對 nextval 預設會拋錯並使 appender
///   壞狀態（探針實證），故不用 AppendDefault。target 投影前必清表 → 起編恆為 1、PK 不撞。</item>
/// <item>未提供且可為 NULL 的欄 → AppendNullValue；未提供、必填、無 nextval 預設 → 建構時 fail loud
///   （欄位映射錯誤不靜默錯位）。</item>
/// </list>
/// </remarks>
internal sealed class DuckDbBulkRowWriter : IBulkRowWriter
{
    private enum StepKind { Provided, AutoId, Null }

    private enum ColType { BigInt, Integer, Text, Boolean }

    private readonly record struct Step(StepKind Kind, int ValueIndex, ColType Type);

    private readonly DuckDBAppender _appender;
    private readonly Step[] _plan;
    private readonly int _columnCount;
    private long _nextAutoId;
    private bool _completed;
    private bool _disposed;

    public DuckDbBulkRowWriter(
        DbConnection connection, DbTransaction transaction, string table, IReadOnlyList<string> columns)
    {
        if (connection is not DuckDbConnectionAdapter adapter || adapter.Inner is not DuckDBConnection inner)
        {
            throw new InvalidOperationException("DuckDB 批量寫入需要包裝的 DuckDBConnection。");
        }

        _columnCount = columns.Count;
        var provided = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            provided[columns[i]] = i;
        }

        var physical = ReadPhysicalColumns(inner, transaction, table);
        _plan = new Step[physical.Count];
        string? autoIdColumn = null;

        for (var p = 0; p < physical.Count; p++)
        {
            var (name, typeText, notNull, defaultText) = physical[p];
            if (provided.TryGetValue(name, out var valueIndex))
            {
                _plan[p] = new Step(StepKind.Provided, valueIndex, MapType(typeText, name, table));
            }
            else if (defaultText is not null && defaultText.Contains("nextval", StringComparison.OrdinalIgnoreCase))
            {
                if (autoIdColumn is not null)
                {
                    throw new InvalidOperationException($"資料表 {table} 有多個 auto-id 欄，批量寫入不支援。");
                }

                autoIdColumn = name;
                _plan[p] = new Step(StepKind.AutoId, -1, default);
            }
            else if (!notNull)
            {
                _plan[p] = new Step(StepKind.Null, -1, default);
            }
            else
            {
                throw new InvalidOperationException(
                    $"資料表 {table} 的必填欄 '{name}' 未提供值且無 nextval 預設——批量寫入欄位映射錯誤（fail loud）。");
            }
        }

        _nextAutoId = autoIdColumn is null ? 0 : ReadNextAutoId(inner, transaction, table, autoIdColumn);
        _appender = inner.CreateAppender(table);
    }

    public Task AppendAsync(object?[] values, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (values.Length != _columnCount)
        {
            throw new ArgumentException($"值個數 {values.Length} 與欄位數 {_columnCount} 不符。", nameof(values));
        }

        var row = _appender.CreateRow();
        foreach (var step in _plan)
        {
            switch (step.Kind)
            {
                case StepKind.AutoId:
                    row.AppendValue((long?)_nextAutoId++);
                    break;
                case StepKind.Null:
                    row.AppendNullValue();
                    break;
                default:
                    AppendTyped(row, step.Type, values[step.ValueIndex]);
                    break;
            }
        }

        row.EndRow();
        return Task.CompletedTask;
    }

    public Task CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_completed)
        {
            _appender.Close();
            _completed = true;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        try
        {
            // 已 Complete：Close 已 flush，Dispose 只釋放原生 handle（探針證 Close→Dispose 安全）。
            // 未 Complete（例外／取消路徑，交易將 rollback）：Dispose 會嘗試 flush 至即將回退的交易，
            // 吞掉可能的錯誤——列由 rollback 丟棄，不影響正確性。
            _appender.Dispose();
        }
        catch (DuckDBException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private static void AppendTyped(IDuckDBAppenderRow row, ColType type, object? value)
    {
        if (value is null or DBNull)
        {
            row.AppendNullValue();
            return;
        }

        switch (type)
        {
            case ColType.BigInt:
                row.AppendValue((long?)Convert.ToInt64(value));
                break;
            case ColType.Integer:
                row.AppendValue((int?)Convert.ToInt32(value));
                break;
            case ColType.Boolean:
                row.AppendValue((bool?)Convert.ToBoolean(value));
                break;
            default:
                row.AppendValue(Convert.ToString(value));
                break;
        }
    }

    private static ColType MapType(string typeText, string column, string table)
    {
        var t = typeText.ToUpperInvariant();
        if (t.StartsWith("BIGINT", StringComparison.Ordinal) || t.StartsWith("HUGEINT", StringComparison.Ordinal))
        {
            return ColType.BigInt;
        }

        if (t.StartsWith("INT", StringComparison.Ordinal) || t.StartsWith("SMALLINT", StringComparison.Ordinal)
            || t.StartsWith("TINYINT", StringComparison.Ordinal))
        {
            return ColType.Integer;
        }

        if (t.StartsWith("VARCHAR", StringComparison.Ordinal) || t == "TEXT")
        {
            return ColType.Text;
        }

        if (t.StartsWith("BOOL", StringComparison.Ordinal))
        {
            return ColType.Boolean;
        }

        throw new InvalidOperationException(
            $"資料表 {table} 欄 '{column}' 型別 '{typeText}' 未納入批量寫入型別映射（fail loud）。");
    }

    private static List<(string Name, string Type, bool NotNull, string? Default)> ReadPhysicalColumns(
        DuckDBConnection inner, DbTransaction transaction, string table)
    {
        using var command = inner.CreateCommand();
        command.Transaction = transaction;
        // table 為本地引擎家族的內部常數表名（非使用者輸入）：字面內插安全。
        command.CommandText =
            $"SELECT name, type, \"notnull\", dflt_value FROM pragma_table_info('{table}') ORDER BY cid;";

        var columns = new List<(string, string, bool, string?)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add((
                reader.GetString(0),
                reader.GetString(1),
                Convert.ToBoolean(reader.GetValue(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"資料表 {table} 無欄位資訊（pragma_table_info 空）。");
        }

        return columns;
    }

    private static long ReadNextAutoId(
        DuckDBConnection inner, DbTransaction transaction, string table, string column)
    {
        using var command = inner.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COALESCE(MAX({column}), 0) + 1 FROM {table};";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
