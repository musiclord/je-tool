using System.Globalization;

namespace JET.AuditCore;

/// <summary>INF 可靠性測試的預設筆數、Excel 輸出容量，以及抽樣清單與底稿 Step 2 寫出隨機種子序號的句子。</summary>
internal static class InfSampling
{
    public const int DefaultSampleSize = 59;
    public const int MaximumSampleSize = 1_048_576 - 52;

    /// <summary>
    /// 範本 A4 的原句接上這批樣本實際使用的隨機種子序號，數字不加千分位。
    /// 呼叫端傳入驗證結果裡記下的種子，不讀案件設定檔：設定檔在抽樣之後被改過、沒有重新抽樣時，
    /// 樣本仍是原本的種子抽出來的。
    /// </summary>
    internal static string SampleSeedSentence(long sampleSeed) =>
        "此Report將提供測試JE母體攸關資料元素可靠性之樣本，本次抽樣所使用之隨機種子序號為:"
        + sampleSeed.ToString(CultureInfo.InvariantCulture);
}
