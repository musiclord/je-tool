using JET.Domain;

namespace JET.Application;

/// <summary>
/// 一次正式匯出 action 的進度回報。這裡握有唯一一個只增不減的計時，並管理每一份報告從準備資料到發布的進度階段。
/// 寫檔的程式回報進度時只知道自己正在寫的那一張工作表；累計的工作表張數與列數由 <see cref="ExportReportProgress"/> 算，
/// 經過時間由這裡算，export.progress 事件也只在這裡送出。
/// </summary>
internal sealed class ExportProgressSession
{
    private readonly IJetEventPublisher _eventPublisher;
    private readonly CancellationToken _cancellationToken;
    private readonly TimeProvider _timeProvider;
    private readonly long _startedTimestamp;
    private readonly HashSet<ReportKind> _startedKinds = [];
    private long _lastElapsedMilliseconds;

    public ExportProgressSession(
        IJetEventPublisher eventPublisher,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _cancellationToken = cancellationToken;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startedTimestamp = _timeProvider.GetTimestamp();
    }

    public ExportReportProgress Start(ReportKind kind)
    {
        if (!_startedKinds.Add(kind))
        {
            throw new InvalidOperationException(
                $"Export report '{ReportKindValues.ToValue(kind)}' 已啟動進度追蹤。");
        }

        var progress = new ExportReportProgress(this, kind);
        progress.PublishPreparing();
        return progress;
    }

    internal void Publish(
        ReportKind kind,
        string phase,
        string? sheetName,
        int sheetsCompleted,
        long rowsWritten)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        var elapsed = (long)Math.Floor(
            _timeProvider.GetElapsedTime(_startedTimestamp).TotalMilliseconds);
        elapsed = Math.Max(0, Math.Max(_lastElapsedMilliseconds, elapsed));
        _lastElapsedMilliseconds = elapsed;

        _eventPublisher.Publish("export.progress", new
        {
            reportKind = ReportKindValues.ToValue(kind),
            phase,
            sheetName,
            sheetsCompleted,
            rowsWritten,
            elapsedMilliseconds = elapsed
        });

        // 事件發布者如果是同步執行的，可能在處理這個事件時就要求取消，測試用的發布者也是這樣。
        // 這時不讓 action 繼續進到下一個階段。
        _cancellationToken.ThrowIfCancellationRequested();
    }
}

internal sealed class ExportReportProgress
{
    private const string PreparingDataPhase = "preparingData";
    private const string WritingSheetPhase = "writingSheet";
    private const string FinalizingWorkbookPhase = "finalizingWorkbook";
    private const string PublishingReportPhase = "publishingReport";

    private readonly ExportProgressSession _session;
    private readonly ReportKind _kind;
    private ExportProgressState _state;
    private string? _currentSheet;
    private long _currentSheetRows;
    private int _sheetsCompleted;
    private long _completedRows;

    internal ExportReportProgress(ExportProgressSession session, ReportKind kind)
    {
        _session = session;
        _kind = kind;
    }

    public Action<WorkingPaperProgress> WriterProgress => OnWriterProgress;

    internal void PublishPreparing()
    {
        EnsureState(ExportProgressState.NotStarted);
        _state = ExportProgressState.Preparing;
        _session.Publish(_kind, PreparingDataPhase, null, 0, 0);
    }

    public void FinalizingWorkbook()
    {
        if (_state is not (ExportProgressState.Preparing or ExportProgressState.Writing))
        {
            throw InvalidTransition(FinalizingWorkbookPhase);
        }

        if (_currentSheet is not null)
        {
            throw new InvalidOperationException(
                $"Export report '{ReportKindValues.ToValue(_kind)}' 的工作表"
                + $" '{_currentSheet}' 尚未回報關閉，不得進入 {FinalizingWorkbookPhase}。");
        }

        _state = ExportProgressState.Finalizing;
        _session.Publish(
            _kind,
            FinalizingWorkbookPhase,
            null,
            _sheetsCompleted,
            _completedRows);
    }

    public void PublishingReport()
    {
        EnsureState(ExportProgressState.Finalizing);
        _state = ExportProgressState.Publishing;
        _session.Publish(
            _kind,
            PublishingReportPhase,
            null,
            _sheetsCompleted,
            _completedRows);
    }

    private void OnWriterProgress(WorkingPaperProgress value)
    {
        if (_state is not (ExportProgressState.Preparing or ExportProgressState.Writing))
        {
            throw InvalidTransition(WritingSheetPhase);
        }

        if (string.IsNullOrWhiteSpace(value.SheetName))
        {
            throw new InvalidOperationException("Export writer progress 必須提供非空白工作表名稱。");
        }

        if (value.RowsWritten < 0)
        {
            throw new InvalidOperationException("Export writer progress 的 rowsWritten 不得為負數。");
        }

        if (value.SheetsCompleted == _sheetsCompleted)
        {
            BeginOrContinueSheet(value);
            _state = ExportProgressState.Writing;
            _session.Publish(
                _kind,
                WritingSheetPhase,
                value.SheetName,
                _sheetsCompleted,
                checked(_completedRows + value.RowsWritten));
            return;
        }

        if (value.SheetsCompleted != checked(_sheetsCompleted + 1))
        {
            throw new InvalidOperationException(
                "Export writer progress 的 sheetsCompleted 必須維持不變或一次增加一張。");
        }

        if (_currentSheet is not null
            && !string.Equals(_currentSheet, value.SheetName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Export writer 在 '{_currentSheet}' 尚未關閉前回報關閉 '{value.SheetName}'。");
        }

        if (value.RowsWritten < _currentSheetRows)
        {
            throw new InvalidOperationException(
                $"Export writer 的工作表 '{value.SheetName}' rowsWritten 不得倒退。");
        }

        _completedRows = checked(_completedRows + value.RowsWritten);
        _sheetsCompleted = value.SheetsCompleted;
        _currentSheet = null;
        _currentSheetRows = 0;
        _state = ExportProgressState.Writing;
        _session.Publish(
            _kind,
            WritingSheetPhase,
            value.SheetName,
            _sheetsCompleted,
            _completedRows);
    }

    private void BeginOrContinueSheet(WorkingPaperProgress value)
    {
        if (_currentSheet is null)
        {
            _currentSheet = value.SheetName;
            _currentSheetRows = 0;
        }
        else if (!string.Equals(_currentSheet, value.SheetName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Export writer 在 '{_currentSheet}' 尚未關閉前開始 '{value.SheetName}'。");
        }

        if (value.RowsWritten < _currentSheetRows)
        {
            throw new InvalidOperationException(
                $"Export writer 的工作表 '{value.SheetName}' rowsWritten 不得倒退。");
        }

        _currentSheetRows = value.RowsWritten;
    }

    private void EnsureState(ExportProgressState expected)
    {
        if (_state != expected)
        {
            throw InvalidTransition(expected.ToString());
        }
    }

    private InvalidOperationException InvalidTransition(string next) => new(
        $"Export report '{ReportKindValues.ToValue(_kind)}'"
        + $" 無法從 {_state} 進入 {next}。");

    private enum ExportProgressState
    {
        NotStarted,
        Preparing,
        Writing,
        Finalizing,
        Publishing
    }
}
