using System.Runtime.CompilerServices;
using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 指定案件的支援日誌匯出：讀整份案件紀錄，再補上這個程序寫不進紀錄的事件（2026-10-21）。
/// 紀錄讀兩次，第一次算出匯出檔開頭的摘要，第二次逐行寫出；兩次都只讀開啟當下的內容。
/// 摘要寫出紀錄從哪裡開始、無法讀取的行、寫入時記下的缺口、沒有記到正常離開案件的程序，
/// 以及這個程序還沒寫進紀錄的事件數。
/// </summary>
internal sealed class SupportCaseHistoryExport
{
    private const int ListedUnreadableLines = 20;

    private readonly ISupportCaseHistoryReader? _reader;
    private readonly string? _correlationId;
    private readonly string? _current;
    private readonly SupportDiagnosticLogEntry[] _unsaved;

    private SupportCaseHistoryExport(
        ISupportCaseHistoryReader? reader,
        string? correlationId,
        string? current,
        SupportDiagnosticLogEntry[] unsaved,
        object summary)
    {
        _reader = reader;
        _correlationId = correlationId;
        _current = current;
        _unsaved = unsaved;
        Summary = summary;
    }

    /// <summary>寫進匯出檔開頭 <c>fields.history</c> 的摘要。</summary>
    public object Summary { get; }

    public static async Task<SupportCaseHistoryExport> ReadAsync(
        ISupportCaseHistory caseHistory,
        ISupportDiagnosticLogStore supportLog,
        string projectId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        // 先取寫不進去的事件再讀紀錄：兩者之間寫入器補寫成功時，事件會同時出現在兩邊，依序號去掉重複的。
        var writeState = caseHistory.WriteState(projectId);
        var current = supportLog.ProcessInstance;
        var status = "not_started";
        Dictionary<string, object?>? readFailure = null;
        var lines = 0;
        var unreadable = 0;
        var unreadableNumbers = new List<long>();
        string? start = null;
        DateTimeOffset? startUtc = null;
        var gapMarkers = 0;
        var markedMissing = 0L;
        var openings = 0;
        var processes = new HashSet<string>(StringComparer.Ordinal);
        var stillOpen = new Dictionary<string, bool>(StringComparer.Ordinal);
        var savedSequences = new HashSet<long>();

        ISupportCaseHistoryReader? reader = null;
        try
        {
            reader = caseHistory.OpenReader(projectId);
            if (reader is not null)
            {
                status = "saved";
                var number = 0L;
                await foreach (var line in reader.ReadLinesAsync(cancellationToken).ConfigureAwait(false))
                {
                    number++;
                    if (!HistoryLine.TryParse(line, out var parsed))
                    {
                        unreadable++;
                        if (unreadableNumbers.Count < ListedUnreadableLines)
                        {
                            unreadableNumbers.Add(number);
                        }

                        continue;
                    }

                    lines++;
                    if (parsed.ProcessInstance is { } process)
                    {
                        processes.Add(process);
                        if (process == current && parsed.Sequence > 0)
                        {
                            savedSequences.Add(parsed.Sequence);
                        }
                    }

                    switch (parsed.EventName)
                    {
                        case "support.history_started" when start is null:
                            start = parsed.HistoryStart;
                            startUtc = parsed.Timestamp;
                            break;
                        case "support.history_gap":
                            gapMarkers++;
                            markedMissing += parsed.MissingCount;
                            break;
                        case "support.case_opened" when parsed.ProcessInstance is not null:
                            openings++;
                            stillOpen[parsed.ProcessInstance] = true;
                            break;
                        case "action.end" when parsed.Action == "project.releaseLock" && parsed.ProcessInstance is not null:
                            stillOpen[parsed.ProcessInstance] = false;
                            break;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 讀不到紀錄時仍匯出這個程序寫不進紀錄的事件，並寫出讀取失敗的原因。
            reader = null;
            status = "read_failed";
            readFailure = Failure(exception);
            savedSequences.Clear();
        }

        var unsaved = writeState.Unsaved
            .Where(entry => entry.ProcessInstance != current || !savedSequences.Contains(entry.Sequence))
            .ToArray();
        var summary = new
        {
            status,
            start,
            startUtc,
            lines,
            unreadableLines = unreadable,
            unreadableLineNumbers = unreadableNumbers,
            incompleteTailBytes = reader?.IncompleteTailBytes ?? 0,
            gapMarkers,
            markedMissingEvents = markedMissing,
            openings,
            processes = processes.Count,
            // 開啟後沒有記到 project.releaseLock 成功的程序，不含這個程序；通常是程式沒有正常結束。
            processesNotClosed = stillOpen.Where(pair => pair.Value && pair.Key != current).Select(pair => pair.Key).ToArray(),
            unsavedEvents = unsaved.Length,
            // 寫不進去的事件超過保留上限而放棄的筆數，還沒在紀錄裡寫成缺口。
            droppedEvents = writeState.Dropped,
            lastWriteFailure = writeState.LastFailure,
            readFailure,
        };
        return new SupportCaseHistoryExport(reader, correlationId, current, unsaved, summary);
    }

    /// <summary>匯出檔的全部行：開頭、案件紀錄裡讀得到的行，以及這個程序還沒寫進紀錄的事件。</summary>
    public async IAsyncEnumerable<string> LinesAsync(
        string header,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return header;
        // 第一次讀取之後寫入器可能剛好把保留的事件補寫進去；這次讀到的序號再排除一次，同一筆不會出現兩次。
        var saved = new HashSet<long>();
        if (_reader is not null)
        {
            await using var lines = _reader.ReadLinesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                string line;
                try
                {
                    if (!await lines.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    line = lines.Current;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 第一次讀完之後紀錄被移走時，只輸出已經讀到的部分。
                    break;
                }

                if (!HistoryLine.TryParse(line, out var parsed))
                {
                    continue;
                }

                if (parsed.ProcessInstance == _current && parsed.Sequence > 0)
                {
                    saved.Add(parsed.Sequence);
                }

                if (_correlationId is null || parsed.CorrelationId == _correlationId)
                {
                    yield return line;
                }
            }
        }

        foreach (var entry in _unsaved)
        {
            if (entry.ProcessInstance == _current && saved.Contains(entry.Sequence))
            {
                continue;
            }

            if (_correlationId is null || entry.CorrelationId == _correlationId)
            {
                yield return SupportDiagnosticNdjson.SerializeLine(entry);
            }
        }
    }

    private static Dictionary<string, object?> Failure(Exception exception) => new(StringComparer.Ordinal)
    {
        ["error_type"] = exception.GetType().FullName,
        ["win32_error"] = (exception.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)
            ? exception.HResult & 0xFFFF
            : null,
    };

    private readonly record struct HistoryLine(
        string? EventName,
        string? CorrelationId,
        string? ProcessInstance,
        long Sequence,
        DateTimeOffset? Timestamp,
        string? Action,
        string? HistoryStart,
        long MissingCount)
    {
        public static bool TryParse(string line, out HistoryLine parsed)
        {
            parsed = default;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || Text(root, "eventName") is not { } eventName)
                {
                    return false;
                }

                var fields = root.TryGetProperty("fields", out var value) && value.ValueKind == JsonValueKind.Object
                    ? value
                    : default;
                parsed = new HistoryLine(
                    eventName,
                    Text(root, "correlationId"),
                    Text(root, "processInstance"),
                    root.TryGetProperty("sequence", out var sequence) && sequence.TryGetInt64(out var number) ? number : 0,
                    root.TryGetProperty("timestamp", out var timestamp) && timestamp.TryGetDateTimeOffset(out var time)
                        ? time
                        : null,
                    fields.ValueKind == JsonValueKind.Object ? Text(fields, "action") : null,
                    fields.ValueKind == JsonValueKind.Object ? Text(fields, "history_start") : null,
                    fields.ValueKind == JsonValueKind.Object
                        && fields.TryGetProperty("missing_count", out var missing) && missing.TryGetInt64(out var count)
                        ? count
                        : 0);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
