using System.Globalization;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// .xls 與 Access 讀出的原生值轉成文字。表示方式和 Open XML 讀取器一致，同一份資料換格式匯入時值相同：
/// 日期寫 yyyy-MM-dd；時間用 TimeSpan 的 "c" 格式；布林寫 true 或 false；數字用不變文化的十進位文字。
/// </summary>
internal static class NativeTabularValue
{
    /// <summary>OLE 自動化日期的零點。Access 只存時間的 Date/Time 值，讀出來的日期部分固定是這一天。</summary>
    private static readonly DateTime OleZeroDate = new(1899, 12, 30);

    internal static (string Text, LegacyFieldKind Kind, int? DecimalPlaces) Read(object value, bool timeOnly = false)
    {
        // 文字去頭尾空白（.NET Trim 的字元集合），和 .csv 與 .xlsx 讀取器相同；只含空白的值變成空字串，由呼叫端當成空白略過。
        if (value is string text) return (text.Trim(), LegacyFieldKind.Text, null);
        if (value is DateTime date)
        {
            var isTimeOnly = timeOnly || (date.Date == OleZeroDate && date.TimeOfDay != TimeSpan.Zero);
            return isTimeOnly
                ? (date.TimeOfDay.ToString("c", CultureInfo.InvariantCulture), LegacyFieldKind.Time, null)
                : (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), LegacyFieldKind.Date, null);
        }
        if (value is TimeSpan time) return (time.ToString("c", CultureInfo.InvariantCulture), LegacyFieldKind.Time, null);
        if (value is bool boolean) return (boolean ? "true" : "false", LegacyFieldKind.Number, 0);
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
