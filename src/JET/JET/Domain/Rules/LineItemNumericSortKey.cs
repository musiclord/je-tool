using System.Globalization;

namespace JET.Domain;

/// <summary>
/// 將 invariant finite-number lexeme 正規化成固定 40 字元、可用 ordinal
/// 文字比較還原數值升冪的 key。這個 key 只供 target GL 的持久化排序使用；
/// 可見值仍保留原本的 <c>line_item</c>。
/// </summary>
internal static class LineItemNumericSortKey
{
    // XLSX native Number 先經 double parse：decimal normalization 最多 29 位有效數字，
    // overflow fallback 的 round-trip double 最多 17 位。CSV/TXT 固定為 Text。
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
