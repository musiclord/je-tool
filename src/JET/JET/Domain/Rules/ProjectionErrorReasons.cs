namespace JET.Domain;

/// <summary>
/// GL 與 TB 投影失敗時給使用者看的理由。IntakeMappingProgram 會把它接在
/// 「第 N 列，欄位「X」，值「Y」：」之後，並在 GL 配對區就地顯示，所以每句先說值有什麼問題，
/// 再說使用者接下來能做什麼；不出現英文或內部名稱。
/// </summary>
internal static class ProjectionErrorReasons
{
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
