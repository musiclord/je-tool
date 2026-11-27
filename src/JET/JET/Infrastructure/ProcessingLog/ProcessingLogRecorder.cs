using System.Reflection;
using System.Text.Json;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Infrastructure;

/// <summary>
/// 處理紀錄模組：操作成功完成後，把這次處理 GL 或 TB 的經過寫給使用者看，寫法參考舊版的歷程紀錄。
/// 它只接在兩個地方：dispatcher 在操作成功後交來的輸入與回應，以及畫面收到的匯出進度；審計邏輯不呼叫它。
/// <list type="bullet">
/// <item>哪些操作要寫、每一筆寫什麼，由 <see cref="ProcessingLogEntries"/> 決定；查詢、開案與失敗的操作不寫。</item>
/// <item>情境的條件文字、資料庫條件式與兩個筆數，讀的是畫面打開已存情境時用的同一個查詢，所以紀錄和畫面一字不差。</item>
/// <item>匯出時每寫完一張工作表，記下那張表的資料列數；條件篩選報告的明細表另記標了 Y 的列數。</item>
/// <item>寫不進檔案時操作照常完成：這幾筆先留在記憶體，下一次寫同一份紀錄時依原順序補上；失敗記一筆支援日誌。</item>
/// </list>
/// 執行人員和建案者用同一個方法取得：Windows 帳號的使用者全名，取不到時用帳號短名。
/// 自己出錯時吞掉，不往外丟。
/// </summary>
public sealed partial class ProcessingLogRecorder : IProcessingLog
{
    private const string FilterVoucherPageAction = "query.filterVoucherPage";

    // 寫不進去的紀錄最多留這麼多筆；再多就放棄最舊的，避免檔案一直被鎖住時佔用記憶體。
    private const int MaxPendingEntries = 200;

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private readonly JetProjectFolder _folder;
    private readonly Func<CancellationToken, Task<string>> _operatorName;
    private readonly Func<string, JsonElement, CancellationToken, Task<object?>> _readAction;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Lock _sheetsGate = new();
    private readonly Dictionary<string, List<string>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private List<ProcessingLogSheet>? _sheets;

    /// <param name="folder">案件資料夾的位置。</param>
    /// <param name="operatorName">取得執行人員的名稱。</param>
    /// <param name="readAction">執行一個只讀的 action，用來讀情境的條件文字與筆數；和畫面送出的是同一個 action。</param>
    /// <param name="logger">寫不進檔案時記一筆支援日誌。</param>
    /// <param name="clock">紀錄的時間；測試可以換掉。</param>
    public ProcessingLogRecorder(
        JetProjectFolder folder,
        Func<CancellationToken, Task<string>> operatorName,
        Func<string, JsonElement, CancellationToken, Task<object?>> readAction,
        ILogger logger,
        TimeProvider? clock = null)
    {
        _folder = folder;
        _operatorName = operatorName;
        _readAction = readAction;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public IDisposable BeginAction(string action)
    {
        if (!action.StartsWith("export.", StringComparison.Ordinal))
        {
            return NoScope.Instance;
        }

        // 匯出是會改資料的操作，同一時間只有一個在跑，所以只要記住目前這一個匯出寫完了哪些工作表。
        var sheets = new List<ProcessingLogSheet>();
        lock (_sheetsGate)
        {
            _sheets = sheets;
        }

        return new SheetScope(this, sheets);
    }

    public void ObservePublishedEvent(string eventName, object payload)
    {
        try
        {
            if (!string.Equals(eventName, SupportEventNames.ExportProgress, StringComparison.Ordinal)
                || Read(payload, "completedSheetRows") is not long rows)
            {
                return;
            }

            var sheet = new ProcessingLogSheet(
                Read(payload, "reportKind") as string ?? string.Empty,
                Read(payload, "sheetName") as string ?? string.Empty,
                rows,
                Read(payload, "completedSheetMarkedRows") as long?);
            lock (_sheetsGate)
            {
                _sheets?.Add(sheet);
            }
        }
        catch (Exception)
        {
            // 處理紀錄只做記錄，讀不到進度也不影響匯出。
        }
    }

    public async Task RecordAsync(string action, JsonElement payload, object? result, string? projectId)
    {
        if (projectId is null || !ProcessingLogEntries.Handles(action))
        {
            return;
        }

        try
        {
            var resultElement = JsonSerializer.SerializeToElement(result, WireOptions);
            var scenarios = await ReadScenariosAsync(action, resultElement);
            ProcessingLogSheet[] sheets;
            lock (_sheetsGate)
            {
                sheets = _sheets?.ToArray() ?? [];
            }

            var entries = ProcessingLogEntries.Describe(action, payload, resultElement, sheets, scenarios);
            if (entries.Count == 0)
            {
                return;
            }

            var operatorName = await OperatorNameAsync();
            var localTime = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), TimeZoneInfo.Local);
            foreach (var target in new[] { ProcessingLogTarget.Gl, ProcessingLogTarget.Tb })
            {
                var texts = entries
                    .Where(entry => entry.Target == target)
                    .Select(entry => ProcessingLogFile.EntryText(entry, localTime, operatorName))
                    .ToArray();
                if (texts.Length > 0)
                {
                    await WriteAsync(projectId, target, texts, localTime);
                }
            }
        }
        catch (Exception exception)
        {
            WriteFailed(_logger, action, "none", exception.GetType().Name, 0);
        }
    }

    /// <summary>
    /// 情境存好之後，照畫面打開已存情境的方式讀第一頁：條件文字、資料庫條件式、符合條件的傳票數量與分錄筆數。
    /// 讀不到的情境只記位置，紀錄裡寫明沒有讀到，請使用者在第五步確認。
    /// </summary>
    private async Task<IReadOnlyList<ProcessingLogScenarioDetail>> ReadScenariosAsync(string action, JsonElement result)
    {
        var positions = ProcessingLogEntries.ScenarioPositionsToRead(action, result);
        if (positions.Count == 0)
        {
            return [];
        }

        var resultRef = result.GetProperty("resultRef");
        var revision = resultRef.TryGetProperty("revision", out var revisionElement) ? revisionElement.GetString() : null;
        var populationScope = resultRef.TryGetProperty("populationScope", out var scopeElement) ? scopeElement.GetString() : null;
        var details = new List<ProcessingLogScenarioDetail>(positions.Count);
        foreach (var position in positions)
        {
            try
            {
                var request = JsonSerializer.SerializeToElement(new
                {
                    scenarioPosition = position,
                    scenarioRevision = revision,
                    populationScope,
                    pageSize = 1,
                });
                var page = JsonSerializer.SerializeToElement(
                    await _readAction(FilterVoucherPageAction, request, CancellationToken.None), WireOptions);
                details.Add(new ProcessingLogScenarioDetail(
                    position,
                    page.TryGetProperty("conditionText", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null,
                    page.TryGetProperty("conditionSql", out var sql) && sql.ValueKind == JsonValueKind.String ? sql.GetString() : null,
                    page.TryGetProperty("voucherCount", out var vouchers) && vouchers.TryGetInt64(out var voucherCount) ? voucherCount : null,
                    page.TryGetProperty("summaryRowCount", out var rows) && rows.TryGetInt64(out var rowCount) ? rowCount : null));
            }
            catch (Exception exception)
            {
                WriteFailed(_logger, action, "scenario", exception.GetType().Name, 0);
                details.Add(new ProcessingLogScenarioDetail(position, null, null, null, null));
            }
        }

        return details;
    }

    private async Task<string> OperatorNameAsync()
    {
        try
        {
            return await _operatorName(CancellationToken.None);
        }
        catch (Exception)
        {
            return "（不明）";
        }
    }

    /// <summary>先補上之前寫不進去的幾筆，再寫這次的；寫不進去時全部留著，下次依原順序再寫。</summary>
    private async Task WriteAsync(string projectId, ProcessingLogTarget target, IReadOnlyList<string> texts, DateTimeOffset localTime)
    {
        var key = projectId + "\n" + target;
        await _writeGate.WaitAsync();
        try
        {
            var batch = _pending.Remove(key, out var pending) ? pending : [];
            batch.AddRange(texts);
            try
            {
                await ProcessingLogFile.AppendAsync(_folder.GetProjectDirectory(projectId), projectId, target, batch, localTime);
            }
            catch (Exception exception)
            {
                if (batch.Count > MaxPendingEntries)
                {
                    batch.RemoveRange(0, batch.Count - MaxPendingEntries);
                }

                _pending[key] = batch;
                WriteFailed(_logger, null, target == ProcessingLogTarget.Gl ? "gl" : "tb", exception.GetType().Name, batch.Count);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static object? Read(object payload, string name) =>
        payload.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(payload);

    [LoggerMessage(EventId = 2700, EventName = SupportEventNames.ProcessingLogWriteFailed, Level = LogLevel.Warning,
        Message = "processing log entry not written action={action} target={log_target} error={error_type} pending={pending_entries}")]
    private static partial void WriteFailed(ILogger logger, string? action, string log_target, string error_type, int pending_entries);

    private sealed class SheetScope(ProcessingLogRecorder owner, List<ProcessingLogSheet> sheets) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._sheetsGate)
            {
                if (ReferenceEquals(owner._sheets, sheets))
                {
                    owner._sheets = null;
                }
            }
        }
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }
}
