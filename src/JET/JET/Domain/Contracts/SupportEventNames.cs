namespace JET.Domain;

/// <summary>支援日誌裡會拿來判斷的事件名。寫入端與讀取端都用這裡的常數，不各自寫字串。</summary>
public static class SupportEventNames
{
    public const string ActionStart = "action.start";
    public const string ActionEnd = "action.end";
    public const string ActionError = "action.error";
    public const string ActionCancelled = "action.cancelled";
    public const string ActionStepFailed = "action.step_failed";
    public const string ActionPayloadRejected = "action.payload_rejected";
    public const string HistoryStarted = "support.history_started";
    public const string HistoryGap = "support.history_gap";
    public const string CaseOpened = "support.case_opened";
    // 開啟或建立另一個案件時，後端自己離開了前一個案件；記在離開的那個案件。
    public const string CaseLeft = "support.case_left";

    // 操作監看模組寫的事件：長時間操作、跑太久或沒有正常結束的 SQL，以及匯出進度。
    public const string ActionStillRunning = "action.still_running";
    public const string ActionSummary = "action.summary";
    public const string ActionStageEntered = "action.stage_entered";
    public const string SqlSlow = "sql.slow";
    public const string SqlCancelled = "sql.cancelled";
    public const string SqlFailed = "sql.failed";
    public const string SqlPlan = "sql.plan";
    public const string ExportProgress = "export.progress";

    // 處理紀錄模組寫不進案件資料夾的處理紀錄時記一筆；只有操作代號、哪一份紀錄、錯誤類型與還沒寫進去的筆數。
    public const string ProcessingLogWriteFailed = "processing_log.write_failed";
}
