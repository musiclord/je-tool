namespace JET.AuditCore;

/// <summary>
/// 三層審計資料模型的「層」。承襲 legacy Excel-VBA 工具的 Source / ETL-Staging / Target-Report
/// 三層結構,外加純系統表的 System 層。
///
/// 注意:這是**審計語義的層**(資料從客戶提供 → 標準化暫存 → 可報導結果),不從實體表名推得。
/// 實體表名不帶分層字首,直接取畫面的名稱,或舊版工具(Excel-VBA 工具或 IDEA 版 JET)對這份資料的稱呼,
/// 例如 <c>account_mapping</c> 與 <c>calendar_day</c> 在審計語義上都是 Source(客戶提供的科目對照與假日曆)。
/// 故本列舉表達的是審計層,以正準名(canonical)而非 physical 名分層。
/// </summary>
internal enum SchemaLayer
{
    /// <summary>來源層:客戶提供的匯入資料(PBC-GL、PBC-TB、ACCOUNT_MAPPING…)。</summary>
    Source,

    /// <summary>暫存層(ETL):標準化後的 GL 與 TB。</summary>
    Staging,

    /// <summary>報表層:可供判讀 / 報導的測試結果摘要與命中(VALIDATION_OVERVIEW…)。</summary>
    Target,

    /// <summary>系統層:組態、匯入批次與純引擎用表,不屬審計三層資料流。</summary>
    System
}

/// <summary>
/// 實體表對「資料預覽 / 結構總覽」的曝光程度。決定下一個任務(資料預覽改顯正準名)該怎麼呈現,
/// 本任務只建目錄、不改前端。
/// </summary>
internal enum SchemaAudience
{
    /// <summary>可逐列瀏覽,且出現在結構總覽(審計工作流程會直接接觸的資料)。</summary>
    DataView,

    /// <summary>出現在結構總覽,但此處不開放逐列瀏覽(結果 / 組態,語義以摘要或專用頁呈現)。</summary>
    StructureOnly,

    /// <summary>純系統 / 暫存 scratch,完全不對外曝光。</summary>
    Hidden
}

/// <summary>
/// 中央三層表名登錄的單筆條目:實體表名 → 正準審計名 + 層 + 曝光 + 中文說明。
/// </summary>
/// <param name="PhysicalName">實體表名(CREATE TABLE 的權威名)。</param>
/// <param name="CanonicalName">正準審計名,只用於資料表摘要顯示(如 PBC-GL、GL)。資料名稱全系統統一,
/// legacy Excel-VBA 工具的 JE_PBC、TB_PBC、JE 對應 PBC-GL、PBC-TB、GL;不是 SQL 識別字或檔名。</param>
/// <param name="Layer">審計三層 + System。</param>
/// <param name="Audience">資料預覽 / 結構總覽的曝光程度。</param>
/// <param name="Description">一句中文說明(用途 / 由來)。</param>
internal sealed record SchemaTableEntry(
    string PhysicalName,
    string CanonicalName,
    SchemaLayer Layer,
    SchemaAudience Audience,
    string Description);

/// <summary>
/// 中央三層表名登錄——專案 DB 每一張實體表 → 正準審計名 / 層 / 曝光 / 說明的**單一事實來源**。
///
/// 為什麼存在:審計方法學承襲 legacy Excel-VBA 工具乾淨的三層結構(Source / ETL-Staging /
/// Target-Report)與審計詞彙表名(JE_PBC、JE、ACCOUNT_MAPPING…);現行實體表名是資料預覽名稱的轉寫
/// (<c>pbc_gl</c>、<c>gl</c>…)。
/// 本目錄把兩套名稱併列,讓資料預覽能以審計人員熟悉的正準名呈現。
///
/// 深模組:對外只給窄查詢介面(<see cref="All"/> / <see cref="ByAudience"/> /
/// <see cref="ResolveCanonical"/> / <see cref="TryGet"/>),內部是一份宣告式 <see cref="All"/> 表;
/// 新增規則 = 加一列,不是散落的 if。實體表清單以 Infrastructure 的 SchemaSql 為準逐一登錄,
/// 漏登錄時 Infrastructure 的漂移守門測試(查 sqlite_master 比對本目錄)會失敗。
///
/// 註:legacy Excel-VBA 工具的 <c>COMPLETENESS_CALCULATED / DIFF / DETAIL</c>、<c>JE_IN_PERIOD /
/// JE_NOT_IN_PERIOD</c>、各 <c>*_OVERVIEW</c> 是查詢時即算的**計算檢視(衍生,不落表)**
/// (CTE 於 ValidationProcedures、或 query 層的期間切分),不是實體表,故不在本目錄內。
/// </summary>
internal static class JetSchemaCatalog
{
    /// <summary>
    /// GL 與 GL(未納入測試) 共用的分錄表名稱。程式要把這張表的名稱當成值傳遞時用這個常數，不另寫字串 "gl"：
    /// 資料集種類的值也是 "gl"，用常數才分得出哪裡傳的是表名，見 GlExcludedPopulationReadGuardTests。
    /// </summary>
    public const string GlTableName = "gl";

    /// <summary>
    /// 宣告式登錄表:專案 DB 每一張實體表恰好一列。順序大致依資料流(來源 → 暫存 → 報表 → 系統)。
    /// </summary>
    public static readonly IReadOnlyList<SchemaTableEntry> All =
    [
        // ── Source:客戶提供 / 匯入原貌(DataView) ─────────────────────────────
        new("pbc_gl", "PBC-GL", SchemaLayer.Source, SchemaAudience.DataView,
            "PBC-GL（匯入的總帳明細原樣，未經欄位配對整理）"),
        new("pbc_tb", "PBC-TB", SchemaLayer.Source, SchemaAudience.DataView,
            "PBC-TB(匯入的試算表原樣,未標準化)"),
        new("account_mapping", "ACCOUNT_MAPPING", SchemaLayer.Source, SchemaAudience.DataView,
            "科目 → 標準化分類對照(未預期借貸組合 / 科目配對分析所需)"),
        new("authorized_preparer", "AUTHORIZED_PREPARER", SchemaLayer.Source, SchemaAudience.DataView,
            "授權編製人員清單(非授權編製人員規則所需)"),
        new("calendar_day", "DATE_DIMENSION", SchemaLayer.Source, SchemaAudience.DataView,
            "日期維度:事務所假日 / 補班日(週末假日過帳核准規則所需)"),

        // ── Staging(ETL):標準化後的測試母體與餘額(DataView) ──────────────────
        new(GlTableName, "GL、GL(未納入測試)", SchemaLayer.Staging, SchemaAudience.DataView,
            "標準化分錄(由 PBC-GL 投影而來;同一張表依查核期間與過帳狀態分成 GL 與 GL(未納入測試))"),
        new("tb", "TB", SchemaLayer.Staging, SchemaAudience.DataView,
            "標準化試算表餘額(本期變動額;完整性測試所需)"),

        // ── Target(Report):測試結果摘要與命中(StructureOnly) ────────────────
        new("validation_result", "VALIDATION_OVERVIEW", SchemaLayer.Target, SchemaAudience.StructureOnly,
            "資料驗證結果摘要(以摘要呈現,非逐列瀏覽)"),
        new("prescreen_result", "PRESCREEN_RESULT", SchemaLayer.Target, SchemaAudience.StructureOnly,
            "預篩選結果摘要(以摘要呈現,非逐列瀏覽)"),
        new("filter_hit", "FILTER_HITS", SchemaLayer.Target, SchemaAudience.StructureOnly,
            "進階篩選命中的行層落地(以高風險條件彙總與命中頁呈現)"),
        new("filter_hit_condition", "FILTER_HIT_CONDITIONS", SchemaLayer.Target, SchemaAudience.Hidden,
            "進階篩選命中傳票裡每筆分錄符合第幾組第幾條(只供傳票分錄的說明讀取,不在結構總覽列出)"),
        new("inf_sample", "INF_SAMPLE", SchemaLayer.Target, SchemaAudience.StructureOnly,
            "INF 抽樣抽中的分錄樣本(以專用頁呈現)"),

        // ── System:組態與匯入批次(StructureOnly) ────────────────────────────
        new("field_mapping", "FIELD_MAPPING_INFO", SchemaLayer.System, SchemaAudience.StructureOnly,
            "已完成的欄位對應(GL / TB 各一列;匯出底稿與 round-trip 所需)"),
        new("field_mapping_previous", "FIELD_MAPPING_PREVIOUS", SchemaLayer.System, SchemaAudience.Hidden,
            "重新匯入前最後一次確認的欄位對應(GL / TB 各一列;只在重開案件時帶回草稿,不參與計算)"),
        new("account_category", "ACCOUNT_CATEGORY", SchemaLayer.System, SchemaAudience.StructureOnly,
            "案件範圍的科目分類 ID、標籤、順序與審計語意角色"),
        new("account_category_path", "ACCOUNT_CATEGORY_PATH", SchemaLayer.System, SchemaAudience.Hidden,
            "本地分類本身與所有下層的關係，供階層條件查詢使用"),
        new("filter_scenario", "FILTER_CRITERIA", SchemaLayer.System, SchemaAudience.StructureOnly,
            "使用者著作的進階篩選情境(條件樹)"),
        new("import_batch", "IMPORT_BATCH", SchemaLayer.System, SchemaAudience.StructureOnly,
            "匯入批次(每個資料集一筆;來源檔資訊與列數)"),
        new("import_batch_source", "IMPORT_BATCH_SOURCE", SchemaLayer.System, SchemaAudience.StructureOnly,
            "匯入批次的多來源明細(一個資料集可由多檔 / 多工作表組成)"),

        // ── System / Staging:純系統或暫存 scratch(Hidden,完全不曝光) ──────────
        new("field_info", "FIELD_INFO", SchemaLayer.System, SchemaAudience.Hidden,
            "Legacy 等價欄位定義(source / post-mapping target；供 internal facts port 使用)"),
        new("gl_rde_field", "GL_RDE_FIELD", SchemaLayer.System, SchemaAudience.Hidden,
            "GL 動態 RDE 的 stable field ID、來源、標籤、型別與順序"),
        new("gl_control_total", "GL_CONTROL_TOTAL", SchemaLayer.System, SchemaAudience.Hidden,
            "存入筆數與合計核對的控制總數(單列中間計算;投影時落地供 validate 對值)"),
        new("result_stale_state", "RESULT_STALE_STATE", SchemaLayer.System, SchemaAudience.Hidden,
            "上游資料修改後，驗證、預篩選與篩選結果的過期狀態"),
        new("message_log", "MESSAGE_LOG", SchemaLayer.System, SchemaAudience.Hidden,
            "前端狀態與訊息的持久化(UX 輔助紀錄,非審計留痕)"),
        new("operation_log", "OPERATION_LOG", SchemaLayer.System, SchemaAudience.Hidden,
            "會改變或銷毀已保存審計資料之操作的最小 append-only 留痕"),
        new("schema_info", "SCHEMA_INFO", SchemaLayer.System, SchemaAudience.Hidden,
            "案件的結構版本(開案時核對是不是現行版本，舊版案件請審計員刪除後重建)"),
        new("pbc_account_mapping", "ACCOUNT_MAPPING_PBC", SchemaLayer.Staging, SchemaAudience.Hidden,
            "科目配對匯入原貌暫存(實體表不曝光;accountMapping 以固定三欄有界預覽)"),
        new("pbc_authorized_preparer", "AUTHORIZED_PREPARER_PBC", SchemaLayer.Staging, SchemaAudience.Hidden,
            "授權編製人員匯入原貌暫存(投影至 AUTHORIZED_PREPARER 後即不再對外;ETL scratch)"),
        new("gl_rde_value", "GL_RDE_VALUE", SchemaLayer.Staging, SchemaAudience.Hidden,
            "GL entry 的非空白 typed RDE 值(文字、日期或 scaled 金額)"),
    ];

    /// <summary>指定曝光程度的條目(資料預覽 / 結構總覽日後依此取表)。</summary>
    public static IEnumerable<SchemaTableEntry> ByAudience(SchemaAudience audience) =>
        All.Where(entry => entry.Audience == audience);

    /// <summary>指定審計層的條目。</summary>
    public static IEnumerable<SchemaTableEntry> ByLayer(SchemaLayer layer) =>
        All.Where(entry => entry.Layer == layer);

    /// <summary>實體表名 → 條目(序數比對;未登錄回 false,不臆造)。</summary>
    public static bool TryGet(string physicalName, out SchemaTableEntry entry)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.PhysicalName, physicalName, StringComparison.Ordinal))
            {
                entry = candidate;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    /// <summary>實體表名 → 正準審計名;未登錄回 null(呼叫端自行決定 fallback)。</summary>
    public static string? ResolveCanonical(string physicalName) =>
        TryGet(physicalName, out var entry) ? entry.CanonicalName : null;
}
