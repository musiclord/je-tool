using System.Globalization;

namespace JET.Domain;

/// <summary>
/// 把一個數字的文字寫法轉成固定 40 個字元的排序鍵；排序鍵用 ordinal 文字比較排出來的順序，就是數值由小到大的順序。
/// 數字的寫法以 invariant culture 為準，而且必須是有限的數。這個排序鍵只存在 <c>gl</c> 裡供排序使用；
/// 看得到的值仍是原本的 <c>line_item</c>。
/// </summary>
internal static class LineItemNumericSortKey
{
    // XLSX 的數值儲存格會先解析成 double：能轉成 decimal 的，正規化後最多 29 位有效數字；
    // 超出 decimal 範圍的改用 double 的 round-trip 寫法，最多 17 位。CSV 與 TXT 的值一律當文字。
    internal const int SignificantDigitCapacity = 29;
    internal const int KeyLength = 1 + 10 + SignificantDigitCapacity;

    private const long ExponentBias = 2_147_483_648L;
    private const long MaximumEncodedExponent = 4_294_967_295L;
    private static readonly string ZeroKey = "1" + new string('0', KeyLength - 1);

    internal static string? CreateOrNull(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        return TryCreate(raw, out var key) ? key : null;
    }

    internal static bool TryCreate(string raw, out string key)
    {
        key = string.Empty;
        var text = raw.Trim();
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            text = "1";
        }
        else if (text.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            text = "0";
        }

        if (text.Length == 0)
        {
            return false;
        }

        var index = 0;
        var negative = false;
        if (text[index] is '+' or '-')
        {
            negative = text[index] == '-';
            index++;
            if (index == text.Length)
            {
                return false;
            }
        }

        Span<char> significantDigits = stackalloc char[SignificantDigitCapacity];
        var significantLength = 0;
        var digitCount = 0;
        var fractionalDigitCount = 0;
        var afterDecimalPoint = false;
        var sawDecimalPoint = false;
        var exponentStart = -1;
        var sawNonZero = false;

        for (; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch is 'e' or 'E')
            {
                exponentStart = index + 1;
                break;
            }

            if (ch == '.')
            {
                if (sawDecimalPoint)
                {
                    return false;
                }

                sawDecimalPoint = true;
                afterDecimalPoint = true;
                continue;
            }

            if (!char.IsAsciiDigit(ch))
            {
                return false;
            }

            digitCount++;
            if (afterDecimalPoint)
            {
                fractionalDigitCount++;
            }

            if (!sawNonZero && ch == '0')
            {
                continue;
            }

            sawNonZero = true;
            if (significantLength == SignificantDigitCapacity)
            {
                return false;
            }

            significantDigits[significantLength++] = ch;
        }

        if (digitCount == 0)
        {
            return false;
        }

        var explicitExponent = 0;
        if (exponentStart >= 0)
        {
            if (exponentStart == text.Length
                || !int.TryParse(
                    text.AsSpan(exponentStart),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out explicitExponent))
            {
                return false;
            }
        }

        if (!sawNonZero)
        {
            key = ZeroKey;
            return true;
        }

        var adjustedExponent =
            (long)significantLength + explicitExponent - fractionalDigitCount;
        if (adjustedExponent is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        var encodedExponent = adjustedExponent + ExponentBias;
        if (negative)
        {
            encodedExponent = MaximumEncodedExponent - encodedExponent;
        }

        Span<char> result = stackalloc char[KeyLength];
        result[0] = negative ? '0' : '2';
        if (!encodedExponent.TryFormat(
                result[1..11],
                out var exponentCharactersWritten,
                "D10",
                CultureInfo.InvariantCulture)
            || exponentCharactersWritten != 10)
        {
            return false;
        }

        for (var digitIndex = 0; digitIndex < SignificantDigitCapacity; digitIndex++)
        {
            var digit = digitIndex < significantLength
                ? significantDigits[digitIndex]
                : '0';
            result[11 + digitIndex] = negative
                ? (char)('9' - (digit - '0'))
                : digit;
        }

        key = new string(result);
        return true;
    }
}
