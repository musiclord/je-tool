namespace JET.Domain;

/// <summary>
/// 目前成功 GL generation 的總帳日期空白 findings。現階段 closed category 只有 blankGlDate；
/// 它刻意讀 raw target（含 period-excluded row），排序鍵 entry_id ASC。
/// </summary>
public interface ISourceQualityPageRepository
{
    Task<PageResult<SourceQualityFindingRow>> GetPageAsync(
        string projectId,
        PageRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// 空值/期外日期紀錄的 keyset 分頁回取。沒有指定排序時依 entry_id(PK) 升冪，也可依審計員選的欄位排序。
/// <c>category</c> 決定 WHERE 述詞(白名單列舉,非任意字串);approvalDateNotInPeriod 需期間。
/// </summary>
public interface IBlankRecordsPageRepository
{
    Task<PageResult<BlankRecordRow>> GetPageAsync(
        string projectId,
        BlankRecordCategory category,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// 資料缺漏輔助檢視分頁的 category 白名單(前端可送的四個值)。字串→列舉的解析與驗證由 handler 負責;
/// repo 只接受合法列舉,故 SQL 述詞選擇是封閉集合(無任意字串注入面)。
/// </summary>
public enum BlankRecordCategory
{
    BlankAccountNumber,
    BlankVoucherNumber,
    BlankDescription,
    ApprovalDateNotInPeriod
}

/// <summary>
/// 完整性全科目的 keyset 分頁，供資料驗證報告與底稿 step1 使用，包含差異為 0 的科目。
/// 與 <see cref="ICompletenessDiffPageRepository"/> 共用逐科目比對的 SQL 定義、排序與游標，
/// 但不過濾差異，且科目名稱只取 TB；TB 名稱空白或 GL 有、TB 無時不以 GL 名稱補值。
/// 差異頁及底稿 step1-3 仍可用 GL 名稱補值。
/// 消費端以有界分頁逐科目輸出；母體大小由目前案件查詢結果決定，不預設案件識別或固定筆數。
/// 回傳型別共用 <see cref="CompletenessDiffAccount"/>，欄位包含科目編號、名稱、TB 與 GL 金額、差異及是否 GL 有而 TB 無。
/// </summary>
public interface ICompletenessAccountPageRepository
{
    /// <summary>periodStart/periodEnd 界定完整性 GL 彙總的本期母體（與 CTE 8 消費端一致）。</summary>
    Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
        string projectId, int moneyScale, string periodStart, string periodEnd, PageRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 科目配對檔分類欄留白的科目 keyset 分頁（排序鍵 account_number ASC）。留白已依 legacy 投影為 Others，
/// 這裡只是讓審計員看到哪些科目沒填，不影響篩選結果。
/// </summary>
public interface IAccountMappingBlankPageRepository
{
    Task<PageResult<AccountMappingBlankAccount>> GetPageAsync(
        string projectId, PageRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 完整性全科目差異(diff≠0)的 keyset 分頁回取。排序鍵 account_number ASC(唯一、有索引);
/// 游標述詞為展開布林式(不用元組比較)。
/// </summary>
public interface ICompletenessDiffPageRepository
{
    /// <summary>periodStart/periodEnd 界定完整性 GL 彙總的本期母體（與 CTE 8 消費端一致）。</summary>
    Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
        string projectId, int moneyScale, string periodStart, string periodEnd, PageRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 借貸不平傳票(GROUP BY voucher_number HAVING SUM(amount_scaled)≠0)的 keyset 分頁回取。
/// 排序鍵 voucher_number ASC(有索引);游標述詞為展開布林式(不用元組比較)。
/// </summary>
public interface IVoucherBalancePageRepository
{
    /// <summary>periodStart/periodEnd 界定借貸不平母體的本期口徑（與 validate.run 計數端同口徑）。</summary>
    Task<PageResult<UnbalancedVoucher>> GetPageAsync(
        string projectId, int moneyScale, string periodStart, string periodEnd, PageRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// 同一批不平傳票，依傳票號碼與總帳日期彙總後依序串流，供底稿 Step 1-1 明細表逐列寫出。
    /// 判定哪些傳票不平仍只看傳票號碼，與 <see cref="GetPageAsync"/> 同口徑。
    /// </summary>
    IAsyncEnumerable<UnbalancedVoucherDateRow> StreamVoucherDateRowsAsync(
        string projectId, CancellationToken cancellationToken);
}

/// <summary>
/// 借貸不平傳票所屬的完整 GL 分錄 keyset 分頁。與 <see cref="IVoucherBalancePageRepository"/>
/// 的傳票彙總不同，本介面回傳所有命中傳票的原始分錄 entry id，供正式 Validation
/// V_Report 6 逐頁回取原始 GL 欄位。排序鍵固定 entry_id ASC；不可把彙總列冒充明細。
/// </summary>
public interface IUnbalancedVoucherPageRepository
{
    Task<PageResult<long>> GetEntryIdsPageAsync(
        string projectId,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// 已存篩選情境命中(filter_hit)行層明細的 keyset 分頁回取。排序鍵 entry_id ASC;
/// 游標述詞為展開布林式 <c>AND g.entry_id &gt; @cursor</c>(@cursor 綁 long;首頁省略)。
/// filter_hit 依 scenario_id 接 filter_scenario 取位置，主鍵 (scenario_id, entry_id) 覆蓋本 seek。
/// 惰性補算(該 position 無列但 filter_scenario 有定義)由 Application handler 重用
/// <see cref="JET.Domain.IFilterHitMaterializer"/> 落地後再呼叫本方法,維持 Infrastructure 不反向依賴 Application。
/// </summary>
public interface IFilterHitsPageRepository
{
    Task<PageResult<FilterHitRow>> GetPageAsync(
        string projectId, int scenarioPosition, int moneyScale, PageRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 單一 row-tag 預篩選規則的完整命中明細。規則鍵由 Application 以
/// <see cref="PrescreenRuleKeys.FilterableKeys"/> 白名單驗證；repository 使用與 prescreen/filter
/// 同源述詞、本期母體，依 entry_id ASC keyset 分頁。
/// </summary>
public interface IPrescreenPageRepository
{
    Task<PageResult<PrescreenHitRow>> GetPageAsync(
        string projectId,
        string ruleKey,
        FilterConditionContext context,
        PageRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// 同一份 row-tag 述詞的 set-based 統計：傳票層為 distinct voucher_number，
    /// 行層為命中 GL 分錄數。Pre-screening_Report 的 E/F 欄分別消費這兩個值。
    /// </summary>
    Task<PrescreenHitCounts> GetCountsAsync(
        string projectId,
        string ruleKey,
        FilterConditionContext context,
        CancellationToken cancellationToken);
}

public sealed record PrescreenHitCounts(long VoucherHitCount, long RowHitCount);

/// <summary>
/// INF 抽樣(inf_sample)行層明細的 keyset 分頁回取。排序鍵 entry_id ASC(PK);
/// 游標述詞為展開布林式 <c>AND g.entry_id &gt; @cursor</c>(@cursor 綁 long;首頁省略)。
/// 樣本表以 (result_id, entry_id) 跨 run 累積；caller 必須明確提供 <c>resultId</c>，
/// 使正式報告可證明資料屬於指定 validation run，而不是在 repository 內隱含選「最新一次」。
/// </summary>
public interface IInfSamplePageRepository
{
    Task<PageResult<InfSampleRow>> GetPageAsync(
        string projectId, string resultId, int moneyScale, PageRequest request, CancellationToken cancellationToken);
}
