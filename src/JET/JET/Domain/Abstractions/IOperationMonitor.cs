namespace JET.Domain;

/// <summary>
/// 只觀察、不參與的操作監看：記下每次操作經過的步驟、執行的 SQL、匯出進度與花掉的時間，寫進日誌。
/// 實作不得改變操作的流程、結果或例外，自己出錯時也不往外丟；沒有注入時，呼叫端照常執行。
/// </summary>
public interface IOperationMonitor
{
    /// <summary>
    /// 開始監看一次操作，回傳的物件在操作結束時釋放。要在操作的日誌範圍內呼叫，之後在背景寫的紀錄才帶得到同一個操作識別碼。
    /// </summary>
    IDisposable BeginOperation(string action, CancellationToken cancellationToken);

    /// <summary>畫面收到的事件也給監看模組看一份，例如匯出進度；只讀，不改變事件本身。</summary>
    void ObservePublishedEvent(string eventName, object payload);
}
