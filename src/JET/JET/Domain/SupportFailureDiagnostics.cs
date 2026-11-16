namespace JET.Domain;

/// <summary>
/// 支援日誌的失敗階段附註。例外往外丟的途中，各層用 catch 的 when 條件記下當時做到哪一步，
/// 例外照原樣往外丟，堆疊不變。只放程式寫死的階段代號與去識別的序號、計數或代號，不放檔名、欄名或資料值；
/// 支援日誌寫入前另以白名單過濾欄位。
/// </summary>
public static class SupportFailureDiagnostics
{
    private static readonly object ContextKey = new();

    /// <summary>
    /// 記下這一層的階段後回傳 false，供 <c>catch (Exception error) when (Mark(error, ...)) { throw; }</c> 使用；
    /// when 條件在堆疊展開前執行，所以內層先記、外層後記，同一個欄位以內層為準。
    /// </summary>
    public static bool Mark(Exception error, string stage, params (string Key, object? Value)[] fields)
    {
        try
        {
            var existing = error.Data[ContextKey] as SupportFailureContext;
            var stages = existing?.Stages ?? [];
            var merged = new Dictionary<string, object?>(
                existing?.Fields ?? new Dictionary<string, object?>(),
                StringComparer.Ordinal);
            foreach (var (key, value) in fields)
            {
                merged.TryAdd(key, value);
            }

            error.Data[ContextKey] = new SupportFailureContext(
                stages.Contains(stage, StringComparer.Ordinal) ? stages : [.. stages, stage],
                merged);
        }
        catch (Exception markError) when (markError is NotSupportedException or ArgumentException)
        {
            // 少數例外的 Data 是唯讀；記不下階段不影響原本的例外。
        }

        return false;
    }

    /// <summary>
    /// 合併整條例外鏈的附註。Stages 由外到內排列；同一個欄位以最內層的值為準。
    /// </summary>
    public static SupportFailureContext? Find(Exception error)
    {
        var contexts = new List<SupportFailureContext>();
        for (var current = error; current is not null && contexts.Count < 16; current = current.InnerException)
        {
            if (current.Data[ContextKey] is SupportFailureContext context)
            {
                contexts.Add(context);
            }
        }

        if (contexts.Count == 0)
        {
            return null;
        }

        var stages = contexts.SelectMany(context => context.Stages.Reverse())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = contexts.Count - 1; index >= 0; index--)
        {
            foreach (var pair in contexts[index].Fields)
            {
                fields.TryAdd(pair.Key, pair.Value);
            }
        }

        return new SupportFailureContext(stages, fields);
    }
}

/// <summary>失敗時記下的階段與去識別欄位。單一例外上的 Stages 依記下先後排列，內層在前。</summary>
public sealed record SupportFailureContext(
    IReadOnlyList<string> Stages,
    IReadOnlyDictionary<string, object?> Fields);

/// <summary>
/// 一次操作目前做到哪一步。dispatcher 每次操作開始時建立一份紀錄；handler 與它呼叫的元件進入主要步驟時呼叫
/// <see cref="Enter"/>。操作失敗或取消時，dispatcher 把最後進入的步驟記到例外上，成為支援日誌的 failure_stage。
/// 紀錄跟著這次操作的非同步流程走，同時進行的其他操作看不到；不經 dispatcher 的呼叫沒有紀錄，Enter 不做事。
/// </summary>
public static class SupportOperationStage
{
    private static readonly AsyncLocal<Recorder?> CurrentRecorder = new();

    /// <summary>目前這次操作的紀錄；不經 dispatcher 的呼叫沒有紀錄，是 null。</summary>
    public static Recorder? Current => CurrentRecorder.Value;

    public static void Enter(string stage)
    {
        if (CurrentRecorder.Value is { } recorder)
        {
            recorder.Stage = stage;
        }
    }

    /// <summary>dispatcher 在一次操作開始時呼叫；回傳的紀錄在操作結束時讀出最後進入的步驟。</summary>
    public static Recorder Begin()
    {
        var recorder = new Recorder();
        CurrentRecorder.Value = recorder;
        return recorder;
    }

    public sealed class Recorder
    {
        private string? _stage;
        private Action<string>? _observer;

        public string? Stage
        {
            get => Volatile.Read(ref _stage);
            internal set
            {
                Volatile.Write(ref _stage, value);
                if (value is not null && Volatile.Read(ref _observer) is { } observer)
                {
                    try
                    {
                        observer(value);
                    }
                    catch (Exception)
                    {
                        // 觀察者只做記錄，它出錯不影響操作本身。
                    }
                }
            }
        }

        /// <summary>
        /// 讓監看模組在每次進入步驟時收到步驟代號。觀察者在呼叫 <see cref="Enter"/> 的執行緒上執行；
        /// 它丟出的例外在這裡吞掉，不影響操作本身。
        /// </summary>
        public void Observe(Action<string> observer) => Volatile.Write(ref _observer, observer);
    }
}
