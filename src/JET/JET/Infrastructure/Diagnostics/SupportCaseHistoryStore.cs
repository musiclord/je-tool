using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 把屬於案件的支援事件持續附加到案件資料夾的 <c>support-log/events.ndjson</c>。使用者 2026-10-21 裁定
/// 存在案件資料夾內、整個案件一個檔、只記啟動代號與版本、保留到刪除案件。
/// <list type="bullet">
/// <item>事件由背景寫入器依序附加，審計操作不等磁碟。每次附加都以只允許別人讀取的方式開檔，
/// 兩個 JET 程式碰到同一案件時輪流寫入；檔案被占用時稍候重試。</item>
/// <item>紀錄資料夾只在持有案件工作鎖時由 <see cref="Prepare"/> 建立，寫入器本身不建立任何資料夾，
/// 別的程式刪除案件時不會把案件資料夾建回來。還沒在這個程序開啟的案件只附加到已經存在的紀錄檔；
/// 沒有紀錄檔時事件先保留，例如舊案件第一次載入的開始事件，開啟案件之後依原順序補寫。</item>
/// <item>寫不進去時不影響審計操作。事件先保留，下次寫入、匯出前或程式結束時依原順序補寫；
/// 超過保留上限才放棄最舊的，補寫時先寫一筆缺口紀錄寫明放棄幾筆與原因。刪除案件時保留的事件一起放棄。</item>
/// <item>檔尾若有沒寫完的行，下一次附加先補換行，那一行在匯出時算成無法讀取。</item>
/// </list>
/// </summary>
public sealed class SupportCaseHistoryStore : ISupportCaseHistory, IDisposable
{
    public const string DirectoryName = "support-log";
    public const string FileName = "events.ndjson";

    private const int MaxBatchItems = 512;

    // 每個案件最多保留的未寫入事件數，與記憶體裡支援日誌的容量相同。
    private const int DefaultMaxUnsaved = 2_000;

    // 另一個 JET 程式附加一次只要幾毫秒；防毒或備份軟體短暫鎖住時，總共等一秒多再算成寫不進去。
    private static readonly TimeSpan[] SharingRetryDelays =
        [.. new[] { 10, 20, 40, 80, 160, 320, 640 }.Select(milliseconds => TimeSpan.FromMilliseconds(milliseconds))];

    // 刪除案件、匯出與程式結束等寫入器時的上限；寫入器卡在重試時也不讓它們無限期等待。
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private readonly JetProjectFolder _folder;
    private readonly int _maxUnsaved;
    private readonly Channel<WorkItem> _queue =
        Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;

    // 只有寫入器讀寫。
    private readonly Dictionary<string, CaseState> _cases = new(StringComparer.OrdinalIgnoreCase);

    // 寫入器更新，匯出讀取。
    private readonly ConcurrentDictionary<string, SupportCaseHistoryWriteState> _writeStates =
        new(StringComparer.OrdinalIgnoreCase);

    private int _disposed;

    public SupportCaseHistoryStore(JetProjectFolder folder)
        : this(folder, DefaultMaxUnsaved)
    {
    }

    internal SupportCaseHistoryStore(JetProjectFolder folder, int maxUnsaved)
    {
        _folder = folder;
        _maxUnsaved = Math.Max(1, maxUnsaved);
        _writer = Task.Run(RunAsync);
    }

    /// <summary>provider 已去識別、已編號而且屬於某個案件的事件。</summary>
    internal void Enqueue(SupportDiagnosticLogEntry entry) => _queue.Writer.TryWrite(new EntryItem(entry));

    public void Prepare(string projectId, bool createdNow)
    {
        Exception? failure = null;
        try
        {
            // 呼叫端持有工作鎖，別的程式這時不能刪除案件，建立子資料夾不會把已刪除的案件資料夾建回來。
            var caseDirectory = _folder.GetProjectDirectory(projectId);
            if (Directory.Exists(caseDirectory) && !IsLinked(caseDirectory))
            {
                Directory.CreateDirectory(Path.Combine(caseDirectory, DirectoryName));
            }
        }
        catch (Exception exception)
        {
            // 建不起來時之後的事件算成寫不進去，匯出時標出原因；不擋開案。
            failure = exception;
        }

        _queue.Writer.TryWrite(new PrepareItem(projectId, createdNow, failure));
    }

    public Task ForgetAsync(string projectId, CancellationToken cancellationToken) =>
        WaitForWriterAsync(done => new ForgetItem(projectId, done), cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken) =>
        WaitForWriterAsync(done => new FlushItem(done), cancellationToken);

    public ISupportCaseHistoryReader? OpenReader(string projectId)
    {
        var caseDirectory = _folder.GetProjectDirectory(projectId);
        var directory = Path.Combine(caseDirectory, DirectoryName);
        var path = Path.Combine(directory, FileName);
        if (!Directory.Exists(caseDirectory) || IsLinked(caseDirectory)
            || !Directory.Exists(directory) || IsLinked(directory) || !File.Exists(path))
        {
            return null;
        }

        return HistoryReader.Open(path);
    }

    public SupportCaseHistoryWriteState WriteState(string projectId) =>
        _writeStates.TryGetValue(projectId, out var state) ? state : SupportCaseHistoryWriteState.Empty;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 程式正常結束時把排隊中與先前寫不進去的事件寫完，例如離開案件那一筆；寫入器卡住時最多等上限時間。
        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(WaitLimit);
        }
        catch (Exception)
        {
            // 寫入器不會丟出例外；這裡只防止程式結束時被紀錄卡住。
        }
    }

    private async Task WaitForWriterAsync(Func<TaskCompletionSource, WorkItem> create, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(create(done)))
        {
            return;
        }

        try
        {
            await done.Task.WaitAsync(WaitLimit, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 寫入器還在重試時不再等；刪除或匯出照常進行，沒寫進去的事件仍保留著。
        }
    }

    private async Task RunAsync()
    {
        var items = new List<WorkItem>(MaxBatchItems);
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (items.Count < MaxBatchItems && _queue.Reader.TryRead(out var item))
            {
                items.Add(item);
            }

            try
            {
                await ProcessAsync(items).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // 寫入器不能停：非預期錯誤只放棄這一批，算進各案件的放棄筆數並記下原因；等候的呼叫端照常放行。
                Abandon(items, exception);
            }

            items.Clear();
        }

        // 程式結束前再補寫一次先前寫不進去的事件。
        try
        {
            await RetryUnsavedAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 程式正在結束，沒有地方再記這次失敗。
        }
    }

    private void Abandon(List<WorkItem> items, Exception exception)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case EntryItem { Entry.InternalProjectId: { } projectId }:
                    var state = State(projectId);
                    state.Dropped++;
                    state.LastFailure = exception;
                    break;
                case ForgetItem forget:
                    forget.Done.TrySetResult();
                    break;
                case FlushItem flush:
                    flush.Done.TrySetResult();
                    break;
            }
        }

        foreach (var projectId in items.OfType<EntryItem>().Select(item => item.Entry.InternalProjectId).OfType<string>().Distinct())
        {
            Publish(projectId, State(projectId));
        }
    }

    private async Task RetryUnsavedAsync()
    {
        foreach (var (projectId, state) in _cases)
        {
            if (state.Unsaved.Count > 0 && !state.Forgotten)
            {
                await WriteCaseSafelyAsync(projectId, []).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessAsync(List<WorkItem> items)
    {
        // 同一批裡依案件分組附加，案件內維持原順序；遇到準備、刪除或等候的指令前，先寫完它之前的事件。
        var pending = new Dictionary<string, List<SupportDiagnosticLogEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item is EntryItem { Entry.InternalProjectId: { } projectId } entry)
            {
                if (!pending.TryGetValue(projectId, out var entries))
                {
                    pending[projectId] = entries = [];
                }

                entries.Add(entry.Entry);
                continue;
            }

            await WritePendingAsync(pending).ConfigureAwait(false);
            switch (item)
            {
                case PrepareItem prepare:
                    var state = State(prepare.ProjectId);
                    state.Prepared = true;
                    state.Forgotten = false;
                    state.CreatedNow = prepare.CreatedNow;
                    state.PrepareFailure = prepare.Failure;
                    break;
                case ForgetItem forget:
                    // 案件資料夾即將刪除：保留中的事件跟著放棄，之後這個案件的事件不再寫入。
                    var forgotten = State(forget.ProjectId);
                    forgotten.Forgotten = true;
                    forgotten.Prepared = false;
                    forgotten.Unsaved.Clear();
                    forgotten.Dropped = 0;
                    forgotten.LastFailure = null;
                    Publish(forget.ProjectId, forgotten);
                    forget.Done.TrySetResult();
                    break;
                case FlushItem flush:
                    // 匯出前再補寫一次先前寫不進去的事件，例如紀錄檔已經不再被占用。
                    await RetryUnsavedAsync().ConfigureAwait(false);
                    flush.Done.TrySetResult();
                    break;
            }
        }

        await WritePendingAsync(pending).ConfigureAwait(false);
    }

    private async Task WritePendingAsync(Dictionary<string, List<SupportDiagnosticLogEntry>> pending)
    {
        foreach (var (projectId, entries) in pending)
        {
            await WriteCaseSafelyAsync(projectId, entries).ConfigureAwait(false);
        }

        pending.Clear();
    }

    private async Task WriteCaseSafelyAsync(string projectId, List<SupportDiagnosticLogEntry> entries)
    {
        try
        {
            await WriteCaseAsync(projectId, entries).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 寫入器不能停。寫檔以外的錯誤，例如查不出案件資料夾，整批算成放棄並記下原因。
            var state = State(projectId);
            state.Dropped += state.Unsaved.Count + entries.Count;
            state.Unsaved.Clear();
            state.LastFailure = exception;
            Publish(projectId, state);
        }
    }

    private async Task WriteCaseAsync(string projectId, List<SupportDiagnosticLogEntry> entries)
    {
        var state = State(projectId);
        if (state.Forgotten)
        {
            return;
        }

        string directory;
        try
        {
            directory = Path.Combine(_folder.GetProjectDirectory(projectId), DirectoryName);
        }
        catch (JetActionException)
        {
            return;
        }

        // 先前寫不進去的事件排在前面，依原順序補寫。寫不成一行 JSON 的事件只放棄那一筆，其餘照寫。
        var batch = new List<SupportDiagnosticLogEntry>(state.Unsaved.Count + entries.Count);
        var lines = new StringBuilder();
        foreach (var entry in state.Unsaved.Concat(entries))
        {
            try
            {
                lines.Append(Line(entry));
                batch.Add(entry);
            }
            catch (Exception exception)
            {
                state.Dropped++;
                state.LastFailure = exception;
            }
        }

        if (batch.Count == 0 && state.Dropped == 0)
        {
            return;
        }

        try
        {
            if (IsLinked(Path.GetDirectoryName(directory)!) || IsLinked(directory))
            {
                // 支援日誌不寫進連結到其他位置的案件資料夾，與匯出的規則相同；保留的事件也不再補寫。
                state.Unsaved.Clear();
                state.Dropped = 0;
                return;
            }

            if (batch.Count == 0)
            {
                // 這一批全部寫不成 JSON：放棄筆數留著，下一次成功寫入時寫成缺口。
                state.Unsaved.Clear();
                return;
            }

            await AppendAsync(Path.Combine(directory, FileName), state, batch[0].ProcessInstance, lines).ConfigureAwait(false);
            state.Unsaved.Clear();
            state.Dropped = 0;
            state.LastFailure = null;
            state.PrepareFailure = null;
            state.HeaderWritten = true;
            state.CreatedNow = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            state.Unsaved.Clear();
            state.Unsaved.AddRange(batch);

            // 超過保留上限時放棄最舊的，下次寫入成功時寫成一筆缺口。
            var overflow = state.Unsaved.Count - _maxUnsaved;
            if (overflow > 0)
            {
                state.Unsaved.RemoveRange(0, overflow);
                state.Dropped += overflow;
            }

            // 還沒在這個程序開啟的案件沒有紀錄檔不算寫入失敗，例如舊案件載入前或載入失敗：事件保留到開啟案件。
            if (state.Prepared || exception is not (DirectoryNotFoundException or FileNotFoundException))
            {
                state.LastFailure = state.PrepareFailure ?? exception;
            }
        }
        finally
        {
            Publish(projectId, state);
        }
    }

    private void Publish(string projectId, CaseState state)
    {
        if (state.Unsaved.Count == 0 && state.Dropped == 0 && state.LastFailure is null)
        {
            _writeStates.TryRemove(projectId, out _);
            return;
        }

        Dictionary<string, object?>? failure = null;
        if (state.LastFailure is { } lastFailure)
        {
            failure = new Dictionary<string, object?>(StringComparer.Ordinal);
            SupportFailureFields.Add(failure, lastFailure);
            failure["error_type"] = lastFailure.GetType().FullName;
        }

        _writeStates[projectId] = new SupportCaseHistoryWriteState(state.Unsaved.ToArray(), state.Dropped, failure);
    }

    private static async Task AppendAsync(string path, CaseState state, string? processInstance, StringBuilder lines)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // 只允許別人讀取與刪除：另一個 JET 程式要寫入時會被擋下而重試，兩邊的行不會交錯；刪除案件時
                // 不會因為正在附加而刪不掉。這個程序開過的案件在檔案不存在時建立檔案，其他案件只開啟既有檔案；
                // 紀錄資料夾不存在時丟出 DirectoryNotFoundException，不建立資料夾。
                using var stream = new FileStream(path, state.Prepared ? FileMode.OpenOrCreate : FileMode.Open,
                    FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, bufferSize: 1, FileOptions.None);
                var text = new StringBuilder();
                var isNew = stream.Length == 0;
                if (!isNew)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    if (stream.ReadByte() != '\n')
                    {
                        text.Append('\n');
                    }
                }

                if (isNew)
                {
                    text.Append(Line(StartEntry(processInstance, state.CreatedNow)));
                }

                if (isNew || !state.HeaderWritten)
                {
                    text.Append(Line(ProcessEntry(processInstance)));
                }

                if (state.Dropped > 0)
                {
                    text.Append(Line(GapEntry(processInstance, state.Dropped, state.LastFailure)));
                }

                var bytes = Encoding.UTF8.GetBytes(text.Append(lines).ToString());
                stream.Seek(0, SeekOrigin.End);
                stream.Write(bytes);
                stream.Flush();
                return;
            }
            catch (IOException exception) when (SourceFileErrors.IsInUse(exception) && attempt < SharingRetryDelays.Length)
            {
                await Task.Delay(SharingRetryDelays[attempt]).ConfigureAwait(false);
            }
        }
    }

    private static string Line(SupportDiagnosticLogEntry entry) => SupportDiagnosticNdjson.SerializeLine(entry) + "\n";

    private static SupportDiagnosticLogEntry StartEntry(string? processInstance, bool createdNow) =>
        Generated(
            processInstance,
            SupportEventNames.HistoryStarted,
            "case support history starts here",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // case_created：案件在這個程序建立；no_earlier_record：之前沒有保存的紀錄，例如用舊版建立或紀錄檔被刪除。
                ["history_start"] = createdNow ? "case_created" : "no_earlier_record",
            });

    private static SupportDiagnosticLogEntry ProcessEntry(string? processInstance) =>
        Generated(
            processInstance,
            "support.history_process",
            "process writes this case history",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["app_version"] = SupportBuildInfo.AppVersion,
                ["informational_version"] = SupportBuildInfo.InformationalVersion,
                ["module_version_id"] = SupportBuildInfo.ModuleVersionId,
                ["build_configuration"] = SupportBuildInfo.BuildConfiguration,
            });

    private static SupportDiagnosticLogEntry GapEntry(string? processInstance, long missing, Exception? failure)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["missing_count"] = missing };
        SupportFailureFields.Add(fields, failure);
        if (failure is not null)
        {
            fields["error_type"] = failure.GetType().FullName;
        }

        return Generated(processInstance, SupportEventNames.HistoryGap, $"{missing} events were not saved", fields);
    }

    // 寫入器自己產生的行沒有事件序號，序號一律為 0。
    private static SupportDiagnosticLogEntry Generated(
        string? processInstance,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?> fields) =>
        new(DateTimeOffset.UtcNow, "Information", "JET.Support", eventName, message, null, null, fields, null)
        {
            ProcessInstance = processInstance,
        };

    private static bool IsLinked(string directory) =>
        (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;

    private CaseState State(string projectId)
    {
        if (!_cases.TryGetValue(projectId, out var state))
        {
            _cases[projectId] = state = new CaseState();
        }

        return state;
    }

    private abstract record WorkItem;

    private sealed record EntryItem(SupportDiagnosticLogEntry Entry) : WorkItem;

    private sealed record PrepareItem(string ProjectId, bool CreatedNow, Exception? Failure) : WorkItem;

    private sealed record ForgetItem(string ProjectId, TaskCompletionSource Done) : WorkItem;

    private sealed record FlushItem(TaskCompletionSource Done) : WorkItem;

    private sealed class CaseState
    {
        /// <summary>這個程序開過這個案件，之後寫不進去都算缺口。</summary>
        public bool Prepared { get; set; }

        /// <summary>案件已刪除，不再寫入；重新建立同名案件時由 Prepare 清除。</summary>
        public bool Forgotten { get; set; }

        public bool CreatedNow { get; set; }

        public bool HeaderWritten { get; set; }

        /// <summary>寫不進去、等下次一起寫的事件，最多保留上限筆。</summary>
        public List<SupportDiagnosticLogEntry> Unsaved { get; } = [];

        /// <summary>超過保留上限而放棄的筆數。</summary>
        public long Dropped { get; set; }

        public Exception? PrepareFailure { get; set; }

        public Exception? LastFailure { get; set; }
    }

    private sealed class HistoryReader(string path, long completeBytes, long incompleteTailBytes)
        : ISupportCaseHistoryReader
    {
        private const int ScanBlock = 64 * 1024;

        public long CompleteBytes { get; } = completeBytes;

        public long IncompleteTailBytes { get; } = incompleteTailBytes;

        public static HistoryReader Open(string path)
        {
            using var stream = OpenRead(path, bufferSize: 1);
            var length = stream.Length;
            var complete = 0L;
            var block = new byte[ScanBlock];
            // 從檔尾往前找最後一個換行；之後的內容是還沒寫完的行。
            for (var end = length; end > 0 && complete == 0;)
            {
                var start = Math.Max(0, end - ScanBlock);
                stream.Seek(start, SeekOrigin.Begin);
                var count = (int)(end - start);
                stream.ReadExactly(block, 0, count);
                var index = Array.LastIndexOf(block, (byte)'\n', count - 1, count);
                if (index >= 0)
                {
                    complete = start + index + 1;
                }

                end = start;
            }

            return new HistoryReader(path, complete, length - complete);
        }

        public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using var stream = OpenRead(path, bufferSize: 64 * 1024);
            using var reader = new StreamReader(
                new LimitedReadStream(stream, CompleteBytes),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: false);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                yield return line;
            }
        }

        // 允許 JET 同時附加與刪除案件；讀取只看開啟當下的長度。
        private static FileStream OpenRead(string path, int bufferSize) =>
            new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize,
                FileOptions.SequentialScan);
    }

    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _remaining = limit;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken)
                .ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
