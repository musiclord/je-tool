namespace JET.Domain;

/// <summary>
/// tag 矩陣行層 keyset 分頁的即時回取(D2)。採每頁兩段查詢(鍵 entry_id ASC):
/// 查詢 1 取本頁「命中傳票之所有行」(document_number 落在任一命中行所屬傳票集合內的全部 GL 行,
/// 含該傳票內未命中任何情境的行;排除 NULL document_number;排序鍵 entry_id ASC、游標展開布林式
/// + LimitClause);查詢 2 以「與查詢 1 完全一致的鍵範圍 (@lo, @hi]」取本頁各行(以 entry_id)的命中
/// 情境位置(scenario_position)。<paramref name="scenarioPositions"/> 是查詢範圍：
/// null 表示全部情境（UI query）；非 null 時只能包含最多 10 個已驗證位置，
/// 且「命中傳票母體」與回傳的 PositionsByEntry 都必須限定在該集合。
///
/// 回傳 Page(<see cref="RowTagRow"/>,不含 entry_id)＋ EntryIds(與 Page.Rows 同序同長,游標鍵在
/// reader 末欄另取)＋ PositionsByEntry(每行的有序去重命中位置;非命中行不在 dict)。matchedPositions
/// 由 Application handler 以 index 對齊 rows[i] ↔ EntryIds[i] → positions 附上(非命中行補空 [])。
/// 惰性補算(全空但有已存情境)由 handler 重用 materializer 落地後再呼叫,維持 Infrastructure 不反向
/// 依賴 Application。
/// </summary>
public interface ITagMatrixRowPageRepository
{
    Task<(PageResult<RowTagRow> Page, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> GetPageAsync(
        string projectId, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken);
}

/// <summary>
/// tag 矩陣傳票層 keyset 分頁的即時回取(D2)。採每頁兩段查詢:
/// 查詢 1 取本頁命中傳票(GROUP BY document_number、聚合 MIN(post_date)/MIN(created_by)/
/// SUM(該傳票全部 GL 行的 debit_amount_scaled)、EXISTS(result_filter_run) 篩命中傳票、
/// 排序鍵 document_number ASC、
/// 游標展開布林式 + LimitClause);查詢 2 以「與查詢 1 完全一致的鍵範圍 (@lo, @hi]」取本頁各傳票的
/// 命中情境位置(DISTINCT scenario_position)。<paramref name="scenarioPositions"/> 是查詢範圍：
/// null 表示全部情境（UI query）；非 null 時只能包含最多 10 個已驗證位置，
/// 且「命中傳票母體」與回傳的 PositionsByDoc 都必須限定在該集合。
/// 兩查詢都排除 NULL document_number，鍵範圍一致以確保每傳票的命中位置不漏不溢。
///
/// 回傳 Page(VoucherTagRow,游標鍵 document_number 在 reader 末欄另取)＋
/// PositionsByDoc(每傳票的有序去重命中位置;空頁→空 dict)。matchedPositions 由 Application handler
/// 以 doc 對齊附上。惰性補算(全空但有已存情境)由 handler 重用 materializer 落地後再呼叫,
/// 維持 Infrastructure 不反向依賴 Application。
/// </summary>
public interface ITagMatrixVoucherPageRepository
{
    Task<(PageResult<VoucherTagRow> Page, IReadOnlyDictionary<string, IReadOnlyList<int>> PositionsByDoc)> GetPageAsync(
        string projectId, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken);
}

/// <summary>
/// tag 矩陣情境摘要的命中數即時回取(D2)。由 result_filter_run 即時 GROUP BY 算每個情境位置的
/// 傳票層命中數(COUNT(DISTINCT document_number),JOIN target_gl_entry)與行層命中數(COUNT(*))。
/// 回傳 dict 只含「有命中」的位置;無命中的位置不在 dict(由 Application handler 補 0)。
/// 惰性補算(全空但 config_filter_scenario 有定義)由 handler 重用 materializer 落地後再呼叫本方法,
/// 維持 Infrastructure 不反向依賴 Application。
/// </summary>
public interface ITagMatrixScenariosRepository
{
    Task<IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)>> GetCountsAsync(
        string projectId, CancellationToken cancellationToken);
}
