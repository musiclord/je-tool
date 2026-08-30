namespace JET.Application;

/// <summary>
/// Host→Web 單向事件推播 port（manifest「Host→Web 事件」章節）。
/// Application handler 發出 UX 提示事件（如 import.progress）；marshal 與序列化
/// 由實作負責（Bridge 的 WebViewEventPublisher）。事件不承載狀態權威
/// （權威一律以 action response 為準），也不得攜帶資料列（guide §1.5.4）。
/// </summary>
public interface IJetEventPublisher
{
    void Publish(string eventName, object payload);
}

/// <summary>無 WebView 環境（測試預設、host 組裝前）的 no-op 實作。</summary>
public sealed class NullEventPublisher : IJetEventPublisher
{
    public void Publish(string eventName, object payload)
    {
    }
}
