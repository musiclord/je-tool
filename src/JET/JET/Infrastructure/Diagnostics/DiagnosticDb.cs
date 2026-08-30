using System.Data.Common;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace JET.Infrastructure;

/// <summary>
/// 診斷日誌的 DB helper（dev-only;logger 為 no-op〔Release〕時零行為差異,只多一次 Stopwatch）。
/// SQL:逐呼叫點以擴充方法記錄完整命令、參數 name=value、duration_ms、rows_affected、provider。
/// 透明 decorator 不可行（concrete 連線型別、SqlBulkCopy、provider 參數型別會被破壞）,故逐呼叫點接入。
/// </summary>
internal static class DiagnosticDb
{
    public static async Task<int> ExecuteNonQueryLoggedAsync(
        this DbCommand command, ILogger logger, string provider, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        DiagnosticDbLog.SqlExecuted(logger, command.CommandText, FormatParameters(command), stopwatch.ElapsedMilliseconds, rows, provider);
        return rows;
    }

    public static async Task<DbDataReader> ExecuteReaderLoggedAsync(
        this DbCommand command, ILogger logger, string provider, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        DiagnosticDbLog.SqlExecuted(logger, command.CommandText, FormatParameters(command), stopwatch.ElapsedMilliseconds, -1, provider);
        return reader;
    }

    public static async Task<object?> ExecuteScalarLoggedAsync(
        this DbCommand command, ILogger logger, string provider, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        DiagnosticDbLog.SqlExecuted(logger, command.CommandText, FormatParameters(command), stopwatch.ElapsedMilliseconds, -1, provider);
        return result;
    }

    /// <summary>開一個 transaction 記錄 scope;其間所有 SQL 日誌共享同一 transaction_id（BeginScope）。</summary>
    public static DiagnosticTransactionScope BeginTransaction(ILogger logger, string provider) => new(logger, provider);

    private static string FormatParameters(DbCommand command)
    {
        if (command.Parameters.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < command.Parameters.Count; i++)
        {
            var parameter = command.Parameters[i];
            if (i > 0)
            {
                builder.Append("; ");
            }

            builder.Append(parameter.ParameterName).Append('=')
                .Append(parameter.Value is null or DBNull ? "NULL" : parameter.Value.ToString());
        }

        return builder.ToString();
    }
}

/// <summary>
/// 包住一個 transaction 的診斷記錄:建構時 log tx.begin 並開 <c>BeginScope({transaction_id})</c>
/// （使其間 SQL 共享 id）;<see cref="Committed"/>/<see cref="RolledBack"/> log 結果 + duration;
/// 未結算即 Dispose（例外路徑）記為 rollback。
/// </summary>
internal sealed class DiagnosticTransactionScope : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _provider;
    private readonly IDisposable? _scope;
    private readonly Stopwatch _stopwatch;
    private bool _settled;

    public DiagnosticTransactionScope(ILogger logger, string provider)
    {
        _logger = logger;
        _provider = provider;
        _scope = logger.BeginScope(new Dictionary<string, object?> { ["transaction_id"] = Guid.NewGuid().ToString("N") });
        _stopwatch = Stopwatch.StartNew();
        DiagnosticDbLog.TransactionBegin(logger, provider);
    }

    public void Committed()
    {
        if (_settled)
        {
            return;
        }

        _settled = true;
        DiagnosticDbLog.TransactionCommit(_logger, _provider, _stopwatch.ElapsedMilliseconds);
    }

    public void RolledBack()
    {
        if (_settled)
        {
            return;
        }

        _settled = true;
        DiagnosticDbLog.TransactionRollback(_logger, _provider, _stopwatch.ElapsedMilliseconds);
    }

    public void Dispose()
    {
        if (!_settled)
        {
            // 既非 commit 亦非 rollback（例外傳出,await using transaction dispose 會 rollback）→ 記為 rollback。
            _settled = true;
            DiagnosticDbLog.TransactionRollback(_logger, _provider, _stopwatch.ElapsedMilliseconds);
        }

        _scope?.Dispose();
    }
}

/// <summary>SQL/transaction 診斷事件（LoggerMessage 來源產生器;結構化欄位即 NDJSON 欄位）。</summary>
internal static partial class DiagnosticDbLog
{
    [LoggerMessage(EventId = 2000, EventName = "sql.executed", Level = LogLevel.Debug,
        Message = "sql.executed provider={provider} rows={rows_affected} ms={duration_ms} sql={sql} params={parameters}")]
    public static partial void SqlExecuted(
        ILogger logger, string sql, string parameters, long duration_ms, int rows_affected, string provider);

    [LoggerMessage(EventId = 2001, EventName = "tx.begin", Level = LogLevel.Debug, Message = "tx.begin provider={provider}")]
    public static partial void TransactionBegin(ILogger logger, string provider);

    [LoggerMessage(EventId = 2002, EventName = "tx.commit", Level = LogLevel.Debug,
        Message = "tx.commit provider={provider} ms={duration_ms}")]
    public static partial void TransactionCommit(ILogger logger, string provider, long duration_ms);

    [LoggerMessage(EventId = 2003, EventName = "tx.rollback", Level = LogLevel.Debug,
        Message = "tx.rollback provider={provider} ms={duration_ms}")]
    public static partial void TransactionRollback(ILogger logger, string provider, long duration_ms);

    [LoggerMessage(EventId = 2100, EventName = "import.milestone", Level = LogLevel.Information,
        Message = "import.milestone phase={phase} rows={rows_processed} ms={elapsed_ms} throughput={throughput}")]
    public static partial void ImportMilestone(
        ILogger logger, string phase, int rows_processed, long elapsed_ms, double throughput);

    [LoggerMessage(EventId = 2101, EventName = "projection.milestone", Level = LogLevel.Information,
        Message = "projection.milestone phase={phase} rows={rows_processed} ms={elapsed_ms} throughput={throughput}")]
    public static partial void ProjectionMilestone(
        ILogger logger, string phase, int rows_processed, long elapsed_ms, double throughput);
}
