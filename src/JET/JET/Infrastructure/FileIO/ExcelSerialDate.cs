using System.Globalization;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// Excel 日期型儲存格的序列值和年月日之間的換算，讀與寫都在這裡。讀的時候換成 Excel 顯示的年月日，
/// 讓使用者在 Excel 看到哪一天，JET 就讀成哪一天；寫的時候只把 1900-03-01 以後的日期寫成序列值，更早的日期寫成文字。
/// Excel 的 1900 日期系統把 1900 年當成閏年：序列值 1 是 1900-01-01，59 是 1900-02-28，60 是日曆上沒有的 1900-02-29，
/// 61 起才和 <see cref="DateTime.FromOADate"/> 換出的日期相同；序列值 0 顯示成 1900 年 1 月 0 日。
/// 1904 日期系統的序列值 0 是 1904-01-01，沒有這個問題。
/// </summary>
internal static class ExcelSerialDate
{
    /// <summary>1900 日期系統的序列值 0 在 Excel 顯示的年月日，日曆上沒有這一天。</summary>
    internal const string DayZero = "1900-01-00";

    /// <summary>1900 日期系統的序列值 60 在 Excel 顯示的年月日，日曆上沒有這一天。</summary>
    internal const string FictitiousLeapDay = "1900-02-29";

    // 9999-12-31 是 Excel 最後一天；序列值到下一天開始以前都還是這一天。
    private const long EndOf1900System = 2958466;
    private const long EndOf1904System = 2957004;

    // 1904 日期系統的負數往前數到 1900-01-01 為止，更早的日期算顯示不出來。
    private const long FirstDayOf1904System = -1460;

    private const long MillisecondsPerDay = 86_400_000;

    private static readonly DateTime Epoch1904 = new(1904, 1, 1);

    /// <summary>
    /// 序列值在 Excel 顯示成哪一天，寫成 yyyy-MM-dd，時間的部分不算。Excel 顯示不出日期時回傳 null，例如 1900 日期系統的負數，
    /// 或晚於 9999-12-31；呼叫端改交出序列值的文字。1900 日期系統的序列值 0 與 60 回傳日曆上沒有的
    /// <see cref="DayZero"/> 與 <see cref="FictitiousLeapDay"/>，確認配對時列為日曆上沒有這一天。
    /// </summary>
    internal static string? ShownDate(double serial, bool date1904)
    {
        if (!double.IsFinite(serial))
        {
            return null;
        }

        if (date1904)
        {
            if (serial < FirstDayOf1904System - 1 || serial >= EndOf1904System)
            {
                return null;
            }

            var day1904 = RoundedDay(serial);
            return day1904 < FirstDayOf1904System || day1904 >= EndOf1904System
                ? null
                : Format(Epoch1904.AddDays(day1904));
        }

        if (serial < 0 || serial >= EndOf1900System)
        {
            return null;
        }

        var day = RoundedDay(serial);
        return day switch
        {
            >= EndOf1900System => null,
            0 => DayZero,
            < 60 => Format(new DateTime(1899, 12, 31).AddDays(day)),
            60 => FictitiousLeapDay,
            _ => Format(new DateTime(1899, 12, 30).AddDays(day)),
        };
    }

    // 和 DateTime.FromOADate 一樣先把序列值四捨五入到毫秒，再取是哪一天：離隔天不到半毫秒的值算隔天，
    // 和 .xls 讀取器換出的日期一致。公式一段一段累加時間時，常留下這種浮點誤差。
    private static long RoundedDay(double serial)
    {
        var milliseconds = (long)(serial * MillisecondsPerDay + (serial >= 0 ? 0.5 : -0.5));
        var day = Math.DivRem(milliseconds, MillisecondsPerDay, out var remainder);
        return remainder < 0 ? day - 1 : day;
    }

    /// <summary>
    /// 日期換成寫進日期儲存格的序列值。早於 1900-03-01 的日期回傳 false，不寫成日期儲存格；
    /// 呼叫端改把存下來的日期文字照原樣寫成文字儲存格，匯出照常完成。
    /// 寫出的活頁簿都用 1900 日期系統。ToOADate 換出的序列值從 1900-03-01 起才和 Excel 的序列值相同，
    /// 更早的日期會差一天，因為 Excel 把 1900 年當成閏年；1900 年以前的日期 Excel 沒有序列值，西元 100 年以前 ToOADate 也換算不出來。
    /// 這一天定義在 <see cref="DateNormalizer.FirstExcelFaithfulDate"/>，確認配對後的日期提醒用同一天列出在底稿寫成文字的日期。
    /// </summary>
    internal static bool TryExcelDateSerial(DateOnly date, out string serial)
    {
        if (date < DateNormalizer.FirstExcelFaithfulDate)
        {
            serial = string.Empty;
            return false;
        }

        serial = date.ToDateTime(TimeOnly.MinValue).ToOADate().ToString("R", CultureInfo.InvariantCulture);
        return true;
    }

    private static string Format(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
