namespace JET.Domain;

/// <summary>
/// 正式工作底稿可匯出的工作表名稱與固定順序。Application 以此驗證 wire payload，
/// Infrastructure writer 以同一份名稱建立工作表，避免跨層各自維護字串。
/// </summary>
public static class WorkingPaperSheetCatalog
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
public sealed record WorkingPaperContext(
    string ProjectId,
    string CompanyName,
    string PeriodStart,
    string PeriodEnd,
    string? PrepStartDate,
    int MoneyScale,
    string ValidationResultId,
    string? ScenarioRevision,
    IReadOnlyList<int> ScenarioPositions,
    GlPopulationScope PopulationScope = GlPopulationScope.AuditPeriod,
    // 案件設定的每週休息日(週日=0…週六=6);null 表示沒有設定,底稿週末表照預設寫週六、週日。
    IReadOnlyList<int>? NonWorkingDays = null);

/// <summary>匯出底稿單張工作表的寫出統計:工作表名與資料列數(表頭/固定文字列不計入 RowsWritten)。</summary>
public sealed record SheetStat(string SheetName, long RowsWritten);

/// <summary>匯出底稿的整體寫出統計:總位元組數 + 各工作表列數(回給前端做完成回饋)。</summary>
public sealed record ExportStats(long BytesWritten, IReadOnlyList<SheetStat> SheetStats);

/// <summary>每完成一張底稿工作表後的非權威進度快照。</summary>
public sealed record WorkingPaperProgress(string SheetName, int SheetsCompleted, long RowsWritten);

/// <summary>
/// 全編製人員彙總的單列(預篩選報告 R5 與匯出底稿 step1-2 用):編製人員、人工旗標、傳票筆數、借/貸金額彙總(scaled 整數)。
/// 與 prescreen 的 <see cref="PreparerSummaryRow"/> 區別:那個截 50 列且帶人工筆數(預篩選摘要用);
/// 這個是不截斷的全名單,照舊版依編製人員加人工旗標分列,同一位人員最多三列:自動、人工與未判定(裁定 2026-11-05-32)。
/// <see cref="IsManual"/> 是 true 人工、false 自動、null 未判定;沒有配對人工傳票欄位時全部是 null,也就是一人一列。
/// </summary>
public sealed record PreparerSummaryExportRow(
    string CreatedBy,
    long EntryCount,
    long DebitTotalScaled,
    long CreditTotalScaled,
    bool? IsManual = null);

/// <summary>
/// 編製人員彙總的顯示名稱：建立人員空白的那一組，預篩選報告、工作底稿 Step 1-2 與畫面一律寫「（空白）」
/// （畫面在 validate-step.js 與 overview-bi.js 用同一個詞）。
/// </summary>
public static class PreparerSummaryDisplay
{
    public const string BlankLabel = "（空白）";

    public static string NameOf(string? createdBy) =>
        string.IsNullOrWhiteSpace(createdBy) ? BlankLabel : createdBy;

    /// <summary>
    /// 人工旗標的寫法：照舊版 idea-tool.bas 第 10557 到 10562 行寫「人工建立分錄」「自動拋轉分錄」，
    /// 預篩選報告 R5 與底稿 Step 1-2 用同一組字（裁定 2026-11-05-32）。
    /// </summary>
    public const string ManualEntryText = "人工建立分錄";

    public const string AutomaticEntryText = "自動拋轉分錄";

    public static string ManualText(bool isManual) => isManual ? ManualEntryText : AutomaticEntryText;
}

/// <summary>
/// 不截斷的全編製人員彙總查詢(匯出底稿 step1-2)。鏡射既有 prescreen preparer 彙總 SQL 但去掉 LIMIT 50:
/// step1-2 要列出每一位編製人員。distinct created_by 基數有界(人員數 + 自動拋轉傳票類型,實務數十~數百),
/// 故回完整清單即可、**不需分頁**——對有界基數加分頁是過度工程化。
/// 查詢 row 與介面相鄰放在 Domain;實作三 provider 在 Infrastructure。
/// </summary>
public interface IPreparerSummaryExportRepository
{
    /// <summary>periodStart/periodEnd 界定編製人員彙總的本期母體（與 prescreen.run 編製者彙總同口徑）。</summary>
    Task<IReadOnlyList<PreparerSummaryExportRow>> FetchAllAsync(
        string projectId,
        string periodStart,
        string periodEnd,
        CancellationToken cancellationToken);
}
