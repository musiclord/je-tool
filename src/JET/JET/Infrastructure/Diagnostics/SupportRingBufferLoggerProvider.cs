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
        "import.milestone",
        "projection.milestone",
        "artifact.recovery.conflict",
        "artifact.journal.discarded",
        "artifact.manifest.reset",
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
        "old_expected_bytes",
        "journal_written_utc",
        "final_written_utc",
        "final_modified_after_journal",
        "final_in_use",
        "final_read_only",
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
            "import.milestone" => $"import milestone {fields.GetValueOrDefault("phase") ?? "unknown"}",
            "projection.milestone" => $"projection milestone {fields.GetValueOrDefault("phase") ?? "unknown"}",
            "artifact.recovery.conflict" => "report artifact recovery conflict",
            "artifact.journal.discarded" => "discarded a leftover report journal from the previous store design",
            "artifact.manifest.reset" => "report manifest was unreadable and has been set aside",
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
