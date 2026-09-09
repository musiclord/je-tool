namespace JET.Domain;

/// <summary>
/// 目前成功 GL generation 的來源品質 findings。現階段 closed category 只有 nullPostDate；
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
/// 空值/期外日期紀錄的 keyset 分頁回取。排序鍵 entry_id ASC(PK);游標述詞為展開布林式
/// <c>AND entry_id &gt; @cursor</c>(@cursor 綁 long;首頁省略);limit 由方言出。
/// <paramref name="category"/> 決定 WHERE 述詞(白名單列舉,非任意字串);outOfRangeDate 需期間。
/// </summary>
public interface INullRecordsPageRepository
{
    Task<PageResult<NullRecordRow>> GetPageAsync(
        string projectId,
        NullRecordCategory category,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// 空值紀錄分頁的 category 白名單(對應 manifest 四值)。字串→列舉的解析與驗證由 handler 負責;
/// repo 只接受合法列舉,故 SQL 述詞選擇是封閉集合(無任意字串注入面)。
/// </summary>
public enum NullRecordCategory
{
    NullAccount,
    NullDocument,
    NullDescription,
    OutOfRangeDate
}

/// <summary>
/// 完整性「全科目」(含差異為 0 者)的 keyset 分頁回取——匯出底稿 step1 完整性測試的資料源。
/// 與 <see cref="ICompletenessDiffPageRepository"/> 同一份 <c>JET.AuditCore.ValidationProcedures.CompletenessDiffCte</c>、
/// 同排序鍵 account_code ASC、同游標展開布林式,**唯一差別是不加 <c>WHERE tb_s &lt;&gt; gl_s</c>**:
/// 消費端以 bounded page 串流逐科目輸出；母體大小完全由目前專案查詢結果決定，不預設案件識別或固定筆數。
///
/// 為什麼是獨立介面而非在 diff repo 加參數:呼叫語意是「全科目 vs 僅差異」兩種不同視圖,
/// 各有固定消費者(step1 全科目、step1-3 僅差異);用布林旗標切會讓 SQL 多一條 god-switch,
/// 拆兩個窄介面讓各 repo 的 WHERE 固定、可讀,符合 data-structure first 與 deep module。
/// 回傳型別共用 <see cref="CompletenessDiffAccount"/>(欄位相同:科目編號/名稱/TB/GL/差異/not-in-tb)。
/// </summary>
public interface ICompletenessAccountPageRepository
{
    /// <summary>periodStart/periodEnd 界定完整性 GL 彙總的本期母體（§2；與 CTE 8 消費端一致）。</summary>
    Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
        string projectId, int moneyScale, string periodStart, string periodEnd, PageRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 完整性全科目差異(diff≠0)的 keyset 分頁回取。排序鍵 account_code ASC(唯一、有索引);
/// 游標述詞為展開布林式(跨 provider,不用元組比較);limit 由方言出。
/// </summary>
/// <summary>
/// 科目配對檔分類欄留白的科目 keyset 分頁（排序鍵 account_code ASC）。留白已依 legacy 投影為 Others，
/// 這裡只是讓審計員看到哪些科目沒填，不影響篩選結果。
/// </summary>
public interface IAccountMappingBlankPageRepository
{
    Task<PageResult<AccountMappingBlankAccount>> GetPageAsync(
        string projectId, PageRequest request, CancellationToken cancellationToken);
}

public interface ICompletenessDiffPageRepository
{
    /// <summary>periodStart/periodEnd 界定完整性 GL 彙總的本期母體（§2；與 CTE 8 消費端一致）。</summary>
    Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
        string projectId, int moneyScale, string periodStart, string periodEnd, PageRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 借貸不平傳票(GROUP BY document_number HAVING SUM(amount_scaled)≠0)的 keyset 分頁回取。
/// 排序鍵 document_number ASC(有索引);游標述詞為展開布林式(跨 provider,不用元組比較);
/// limit 由方言出。
/// </summary>
public interface IDocBalancePageRepository
{
    /// <summary>periodStart/periodEnd 界定借貸不平母體的本期口徑（§2；與 validate.run 計數端同口徑）。</summary>
    Task<PageResult<UnbalancedDocument>> GetPageAsync(
        string projectId, int moneyScale, string periodStart, string periodEnd, PageRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 借貸不平傳票所屬的完整 GL 分錄 keyset 分頁。與 <see cref="IDocBalancePageRepository"/>
/// 的傳票彙總不同，本介面回傳所有命中傳票的原始分錄 entry id，供正式 Validation
/// V_Report 6 逐頁回取原始 GL 欄位。排序鍵固定 entry_id ASC；不可把彙總列冒充明細。
/// </summary>
public interface IUnbalancedGlEntryPageRepository
{
    Task<PageResult<long>> GetEntryIdsPageAsync(
        string projectId,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// 已存篩選情境命中(result_filter_run)行層明細的 keyset 分頁回取。排序鍵 entry_id ASC;
/// 游標述詞為展開布林式 <c>AND g.entry_id &gt; @cursor</c>(@cursor 綁 long;首頁省略);limit 由方言出。
/// result_filter_run PK (scenario_position, entry_id) 覆蓋本 seek。
/// 惰性補算(該 position 無列但 config_filter_scenario 有定義)由 Application handler 重用
/// <see cref="JET.Domain.IFilterRunMaterializer"/> 落地後再呼叫本方法,維持 Infrastructure 不反向依賴 Application。
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
        FilterRuleContext context,
        PageRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// 同一份 row-tag 述詞的 set-based 統計：傳票層為 distinct document_number，
    /// 行層為命中 GL 分錄數。Pre-screening_Report 的 E/F 欄分別消費這兩個值。
    /// </summary>
    Task<PrescreenHitCounts> GetCountsAsync(
        string projectId,
        string ruleKey,
        FilterRuleContext context,
        CancellationToken cancellationToken);
}

public sealed record PrescreenHitCounts(long VoucherHitCount, long RowHitCount);

/// <summary>
/// INF 抽樣(result_inf_sampling_test_sample)行層明細的 keyset 分頁回取。排序鍵 entry_id ASC(PK);
/// 游標述詞為展開布林式 <c>AND g.entry_id &gt; @cursor</c>(@cursor 綁 long;首頁省略);limit 由方言出。
/// 樣本表以 (run_id, entry_id) 跨 run 累積；caller 必須明確提供 <paramref name="runId" />，
/// 使正式報告可證明資料屬於指定 validation run，而不是在 repository 內隱含選「最新一次」。
/// </summary>
public interface IInfSamplePageRepository
{
    Task<PageResult<InfSampleRow>> GetPageAsync(
        string projectId, string runId, int moneyScale, PageRequest request, CancellationToken cancellationToken);
}
