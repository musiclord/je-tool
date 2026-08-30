namespace JET.Application;

/// <summary>
/// 當前使用者身分：合格化的 Windows 帳號（<c>網域\帳號</c>；未加網域的機器為 <c>機器名\帳號</c>），
/// 由 composition root 以 <see cref="System.Security.Principal.WindowsIdentity"/> 取得並注入。這是
/// <b>client 自報的軟性身分</b>——SQL Server 連線用共用 jetapp 登入，資料庫端不驗證 app 報上的身分
/// （硬身分＝AD 整合驗證屬公司佈署輪；spec §1、§7 誠實邊界）。線上專案可見性 ACL 與使用者目錄
/// （<c>dbo.app_user</c>／<c>dbo.project_access</c>）以 <see cref="Name"/> 原樣為鍵；埠層以字串收受，維持 Domain 純度。
/// </summary>
public sealed record CurrentPrincipal(string Name)
{
    /// <summary>
    /// 顯示用的帳號短名：<see cref="Name"/> 最後一個 <c>'\'</c> 之後的片段（無反斜線則等於 <see cref="Name"/>）。
    /// 右上角身分徽章與 <c>dbo.app_user.display_name</c> 現階段用此值。
    /// </summary>
    public string ShortName => Name[(Name.LastIndexOf('\\') + 1)..];
}
