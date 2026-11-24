using System.Text.Json;

namespace JET.Domain;

/// <summary>
/// 給審計員看的處理紀錄：處理 GL 或 TB 的操作完成後，記下做了什麼、誰做的、用了哪些條件，以及處理後的筆數，
/// 寫成案件資料夾裡的文字檔。實作只讀操作的輸入、回應與畫面收到的事件，不參與審計判斷，
/// 也不改變操作的流程、結果或例外；自己出錯時吞掉，不往外丟。沒有注入時，呼叫端照常執行。
/// </summary>
public interface IProcessingLog
{
    /// <summary>
    /// 開始一次操作，回傳的物件在操作結束時釋放。期間收到的匯出進度用來記每張工作表的資料列數。
    /// </summary>
    IDisposable BeginAction(string action);

    /// <summary>畫面收到的事件也給處理紀錄看一份，例如匯出進度；只讀，不改變事件本身。</summary>
    void ObservePublishedEvent(string eventName, object payload);

    /// <summary>
    /// 操作成功完成後呼叫。<paramref name="projectId"/> 是操作完成時開著的案件；沒有開著的案件時不寫。
    /// </summary>
    Task RecordAsync(string action, JsonElement payload, object? result, string? projectId);
}
