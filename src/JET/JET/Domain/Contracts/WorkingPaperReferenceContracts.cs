namespace JET.Domain;

/// <summary>
/// 科目配對匯出列(匯出底稿「自動化工具-科目配對資訊」sheet 15):
/// GL_NUMBER=AccountNumber、GL_NAME=<see cref="GlName"/>、STANDARDIZED_ACCOUNT_NAME=Category。
/// <see cref="AccountName"/> 是已儲存的科目配對的名稱，沒有名稱時是 null。
/// <see cref="NotInTb"/> 為真代表「該科目在 GL 有、TB 無」(完整性 not-in-tb 集合)；GL_NAME 寫什麼不看這個旗標。
/// </summary>
public sealed record AccountMappingExportRow(
    string AccountNumber,
    string? AccountName,
    string Category,
    bool NotInTb)
{
    /// <summary>沒有 TB 名稱可寫的科目在 GL_NAME 寫的字。匯入科目配對時遇到它當成沒有科目名稱。</summary>
    public const string NotInTbName = "Not in TB";

    /// <summary>
    /// GL_NAME 欄實際寫的字。已儲存的科目配對有名稱時寫這個名稱，科目只在 GL 出現也一樣；
    /// 沒有名稱（沒有值、空字串、只有空白字元）時寫「Not in TB」，這一欄不留白。
    /// 科目配對範本在 TB 沒有名稱的科目寫「Not in TB」，匯入時當成沒有名稱；在畫面分類時同樣只存 TB 的名稱。
    /// 所以這裡寫回的字和審計員上傳的範本一致，審計員在範本自己填的名稱照樣寫出。
    /// </summary>
    public string GlName => string.IsNullOrWhiteSpace(AccountName) ? NotInTbName : AccountName;
}

/// <summary>
/// 科目配對範本列，供審計員填分類。使用有效 GL 與 TB 聯集的可配對科目母體，排除空白科目編號。
/// A=<see cref="AccountNumber"/>(population.account_number)、B=<see cref="TemplateName"/>。
/// <see cref="AccountName"/> 是 TB 的科目名稱；TB 沒有名稱或科目只在 GL 出現時是 null，範本不帶 GL 的名稱。
/// B 欄在 TB 有名稱時寫名稱，其餘照舊版寫字面「Not in TB」：舊版範本只看 TB 的名稱有沒有內容，名稱是空的就寫這個字。
/// <see cref="NotInTb"/> 為真代表科目在 GL 有、TB 沒有(完整性 not_in_tb 集合)，和匯出底稿 sheet 15 用同一個旗標。
/// 匯回範本時「Not in TB」當成沒有科目名稱。
/// C 欄(分類)由 writer 留空 + 下拉,不在此 model(範本產出前尚未配對)。
/// </summary>
public sealed record AccountMappingTemplateRow(
    string AccountNumber,
    string? AccountName,
    bool NotInTb = false)
{
    /// <summary>範本 B 欄實際寫的字。科目只在 GL 出現，或 TB 沒有科目名稱（沒有值、空字串、只有空白字元）時是「Not in TB」。</summary>
    public string TemplateName =>
        NotInTb || string.IsNullOrWhiteSpace(AccountName) ? AccountMappingExportRow.NotInTbName : AccountName;
}

/// <summary>
/// 科目配對的唯讀全列匯出查詢（匯出底稿 sheet 15）。回 <c>account_mapping</c> 每一列，並附上 not-in-tb 旗標，
/// 取自 <c>JET.AuditCore.ValidationProcedures.CompletenessDiffCte</c> 的 not_in_tb：GL 有、TB 無時是 1。
/// 科目數有上限，實務上數百，所以一次回完整清單，不分頁，做法和 <see cref="IPreparerSummaryExportRepository"/> 相同。
///
/// <see cref="IAccountMappingStore"/> 只做 replace-only 匯入；匯出經 WorkingPaperWriter 注入的唯讀 repo 讀取，所以另用這個介面。
/// </summary>
public interface IAccountMappingExportRepository
{
    /// <summary>periodStart/periodEnd 界定完整性 not-in-tb 判定的 GL 母體本期範圍，和完整性差異查詢用的期間相同。</summary>
    Task<IReadOnlyList<AccountMappingExportRow>> FetchAllAsync(
        string projectId,
        string periodStart,
        string periodEnd,
        CancellationToken cancellationToken);

    /// <summary>
    /// 科目配對範本的母體讀取(產空白範本用):回可配對的科目母體,即 GL∪TB 完整性 diff 去掉空白科目編號
    /// (<c>JET.Infrastructure.AccountMappingPopulationQuery</c>，與「配對檔未列的科目」共用母體)，依 account_number 升冪。
    /// 科目編輯清單另外包含配對檔多出的科目。
    /// 與 <see cref="FetchAllAsync"/> 的差異:那讀 account_mapping(已配對,供匯出 sheet 15),
    /// 這讀可配對科目母體，排除空白科目編號，不論目前是否已配對，供產範本讓審計員填 C 欄。
    /// periodStart/periodEnd 界定 GL 母體本期口徑(與 CTE @periodStart/@periodEnd 一致)。
    /// </summary>
    Task<IReadOnlyList<AccountMappingTemplateRow>> FetchTemplateRowsAsync(
        string projectId,
        string periodStart,
        string periodEnd,
        CancellationToken cancellationToken);
}

/// <summary>
/// 行事曆匯出(假日 + 補班)的唯讀讀回:回每日 <see cref="CalendarDayEntry"/>(日期 yyyy-MM-dd + 名稱)。
/// 用於匯出底稿「自動化工具-假期假日資訊」sheet 14 的假日表與補班段。日數有界(一年百列上下),
/// 故回完整清單、不分頁。day_type 由 <see cref="CalendarDayType"/> 指定,日期升冪。
///
/// <see cref="ICalendarStore"/> 只做匯入（replace 語意）與計數，沒有逐日讀回；匯出要逐日列出，
/// 所以經 WorkingPaperWriter 注入這個唯讀 repo。
/// </summary>
public interface ICalendarExportRepository
{
    Task<IReadOnlyList<CalendarDayEntry>> FetchDaysAsync(
        string projectId,
        CalendarDayType type,
        CancellationToken cancellationToken);
}
