namespace JET.Domain;

/// <summary>已停用寫法仍可解析與讀回；新增、預覽、儲存及開案檢查共用這些原因，不自動轉換定義。</summary>
internal static class FilterSinglePurposeRules
{
    internal const string SameVoucher = "條件組的「同一傳票」比對範圍已停用。請重建條件組，保留第一條條件，其餘各條分別放進「整張傳票條件」並選「至少一筆符合」。";
    internal const string AccountDirection = "科目分類條件裡的借貸方向已停用。請重新加入科目分類條件；要限定方向時，另加「借貸別」條件，兩者放進同一個子條件組。";
    internal const string AccountAbsence = "科目分類條件的「這一側都不屬於」已停用。請改用「整張傳票條件」的「不存在符合」，子條件選「借貸別」與「科目分類屬於」。";
    internal const string VoucherDirection = "整張傳票條件的借貸範圍已停用。請重新加入整張傳票條件；要限定方向時，在子條件加入「借貸別」。原本的「全部符合」請依查核目的重組條件。";
    internal const string SignedAmount = "金額條件的「含正負號」已停用。請改用絕對值比較，核對比較方式與金額；要限定方向時，另加「借貸別」條件並用子條件組組合。";
    internal const string Keywords = "舊式摘要關鍵字條件已停用。請改用「傳票摘要」的「包含任一文字」，填入原本的關鍵字。";
    internal const string TrailingZeros = "舊式連續零尾數條件已停用。請在同一個子條件組加入「金額」的「整數尾數符合」及「大於或等於 1」，尾數填入原位數的 0。";
    internal const string TrailingDigits = "舊式金額尾數條件已停用。請改用「金額」的「整數尾數符合」，填入原本的尾數清單。";
    internal const string PreparerEntryCount = "舊式編製人員分錄筆數條件已停用。請改用「科目與人員統計」條件：統計對象選「傳票建立人員」，計算單位選「分錄筆數」，比較方式選「小於或等於」，填入原本的筆數。";
    internal const string AccountEntryCount = "舊式科目分錄筆數條件已停用。請改用「科目與人員統計」條件：統計對象選「會計科目編號」，計算單位選「分錄筆數」，比較方式選「小於或等於」，填入原本的筆數。";

    /// <summary>
    /// 整個型別已停用的條件（預篩選收斂計畫階段九把兩種筆數條件收進「科目與人員統計」）：第五步的型別表不再列出，
    /// 舊情境裡的這幾條仍可讀回；前端 ui-core.js 的 RETIRED_RULE_TYPES 鏡像這一份。
    /// </summary>
    internal static readonly IReadOnlySet<FilterRuleType> RetiredTypes = new HashSet<FilterRuleType>
    {
        FilterRuleType.CustomKeywords, FilterRuleType.CustomTrailingZeros, FilterRuleType.TrailingDigits,
        FilterRuleType.CustomPreparerEntryCount, FilterRuleType.CustomAccountEntryCount
    };

    internal static string? Reason(FilterRuleSpec rule) => rule.Type switch
    {
        FilterRuleType.FieldValue when rule.DrCr is "debit" or "credit" =>
            (rule.Field switch { "accNum" => "科目編號", "accName" => "科目名稱", _ => "欄位" })
            + "條件裡的分錄方向已停用。請重新加入欄位條件，另加「借貸別」條件；兩者要由同一筆分錄符合時，放進同一個子條件組。",
        FilterRuleType.FieldValue when rule.Field == JetFieldCatalog.GlAmount && rule.FieldId is null && rule.AmountBasis == "signed" => SignedAmount,
        FilterRuleType.AccountSide when rule.CategoryMode == "absent" => AccountAbsence,
        FilterRuleType.AccountSide when rule.DrCr is "debit" or "credit" or "any" => AccountDirection,
        FilterRuleType.Voucher when rule.Side is "all" or "debit" or "credit" => VoucherDirection,
        FilterRuleType.CustomKeywords => Keywords,
        FilterRuleType.CustomTrailingZeros => TrailingZeros,
        FilterRuleType.TrailingDigits => TrailingDigits,
        FilterRuleType.CustomPreparerEntryCount => PreparerEntryCount,
        FilterRuleType.CustomAccountEntryCount => AccountEntryCount,
        _ => null
    };
}
