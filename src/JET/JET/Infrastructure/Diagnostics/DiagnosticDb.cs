using System.Data.Common;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace JET.Infrastructure;

/// <summary>
/// 把執行過的 SQL 記進診斷日誌的擴充方法。每筆紀錄的層級是 Debug，內容有完整的命令文字、
/// 參數的名稱與值、duration_ms、rows_affected，以及呼叫端傳入的資料庫種類 provider。
/// logger 沒有開 Debug 層級時不組參數字串，Release 組建就是這種情形。
/// 要記錄的地方逐一改呼叫這裡的方法。沒有改用一層包裝把連線整個包起來，
/// 因為那樣會換掉連線的具體型別，SqlBulkCopy 與各資料庫專用的參數型別就不能用了。
/// </summary>
internal static class DiagnosticDb
{
    public static async Task<int> ExecuteNonQueryLoggedAsync(
        this DbCommand command, ILogger logger, string provider, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (logger.IsEnabled(LogLevel.Debug))
        {
            DiagnosticDbLog.SqlExecuted(logger, command.CommandText, FormatParameters(command), stopwatch.ElapsedMilliseconds, rows, provider);
        }
        return rows;
    }

    public static async Task<DbDataReader> ExecuteReaderLoggedAsync(
        this DbCommand command, ILogger logger, string provider, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (logger.IsEnabled(LogLevel.Debug))
        {
            DiagnosticDbLog.SqlExecuted(logger, command.CommandText, FormatParameters(command), stopwatch.ElapsedMilliseconds, -1, provider);
        }
        return reader;
    }

    public static async Task<object?> ExecuteScalarLoggedAsync(
        this DbCommand command, ILogger logger, string provider, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (logger.IsEnabled(LogLevel.Debug))
        {
            DiagnosticDbLog.SqlExecuted(logger, command.CommandText, FormatParameters(command), stopwatch.ElapsedMilliseconds, -1, provider);
        }
        return result;
    }

    /// <summary>開始記錄一個 transaction。用 BeginScope 帶上新的 transaction_id，結束前記下的 SQL 日誌都有同一個編號。</summary>
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
/// 一個 transaction 的診斷紀錄。建構時記一筆 tx.begin，並用 <c>BeginScope({transaction_id})</c>
/// 讓這段期間的 SQL 日誌帶同一個編號。呼叫 <see cref="Committed"/> 或 <see cref="RolledBack"/>
/// 時記下結果與花費的時間。兩者都沒呼叫就 Dispose 時記成 rollback，丟出例外時就是這種情形。
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
            // 沒有 commit 也沒有 rollback 就走到這裡，代表有例外往外丟。
            // 資料庫的 transaction 在 await using 結束時會自己 rollback，所以這裡記成 rollback。
            _settled = true;
            DiagnosticDbLog.TransactionRollback(_logger, _provider, _stopwatch.ElapsedMilliseconds);
        }

        _scope?.Dispose();
    }
}

/// <summary>SQL 與 transaction 的診斷事件，由 LoggerMessage 來源產生器產生。方法的參數名稱就是寫進 NDJSON 的欄位名稱。</summary>
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
