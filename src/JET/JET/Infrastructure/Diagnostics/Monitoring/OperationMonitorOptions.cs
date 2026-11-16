namespace JET.Infrastructure;

/// <summary>
/// 操作監看模組的門檻。正式程式用預設值；測試可以縮短，讓跑太久的情形在幾百毫秒內出現。
/// </summary>
public sealed record OperationMonitorOptions
{
    public static OperationMonitorOptions Default { get; } = new();

    /// <summary>背景檢查進行中的操作與 SQL 的間隔。</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 一次進入 SQLite（執行語句或讀下一列）連續超過這個時間，記一筆還在跑的 sql.slow；
    /// 語句結束時花在 SQLite 裡的時間合計超過這個時間，再記一筆跑完的 sql.slow。
    /// </summary>
    public TimeSpan SlowSqlThreshold { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>同一條 SQL 還沒跑完時，每隔這個時間再記一筆 sql.slow。</summary>
    public TimeSpan SlowSqlRepeat { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>操作跑超過這個時間，記第一筆 action.still_running。</summary>
    public TimeSpan StillRunningAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>之後每隔這個時間再記一筆 action.still_running。</summary>
    public TimeSpan StillRunningRepeat { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>匯出進度沒有換階段、換工作表時，最多每隔這個時間記一筆 export.progress。</summary>
    public TimeSpan ExportProgressInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 操作跑超過這個時間，或有跑太久、失敗的 SQL、執行中要求過取消、記過 action.still_running 時，
    /// action.summary 記成 Information，支援日誌也收得到。其餘執行過 SQL 或進入過步驟的操作，摘要只記在診斷日誌；
    /// 兩者都沒有的短操作不記摘要。
    /// </summary>
    public TimeSpan SummaryAfter { get; init; } = TimeSpan.FromSeconds(5);
}
