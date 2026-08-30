namespace JET.Domain;

/// <summary>
/// 單庫使用者目錄 <c>dbo.app_user</c> 的一列：使用者編號（IDENTITY、部門內唯一、永不改變）搭配
/// 合格化 Windows 帳號 <see cref="Principal"/>。UI 顯示為 <c>U{UserId}</c>。
/// 現行契約：docs/action-contract-manifest.md 的 system.whoAmI。
/// </summary>
public sealed record AppUserRecord(int UserId, string Principal);

/// <summary>
/// 線上單庫使用者目錄（<c>dbo.app_user</c>）的埠：發使用者編號、記錄「誰來過」，是後續名單授權機制的基座。
/// <b>天生只屬 sqlServer——不走 ProviderRouting</b>（比照 <see cref="IProjectRegistry"/>）：編號的唯一權威在
/// 單庫的目錄表，離線時由本機快取（<see cref="IUserProfileCache"/>）補位。埠置於 Domain/Abstractions
/// （非 Application/Ports），使 Infrastructure 實作維持 <c>Infrastructure → Domain</c> 的既定依賴方向
/// （同 <see cref="IProjectRegistry"/> 先例，不擴張 AGENTS.md 的「Infrastructure 實作 Application 埠」例外清單）。
/// 這不是驗證機制：它記錄身分、發編號，不證明「他是他」（spec §7 誠實邊界）。
/// </summary>
public interface IUserDirectory
{
    /// <summary>
    /// 確保 <paramref name="principal"/> 在目錄中有一列並回其編號：查無則 INSERT、INSERT 撞唯一鍵（併發競速）
    /// 則重查；順帶 best-effort 更新 <c>last_seen_utc</c>。冪等、可重試。<paramref name="displayName"/> 現階段
    /// ＝帳號短名（名單授權輪可改人名）。連線未設定時拋 <see cref="JetErrorCodes.SqlServerNotConfigured"/>。
    /// </summary>
    Task<AppUserRecord> EnsureUserAsync(string principal, string displayName, CancellationToken cancellationToken);
}

/// <summary>
/// 使用者編號的本機離線快取（<c>%LOCALAPPDATA%\JET\user-profile.json</c>）的埠：記住「principal → 編號」。
/// 編號永不改變，故快取無過期問題；快取內 principal 與當前身分不符時整份忽略（spec §1，不張冠李戴發別人的編號）。
/// 線上不可達時，<c>system.whoAmI</c> 據此退階回報編號（<c>numberSource:"cached"</c>）。
/// </summary>
public interface IUserProfileCache
{
    /// <summary>回快取的使用者編號；檔不存在／損壞／principal 不符 → null。</summary>
    Task<int?> TryGetUserNumberAsync(string principal, CancellationToken cancellationToken);

    /// <summary>原子覆寫本機快取為 <c>{ principal, userNumber, savedUtc }</c>（目錄不存在則建立）。</summary>
    Task SaveUserNumberAsync(string principal, int userNumber, CancellationToken cancellationToken);
}
