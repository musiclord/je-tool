using System.Globalization;

namespace JET.Domain;

/// <summary>
/// 金額解析與 scaled integer 轉換（見 docs/jet-guide.md 第 10 節「大資料量原則」：金額以固定精度整數表示）。
/// 來源金額一律以 decimal 解析驗證，再乘以 project MoneyScale
/// 以 AwayFromZero 取整為 64-bit scaled integer。
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

        var digits = trimmed;
        if (digits.StartsWith('(') && digits.EndsWith(')'))
        {
            digits = digits[1..^1].Trim();
            // 括號已代表負號，不能再疊加另一個正負號。
            if (digits.IndexOfAny(['+', '-']) >= 0) return false;
        }
        if (!HasValidThousandsGroups(digits)) return false;

        return decimal.TryParse(
            trimmed,
            NumberStyles.Number | NumberStyles.AllowParentheses,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool HasValidThousandsGroups(string text)
    {
        if (!text.Contains(',')) return true;
        var unsigned = text.Trim();
        if (unsigned.Length > 0 && unsigned[0] is '+' or '-') unsigned = unsigned[1..];
        if (unsigned.Length > 0 && unsigned[^1] is '+' or '-') unsigned = unsigned[..^1];
        var decimalPoint = unsigned.IndexOf('.');
        var integer = decimalPoint >= 0 ? unsigned[..decimalPoint] : unsigned;
        if (decimalPoint >= 0 && unsigned[(decimalPoint + 1)..].Contains(',')) return false;
        var groups = integer.Split(',');
        if (groups[0].Length is < 1 or > 3) return false;
        for (var index = 0; index < groups.Length; index++)
        {
            if (index > 0 && groups[index].Length != 3) return false;
            if (groups[index].Any(character => !char.IsAsciiDigit(character))) return false;
        }
        return true;
    }

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
}
