namespace JET.Domain;

/// <summary>
/// 正式工作底稿可匯出的工作表名稱與固定順序。Application 以此驗證 wire payload，
/// Infrastructure writer 以同一份名稱建立工作表，避免跨層各自維護字串。
/// </summary>
public static class WorkpaperSheetCatalog
{
    public const string Cover = "資料預先整理之說明";
    public const string Intro = "JE WorkingPaper說明";
    public const string Step1 = "step1 完整性測試";
    public const string Step11 = "step1-1 借貸不平測試";
    public const string Step12 = "step1-2 分錄編製人員說明";
    public const string Step13 = "step1-3 完整性測試之差異說明";
    public const string Step2 = "step2 可靠性測試";
    public const string Step3 = "step3 高風險條件彙總";
    public const string Step4 = "step4 符合高風險條件傳票";
    public const string Step41 = "step4-1 符合高風險條件傳票明細";
    public const string Step5 = "step5 財務報表關帳後調整之分錄";
    public const string FieldInfo = "自動化工具-檔案欄位資訊";
    public const string CalendarInfo = "自動化工具-假期假日資訊";
    public const string AccountMapping = "自動化工具-科目配對資訊";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly<string>(
    [
        Cover,
        Intro,
        Step1,
        Step11,
        Step12,
        Step13,
        Step2,
        Step3,
        Step4,
        Step41,
        Step5,
        FieldInfo,
        CalendarInfo,
        AccountMapping
    ]);
}

/// <summary>
/// 匯出底稿(WorkingPaper)寫出所需的專案層脈絡。封面/各表表頭取自此(公司名、測試期間)、
/// 金額顯示換算取 <see cref="MoneyScale"/>(scaled 整數 → 顯示值)、條件表(完整性差異調節)
/// 需上期起日。正式底稿固定輸出完整方法學工作表；使用者只選擇要納入的已存情境。
/// Validation 與 filter reference 必須由 handler 驗證為目前有效版本後傳入，writer 不隱含選「最新」。
/// 純資料載體,框架無關(Domain)。
/// </summary>
public sealed record WorkpaperContext(
    string ProjectId,
    string CompanyName,
    string PeriodStart,
    string PeriodEnd,
    string? LastPeriodStart,
    int MoneyScale,
    string ValidationRunId,
    string ScenarioRevision,
    IReadOnlyList<int> ScenarioPositions,
    // Public compatibility carrier retained so older callers keep the same record shape.
    // Production finalized plans and writers ignore it; visible step3 is fixed to B:E.
    IReadOnlyDictionary<int, string>? ScenarioConditionLogic = null,
    GlPopulationScope PopulationScope = GlPopulationScope.AuditPeriod);

/// <summary>匯出底稿單張工作表的寫出統計:工作表名與資料列數(表頭/固定文字列不計入 RowsWritten)。</summary>
public sealed record SheetStat(string SheetName, long RowsWritten);

/// <summary>匯出底稿的整體寫出統計:總位元組數 + 各工作表列數(回給前端做完成回饋)。</summary>
public sealed record ExportStats(long BytesWritten, IReadOnlyList<SheetStat> SheetStats);

/// <summary>每完成一張底稿工作表後的非權威進度快照。</summary>
public sealed record WorkpaperProgress(string SheetName, int SheetsCompleted, long RowsWritten);

/// <summary>
/// 全編製人員彙總的單列(匯出底稿 step1-2 用):編製人員、傳票筆數、借/貸金額彙總(scaled 整數)。
/// 與 prescreen 的 <see cref="CreatorSummaryRow"/> 區別:那個截 50 列且帶人工筆數(預篩選摘要用);
/// 這個是不截斷的全名單,step1-2 需列出每一位(含自動拋轉的傳票類型)。
/// </summary>
public sealed record CreatorSummaryExportRow(
    string CreatedBy,
    long EntryCount,
    long DebitTotalScaled,
    long CreditTotalScaled);

/// <summary>
/// 不截斷的全編製人員彙總查詢(匯出底稿 step1-2)。鏡射既有 prescreen creator 彙總 SQL 但去掉 LIMIT 50:
/// step1-2 要列出每一位編製人員。distinct created_by 基數有界(人員數 + 自動拋轉傳票類型,實務數十~數百),
/// 故回完整清單即可、**不需分頁**——對有界基數加分頁是過度工程化。
/// 與 <see cref="IPrescreenRunRepository"/> 同樣放 Domain(查詢 row + 介面相鄰);實作三 provider 在 Infrastructure。
/// </summary>
public interface ICreatorSummaryExportRepository
{
    /// <summary>periodStart/periodEnd 界定編製人員彙總的本期母體（與 prescreen.run 編製者彙總同口徑）。</summary>
    Task<IReadOnlyList<CreatorSummaryExportRow>> FetchAllAsync(
        string projectId,
        string periodStart,
        string periodEnd,
        CancellationToken cancellationToken);
}
