namespace JET.Application;

/// <summary>
/// 依 bridge requestId 請求取消在途作業的窄介面。Application 只知道「要求取消」，
/// requestId 與 CancellationTokenSource 的生命週期由 Bridge adapter 管理。
/// </summary>
public interface IOperationCancellationService
{
    bool TryCancel(string requestId);
}
