namespace JET.Domain;

/// <summary>
/// 科目配對匯出列(匯出底稿「自動化工具-科目配對資訊」sheet 15):
/// GL_NUMBER=AccountCode、GL_NAME、STANDARDIZED_ACCOUNT_NAME=Category。
/// <see cref="NotInTb"/> 為真代表「該科目在 GL 有、TB 無」(完整性 not-in-tb 集合);
/// 此時 sheet 的 GL_NAME 欄寫字面「Not in TB」而非 <see cref="AccountName"/>——
/// 由 emitter 依此旗標渲染(data-structure 對映,非逐列特判)。
/// </summary>
public sealed record AccountMappingExportRow(
    string AccountCode,
    string? AccountName,
    string Category,
    bool NotInTb);

/// <summary>
/// 科目配對範本列(產空白範本供審計員填分類):母體＝GL∪TB 完整性 diff 的科目清單。
/// A=<see cref="AccountCode"/>(diff.account_code)、B=<see cref="AccountName"/>(diff.account_name,
/// GL∪TB 的名稱;GL-only 科目寫其 GL 名稱而**非**字面「Not in TB」——審計員要靠名稱辨識科目才能分類,
/// 此為對匯出 sheet 15「GL-only 寫 Not in TB」的**刻意偏離**(理由見 guide §2.3 / design §2)。
/// C 欄(分類)由 writer 留空 + 下拉,不在此 model(範本產出前尚未配對)。
/// </summary>
public sealed record AccountMappingTemplateRow(
    string AccountCode,
    string? AccountName);

/// <summary>
/// 科目配對的唯讀全列匯出查詢(匯出底稿 sheet 15)。回 <see cref="target_account_mapping"/> 每一列
/// 並附上 not-in-tb 旗標(以 <c>JET.AuditCore.ValidationProcedures.CompletenessDiffCte</c> 的
/// not_in_tb 為單一事實來源:GL 有 TB 無 = 1)。科目數有界(實務數百),故回完整清單、不分頁
/// (對有界基數加分頁是過度工程化;鏡射 <see cref="ICreatorSummaryExportRepository"/>)。
///
/// 為什麼新立唯讀 repo 而非擴充 <see cref="IAccountMappingStore"/>:後者是匯入(replace-only)專用,
/// 匯出走 WorkpaperWriter 既有的「唯讀 repo 注入」管線(三 provider + ProviderRouting),兩者關注點不同。
/// </summary>
public interface IAccountMappingExportRepository
{
    /// <summary>periodStart/periodEnd 界定完整性 not-in-tb 判定的 GL 母體本期口徑（§2；與 CTE 8 消費端一致）。</summary>
    Task<IReadOnlyList<AccountMappingExportRow>> FetchAllAsync(
        string projectId,
        string periodStart,
        string periodEnd,
        CancellationToken cancellationToken);

    /// <summary>
    /// 科目配對範本的母體讀取(產空白範本用):回 GL∪TB 完整性 diff 的每個科目
    /// (<c>JET.AuditCore.ValidationProcedures.CompletenessDiffCte</c> 的 diff CTE),依 account_code 升冪。
    /// 與 <see cref="FetchAllAsync"/> 的差異:那讀 target_account_mapping(已配對,供匯出 sheet 15),
    /// 這讀 diff(尚未配對的母體,供產範本讓審計員填 C 欄)——不同關注點,故新方法而非改既有。
    /// periodStart/periodEnd 界定 GL 母體本期口徑(§2;與 CTE @periodStart/@periodEnd 一致)。
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
/// 缺逐日讀回 API;匯出需逐日列出,走 WorkpaperWriter 既有唯讀 repo 注入管線(三 provider)。
/// </summary>
public interface ICalendarExportRepository
{
    Task<IReadOnlyList<CalendarDayEntry>> FetchDaysAsync(
        string projectId,
        CalendarDayType type,
        CancellationToken cancellationToken);
}
