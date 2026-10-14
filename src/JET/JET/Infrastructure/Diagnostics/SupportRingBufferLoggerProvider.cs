using System.Diagnostics;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Infrastructure;

/// <summary>
/// Release/Debug 共用的去識別支援事件 provider。只接收 allowlist 事件，不保存 SQL、參數值、檔名、
/// 絕對路徑、原始 exception message 或案件名稱；buffer 滿時覆寫最舊事件。
/// </summary>
public sealed class SupportRingBufferLoggerProvider :
    ILoggerProvider,
    ISupportExternalScope,
    ISupportDiagnosticLogStore
{
    private readonly BoundedRingBuffer<SupportDiagnosticLogEntry> _buffer;
    private readonly BoundedRingBuffer<SupportDiagnosticLogEntry> _failures;
    private readonly int _capacity;
    private long _received;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public SupportRingBufferLoggerProvider(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _buffer = new BoundedRingBuffer<SupportDiagnosticLogEntry>(_capacity);
        _failures = new BoundedRingBuffer<SupportDiagnosticLogEntry>(Math.Clamp(_capacity / 4, 1, 32));
    }

    internal IExternalScopeProvider ScopeProvider => _scopeProvider;

    public ILogger CreateLogger(string categoryName) => new SupportRingBufferLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public long EventsOmitted => Math.Max(0, Interlocked.Read(ref _received) - Snapshot().Count);

    public IReadOnlyList<SupportDiagnosticLogEntry> Snapshot()
    {
        var failures = _failures.Snapshot();
        var recent = _buffer.Snapshot().Where(entry => !failures.Contains(entry))
            .TakeLast(_capacity - failures.Count);
        return failures.Concat(recent).OrderBy(entry => entry.Timestamp).ToArray();
    }

    internal void Add(SupportDiagnosticLogEntry entry)
    {
        Interlocked.Increment(ref _received);
        _buffer.Add(entry);
        if (entry.EventName == "action.error") _failures.Add(entry);
    }

    public void Dispose()
    {
    }
}

internal sealed class SupportRingBufferLogger(string category, SupportRingBufferLoggerProvider owner) : ILogger
{
    private static readonly HashSet<string> AllowedEvents = new(StringComparer.Ordinal)
    {
        "action.start",
        "action.end",
        "action.error",
        "action.payload_rejected",
        "import.milestone",
        "import.inspect",
        "import.account_mapping.summary_failed",
        "import.multiline_fields",
        "projection.milestone",
        "artifact.recovery.conflict",
        "artifact.journal.discarded",
        "artifact.manifest.reset",
        "project_audit.write_failed",
        "sql_server.unreachable",
        "filter.scenario_unreadable",
        "filter.compile_check_failed",
    };

    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "action",
        "result_status",
        "duration_ms",
        "phase",
        "rows_processed",
        "elapsed_ms",
        "throughput",
        "operation",
        "manifest_state",
        "transition_role",
        "artifact_kind",
        "stage_state",
        "final_state",
        "quarantine_state",
        "expected_bytes",
        "actual_bytes",
        "new_content_matches",
        "old_fallback_matches",
        "error_code",
        "error_type",
        "old_expected_bytes",
        "journal_written_utc",
        "final_written_utc",
        "final_modified_after_journal",
        "final_in_use",
        "final_read_only",
        // import.inspect：選檔階段成功時記下的格式、欄數、工作表數、編碼與分隔符；沒有檔名、欄名或資料。
        "file_format",
        "column_count",
        "worksheet_count",
        "encoding",
        "delimiter_codepoint",
        // import.multiline_fields：用引號包住、內含換行的資料筆數與前幾個列號（V11）；只有位置，沒有內容。
        "source_no",
        "multiline_record_count",
        "multiline_record_rows",
        // action.payload_rejected：沒過的格式檢查代號，程式寫死，不含資料內容。
        "payload_check",
        // sql_server.unreachable：連不上 SQL Server 時的錯誤分類代號，不含伺服器、帳號或原始訊息。
        "failure_category",
        // filter.scenario_unreadable：第五步損毀條件的位置與程式寫死的原因代號，不含情境名稱或條件內容。
        "scenario_position",
        "group_no",
        "rule_no",
        "reason_code",
        // filter.compile_check_failed：篩選條件編譯時沒過的檢查代號，程式寫死，不含欄位名稱或輸入值。
        "compile_check",
    };

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        owner.ScopeProvider.Push(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || eventId.Name is null || !AllowedEvents.Contains(eventId.Name))
        {
            return;
        }

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        string? correlationId = null;
        string? currentProjectId = null;
        string? targetProjectId = null;
        owner.ScopeProvider.ForEachScope(
            (scope, _) =>
            {
                if (scope is not IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    return;
                }

                foreach (var pair in pairs)
                {
                    switch (pair.Key)
                    {
                        case "correlation_id":
                            correlationId = Limit(pair.Value?.ToString(), 64);
                            break;
                        case "project_id":
                            currentProjectId = pair.Value?.ToString();
                            break;
                        case "support_project_id":
                            targetProjectId = pair.Value?.ToString();
                            break;
                        default:
                            AddAllowedField(fields, pair.Key, pair.Value);
                            break;
                    }
                }
            },
            (object?)null);

        if (state is IEnumerable<KeyValuePair<string, object?>> statePairs)
        {
            foreach (var pair in statePairs)
            {
                AddAllowedField(fields, pair.Key, pair.Value);
            }
        }

        if (string.Equals(fields.GetValueOrDefault("action")?.ToString(), "support.log.export", StringComparison.Ordinal))
        {
            return;
        }

        ImportSupportDiagnostics.AddFields(fields, exception);
        var message = SafeMessage(eventId.Name, fields);
        owner.Add(new SupportDiagnosticLogEntry(
            DateTimeOffset.UtcNow,
            logLevel.ToString(),
            Limit(category, 160)!,
            eventId.Name,
            message,
            correlationId,
            targetProjectId ?? currentProjectId,
            fields,
            SafeException(exception)));
    }

    private static void AddAllowedField(Dictionary<string, object?> fields, string key, object? value)
    {
        if (!AllowedFields.Contains(key))
        {
            return;
        }

        fields[key] = value switch
        {
            null => null,
            string text => Limit(text, 256),
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
            _ => Limit(value.ToString(), 256),
        };
    }

    private static string SafeMessage(string eventName, IReadOnlyDictionary<string, object?> fields)
    {
        var action = fields.GetValueOrDefault("action")?.ToString() ?? "unknown";
        return eventName switch
        {
            "action.start" => $"action {action} start",
            "action.end" => $"action {action} {fields.GetValueOrDefault("result_status") ?? "ok"} in {fields.GetValueOrDefault("duration_ms") ?? 0} ms",
            "action.error" => $"action {action} failed in {fields.GetValueOrDefault("duration_ms") ?? 0} ms",
            "action.payload_rejected" => $"action {action} payload rejected by check {fields.GetValueOrDefault("payload_check") ?? "unknown"}",
            "import.milestone" => $"import milestone {fields.GetValueOrDefault("phase") ?? "unknown"}",
            "import.inspect" => $"import inspect {fields.GetValueOrDefault("file_format") ?? "unknown"} columns={fields.GetValueOrDefault("column_count") ?? "n/a"}",
            "import.account_mapping.summary_failed" => "account mapping saved; difference summary unavailable",
            "import.multiline_fields" => $"import source {fields.GetValueOrDefault("source_no") ?? "n/a"} has {fields.GetValueOrDefault("multiline_record_count") ?? 0} records with quoted line breaks",
            "projection.milestone" => $"projection milestone {fields.GetValueOrDefault("phase") ?? "unknown"}",
            "artifact.recovery.conflict" => "report artifact recovery conflict",
            "artifact.journal.discarded" => "discarded a leftover report journal from the previous store design",
            "artifact.manifest.reset" => "report manifest was unreadable and has been set aside",
            "project_audit.write_failed" => "project operation record not written",
            "sql_server.unreachable" => $"sql server unreachable: {fields.GetValueOrDefault("failure_category") ?? "unknown"}",
            "filter.scenario_unreadable" => $"action {action} filter scenario {fields.GetValueOrDefault("scenario_position") ?? "n/a"} "
                + $"group {fields.GetValueOrDefault("group_no") ?? "n/a"} rule {fields.GetValueOrDefault("rule_no") ?? "n/a"} "
                + $"unreadable: {fields.GetValueOrDefault("reason_code") ?? "unknown"}",
            "filter.compile_check_failed" => $"action {action} filter group {fields.GetValueOrDefault("group_no") ?? "n/a"} "
                + $"rule {fields.GetValueOrDefault("rule_no") ?? "n/a"} compile check failed: {fields.GetValueOrDefault("compile_check") ?? "unknown"}",
            _ => eventName,
        };
    }

    private static string? SafeException(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        var parts = new List<string>();
        var chain = ImportSupportDiagnostics.Chain(exception).Take(9).ToArray();
        foreach (var current in chain.Take(8))
        {
            parts.Add($"{current.GetType().FullName} HResult={current.HResult}");
            var frames = new StackTrace(current, fNeedFileInfo: false).GetFrames() ?? [];
            foreach (var frame in frames.Take(5))
            {
                var method = frame.GetMethod();
                if (method is not null) parts.Add($"at {method.DeclaringType?.FullName ?? "unknown"}.{method.Name}");
            }
            if (frames.Length > 5) parts.Add("[stack truncated]");
        }
        if (chain.Length > 8) parts.Add("[exception chain truncated]");
        var result = string.Join('\n', parts);
        return result.Length <= 4096 ? result : result[..4075] + "\n[output truncated]";
    }

    private static string? Limit(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
