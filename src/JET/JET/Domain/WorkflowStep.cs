namespace JET.Domain;

/// <summary>
/// 使用者在畫面依序操作的六個步驟：建立案件、匯入資料、欄位配對、資料驗證與測試、進階條件篩選、匯出底稿。
/// 值是畫面步驟從 0 起算的位置，和案件設定檔 currentStep 存的整數相同。
/// </summary>
public enum WorkflowStep
{
    Create = 0,
    Import = 1,
    Mapping = 2,
    Validate = 3,
    Filter = 4,
    Export = 5,
}
