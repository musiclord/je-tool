namespace JET.AuditCore;

/// <summary>
/// 預篩選在使用者介面上的定位文案。這些句子會影響審計解讀，因此由 AuditCore
/// 形成唯一權威輸出；Application 只轉成 wire，前端只鏡射並負責版面。
/// </summary>
internal sealed record PrescreenPositioning(
    string AggregateGuidance,
    string SignalGuidance,
    string ReportGuidance,
    string OverviewGuidance,
    string ExportDefaultGuidance,
    string ExportPendingRunGuidance);

internal static class PrescreenPositioningRenderer
{
    internal static PrescreenPositioning Render() => new(
        AggregateGuidance:
            "先看依分錄編製者與較少使用科目的全期彙總；這兩項是常用的母體判讀面。",
        SignalGuidance:
            "逐筆命中只供初步判讀，不是高風險裁定；要形成測試範圍，請到「進階條件篩選」組合 KCT 與其他條件。",
        ReportGuidance:
            "Pre-screening Report 預設隨匯出底稿一併產出，這裡可以先單獨產生；不產生也不影響進階條件篩選、Criteria Selection Report 或 Working Paper。",
        OverviewGuidance:
            "彙總只描述母體分布；逐筆命中不等於錯誤，也不是高風險裁定；兩者都不代替審計判斷。",
        ExportDefaultGuidance:
            "匯出底稿時預設一併產出 Pre-screening Report；取消勾選只會少這一份，其餘報告與底稿內容都不受影響。",
        ExportPendingRunGuidance:
            "目前沒有可用的預篩選結果。維持勾選並按下產生，系統會先執行一次預篩選再產出這份報告；大型案件的預篩選可能需要數分鐘到十餘分鐘。");
}
