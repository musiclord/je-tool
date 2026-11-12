namespace JET.Domain;

/// <summary>驗證結果或預篩選結果。兩種結果分開存放，只在程式裡用這個值分辨。</summary>
public enum ResultKind
{
    Validation,
    Prescreen
}

/// <summary>
/// 一次資料驗證或預篩選的結果：結果編號、產生時間與完整回應 JSON。
/// 驗證結果存在 validation_result，預篩選結果存在 prescreen_result；<see cref="Kind"/> 不寫進資料庫，
/// 由讀出的那張表決定。project.load 以存下的摘要接續；必要時由後端 renderer 補上衍生欄位。
/// </summary>
public sealed record ResultRecord(
    string ResultId,
    ResultKind Kind,
    DateTimeOffset GeneratedUtc,
    string SummaryJson);

public interface IResultStore
{
    Task SaveAsync(string projectId, ResultRecord record, CancellationToken cancellationToken);

    Task<ResultRecord?> FindLatestAsync(string projectId, ResultKind kind, CancellationToken cancellationToken);
}

/// <summary>
/// 衍生結果失效狀態。true 表示該類結果曾存在、但已被上游 mutation
/// 失效；從未執行仍為 false，因此不能由 latest result 是否為 null 反推。
/// </summary>
public sealed record AuditResultStaleState(
    bool Validation,
    bool Prescreen,
    bool Filter);

public interface IResultStaleStateStore
{
    /// <summary>
    /// 準備日變更時清除預篩選、篩選命中與已儲存情境，保留驗證與抽樣。清除放在同一筆交易裡，
    /// 提交前先執行 <paramref name="saveSettings"/>（寫入新設定）；它失敗時交易回復，資料庫不變。
    /// </summary>
    Task InvalidateForPreparationDateChangeAsync(
        string projectId, Func<CancellationToken, Task> saveSettings, CancellationToken cancellationToken);

    Task<AuditResultStaleState> ReadAsync(
        string projectId,
        CancellationToken cancellationToken);

    /// <summary>影響篩選的來源變更版本；獨立於尚未重算的其他情境。</summary>
    Task<string> ReadFilterDataRevisionAsync(string projectId, CancellationToken cancellationToken);
}

/// <summary>
/// 已保存的篩選情境定義（filter_scenario；上限 10）。
/// DefinitionJson 為 scenario JSON（含 name/rationale/groups），resume 時原樣回放。
/// 情境儲存時，那一個情境的每條條件只留該類型用得到的欄位（FilterConditionWireFields）；情境層、條件組層，
/// 以及尚未重新儲存的舊情境照原內容保留。
/// Position 是畫面上的順序（從 1 起算）；<see cref="ScenarioId"/> 是這個情境不變的編號，結果依它存放，
/// 移除別的情境時不會改變。沒有指定時等於 Position，所以一次寫入整批的新清單就是 1 到 n。
/// </summary>
public sealed record SavedFilterScenario(
    int Position,
    string Name,
    string Rationale,
    string DefinitionJson,
    DateTimeOffset SavedUtc)
{
    public int ScenarioId { get; init; } = Position;
}

public interface IFilterScenarioStore
{
    /// <summary>整批取代定義並清掉全部結果，篩選結果標成過期；只用在情境有問題、結果不能發布的時候。</summary>
    Task ReplaceAllAsync(
        string projectId,
        IReadOnlyList<SavedFilterScenario> scenarios,
        CancellationToken cancellationToken);

    /// <summary>
    /// 移除一個情境：同一個交易裡刪掉它的定義與結果，後面的情境在畫面上的順序往前補。
    /// 其他情境的定義、保存時間與結果都不動，也不重算；結果過期旗標不變。
    /// </summary>
    Task RemoveAsync(
        string projectId,
        SavedFilterScenario scenario,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SavedFilterScenario>> ListAsync(
        string projectId,
        CancellationToken cancellationToken);
}
