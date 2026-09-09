namespace JET.Domain;

/// <summary>
/// 作業序列化的動作分類（單一事實來源）。dispatcher 依此判定是否對 action 上序列化閘。
/// 純類別、無框架相依（Bridge 引用 Domain 合法）。現行契約見
/// docs/action-contract-manifest.md 的 operation_in_progress 說明。
/// </summary>
/// <remarks>
/// 分類原則：
/// <list type="bullet">
///   <item><b>變更型（exclusive）</b>——改變專案資料庫／專案中繼資料的動作，或重量級匯出。
///   同一時間至多一項；第二項回 <see cref="JetErrorCodes.OperationInProgress"/>。</item>
///   <item><b>條件式取閘（conditional gate）</b>——action 由 handler 自行非阻塞試取共用閘，
///   讓 no-op／純讀分支保持原分類，同時把真正 mutation 包在同一原子區段。</item>
///   <item><b>併行（concurrent）</b>——唯讀查詢／檢視／計算／檔案選取可與作業併行、不佔閘；
///   文件化的唯一 current-project database 寫入例外是 <c>log.append</c>，其寫入範圍只限
///   <c>app_message_log</c>。</item>
/// </list>
/// 四支篩選命中查詢維持 concurrent，因為已有結果時仍是純讀。只有空結果觸發惰性補算的條件式
/// 寫入分支會另行試取共用 <see cref="ActionExecutionGate"/>；取不到即回 operation_in_progress。
/// <c>project.releaseLock</c> 則整個「放鎖＋離開 session」都由 handler 試取同一把閘；它不由 dispatcher
/// 預先取閘，避免非重入閘自我阻塞。
/// 新增 action 時必須在此明確歸類（<see cref="IsClassified"/> 有測試守衛，遍歷 dispatcher 已註冊 action，
/// 未歸類即紅）。未歸類的 action 在執行期以 <see cref="IsExclusive"/> 回 true（fail-safe：預設保護狀態）。
/// </remarks>
public static class ActionExecutionPolicy
{
    // 變更型：同一時間至多一項執行。
    private static readonly HashSet<string> ExclusiveActions = new(StringComparer.Ordinal)
    {
        "project.create",
        "project.load",
        "project.delete",
        "project.saveProgress",
        "accountTaxonomy.save",
        "import.gl.fromFile",
        "import.tb.fromFile",
        "import.accountMapping.fromFile",
        "import.authorizedPreparer.fromFile",
        "import.holiday",
        "import.makeupDay",
        "import.holiday.fromFile",
        "import.makeupDay.fromFile",
        "calendar.setNonWorkingDays",
        "mapping.commit.gl",
        "mapping.commit.tb",
        "validate.run",
        "prescreen.run",
        "filter.commit",
        "export.validationArtifacts",
        "export.prescreenReport",
        "export.criteriaSelectionReport",
        "export.workpaperStream",
        "export.accountMappingTemplate",
    };

    // handler 自行試取共用閘；不得同時放進 ExclusiveActions，否則 dispatcher 先取後會自我阻塞。
    private static readonly HashSet<string> ConditionalGateActions = new(StringComparer.Ordinal)
    {
        "project.releaseLock",
    };

    // 併行：可與變更型作業併行，不佔閘（log.append 是唯一 current-project DB bounded-write 例外）。
    private static readonly HashSet<string> ConcurrentActions = new(StringComparer.Ordinal)
    {
        "system.ping",
        "system.databaseInfo",
        "system.whoAmI",
        "project.listLocal",
        "project.list",
        "project.heartbeat",   // 背景心跳保活（控制面第六輪租約鎖）——絕不可被作業 busy 閘擋住
        "operation.cancel",     // 必須能穿過 exclusive busy 閘，取消正在執行的目標 request

        "project.loadDemo", // 只取 demo metadata，尚未建案
        "demo.exportGlFile",
        "demo.exportTbFile",
        "demo.exportAccountMappingFile",
        "demo.exportAuthorizedPreparerFile",
        "import.inspectFile",
        "import.previewFile",
        "mapping.autoSuggest",
        "mapping.restoreDraft",
        "mapping.valueProfile",
        "filter.preview",
        "query.dataPreview",
        "query.completenessDiffPage",
        "query.docBalancePage",
        "query.nullRecordsPage",
        "query.sourceQualityPage",
        "query.filterHitsPage",
        "query.filterVoucherPage",
        "query.filterVoucherRowsPage",
        "query.prescreenPage",
        "query.infSamplePage",
        "query.tagMatrixScenarios",
        "query.tagMatrixVoucherPage",
        "query.tagMatrixRowPage",
        "query.accountMappingBlankPage",
        "log.append", // 唯一 current-project DB concurrent 寫入例外：只寫 app_message_log，不碰案件資料
        "log.recent",
        "host.selectFile",
        "host.selectFiles",
        "host.selectSavePath",
        "host.openFolder",
        "host.exitApp",
        "dev.db.overview",
        "dev.db.tableData",
        "dev.db.reconcile",
        "dev.log.export",
        "dev.log.exportFile", // 讀 sink 檔／ring buffer、寫選定案件目錄中的獨立 DEV 文字檔
        "support.log.export", // 寫選定案件資料夾中的獨立支援文字檔，不改業務資料或 artifact catalog
    };

    /// <summary>此 action 是否為變更型（需序列化）。未歸類者 fail-safe 回 true。</summary>
    public static bool IsExclusive(string action)
        => ExclusiveActions.Contains(action) || !IsClassified(action);

    /// <summary>此 action 是否由 handler 在真正 mutation 邊界自行非阻塞試取共用閘。</summary>
    public static bool RequiresConditionalGate(string action)
        => ConditionalGateActions.Contains(action);

    /// <summary>此 action 是否已被明確歸類（測試守衛用；新增 action 未歸類即紅）。</summary>
    public static bool IsClassified(string action)
        => ExclusiveActions.Contains(action)
            || ConditionalGateActions.Contains(action)
            || ConcurrentActions.Contains(action);
}
