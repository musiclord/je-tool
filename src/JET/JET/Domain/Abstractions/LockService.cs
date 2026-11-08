namespace JET.Domain;

/// <summary>
/// 取鎖結果（<see cref="ILockService.AcquireAsync"/> 的回傳型別）：封閉層級——只有
/// <see cref="Acquired"/>（取得工作鎖）與 <see cref="Held"/>（另一個 JET 正持有，附持鎖者資訊）。
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

    /// <summary>另一個 JET 正持有——附持鎖者資訊，供 <c>project_locked</c> 訊息。</summary>
    public sealed record Held(string LockedBy, string MachineName, DateTimeOffset LockedUtc) : LockOutcome;
}

/// <summary>
/// 專案工作鎖的埠。開啟案件時取得、離開時釋放，避免另一個執行個體同時修改同案。
/// SQLite 案件用跨程序檔案鎖。埠置於 Domain，維持 Application 不依賴 Infrastructure。
/// </summary>
public interface ILockService
{
    /// <summary>
    /// 取工作鎖：沒有人持有或自己已持有 → <see cref="LockOutcome.Acquired"/>；
    /// 另一個 JET 正持有 → <see cref="LockOutcome.Held"/>（附持鎖者資訊）。
    /// </summary>
    Task<LockOutcome> AcquireAsync(string projectId, string principal, CancellationToken cancellationToken);

    /// <summary>釋放：只放開自己持有的鎖；不是自己持有的不做事。</summary>
    Task ReleaseAsync(string projectId, string principal, CancellationToken cancellationToken);
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
/// project.delete 專用互斥埠。它與一般工作鎖分開，因為刪案的 lease 要等資料庫刪除成功才算完成
/// （見 <see cref="IProjectDeletionLockLease"/>）。SQLite 案件以工作鎖的同一把檔案鎖保護閒置中的案件。
/// </summary>
public interface IProjectDeletionLockService
{
    Task<ProjectDeletionLockOutcome> TryAcquireAsync(
        string projectId,
        string principal,
        CancellationToken cancellationToken);
}
