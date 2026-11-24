namespace JET.Domain;

/// <summary>
/// 讀取配對到來源欄的金額儲存格，GL 與 TB 的投影共用同一份規則：
/// 沒有配對、來源列沒有這一格或內容空白都當成 0，因為借方、貸方分欄時常有一邊是空的；
/// 有內容卻不是有效金額才回報錯誤。金額的解析規則在 <see cref="MoneyScaling.TryParseAmount"/>。
/// </summary>
internal static class MappedAmountCell
{
    /// <summary>
    /// 依 <paramref name="mapping"/> 找出 <paramref name="key"/> 配對的來源欄並解析金額。
    /// 回報錯誤時欄位寫來源欄名；沒有配對就寫 <paramref name="key"/>。
    /// </summary>
    internal static bool TryParse(
        StagingRow row,
        IReadOnlyDictionary<string, string> mapping,
        string key,
        out decimal value,
        out RowProjectionError? error)
    {
        value = 0m;
        error = null;

        var isMapped = mapping.TryGetValue(key, out var column) && !string.IsNullOrWhiteSpace(column);
        var raw = isMapped && row.Values.TryGetValue(column!, out var cell) ? cell : null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (MoneyScaling.TryParseAmount(raw, out value))
        {
            return true;
        }

        error = new RowProjectionError(
            row.SourceRowNumber,
            isMapped ? column! : key,
            raw,
            ProjectionErrorReasons.AmountInvalid);
        return false;
    }
}
