using System.Data.Common;

namespace JET.Infrastructure;

/// <summary>
/// SQLite 的 <see cref="IBulkRowWriter"/> 實作：把現行「單一顯式交易內、單一 command 重用參數、逐列
/// <c>ExecuteNonQueryAsync</c>」的寫入原封不動包起來——這是效能修法的「行為凍結臂」，落庫結果與改動前
/// 逐字相同（SQLite 動態型別，int／long 皆存 INTEGER、string 存 TEXT、null 存 NULL，與原路徑一致）。
/// 建構時以給定欄位清單組出 <c>INSERT INTO t (c0,…) VALUES (@c0,…)</c>，逐列只換參數值再執行。
/// </summary>
internal sealed class SqliteBulkRowWriter : IBulkRowWriter
{
    private readonly DbCommand _command;
    private readonly DbParameter[] _parameters;

    public SqliteBulkRowWriter(
        DbConnection connection, DbTransaction transaction, string table, IReadOnlyList<string> columns)
    {
        if (columns.Count == 0)
        {
            throw new ArgumentException("批量寫入至少需一個欄位。", nameof(columns));
        }

        _command = connection.CreateCommand();
        _command.Transaction = transaction;

        var columnList = string.Join(", ", columns);
        var valueList = string.Join(", ", Enumerable.Range(0, columns.Count).Select(i => $"@c{i}"));
        _command.CommandText = $"INSERT INTO {table} ({columnList}) VALUES ({valueList});";

        _parameters = new DbParameter[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var parameter = _command.CreateParameter();
            parameter.ParameterName = $"@c{i}";
            _command.Parameters.Add(parameter);
            _parameters[i] = parameter;
        }
    }

    public async Task AppendAsync(object?[] values, CancellationToken cancellationToken)
    {
        if (values.Length != _parameters.Length)
        {
            throw new ArgumentException(
                $"值個數 {values.Length} 與欄位數 {_parameters.Length} 不符。", nameof(values));
        }

        for (var i = 0; i < _parameters.Length; i++)
        {
            _parameters[i].Value = values[i] ?? DBNull.Value;
        }

        await _command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>逐列已即時執行，無待 flush 的緩衝——no-op（介面對稱，供 DuckDB 臂延後 flush 用）。</summary>
    public Task CompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync() => await _command.DisposeAsync().ConfigureAwait(false);
}
