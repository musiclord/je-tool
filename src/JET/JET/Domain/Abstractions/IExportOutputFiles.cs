namespace JET.Domain;

/// <summary>
/// 匯出時寫在案件資料夾裡的輸出檔。實作在建立、寫入、定位、清空與關檔時遇到磁碟已滿或沒有寫入權限，
/// 丟出寫明原因與使用者接下來能做什麼的 <see cref="JetActionException"/>；其他錯誤照原樣往外丟。
/// 輸出檔自己的錯誤在這裡就認出來，所以寫出程式途中別的檔案出錯，例如 Windows 暫存資料夾裡的暫存檔，
/// 不會被說成案件資料夾的問題。
/// </summary>
public abstract class ExportOutputStream : Stream
{
    /// <summary>把內容寫進磁碟本身，不只留在 Windows 的快取。</summary>
    public abstract void FlushToDisk();

    /// <summary>
    /// 關閉寫入已經失敗的輸出檔。緩衝區裡還沒寫出的內容在關檔時會再寫一次，所在磁碟已滿時這次也會失敗；
    /// 寫到一半的檔案之後本來就要刪掉，所以關檔時的讀寫錯誤不往外丟，呼叫端照樣回報原本的錯誤。檔案仍會關閉。
    /// </summary>
    public abstract ValueTask CloseAfterFailedWriteAsync();
}

/// <summary>
/// 在案件資料夾建立匯出用輸出檔的埠，實作在 Infrastructure。埠置於 Domain，讓 Application 寫工作檔時不依賴 Infrastructure。
/// </summary>
public interface IExportOutputFiles
{
    /// <summary>在 <paramref name="path"/> 建立新的輸出檔；同名檔案已經存在時照常失敗。</summary>
    ExportOutputStream CreateNew(string path, FileOptions options);
}
