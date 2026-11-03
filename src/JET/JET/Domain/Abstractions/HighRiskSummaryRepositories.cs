namespace JET.Domain;

/// <summary>
/// 高風險條件彙總行層 keyset 分頁的即時回取。採每頁兩段查詢(鍵 entry_id ASC):
/// 查詢 1 取本頁「命中傳票之所有行」(voucher_number 落在任一命中行所屬傳票集合內的全部 GL 行,
/// 含該傳票內未命中任何情境的行;排除 NULL voucher_number;排序鍵 entry_id ASC、游標展開布林式
/// + LimitClause);查詢 2 以「與查詢 1 完全一致的鍵範圍 (@lo, @hi]」取本頁各行(以 entry_id)的命中
/// 情境位置(filter_scenario.position)。<paramref name="scenarioPositions"/> 是查詢範圍：
/// null 表示全部情境（UI query）；非 null 時只能包含最多 <see cref="FilterScenarioLimits.MaxSavedScenarios"/> 個已驗證位置，
/// 且「命中傳票母體」與回傳的 PositionsByEntry 都必須限定在該集合。
///
/// 回傳 Page(<see cref="RowTagRow"/>,不含 entry_id)＋ EntryIds(與 Page.Rows 同序同長,游標鍵在
/// reader 末欄另取)＋ PositionsByEntry(每行的有序去重命中位置;非命中行不在 dict)。matchedPositions
/// 由 Application handler 以 index 對齊 rows[i] ↔ EntryIds[i] → positions 附上(非命中行補空 [])。
/// 持久化結果已失效時，由 handler 重用 materializer 落地後再呼叫；有效的零筆結果不重算。維持 Infrastructure 不反向
/// 依賴 Application。
/// </summary>
public interface IHighRiskSummaryRowPageRepository
{
    Task<(PageResult<RowTagRow> Page, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> GetPageAsync(
        string projectId, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken);
}

/// <summary>
/// 高風險條件彙總傳票層 keyset 分頁的即時回取。採每頁兩段查詢:
/// 查詢 1 取本頁命中傳票(GROUP BY voucher_number、聚合 MIN(gl_date)/MIN(created_by)/
/// SUM(該傳票全部 GL 行的 debit_amount_scaled)、EXISTS(filter_hit) 篩命中傳票、
/// 排序鍵 voucher_number ASC、
/// 游標展開布林式 + LimitClause);查詢 2 以「與查詢 1 完全一致的鍵範圍 (@lo, @hi]」取本頁各傳票的
/// 命中情境位置(DISTINCT filter_scenario.position)。<paramref name="scenarioPositions"/> 是查詢範圍：
/// null 表示全部情境（UI query）；非 null 時只能包含最多 <see cref="FilterScenarioLimits.MaxSavedScenarios"/> 個已驗證位置，
/// 且「命中傳票母體」與回傳的 PositionsByDoc 都必須限定在該集合。
/// 兩查詢都排除 NULL voucher_number，鍵範圍一致以確保每傳票的命中位置不漏不溢。
///
/// 回傳 Page(VoucherTagRow,游標鍵 voucher_number 在 reader 末欄另取)＋
/// PositionsByDoc(每傳票的有序去重命中位置;空頁→空 dict)。matchedPositions 由 Application handler
/// 以 doc 對齊附上。持久化結果已失效時，由 handler 重用 materializer 落地後再呼叫；有效的零筆結果不重算。
/// 維持 Infrastructure 不反向依賴 Application。
/// </summary>
public interface IHighRiskSummaryVoucherPageRepository
{
    Task<(PageResult<VoucherTagRow> Page, IReadOnlyDictionary<string, IReadOnlyList<int>> PositionsByDoc)> GetPageAsync(
        string projectId, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken);
}

/// <summary>
/// 高風險條件彙總情境摘要的命中數即時回取。由 filter_hit 即時 GROUP BY 算每個情境位置的
/// 傳票層命中數(COUNT(DISTINCT voucher_number),JOIN gl)與行層命中數(COUNT(*))。
/// 回傳 dict 只含「有命中」的位置;無命中的位置不在 dict(由 Application handler 補 0)。
/// 持久化結果已失效時，由 handler 重用 materializer 落地後再呼叫本方法；有效的零筆結果不重算。
/// 維持 Infrastructure 不反向依賴 Application。
/// </summary>
public interface IHighRiskSummaryScenariosRepository
{
    Task<IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)>> GetCountsAsync(
        string projectId, CancellationToken cancellationToken);

    /// <summary>
    /// 指定情境的整張命中傳票分錄數：命中列所屬具名傳票在 GL 中的全部分錄，集合與 Criteria Selection
    /// 工作表寫出的列相同。只傳以整張傳票計數的情境位置；沒有命中的位置不在回傳 dict。
    /// </summary>
    Task<IReadOnlyDictionary<int, long>> GetWholeVoucherRowCountsAsync(
        string projectId, IReadOnlyList<int> scenarioPositions, CancellationToken cancellationToken);

    /// <summary>
    /// 各情境命中列中傳票號碼空白的分錄數。空白號碼不是一張傳票：這些分錄算進行層命中數，卻不會寫進
    /// Criteria Selection 工作表，報告摘要頁據此加一列說明（裁定 2026-10-19-12）。沒有這種命中的位置不在回傳 dict。
    /// 兩個正式實作都覆寫；預設實作只給不匯出報告的測試替身用。
    /// </summary>
    Task<IReadOnlyDictionary<int, long>> GetBlankDocumentHitRowCountsAsync(
        string projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<int, long>>(new Dictionary<int, long>());
}
