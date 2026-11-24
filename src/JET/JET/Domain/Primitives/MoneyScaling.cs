using System.Globalization;

namespace JET.Domain;

/// <summary>
/// 金額解析與 scaled integer 轉換：金額以固定精度整數表示。
/// 來源金額一律以 decimal 解析驗證，再乘以 project MoneyScale
/// 以 AwayFromZero 取整為 64-bit scaled integer。
/// 文字轉金額、金額放大成整數、整數還原成十進位，都只寫在這個類別；其他地方呼叫這裡的方法。
/// </summary>
public static class MoneyScaling
{
    public static bool TryParseAmount(string? text, out decimal value)
    {
        value = 0m;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        // 會計格式零：Excel 會計數字格式對 0 顯示單獨一個半形連字號。
        // 僅此一個字元成立；全形/破折號/多字元組合仍走 decimal 解析被拒。
        if (trimmed == "-")
        {
            return true;
        }

        // 正負號、括號、空白與小數點的語法交給 decimal.TryParse：它拒絕括號內再帶正負號、括號不成對，
        // 以及小數部分出現逗號。它不檢查千分位的位置（"12,34" 會讀成 1234），所以千分位分組另外檢查。
        if (!HasValidThousandsGroups(trimmed)) return false;

        return decimal.TryParse(
            trimmed,
            NumberStyles.Number | NumberStyles.AllowParentheses,
            CultureInfo.InvariantCulture,
            out value);
    }

    // 第一段由數字與逗號組成的連續字元就是整數部分；逗號出現在其他位置的寫法，decimal.TryParse 會拒絕。
    private static bool HasValidThousandsGroups(string text)
    {
        if (!text.Contains(',')) return true;
        var integerPart = new string(text.SkipWhile(character => !IsDigitOrComma(character)).TakeWhile(IsDigitOrComma).ToArray());
        var groups = integerPart.Split(',');
        if (groups[0].Length is < 1 or > 3) return false;
        return groups.Skip(1).All(group => group.Length == 3);
    }

    private static bool IsDigitOrComma(char character) => char.IsAsciiDigit(character) || character == ',';

    public static bool TryToScaled(decimal value, int moneyScale, out long scaled)
    {
        scaled = 0;

        decimal rounded;
        try
        {
            rounded = Math.Round(value * moneyScale, 0, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (rounded < long.MinValue || rounded > long.MaxValue)
        {
            return false;
        }

        scaled = (long)rounded;
        return true;
    }

    /// <summary>
    /// 把文字直接轉成放大後的整數：先照 <see cref="TryParseAmount"/> 解析，再照 <see cref="TryToScaled"/> 放大取整。
    /// 文字不是有效金額，或放大後超出 64 位元整數範圍時回傳 false。
    /// </summary>
    public static bool TryParseScaled(string? text, int moneyScale, out long scaled)
    {
        scaled = 0;
        return TryParseAmount(text, out var amount) && TryToScaled(amount, moneyScale, out scaled);
    }

    /// <summary>把放大後的整數還原成十進位金額，用在畫面、報告與底稿的顯示；除法不取整，放大時保留的位數都留著。</summary>
    public static decimal FromScaled(long scaled, int moneyScale) => (decimal)scaled / moneyScale;
}
