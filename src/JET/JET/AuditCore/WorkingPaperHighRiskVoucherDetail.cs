using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 工作底稿 step4-1 匯出專用的一列分錄。<c>EntryId</c> 不是底稿上看得到的欄；
/// 其他排序欄都相同時用它決定先後，讓每次匯出的順序相同。
/// <c>RawJson</c> 是來源檔的原始列，只給值取自來源檔的那幾欄使用。
/// </summary>
internal sealed record WorkingPaperHighRiskVoucherDetailSourceRow(
    long EntryId,
    string? VoucherNumber,
    string? LineItem,
    string? GlDate,
    string? ApprovalDate,
    string? CreatedBy,
    string? ApprovedBy,
    string? AccountNumber,
    string? AccountName,
    long AmountScaled,
    string? Description,
    string? SourceModule,
    bool? IsManual,
    string RawJson,
    IReadOnlyList<int> MatchedPositions,
    string? PostingStatus = null);

/// <summary>
/// 工作底稿 step4-1 匯出用的 prepared session。
/// 正式匯出用一條連線、一個交易，以集合式 SQL 把要匯出的列先寫進暫存表，之後只能依排序從頭到尾讀一次。
/// 排序照底稿最後的輸出順序，依序是傳票號碼、項次、entry_id；
/// 畫面用的 <c>query.highRiskSummaryRowPage</c> 沒有指定排序時依 entry_id 由小到大排序。
/// </summary>
internal interface IWorkingPaperHighRiskVoucherDetailPreparedSessionFactory
{
    Task<IWorkingPaperHighRiskVoucherDetailPreparedSession> PrepareAsync(
        string projectId,
        GlPopulationContext context,
        IReadOnlyList<int> scenarioPositions,
        LegacyFieldKind lineItemKind,
        CancellationToken cancellationToken);

    async Task<IWorkingPaperHighRiskVoucherDetailPreparedSession> PrepareAsync(
        string projectId,
        GlPopulationContext context,
        IReadOnlyList<int> scenarioPositions,
        LegacyFieldKind lineItemKind,
        CancellationToken cancellationToken,
        IReadOnlyList<int> hitVoucherScenarioPositions)
    {
        ArgumentNullException.ThrowIfNull(hitVoucherScenarioPositions);
        if (hitVoucherScenarioPositions.Count != 0)
        {
            throw new InvalidOperationException(
                "WorkingPaper step4-1 prepared session 尚未支援整張命中傳票的標記範圍。");
        }

        return await PrepareAsync(
            projectId,
            context,
            scenarioPositions,
            lineItemKind,
            cancellationToken);
    }
}

/// <summary>只能用一次的 prepared session；dispose 時必須清掉留在這條連線上的暫存表等暫存狀態。</summary>
internal interface IWorkingPaperHighRiskVoucherDetailPreparedSession : IAsyncDisposable
{
    WorkingPaperHighRiskVoucherDetailPreparedSessionMetrics Metrics { get; }

    IAsyncEnumerable<WorkingPaperHighRiskVoucherDetailSourceRow> ReadRowsAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Step4-1 匯出時各種資料庫動作的次數，供測試核對。每個欄位計數的是實際執行嘗試；
/// 數量必須與輸出列數、Excel 續頁工作表的數量無關。
/// </summary>
internal sealed class WorkingPaperHighRiskVoucherDetailPreparedSessionMetrics(string provider)
{
    private readonly List<WorkingPaperHighRiskVoucherDetailPreparedCommandMetric> _commands = [];

    public string Provider { get; } = provider;

    public int Connections { get; internal set; }

    public int Transactions { get; internal set; }

    public int TemporaryTableInitializationCommands { get; internal set; }

    public int HitVoucherMaterializationCommands { get; internal set; }

    public int RowTagMaterializationCommands { get; internal set; }

    public int WidthAggregations { get; internal set; }

    public int OrderedReaderCommands { get; internal set; }

    public int CleanupCommands { get; internal set; }

    public long RowsRead { get; internal set; }

    public string TransactionIsolationLevel { get; internal set; } = string.Empty;

    public string ConsistencyMode { get; internal set; } = string.Empty;

    public bool DedicatedConnection { get; internal set; }

    public bool ReaderCompleted { get; internal set; }

    public bool CleanupCommandSucceeded { get; internal set; }

    public bool TransactionCommitted { get; internal set; }

    public bool TransactionRolledBack { get; internal set; }

    public bool TransactionDisposed { get; internal set; }

    public bool ConnectionDisposed { get; internal set; }

    public bool Disposed { get; internal set; }

    public bool TemporaryObjectsCleared { get; internal set; }

    public IReadOnlyList<WorkingPaperHighRiskVoucherDetailPreparedCommandMetric> Commands => _commands;

    internal void RecordCommand(
        string operation,
        string commandText,
        IReadOnlyList<string> parameterNames)
    {
        _commands.Add(new WorkingPaperHighRiskVoucherDetailPreparedCommandMetric(
            operation,
            commandText,
            parameterNames));
    }
}

internal sealed record WorkingPaperHighRiskVoucherDetailPreparedCommandMetric(
    string Operation,
    string CommandText,
    IReadOnlyList<string> ParameterNames);
