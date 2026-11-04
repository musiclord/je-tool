namespace JET.Domain;

/// <summary>
/// 跨層共用的業務錯誤契約（port 契約的一部分，與 repository 介面同屬 Domain）。
/// Bridge 會將 Code 直接放入 response error.code。
/// Application handler 與 Infrastructure 實作可拋；Domain 規則程式碼本身不拋（以 result record 回報）。
/// </summary>
public sealed class JetActionException(
    string code,
    string message,
    string? field = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;

    /// <summary>
    /// 只有錯誤能由後端明確歸屬到 payload 欄位時才提供；前端不得由 code 或訊息推測。
    /// </summary>
    public string? Field { get; } = field;

    /// <summary>
    /// 逐條錯誤與其位置。<c>invalid_scenario</c> 帶情境裡的第幾組、第幾條，前端把該列標紅並就地說原因；
    /// 欄位配對的 <c>projection_failed</c> 每項是一組問題並帶來源欄，第三步逐組列出。沒有時維持整段訊息。
    /// </summary>
    public IReadOnlyList<JetErrorDetail>? Details { get; init; }
}

/// <summary>一條可歸屬位置的錯誤；Group 與 Rule 從 1 起算，無法歸屬時為 null。</summary>
public sealed record JetErrorDetail(int? Group, int? Rule, string Message)
{
    /// <summary>
    /// 欄位配對無法轉換時，這一組問題出在哪個來源欄；第三步用它找到畫面上的設定位置。其他錯誤為 null。
    /// </summary>
    public string? SourceColumn { get; init; }

    /// <summary>可定位設定的原因代碼；前端不從中文訊息推測錯誤原因。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasonCode { get; init; }

    /// <summary>
    /// 篩選情境的 <c>invalid_scenario</c> 才帶：這條錯誤屬於清單上第幾個情境（從 1 起算）。
    /// 前端只在它是正在編輯的情境時把條件列標紅；其他錯誤為 null 且不輸出。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? ScenarioPosition { get; init; }

    /// <summary>與 <see cref="ScenarioPosition"/> 同時出現：那個情境的名稱，讓畫面說得出是哪個情境。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ScenarioName { get; init; }
}

/// <summary>
/// 畫面照正常操作不會送出的資料被後端拒絕時用的錯誤。畫面只看到非預期錯誤那一句，錯誤代碼不變；
/// 哪一項檢查沒過只用固定的英文代號記進支援日誌，不帶使用者資料、本機路徑或來源欄名。
/// 這類錯誤出現時只會是程式送錯資料或存檔損毀，審計員在畫面上無從修正，細節只對查原因有用。
/// </summary>
public static class JetPayloadRejection
{
    /// <summary>非預期錯誤回給畫面的句子（用字依裁定 2026-10-13-28）；畫面程式 jet-api.js 有同一句。</summary>
    public const string UnexpectedErrorMessage = "發生非預期的錯誤，此操作未完成。請再試一次。";

    private const string CheckDataKey = "jet.payload_check";

    /// <summary>check 只能是程式裡寫死的代號，不能代入 payload 內容。</summary>
    public static JetActionException Create(string code, string check)
    {
        var exception = new JetActionException(code, UnexpectedErrorMessage);
        exception.Data[CheckDataKey] = check;
        return exception;
    }

    public static string? CheckOf(Exception exception) =>
        exception is JetActionException ? exception.Data[CheckDataKey] as string : null;
}

/// <summary>
/// 篩選條件編譯時的檢查沒過：驗證器已先檢查過，走到這裡表示驗證器與執行時檢查不一致，是程式錯誤，
/// 不是畫面送錯資料（用字依裁定 2026-10-14-01）。畫面只看到非預期錯誤那一句，錯誤代碼仍是
/// <c>invalid_scenario</c>；程式寫死的檢查代號與組號、條號記進支援日誌事件 <c>filter.compile_check_failed</c>，
/// 不帶欄位名稱、輸入值、科目、金額或路徑。
/// </summary>
public static class FilterCompileCheck
{
    private const string CheckKey = "jet.filter_compile_check";
    private const string GroupKey = "jet.filter_compile_group";
    private const string RuleKey = "jet.filter_compile_rule";

    /// <summary>check 只能是程式裡寫死的代號，不能代入條件內容。</summary>
    public static JetActionException Create(string check)
    {
        var exception = new JetActionException(JetErrorCodes.InvalidScenario, JetPayloadRejection.UnexpectedErrorMessage);
        exception.Data[CheckKey] = check;
        return exception;
    }

    public static string? CheckOf(Exception exception) =>
        exception is JetActionException ? exception.Data[CheckKey] as string : null;

    /// <summary>記下最外層條件的組號與條號（只記第一次）；一律回傳 false，給例外過濾器使用而不攔下例外。</summary>
    public static bool Locate(Exception exception, int groupNo, int ruleNo)
    {
        if (CheckOf(exception) is not null && !exception.Data.Contains(GroupKey))
        {
            exception.Data[GroupKey] = groupNo;
            exception.Data[RuleKey] = ruleNo;
        }

        return false;
    }

    public static (int? Group, int? Rule) PositionOf(Exception exception) =>
        (exception.Data[GroupKey] as int?, exception.Data[RuleKey] as int?);
}

/// <summary>
/// 找不到案件資料夾，以及舊版 JET 建立的案件時的句子（用字依裁定 2026-10-14-01）。
/// 同一種情況全程式只用這幾句，不印程式代號，也不寫「可能」。
/// </summary>
public static class ProjectFolderMessages
{
    /// <summary>案件開啟中，案件資料夾被移走或刪除後再做任何查詢或操作。</summary>
    public const string ActiveProjectMissing =
        "找不到目前案件的資料夾，此操作未完成。請回首頁，確認案件資料夾仍在原來的位置後重新開啟案件。";

    /// <summary>案件資料夾的路徑格式無效。</summary>
    public const string InvalidPath = "案件資料夾的路徑無效，此操作未完成。請回首頁重新開啟案件。";

    /// <summary>開啟案件時，案件清單上的案件資料夾已不在。</summary>
    public static string CannotOpen(string projectName) => Missing(projectName, "開啟");

    /// <summary>刪除案件時，案件清單上的案件資料夾已不在。</summary>
    public static string CannotDelete(string projectName) => Missing(projectName, "刪除");

    /// <summary>案件資料庫的版本值缺漏、讀不出整數，或比目前版本還新。</summary>
    public const string UnreadableDatabaseVersion =
        "這個案件資料庫的版本資訊無法辨識，目前版本無法開啟。請從備份還原案件資料夾，或用目前版本重新建立案件，再重新匯入資料。";

    /// <summary>
    /// 舊版 JET 建立的案件。案件設定檔、案件資料庫與欄位配對讀到舊版內容時都用這一句，下一步是刪除後重建
    /// （裁定 2026-10-30-23：案件資料庫只留現行結構，版本不符時請審計員刪除後重建）。讀不到案件名稱時寫「這個案件」。
    /// </summary>
    public static string OldVersionProject(string? projectName = null) =>
        (string.IsNullOrEmpty(projectName) ? "這個案件" : $"案件「{projectName}」")
        + "是舊版 JET 建立的，目前版本無法開啟。請在案件清單刪除這個案件，再用目前版本重新建立並匯入資料。";

    private static string Missing(string projectName, string verb) =>
        $"案件「{projectName}」的資料夾已不在 JET 的案件位置，無法{verb}。請重新整理案件清單，並確認案件資料夾仍在原來的位置。";
}

/// <summary>可跨 bridge 使用的結構化錯誤欄位名稱。</summary>
public static class JetErrorFields
{
    public const string ProjectName = "projectName";
}

/// <summary>錯誤碼註冊表。docs/action-contract-manifest.md 以此處為準，不另列清單。</summary>
public static class JetErrorCodes
{
    public const string BridgeError = "bridge_error";
    public const string InvalidPayload = "invalid_payload";
    public const string NoActiveProject = "no_active_project";
    public const string ProjectNotFound = "project_not_found";
    public const string InvalidProjectSchema = "invalid_project_schema";
    public const string UnsupportedProvider = "unsupported_provider";
    public const string FileNotFound = "file_not_found";
    public const string UnsupportedFileType = "unsupported_file_type";
    public const string ImportProgressFailed = "import_progress_failed";
    public const string FileReadError = "file_read_error";
    public const string SheetNotFound = "sheet_not_found";
    public const string EmptyWorkbook = "empty_workbook";
    public const string NoImportBatch = "no_import_batch";
    public const string ColumnMismatch = "column_mismatch";
    public const string MissingRequiredMapping = "missing_required_mapping";
    public const string MappingColumnNotFound = "mapping_column_not_found";
    public const string MappingMetadataMissing = "mapping_metadata_missing";
    public const string MappingMetadataInvalid = "mapping_metadata_invalid";
    public const string ProjectionFailed = "projection_failed";
    public const string CategoryRevisionConflict = "category_revision_conflict";
    public const string CategoryInUse = "category_in_use";
    public const string UnsupportedMode = "unsupported_mode";
    public const string TableNotAllowed = "table_not_allowed";
    public const string GlMappingNotConfirmed = "gl_mapping_not_confirmed";
    public const string CompletenessPrerequisiteFailed = "completeness_prerequisite_failed";
    public const string InvalidScenario = "invalid_scenario";

    /// <summary>
    /// 還沒有已儲存的篩選情境，需要情境的動作無法執行。畫面把它當成前置條件不足，寫「尚無法執行」
    /// （用字依裁定 2026-10-14-01）。
    /// </summary>
    public const string NoSavedScenario = "no_saved_scenario";
    public const string ScenarioLimitReached = "scenario_limit_reached";
    public const string StaleResult = "stale_result";
    public const string ReportNotFound = "report_not_found";
    public const string SupportLogExportFailed = "support_log_export_failed";
    public const string GlAmountsAllZero = "gl_amounts_all_zero";
    public const string EmptyGl = "empty_gl";

    /// <summary>當前使用者對此線上（sqlServer）案件無存取權（project.load／project.delete 授權前置）。</summary>
    public const string NotAuthorized = "not_authorized";

    /// <summary>
    /// 案件由另一持有人開啟時回此碼：SQL Server 代表未過期租約，本地代表另一程序持有排他檔案 handle。
    /// project.load 與本地 project.delete 都會使用；訊息含持鎖者／機器／開啟時間。
    /// </summary>
    public const string ProjectLocked = "project_locked";

    /// <summary>
    /// 已有一項變更型作業進行中，又收到第二項變更型作業，或 concurrent query 的空結果補算分支
    /// 取不到共用閘時回此碼（非阻塞試取、不排隊）。變更型／唯讀分類見 <see cref="ActionExecutionPolicy"/>；
    /// wire 契約見 docs/action-contract-manifest.md。
    /// </summary>
    public const string OperationInProgress = "operation_in_progress";

    /// <summary>在途作業於 cooperative cancellation point 回應取消。</summary>
    public const string OperationCancelled = "operation_cancelled";

    // 本地引擎錯誤映射（單一映射點在 LocalEngineErrors、由 dispatcher 統一轉譯）。
    /// <summary>本地資料庫暫時忙碌，另一項交易仍占用寫入資源。</summary>
    public const string DatabaseBusy = "database_busy";
    /// <summary>本地資料庫檔案被另一個程序排他鎖定。</summary>
    public const string DatabaseLocked = "database_locked";
    /// <summary>專案所在儲存空間不足，資料庫無法繼續寫入。</summary>
    public const string DatabaseStorageFull = "database_storage_full";
    /// <summary>本地資料庫檔案損壞或不是有效資料庫。</summary>
    public const string DatabaseCorrupt = "database_corrupt";

    // SQL Server 引擎錯誤映射（單一映射點在 SqlServerEngineErrors、由 dispatcher 統一轉譯）。
    /// <summary>選用 SQL Server，但連線或單一資料庫名尚未設定。</summary>
    public const string SqlServerNotConfigured = "sql_server_not_configured";
    /// <summary>連線目標是已淘汰的 SQL Server Express（含 LocalDB）。</summary>
    public const string SqlServerExpressUnsupported = "sql_server_express_unsupported";
    /// <summary>SQL Server 登入失敗（引擎錯誤 18456）。</summary>
    public const string SqlServerLoginFailed = "sql_server_login_failed";
    /// <summary>唯一鍵／主鍵衝突（引擎錯誤 2601/2627）。</summary>
    public const string DuplicateKey = "duplicate_key";
    /// <summary>死鎖犧牲者（引擎錯誤 1205；有限次重試後仍失敗才上 wire）。</summary>
    public const string SqlServerDeadlock = "sql_server_deadlock";
    /// <summary>SQL Server 執行逾時（用戶端逾時 -2／WAIT_TIMEOUT 258）。</summary>
    public const string SqlServerTimeout = "sql_server_timeout";
}
