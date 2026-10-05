namespace JET.Domain;

/// <summary>
/// 取鎖結果（<see cref="ILockService.AcquireAsync"/> 的回傳型別）：封閉層級——只有
/// <see cref="Acquired"/>（取得或接管過期租約）與 <see cref="Held"/>（他人以未過期租約持有，附持鎖者資訊）。
/// private 建構子封住繼承，模式比對窮盡即安全。
/// </summary>
public abstract record LockOutcome
{
    private LockOutcome() { }

    /// <summary>
    /// 取得工作鎖。<paramref name="NewlyAcquired"/> 為 true 表示本次插入或接管；false 表示呼叫者原本已持有，
    /// 讓上層失敗補償不會誤放既有 session 的鎖。
    /// </summary>
    public sealed record Acquired(bool NewlyAcquired = true) : LockOutcome;

    /// <summary>他人以未過期租約持有——附回讀的持鎖者資訊，供 <c>project_locked</c> 訊息。</summary>
    public sealed record Held(string LockedBy, string MachineName, DateTimeOffset LockedUtc) : LockOutcome;
}

/// <summary>
/// 單一未過期租約鎖的公開資訊（<see cref="ILockService.ListActiveAsync"/> 的元素）：供 <c>project.list</c>
/// 對線上案件顯示「🔒 由 {LockedBy} 開啟中」。<see cref="LockedUtc"/> 為開啟時間（非最後心跳）。
/// </summary>
public sealed record ProjectLockInfo(
    string ProjectId, string LockedBy, string MachineName, DateTimeOffset LockedUtc);

/// <summary>
/// 專案工作鎖的埠。開啟案件時取得、離開時釋放，避免另一個執行個體同時修改同案。
/// <b>provider 路由</b>：sqlServer 走租約表＋心跳；本地（sqlite/duckdb）走跨程序檔案鎖。
/// 埠置於 Domain（維持 Application 不依賴 Infrastructure，與 <see cref="IProjectRegistry"/>／<see cref="IAppConfigStore"/> 同層）。
/// </summary>
public interface ILockService
{
    /// <summary>
    /// 取專案租約鎖（原子臨界區）：空表插入、自己續租、或他人過期租約接管 → <see cref="LockOutcome.Acquired"/>；
    /// 他人以未過期租約持有 → <see cref="LockOutcome.Held"/>（附持鎖者資訊）。過期門檻讀自 app_config（缺鍵回程式常數）。
    /// </summary>
    Task<LockOutcome> AcquireAsync(string projectId, string principal, CancellationToken cancellationToken);

    /// <summary>心跳續租：只更新自己持有的列的 heartbeat_utc；被他人接管則 no-op。</summary>
    Task RenewAsync(string projectId, string principal, CancellationToken cancellationToken);

    /// <summary>釋放：只刪自己持有的列；釋放他人的為 no-op。</summary>
    Task ReleaseAsync(string projectId, string principal, CancellationToken cancellationToken);

    /// <summary>目前所有未過期的租約鎖（供 <c>project.list</c> 顯示鎖徽章）。</summary>
    Task<IReadOnlyList<ProjectLockInfo>> ListActiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 刪案前的非阻塞互斥結果。刪案需要一個帶完成語意的 lease：資料庫尚未成功刪除時離開 lease，
/// 不得誤放掉原本已由目前 session 持有的工作鎖；刪除 commit 後由 handler 明示完成。
/// </summary>
public abstract record ProjectDeletionLockOutcome
{
    private ProjectDeletionLockOutcome() { }

    public sealed record Acquired(IProjectDeletionLockLease Lease) : ProjectDeletionLockOutcome;

    public sealed record Held(string LockedBy, string MachineName, DateTimeOffset LockedUtc)
        : ProjectDeletionLockOutcome;
}

/// <summary>刪案互斥 lease；只有資料庫刪除已 commit 時才呼叫 <see cref="Complete"/>。</summary>
public interface IProjectDeletionLockLease : IAsyncDisposable
{
    void Complete();
}

/// <summary>
/// project.delete 專用互斥埠。它與一般工作鎖分開，讓 SQL Server 維持既有「刪案交易內清租約」語意，
/// 同時讓本地 provider 以同一把檔案鎖保護閒置中的案件。
/// </summary>
public interface IProjectDeletionLockService
{
    Task<ProjectDeletionLockOutcome> TryAcquireAsync(
        string projectId,
        string principal,
        CancellationToken cancellationToken);
}

/// <summary>
/// 專案租約鎖的參數（存在 <c>dbo.app_config</c>）：以 <see cref="IAppConfigStore.GetAsync"/>
/// 讀、缺鍵回程式常數預設。<see cref="HeartbeatSeconds"/> 由 <c>project.load</c> 回應帶給前端計時器；
/// <see cref="TimeoutSeconds"/> 於取鎖 SQL 綁 <c>@timeout</c>（心跳過期即由他人接管）。純常數＋解析，無框架相依。
/// </summary>
public static class ProjectLockDefaults
{
    /// <summary>心跳間隔預設（秒）：持有人每 30 秒更新 heartbeat_utc。</summary>
    public const int HeartbeatSeconds = 30;

    /// <summary>租約逾時預設（秒）：heartbeat_utc 早於「現在 − 120 秒」即視為過期、可被接管。</summary>
    public const int TimeoutSeconds = 120;

    /// <summary>app_config 鍵：心跳間隔（秒）。</summary>
    public const string HeartbeatSecondsKey = "lock.heartbeatSeconds";

    /// <summary>app_config 鍵：租約逾時（秒）。</summary>
    public const string TimeoutSecondsKey = "lock.timeoutSeconds";

    /// <summary>
    /// 把 app_config 取回的 value_json 解析為正整數；null／空／非正整數／不可解析一律回 <paramref name="fallback"/>
    /// （純函式，缺鍵回程式常數的統一落點——Application 與 Infrastructure 兩側共用）。
    /// </summary>
    public static int ParsePositive(string? raw, int fallback) =>
        int.TryParse(raw, out var value) && value > 0 ? value : fallback;
}
