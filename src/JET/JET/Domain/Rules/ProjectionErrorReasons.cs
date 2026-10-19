namespace JET.Domain;

internal static class ProjectionErrorCodes
{
    internal const string ManualBlank = "manual_blank";
    internal const string ManualUnlisted = "manual_unlisted";
    internal const string ManualOverlap = "manual_overlap";
    internal const string DebitCreditBlank = "dc_blank";
    internal const string DebitCreditUnlisted = "dc_unlisted";
}

/// <summary>
/// GL 與 TB 投影失敗時給使用者看的理由。每句先說值有什麼問題，句號之後說使用者接下來能做什麼；
/// 不出現英文或內部名稱。ProjectionFailureText 以第一個句號分開兩段，同一欄同一種問題只寫一次處理方式，
/// 寫成「來源欄「X」有 N 列＜問題＞：值與列號。＜處理方式＞。」，所以問題那段要能接在「有 N 列」後面讀。
/// </summary>
internal static class ProjectionErrorReasons
{
    public const string DebitCreditBlank =
        "借貸別是空白。請補齊來源資料，或改選正確的借貸別欄位";

    public const string DebitCreditUnlisted =
        "不是借方代碼也不是貸方代碼。請確認兩個代碼是否正確";

    public const string ManualBlank =
        "是空白。請在「來源空白時」選擇處理方式，或先補齊來源資料";

    public const string ManualUnlisted =
        "未歸類為人工或自動。請將這些值歸類，或在「判定方式」改為只列一側";

    public const string AmountInvalid =
        "不是有效的金額。請確認來源的金額格式，或改選正確的金額欄";

    public const string AmountOutOfRange =
        "金額超出可儲存的範圍。請確認金額是否正確，或調整案件的金額小數位數";

    public const string ControlTotalOutOfRange =
        "加總後的金額超出可儲存的範圍。請確認金額是否正確，或調整案件的金額小數位數";

    public const string DateInvalid =
        "無法辨識為日期。請確認來源的日期格式，或改選正確的日期欄";

    public const string RdeDateInvalid =
        "無法辨識為日期。請確認來源的日期格式，或將此攸關資料元素欄位改為文字";

    public const string RdeMoneyInvalid =
        "不是有效的金額，或小數位數超過案件設定。請確認來源資料，或將此攸關資料元素欄位改為文字";

    public static string RdeTextTooLong(int limit) =>
        $"文字超過 {limit} 個字。請縮短來源資料，或取消保留此欄位";

    public static string TextTooLong(int limit) =>
        $"文字超過 {limit} 個字。請縮短來源資料，或改選正確的欄位";
}
