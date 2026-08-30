using System.Globalization;

namespace JET.Domain;

/// <summary>
/// 金額解析與 scaled integer 轉換（jet-guide.md §1.5.3）。
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

        // 會計格式零（guide §3.1.2）：Excel 會計數字格式對 0 顯示單獨一個半形連字號。
        // 僅此一個字元成立；全形/破折號/多字元組合仍走 decimal 解析被拒。
        if (trimmed == "-")
        {
            return true;
        }

        return decimal.TryParse(
            trimmed,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out value);
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
