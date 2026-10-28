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
    private readonly SupportCaseHistoryStore? _caseHistory;
    private readonly int _capacity;
    private readonly Lock _order = new();
    private long _received;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    /// <param name="caseHistory">
    /// 屬於案件的事件另外附加到該案件的紀錄檔，關閉案件或重新啟動後匯出仍讀得到（2026-10-21）；
    /// provider 結束時一起結束，先寫完排隊中的事件。
    /// </param>
    public SupportRingBufferLoggerProvider(int capacity, SupportCaseHistoryStore? caseHistory = null)
    {
        _capacity = Math.Max(1, capacity);
        _buffer = new BoundedRingBuffer<SupportDiagnosticLogEntry>(_capacity);
        _failures = new BoundedRingBuffer<SupportDiagnosticLogEntry>(Math.Clamp(_capacity / 4, 1, 32));
        _caseHistory = caseHistory;
    }

    internal IExternalScopeProvider ScopeProvider => _scopeProvider;

    public ILogger CreateLogger(string categoryName) => new SupportRingBufferLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public long EventsOmitted => Math.Max(0, Interlocked.Read(ref _received) - Snapshot().Count);

    public string ProcessInstance { get; } = Guid.NewGuid().ToString("N")[..12];

    public IReadOnlyList<SupportDiagnosticLogEntry> Snapshot()
    {
        var failures = _failures.Snapshot();
        var recent = _buffer.Snapshot().Where(entry => !failures.Contains(entry))
            .TakeLast(_capacity - failures.Count);
        return failures.Concat(recent).OrderBy(entry => entry.Sequence).ToArray();
    }

    internal void Add(SupportDiagnosticLogEntry entry)
    {
        // 時間、編號與排入案件紀錄在同一個鎖內，兩個操作同時記錄時，紀錄檔的順序與時間仍和事件序號一致。
        lock (_order)
        {
            var sequenced = entry with
            {
                Timestamp = DateTimeOffset.UtcNow,
                ProcessInstance = ProcessInstance,
                Sequence = Interlocked.Increment(ref _received),
            };
            _buffer.Add(sequenced);
            if (sequenced.EventName == SupportEventNames.ActionError) _failures.Add(sequenced);
            if (sequenced.InternalProjectId is not null) _caseHistory?.Enqueue(sequenced);
        }
    }

    public void Dispose()
    {
        _caseHistory?.Dispose();
    }
}

internal sealed class SupportRingBufferLogger(string category, SupportRingBufferLoggerProvider owner) : ILogger
{
    private static readonly HashSet<string> AllowedEvents = new(StringComparer.Ordinal)
    {
        SupportEventNames.ActionStart,
        SupportEventNames.ActionEnd,
        SupportEventNames.ActionError,
        // 取消不是失敗，另記一筆，不佔失敗摘要的保留名額（2026-10-21）。
        SupportEventNames.ActionCancelled,
        // 主要工作已完成或已失敗後，附帶步驟（更新案件步驟、刪資料夾、放工作鎖）也失敗。
        SupportEventNames.ActionStepFailed,
        SupportEventNames.ActionPayloadRejected,
        "import.milestone",
        "import.inspect",
        "import.account_mapping.summary_failed",
        "import.multiline_fields",
        "projection.milestone",
        "artifact.recovery.conflict",
        "artifact.journal.discarded",
        "artifact.manifest.reset",
        "project_audit.write_failed",
        // 資料已提交後寫不進操作紀錄，或讀不回結果狀態、授權清單比對：只記操作代號、讀取階段與錯誤類型（2026-10-19）。
        "audit_log.append_failed",
        "workflow_state.read_failed",
        "sql_server.unreachable",
        "filter.scenario_unreadable",
        "filter.compile_check_failed",
        // 每次建立或載入案件記一筆，帶 JET 版本與資料庫種類，不記電腦、帳號或路徑（使用者 2026-10-21 裁定）。
        SupportEventNames.CaseOpened,
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
        // action.start：輸入結構只有程式定義的欄位名稱、型別與筆數，程式代號類的值才照記；
        // retry_of 是同一案件同一動作上一次失敗或取消的操作識別碼（2026-10-21）。
        "payload_shape",
        "retry_of",
        // action.end：這次操作產生的批次、執行、報告等版本代號與筆數，不含檔名或路徑。
        "result_refs",
        // action.cancelled：true 表示使用者或關窗要求取消；false 表示程式內部丟出取消例外。
        "cancellation_requested",
        // action.step_failed：失敗的附帶步驟代號，程式寫死。
        "step",
        // support.case_opened：建立或載入、資料庫種類，以及 JET 版本、模組代號與組建設定。
        "opened_by",
        "database_provider",
        "app_version",
        "informational_version",
        "module_version_id",
        "build_configuration",
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
        SupportFailureFields.Add(fields, exception);
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
            SupportExceptionFormatter.Format(exception)));
    }

    private static void AddAllowedField(Dictionary<string, object?> fields, string key, object? value)
    {
        if (!AllowedFields.Contains(key))
        {
            return;
        }

        // 輸入結構與結果版本本身就是有界的摘要，允許比一般欄位長。
        var limit = key is "payload_shape" or "result_refs" ? 1024 : 256;
        fields[key] = value switch
        {
            null => null,
            string text => Limit(text, limit),
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
            _ => Limit(value.ToString(), limit),
        };
    }

    private static string SafeMessage(string eventName, IReadOnlyDictionary<string, object?> fields)
    {
        var action = fields.GetValueOrDefault("action")?.ToString() ?? "unknown";
        return eventName switch
        {
            SupportEventNames.ActionStart => $"action {action} start",
            SupportEventNames.ActionEnd => $"action {action} {fields.GetValueOrDefault("result_status") ?? "ok"} in {fields.GetValueOrDefault("duration_ms") ?? 0} ms",
            SupportEventNames.ActionError => $"action {action} failed in {fields.GetValueOrDefault("duration_ms") ?? 0} ms",
            SupportEventNames.ActionCancelled => $"action {action} cancelled in {fields.GetValueOrDefault("duration_ms") ?? 0} ms",
            SupportEventNames.ActionStepFailed => $"action {action} step {fields.GetValueOrDefault("step") ?? "unknown"} failed",
            SupportEventNames.ActionPayloadRejected => $"action {action} payload rejected by check {fields.GetValueOrDefault("payload_check") ?? "unknown"}",
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
            SupportEventNames.CaseOpened => $"case opened by {fields.GetValueOrDefault("opened_by") ?? "unknown"} "
                + $"provider={fields.GetValueOrDefault("database_provider") ?? "unknown"}",
            _ => eventName,
        };
    }

    private static string? Limit(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
