namespace JET.Domain;

/// <summary>
/// 規則執行摘要的持久化（result_rule_run）：runId + 完整 response JSON。
/// project.load 以 raw summary resume；必要時可由後端 renderer 補上衍生欄位。
/// </summary>
public sealed record RuleRunRecord(
    string RunId,
    string RunKind,
    DateTimeOffset GeneratedUtc,
    string SummaryJson);

public static class RuleRunKinds
{
    public const string Validate = "validate";
    public const string Prescreen = "prescreen";
}

public interface IRuleRunStore
{
    Task SaveAsync(string projectId, RuleRunRecord record, CancellationToken cancellationToken);

    Task<RuleRunRecord?> FindLatestAsync(string projectId, string runKind, CancellationToken cancellationToken);
}

/// <summary>
/// Schema v7 的衍生結果失效狀態。true 表示該類結果曾存在、但已被上游 mutation
/// 失效；從未執行仍為 false，因此不能由 latest result 是否為 null 反推。
/// </summary>
public sealed record AuditResultStaleState(
    bool Validation,
    bool Prescreen,
    bool Filter);

public interface IResultStaleStateStore
{
    /// <summary>準備日變更時使預篩選與篩選結果失效；保留驗證、抽樣與已儲存的情境定義。</summary>
    Task InvalidateForPreparationDateChangeAsync(string projectId, CancellationToken cancellationToken);

    Task<AuditResultStaleState> ReadAsync(
        string projectId,
        CancellationToken cancellationToken);

    /// <summary>影響篩選的來源變更版本；獨立於尚未重算的其他情境。</summary>
    Task<string> ReadFilterDataRevisionAsync(string projectId, CancellationToken cancellationToken);
}

/// <summary>
/// 已保存的篩選情境定義（config_filter_scenario；replace-all、上限 10）。
/// DefinitionJson 為前端送入的完整 scenario JSON（含 name/rationale/groups），
/// resume 時原樣回放。
/// </summary>
public sealed record SavedFilterScenario(
    int Position,
    string Name,
    string Rationale,
    string DefinitionJson,
    DateTimeOffset SavedUtc);

public interface IFilterScenarioStore
{
    Task ReplaceAllAsync(
        string projectId,
        IReadOnlyList<SavedFilterScenario> scenarios,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SavedFilterScenario>> ListAsync(
        string projectId,
        CancellationToken cancellationToken);
}
