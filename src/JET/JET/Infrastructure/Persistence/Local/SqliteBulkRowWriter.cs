using System.Data.Common;

namespace JET.Infrastructure;

/// <summary>
/// SQLite 的 <see cref="IBulkRowWriter"/> 實作。建構時照欄位清單組出一句
/// <c>INSERT INTO t (c0,…) VALUES (@c0,…)</c>，之後每一列只換參數的值，
/// 在建構時傳入的交易裡用同一個 command 執行 <c>ExecuteNonQueryAsync</c>。
/// SQLite 的儲存型別跟著值走：int 與 long 存成 INTEGER，string 存成 TEXT，null 存成 NULL。
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

    /// <summary>每一列在 <see cref="AppendAsync"/> 裡就已經執行，沒有還沒送出的資料，所以這裡不做任何事。</summary>
    public Task CompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync() => await _command.DisposeAsync().ConfigureAwait(false);
}
