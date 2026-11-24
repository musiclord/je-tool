namespace JET.Infrastructure;

/// <summary>匯出寫入器放掉手上的串流、暫存檔與壓縮檔項目時共用的做法。</summary>
internal static class ExportResources
{
    /// <summary>
    /// 依序釋放每一項；其中一項釋放失敗時繼續釋放後面的，失敗不往外丟。
    /// XML 寫入器與壓縮檔項目關閉時還會把緩衝裡的內容寫出去，所以關閉本身也可能失敗。
    /// 這個方法只用在內容已經不要、或已經寫完並關閉過的時候：匯出失敗或取消後再丟一個關閉的錯誤，只會蓋掉原本的原因。
    /// 內容剛寫完、關閉失敗就代表輸出不完整的地方，要先自己關閉並讓失敗丟出，再用這個方法放掉其餘的。
    /// </summary>
    internal static void ReleaseAll(params IDisposable?[] resources)
    {
        foreach (var resource in resources)
        {
            try
            {
                resource?.Dispose();
            }
            catch (Exception)
            {
                // 釋放只是盡量做完；這一項放不掉也要繼續放其餘的。
            }
        }
    }
}
