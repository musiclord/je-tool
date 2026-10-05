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
}

/// <summary>可跨 bridge 使用的結構化錯誤欄位名稱。</summary>
public static class JetErrorFields
{
    public const string CaseName = "caseName";
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
    public const string TaxonomyRevisionConflict = "taxonomy_revision_conflict";
    public const string TaxonomyCategoryInUse = "taxonomy_category_in_use";
    public const string UnsupportedMode = "unsupported_mode";
    public const string TableNotAllowed = "table_not_allowed";
    public const string NoTargetData = "no_target_data";
    public const string CompletenessPrerequisiteFailed = "completeness_prerequisite_failed";
    public const string InvalidScenario = "invalid_scenario";
    public const string ScenarioLimitReached = "scenario_limit_reached";
    public const string StaleResult = "stale_result";
    public const string ArtifactNotFound = "artifact_not_found";
    public const string SupportLogExportFailed = "support_log_export_failed";
    public const string GlAmountsAllZero = "gl_amounts_all_zero";
    public const string EmptyEffectivePopulation = "empty_effective_population";

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
