using System.Globalization;
using JET.Domain;

namespace JET.Infrastructure;

internal static class NativeTabularValue
{
    internal static (string Text, LegacyFieldKind Kind, int? DecimalPlaces) Read(object value, bool timeOnly = false)
    {
        if (value is string text) return (text, LegacyFieldKind.Text, null);
        if (value is DateTime date)
            return timeOnly ? (date.ToString("HH:mm:ss", CultureInfo.InvariantCulture), LegacyFieldKind.Time, null)
                : (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), LegacyFieldKind.Date, null);
        if (value is TimeSpan time) return (time.ToString("c", CultureInfo.InvariantCulture), LegacyFieldKind.Time, null);
        if (value is bool boolean) return (boolean ? "1" : "0", LegacyFieldKind.Number, 0);
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal or float or double)
        {
            try
            {
                var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return (number.ToString(CultureInfo.InvariantCulture), LegacyFieldKind.Number,
                    (decimal.GetBits(number)[3] >> 16) & 0xff);
            }
            catch (OverflowException)
            {
                return (Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
                    LegacyFieldKind.Number, 0);
            }
        }
        if (value is Guid guid) return (guid.ToString(), LegacyFieldKind.Text, null);
        throw new JetActionException(JetErrorCodes.FileReadError,
            "來源含有目前無法讀取的複合或二進位欄位；請選擇一般文字、日期或數值資料表。");
    }
}
