using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// WorkingPaper step4-1 export-only row。EntryId 只作 deterministic tie-break 與
/// opaque cursor，不是可見欄；RawJson 只供 finalized actual raw 欄投影。
/// </summary>
internal sealed record WorkpaperStep41SourceRow(
    long EntryId,
    string? DocumentNumber,
    string? LineItem,
    string? PostDate,
    string? ApprovalDate,
    string? CreatedBy,
    string? ApprovedBy,
    string? AccountCode,
    string? AccountName,
    long AmountScaled,
    string? Description,
    string? SourceModule,
    bool? IsManual,
    string RawJson,
    IReadOnlyList<int> MatchedPositions);

/// <summary>一頁 export-only step4-1 rows；NextCursor 是不透明 composite keyset cursor。</summary>
internal sealed record WorkpaperStep41Page(
    IReadOnlyList<WorkpaperStep41SourceRow> Rows,
    string? NextCursor);

/// <summary>
/// step4-1 專用 typed row port。公開 query.tagMatrixRowPage 保持 entry_id ASC 與既有 wire，
/// 本 port 才採 document number、line item、entry_id 的最終輸出順序。
/// </summary>
internal interface IWorkpaperStep41PageRepository
{
    Task<WorkpaperStep41Page> GetPageAsync(
        string projectId,
        GlPopulationContext context,
        PageRequest request,
        IReadOnlyList<int> scenarioPositions,
        LegacyFieldKind lineItemKind,
        CancellationToken cancellationToken);
}

/// <summary>
/// Finalized WorkingPaper step4-1 的 provider-neutral prepared export session。
/// Production 以一條連線／一個交易完成 set materialization，並只允許一次 ordered
/// forward-only reader；舊 page port 僅保留給 plan-less 相容路徑與 frozen baseline。
/// </summary>
internal interface IWorkpaperStep41PreparedSessionFactory
{
    Task<IWorkpaperStep41PreparedSession> PrepareAsync(
        string projectId,
        GlPopulationContext context,
        IReadOnlyList<int> scenarioPositions,
        LegacyFieldKind lineItemKind,
        CancellationToken cancellationToken);

    async Task<IWorkpaperStep41PreparedSession> PrepareAsync(
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

/// <summary>一次性 prepared session；dispose 必須清除 connection-scoped temp state。</summary>
internal interface IWorkpaperStep41PreparedSession : IAsyncDisposable
{
    WorkpaperStep41PreparedSessionMetrics Metrics { get; }

    IAsyncEnumerable<WorkpaperStep41SourceRow> ReadRowsAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Step4-1 結構性驗收 instrumentation。每個欄位計數的是實際執行嘗試；
/// 數量必須與輸出列數／Excel continuation page 數無關。
/// </summary>
internal sealed class WorkpaperStep41PreparedSessionMetrics(string provider)
{
    private readonly List<WorkpaperStep41PreparedCommandMetric> _commands = [];

    public string Provider { get; } = provider;

    public int SchemaReadinessCommands { get; internal set; }

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

    public IReadOnlyList<WorkpaperStep41PreparedCommandMetric> Commands => _commands;

    internal void RecordCommand(
        string operation,
        string commandText,
        IReadOnlyList<string> parameterNames)
    {
        _commands.Add(new WorkpaperStep41PreparedCommandMetric(
            operation,
            commandText,
            parameterNames));
    }
}

internal sealed record WorkpaperStep41PreparedCommandMetric(
    string Operation,
    string CommandText,
    IReadOnlyList<string> ParameterNames);
