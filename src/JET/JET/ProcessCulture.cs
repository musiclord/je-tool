using System.Globalization;

namespace JET;

/// <summary>
/// Windows 地區格式把日曆設成中華民國曆時，沒有指定文化的 yyyy 會被當成民國年解讀。程式啟動時保留地區設定，
/// 只把日曆換成西元曆，以後新寫的日期處理即使忘了指定文化，也不會讀成民國年（使用者 2026-10-19 裁定）。
/// 數字、貨幣與其他地區設定都不動。
/// </summary>
internal static class ProcessCulture
{
    public static void UseGregorianCalendar()
    {
        var culture = WithGregorianCalendar(CultureInfo.CurrentCulture);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
    }

    /// <summary>已經是西元曆，或這個地區沒有西元曆可選時原樣回傳；否則回傳只換掉日曆的複本，不改動傳入的文化。</summary>
    internal static CultureInfo WithGregorianCalendar(CultureInfo culture)
    {
        if (culture.DateTimeFormat.Calendar is GregorianCalendar)
        {
            return culture;
        }

        var gregorian = culture.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault();
        if (gregorian is null)
        {
            return culture;
        }

        var adjusted = (CultureInfo)culture.Clone();
        adjusted.DateTimeFormat.Calendar = gregorian;
        return adjusted;
    }
}
