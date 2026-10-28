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
}
