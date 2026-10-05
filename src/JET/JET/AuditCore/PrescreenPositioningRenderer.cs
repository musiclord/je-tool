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
            "查看編製人員與較少使用科目的分錄筆數及金額。",
        SignalGuidance:
            "符合條件的分錄供初步查核；請在「進階條件篩選」設定本案的測試範圍。",
        ReportGuidance:
            "可在此產生預篩選報告，或在匯出底稿時一併產生。不產生也可繼續篩選與匯出底稿。",
        OverviewGuidance:
            "預篩選呈現查核期間分錄的分布與符合條件的分錄，是否需進一步查核由審計員判斷。",
        ExportDefaultGuidance:
            "預設一併產出預篩選報告；取消勾選不影響其他報告及底稿內容。",
        ExportPendingRunGuidance:
            "尚無預篩選結果，匯出時會先執行預篩選。大型案件可能需要數分鐘到十餘分鐘。");
}
