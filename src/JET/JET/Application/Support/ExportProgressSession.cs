using JET.Domain;

namespace JET.Application;

/// <summary>
/// Owns the single monotonic clock and the per-artifact lifecycle for one formal
/// export action. Writer callbacks remain sheet-local; this adapter is the only
/// place that shapes the cumulative wire contract.
/// </summary>
internal sealed class ExportProgressSession
{
    private readonly IJetEventPublisher _eventPublisher;
    private readonly CancellationToken _cancellationToken;
    private readonly TimeProvider _timeProvider;
    private readonly long _startedTimestamp;
    private readonly HashSet<ReportArtifactKind> _startedKinds = [];
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

    public ExportArtifactProgress Start(ReportArtifactKind kind)
    {
        if (!_startedKinds.Add(kind))
        {
            throw new InvalidOperationException(
                $"Export artifact '{ReportArtifactKindValues.ToValue(kind)}' 已啟動進度追蹤。");
        }

        var progress = new ExportArtifactProgress(this, kind);
        progress.PublishPreparing();
        return progress;
    }

    internal void Publish(
        ReportArtifactKind kind,
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
            artifactKind = ReportArtifactKindValues.ToValue(kind),
            phase,
            sheetName,
            sheetsCompleted,
            rowsWritten,
            elapsedMilliseconds = elapsed
        });

        // A synchronous publisher (including the isolated GUI fixture) may
        // request cancellation while observing this event. Do not allow the
        // action to advance to another phase in that case.
        _cancellationToken.ThrowIfCancellationRequested();
    }
}

internal sealed class ExportArtifactProgress
{
    private const string PreparingDataPhase = "preparingData";
    private const string WritingSheetPhase = "writingSheet";
    private const string FinalizingWorkbookPhase = "finalizingWorkbook";
    private const string PublishingArtifactPhase = "publishingArtifact";

    private readonly ExportProgressSession _session;
    private readonly ReportArtifactKind _kind;
    private ExportProgressState _state;
    private string? _currentSheet;
    private long _currentSheetRows;
    private int _sheetsCompleted;
    private long _completedRows;

    internal ExportArtifactProgress(ExportProgressSession session, ReportArtifactKind kind)
    {
        _session = session;
        _kind = kind;
    }

    public Action<WorkpaperProgress> WriterProgress => OnWriterProgress;

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
                $"Export artifact '{ReportArtifactKindValues.ToValue(_kind)}' 的工作表"
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

    public void PublishingArtifact()
    {
        EnsureState(ExportProgressState.Finalizing);
        _state = ExportProgressState.Publishing;
        _session.Publish(
            _kind,
            PublishingArtifactPhase,
            null,
            _sheetsCompleted,
            _completedRows);
    }

    private void OnWriterProgress(WorkpaperProgress value)
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

    private void BeginOrContinueSheet(WorkpaperProgress value)
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
        $"Export artifact '{ReportArtifactKindValues.ToValue(_kind)}'"
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
