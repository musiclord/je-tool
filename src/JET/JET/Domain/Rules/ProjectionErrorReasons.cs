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
/// 寫成「欄位「X」有 N 列＜問題＞：值與列號。＜處理方式＞。」，所以問題那段要能接在「有 N 列」後面讀。
/// </summary>
internal static class ProjectionErrorReasons
{
    public const string DebitCreditBlank =
        "借貸別是空白。請補齊來源資料的借貸別，或回第三步選擇正確的借貸別欄位，填寫借方代碼與貸方代碼後再確認配對";

    public const string DebitCreditUnlisted =
        "未符合借方代碼或貸方代碼。請確認來源資料的借貸別，或回第三步填寫正確的借方代碼與貸方代碼後再確認配對";

    public const string ManualBlank =
        "是空白。請回到欄位配對，在「來源空白時」選擇視為人工、視為自動或不判定；也可先補齊來源資料，或取消這個欄位";

    public const string ManualUnlisted =
        "未歸類為人工或自動。請回到欄位配對把這些值歸類，或在「判定方式」選擇只列一側並將其餘非空白值歸到另一側；不需判定時可取消這個欄位";

    public const string AmountInvalid =
        "不是有效金額。請確認來源資料的金額格式，或回到欄位配對改選正確的金額欄";

    public const string AmountOutOfRange =
        "金額換算後超過系統可保存的範圍。請確認這個金額是否正確，或調整案件的金額小數位數";

    public const string ControlTotalOutOfRange =
        "金額加總後超過系統可保存的範圍。請確認金額是否正確，或調整案件的金額小數位數";

    public const string DateInvalid =
        "無法解析為日期。請確認來源資料的日期格式，或回到欄位配對改選正確的日期欄";

    public const string RdeDateInvalid =
        "無法解析為日期。請確認來源資料的日期格式，或回到欄位配對把這個攸關資料元素改為文字型別";

    public const string RdeMoneyInvalid =
        "不是有效金額，或小數位數超過案件設定。請確認來源資料，或回到欄位配對把這個攸關資料元素改為文字型別";

    public static string RdeTextTooLong(int limit) =>
        $"文字長度超過攸關資料元素可保存的 {limit} 個字元（以 UTF-16 計算）。請縮短來源資料，或回到欄位配對取消保留這個欄位";
}
