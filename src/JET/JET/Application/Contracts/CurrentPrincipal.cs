namespace JET.Application;

/// <summary>
/// 當前使用者身分：合格化的 Windows 帳號（<c>網域\帳號</c>；未加網域的機器為 <c>機器名\帳號</c>），
/// 由 composition root 以 <see cref="System.Security.Principal.WindowsIdentity"/> 取得並注入，
/// 不採用前端傳入的 operatorId。現階段部門內使用，以 IT 管理主機的 Windows 帳號識別操作人員。
/// 案件鎖的持有人以 <see cref="Name"/> 原樣記錄；埠層以字串收受，維持 Domain 純度。
/// </summary>
public sealed record CurrentPrincipal(string Name)
{
    /// <summary>
    /// 顯示用的帳號短名：<see cref="Name"/> 最後一個 <c>'\'</c> 之後的片段（無反斜線則等於 <see cref="Name"/>）。
    /// 右上角身分徽章用此值。
    /// </summary>
    public string ShortName => Name[(Name.LastIndexOf('\\') + 1)..];
}
