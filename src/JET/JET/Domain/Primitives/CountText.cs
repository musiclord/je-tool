using System.Globalization;

namespace JET.Domain;

/// <summary>
/// 後端訊息裡數量的唯一寫法。裁定 2026-10-30-19：畫面上的筆數、列數這類數量加千分位；
/// Excel 列號、第幾組第幾條與頁碼這類位置，以及年份、日期、傳票號碼與科目代號這類代碼不是數量，不經過這裡。
/// 前端的對應是 ui-core.js 的 <c>countText</c>。
/// </summary>
public static class CountText
{
    public static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
