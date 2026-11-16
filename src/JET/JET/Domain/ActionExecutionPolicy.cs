namespace JET.Domain;

/// <summary>
/// 每個 action 要不要和其他作業互斥，分類只寫在這裡。dispatcher 依這份分類決定要不要先取得共用的閘。
/// 這個類別不依賴任何框架，所以 Bridge 可以直接引用 Domain 的它。
/// </summary>
/// <remarks>
/// 分類原則：
/// <list type="bullet">
///   <item><b>變更型（exclusive）</b>：會改專案資料庫或專案中繼資料的動作，以及耗時的匯出。
///   同一時間最多執行一項；第二項回 <see cref="JetErrorCodes.OperationInProgress"/>。</item>
///   <item><b>由 handler 自己取閘（conditional gate）</b>：handler 自己試著取共用的閘，取不到不等待。
///   這樣不做事或只讀取的分支不必取閘，真正改資料的部分則整段包在閘裡。</item>
///   <item><b>併行（concurrent）</b>：唯讀的查詢、檢視、計算與選檔案，可以和其他作業同時進行，不取閘。
///   其中只有 <c>log.append</c> 會寫目前案件的資料庫，而且只寫 <c>message_log</c>。</item>
/// </list>
/// 四支篩選命中查詢歸在併行，因為讀取目前的結果不必寫入。只有結果已標成失效、要在查詢時補算的那個分支會寫入，
/// 這個分支另外試著取共用的 <see cref="ActionExecutionGate"/>；取不到就回 operation_in_progress。
/// <c>project.releaseLock</c> 放開案件鎖並離開 session 的整段動作，由 handler 取同一把閘；
/// dispatcher 不先替它取閘，因為這把閘不能重複取得，先取了 handler 就會卡住自己。
/// 新增 action 時一定要在這裡歸類：測試會走過 dispatcher 註冊的每個 action，用 <see cref="IsClassified"/> 檢查，
/// 沒有歸類就失敗。執行時遇到沒有歸類的 action，<see cref="IsExclusive"/> 回 true，當成變更型處理，寧可多擋。
/// </remarks>
public static class ActionExecutionPolicy
{
    /// <summary>
    /// 資料庫的 async API 可能同步執行。除原生主機介面及取消控制外，dispatcher 一律在背景執行，
    /// 包含唯讀查詢內的惰性補算；是否互斥仍由原有分類判定。
    /// </summary>
    public static bool RunsInBackground(string action) => action is not
        ("host.selectFile" or "host.selectFiles" or "host.openFolder" or "host.exitApp" or "operation.cancel");

    // 變更型：同一時間至多一項執行。
    private static readonly HashSet<string> ExclusiveActions = new(StringComparer.Ordinal)
    {
        "project.create",
        "project.load",
        "project.delete",
        "project.saveProgress",
        "project.update",
        "accountCategory.save",
        "accountMapping.save",
        "import.gl.fromFile",
        "import.tb.fromFile",
        "import.accountMapping.fromFile",
        "import.authorizedPreparer.fromFile",
        "import.authorizedPreparer.clear",
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
        "filter.scenario.save",
        "filter.scenario.remove",
        "filter.scenario.recheck",
        "export.validationReports",
        "export.prescreenReport",
        "export.criteriaSelectionReport",
        "export.workingPaperStream",
        "export.accountMappingTemplate",
    };

    // handler 自行試取共用閘；不得同時放進 ExclusiveActions，否則 dispatcher 先取後會自我阻塞。
    private static readonly HashSet<string> ConditionalGateActions = new(StringComparer.Ordinal)
    {
        JetActionNames.ProjectReleaseLock,
    };

    // 併行：可與變更型作業併行，不佔閘（log.append 是唯一 current-project DB bounded-write 例外）。
    private static readonly HashSet<string> ConcurrentActions = new(StringComparer.Ordinal)
    {
        "system.ping",
        "system.whoAmI",
        "project.listLocal",
        "project.deletePreview",
        "operation.cancel",     // 變更型作業進行中也要能執行，才能取消正在執行的那個請求

        "project.loadDemo", // 只取 demo metadata，尚未建案
        "demo.exportGlFile",
        "demo.exportTbFile",
        "demo.exportAccountMappingFile",
        "demo.exportAuthorizedPreparerFile",
        "import.inspectFile",
        "import.previewFile",
        "mapping.restoreDraft",
        "mapping.valueProfile",
        "filter.preview",
        "query.dataPreview",
        "query.completenessDiffPage",
        "query.voucherBalancePage",
        "query.blankRecordsPage",
        "query.sourceQualityPage",
        "query.filterHitsPage",
        "query.filterVoucherPage",
        "query.filterVoucherRowsPage",
        "query.prescreenPage",
        "query.infSamplePage",
        "query.highRiskSummaryScenarios",
        "query.highRiskSummaryVoucherPage",
        "query.highRiskSummaryRowPage",
        "query.accountMappingBlankPage",
        "query.accountMappingDifferencePage",
        "query.accountMappingPage",
        "log.append", // 唯一 current-project DB concurrent 寫入例外：只寫 message_log，不碰案件資料
        "log.recent",
        "host.selectFile",
        "host.selectFiles",
        "host.openFolder",
        "host.exitApp",
        "dev.db.overview",
        "dev.db.tableData",
        "dev.log.exportFile", // 讀 sink 檔／ring buffer、寫選定案件目錄中的獨立 DEV 文字檔
        "support.log.export", // 寫選定案件資料夾中的獨立支援文字檔，不改業務資料或報告清單
    };

    // 確實會開當前案件資料庫的 action。其中的變更型作業結束後，dispatcher 把記錄檔併回（見 CheckpointsProjectDatabase）；
    // 沒列到就不併回。project.load 由 handler 自己處理開案失敗；project.create、project.delete、project.releaseLock
    // 會建立、刪除或離開案件，不在其中。
    private static readonly HashSet<string> ProjectDatabaseActions = new(StringComparer.Ordinal)
    {
        // 變更型
        "project.update",
        "accountCategory.save",
        "accountMapping.save",
        "import.gl.fromFile",
        "import.tb.fromFile",
        "import.accountMapping.fromFile",
        "import.authorizedPreparer.fromFile",
        "import.authorizedPreparer.clear",
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
        "filter.scenario.save",
        "filter.scenario.remove",
        "filter.scenario.recheck",
        "export.validationReports",
        "export.prescreenReport",
        "export.criteriaSelectionReport",
        "export.workingPaperStream",
        "export.accountMappingTemplate",

        // 併行
        "mapping.restoreDraft",
        "mapping.valueProfile",
        "filter.preview",
        "query.dataPreview",
        "query.completenessDiffPage",
        "query.voucherBalancePage",
        "query.blankRecordsPage",
        "query.sourceQualityPage",
        "query.filterHitsPage",
        "query.filterVoucherPage",
        "query.filterVoucherRowsPage",
        "query.prescreenPage",
        "query.infSamplePage",
        "query.highRiskSummaryScenarios",
        "query.highRiskSummaryVoucherPage",
        "query.highRiskSummaryRowPage",
        "query.accountMappingBlankPage",
        "query.accountMappingDifferencePage",
        "query.accountMappingPage",
        "log.recent",
        "dev.db.overview",
        "dev.db.tableData",
    };

    /// <summary>
    /// 這個 action 是否會開當前案件的資料庫。沒列到的一律視為不開。
    /// </summary>
    public static bool OpensProjectDatabase(string action)
        => ProjectDatabaseActions.Contains(action);

    /// <summary>全部會開當前案件資料庫的 action；給測試檢查用。</summary>
    public static IReadOnlyCollection<string> ProjectDatabaseActionNames
        => [.. ProjectDatabaseActions];

    /// <summary>
    /// dispatcher 是否在這個 action 結束後把當前案件資料庫的記錄檔併回（見 IProjectDatabaseCheckpoint）：
    /// 會開當前案件資料庫的變更型作業，成功、失敗或取消都併回。建案、開案、刪案與離開案件不在其中，
    /// 離開案件與開案失敗由 handler 自己關閉資料庫。
    /// </summary>
    public static bool CheckpointsProjectDatabase(string action)
        => ExclusiveActions.Contains(action) && ProjectDatabaseActions.Contains(action);

    /// <summary>此 action 是否為變更型，同一時間只能執行一項。沒有歸類的 action 也回 true，當成變更型處理。</summary>
    public static bool IsExclusive(string action)
        => ExclusiveActions.Contains(action) || !IsClassified(action);

    /// <summary>此 action 是否由 handler 在真正 mutation 邊界自行非阻塞試取共用閘。</summary>
    public static bool RequiresConditionalGate(string action)
        => ConditionalGateActions.Contains(action);

    /// <summary>此 action 是否已被明確歸類。測試用它檢查，新增的 action 沒有歸類時測試會失敗。</summary>
    public static bool IsClassified(string action)
        => ExclusiveActions.Contains(action)
            || ConditionalGateActions.Contains(action)
            || ConcurrentActions.Contains(action);
}
