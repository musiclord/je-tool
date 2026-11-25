namespace JET.Domain;

/// <summary>
/// 投影結果。Errors 非空時代表 repository 已 rollback、target 未寫入任何資料。
/// </summary>
public sealed record ProjectionResult(
    int ProjectedRowCount,
    IReadOnlyList<RowProjectionError> Errors)
{
    /// <summary>
    /// 全來源列掃描所得的錯誤總數。Errors 只保存有界樣本，不能拿樣本數冒充總數。
    /// </summary>
    public int TotalErrorCount { get; init; } = Errors.Count;

    /// <summary>完整來源的分組計數與有界樣本；舊呼叫端未提供時仍由 Errors 呈現。</summary>
    internal IReadOnlyList<ProjectionErrorGroupSummary>? ErrorGroups { get; init; }

    /// <summary>GL 投影時記下的控制總數，只算納入測試的分錄；TB 投影及失敗結果維持 null。</summary>
    public GlTotals? GlTotals { get; init; }

    /// <summary>
    /// 非阻斷的提交後提醒（投影已成功）。目前用於「必填文字欄整欄空白，疑似配錯欄」
    /// （如重複標頭中的空白欄）。預設空；前端在配對提交成功後一併呈現給使用者。
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// GL 投影成功後的日期提醒：哪個欄位有讀不成日期的值、年份不在 1900 到 2100 年之間的日期，
    /// 或早於 1900-03-01、底稿寫成文字的日期，各有幾筆、是哪些值。
    /// 不擋確認配對；沒有時是空的，TB 投影及失敗結果也是空的。
    /// </summary>
    public IReadOnlyList<GlDateNotice> DateNotices { get; init; } = [];

    /// <summary>
    /// GL 投影時依 <see cref="GlMappingOptions.FillDownFields"/> 把空白補成上一列值的格數；
    /// 沒有勾選欄位、TB 投影及失敗結果都是 0。寫進配對確認的回應，審計員據此記在 CAATs 文件。
    /// </summary>
    public long FillDownCellCount { get; init; }
}

/// <summary>
/// GL 投影在同一個交易裡算出的控制總數。PbcRowCount 是讀進來的 PBC-GL 列數，
/// 等於 GlRowCount 加上兩種未納入原因的列數；借貸合計只算 GL，不含 GL(未納入測試)。
/// </summary>
public sealed record GlTotals(
    long PbcRowCount,
    long GlRowCount,
    long ExcludedByPeriodCount,
    long ExcludedByPostingStatusCount,
    long GlDebitScaled,
    long GlCreditScaled);

/// <summary>staging → target 投影的非權威進度快照。</summary>
public sealed record ProjectionProgress(long RowsProcessed);

/// <summary>
/// 管案件資料庫裡的 GL 分錄。確認 GL 欄位配對時，把匯入暫存表 <c>pbc_gl</c> 的列轉成 <c>gl</c> 的分錄，
/// 並一併重寫攸關資料元素（<c>gl_rde_field</c>、<c>gl_rde_value</c>）與控制總數（<c>gl_control_total</c>）。
/// 這個介面只有這一個寫入動作，不提供查詢。
/// </summary>
public interface IGlRepository
{
    Task<ProjectionResult> ProjectStagingToTargetAsync(
        string projectId,
        string batchId,
        GlMappingSpec spec,
        int moneyScale,
        DateParseOptions dateOptions,
        DateOnly periodStart,
        DateOnly periodEnd,
        bool postingStatusMapped,
        GlPostingStatusPolicy? postingStatusPolicy,
        DateTimeOffset committedUtc,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null);
}

public interface ITbRepository
{
    Task<ProjectionResult> ProjectStagingToTargetAsync(
        string projectId,
        string batchId,
        TbMappingSpec spec,
        int moneyScale,
        DateTimeOffset committedUtc,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null);
}
