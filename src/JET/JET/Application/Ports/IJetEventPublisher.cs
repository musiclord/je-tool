namespace JET.Application;

/// <summary>
/// 由 C# 主動通知前端的事件，只有這一個方向。
/// handler 用它送出進度這類提示，例如 import.progress。切回介面執行緒與序列化由實作負責，
/// 也就是 Bridge 的 WebViewEventPublisher。事件只是提示：狀態一律以 action 的回應為準，
/// 事件也不得帶資料列，大量資料不經這裡送到前端。
/// </summary>
public interface IJetEventPublisher
{
    void Publish(string eventName, object payload);
}

/// <summary>沒有 WebView 時用的實作，什麼都不做。組裝時沒有傳入事件發布者就用它，測試預設也是。</summary>
public sealed class NullEventPublisher : IJetEventPublisher
{
    public void Publish(string eventName, object payload)
    {
    }
}
