namespace JET.Application;

/// <summary>
/// 依請求的 requestId 要求取消一個還在進行的作業。Application 只負責提出取消；
/// requestId 對應的 CancellationTokenSource 何時建立與釋放，由 Bridge 管理。
/// </summary>
public interface IOperationCancellationService
{
    bool TryCancel(string requestId);
}
