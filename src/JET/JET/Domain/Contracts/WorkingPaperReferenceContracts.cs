namespace JET.Domain;

/// <summary>
/// 科目配對匯出列(匯出底稿「自動化工具-科目配對資訊」sheet 15):
/// GL_NUMBER=AccountNumber、GL_NAME、STANDARDIZED_ACCOUNT_NAME=Category。
/// <see cref="NotInTb"/> 為真代表「該科目在 GL 有、TB 無」(完整性 not-in-tb 集合);
/// 此時 sheet 的 GL_NAME 欄寫字面「Not in TB」而非 <see cref="AccountName"/>——
/// 由 emitter 依此旗標渲染(data-structure 對映,非逐列特判)。
/// </summary>
public sealed record AccountMappingExportRow(
    string AccountNumber,
    string? AccountName,
    string Category,
    bool NotInTb)
{
    /// <summary>GL 有、TB 沒有的科目在 GL_NAME 寫的字。匯入科目配對時遇到它當成沒有科目名稱。</summary>
    public const string NotInTbName = "Not in TB";
}

/// <summary>
/// 科目配對範本列，供審計員填分類。使用有效 GL 與 TB 聯集的可配對科目母體，排除空白科目編號。
/// A=<see cref="AccountNumber"/>(population.account_number)、B=<see cref="TemplateName"/>：
/// <see cref="NotInTb"/> 為真(GL 有、TB 沒有,完整性 not_in_tb 集合)時照舊版寫字面「Not in TB」,
/// 和匯出底稿 sheet 15 用同一個旗標;其他科目寫 <see cref="AccountName"/>(TB 名稱,TB 沒寫時用 GL 名稱)。
/// 匯回範本時「Not in TB」當成沒有科目名稱。
/// C 欄(分類)由 writer 留空 + 下拉,不在此 model(範本產出前尚未配對)。
/// </summary>
public sealed record AccountMappingTemplateRow(
    string AccountNumber,
    string? AccountName,
    bool NotInTb = false)
{
    /// <summary>範本 B 欄實際寫的字。</summary>
    public string? TemplateName => NotInTb ? AccountMappingExportRow.NotInTbName : AccountName;
}

/// <summary>
/// 科目配對的唯讀全列匯出查詢(匯出底稿 sheet 15)。回 <c>account_mapping</c> 每一列
/// 並附上 not-in-tb 旗標(以 <c>JET.AuditCore.ValidationProcedures.CompletenessDiffCte</c> 的
/// not_in_tb 為單一事實來源:GL 有 TB 無 = 1)。科目數有界(實務數百),故回完整清單、不分頁
/// (對有界基數加分頁是過度工程化;鏡射 <see cref="IPreparerSummaryExportRepository"/>)。
///
/// 為什麼新立唯讀 repo 而非擴充 <see cref="IAccountMappingStore"/>:後者是匯入(replace-only)專用,
/// 匯出走 WorkingPaperWriter 既有的「唯讀 repo 注入」管線,兩者關注點不同。
/// </summary>
public interface IAccountMappingExportRepository
{
    /// <summary>periodStart/periodEnd 界定完整性 not-in-tb 判定的 GL 母體本期口徑（與 CTE 8 消費端一致）。</summary>
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
/// 為什麼新立唯讀 repo 而非擴充 <see cref="ICalendarStore"/>:後者是匯入(replace 語意)+ 計數專用,
/// 缺逐日讀回 API;匯出需逐日列出,走 WorkingPaperWriter 既有唯讀 repo 注入管線。
/// </summary>
public interface ICalendarExportRepository
{
    Task<IReadOnlyList<CalendarDayEntry>> FetchDaysAsync(
        string projectId,
        CalendarDayType type,
        CancellationToken cancellationToken);
}
