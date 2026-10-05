namespace JET.Domain;

/// <summary>同一 filter revision 的 GL 母體範圍。</summary>
public enum GlPopulationScope
{
    AuditPeriod
}

/// <summary>wire／持久化使用的正準 populationScope 值與人類可讀標籤。</summary>
public static class GlPopulationScopeValues
{
    public const string AuditPeriod = "auditPeriod";

    public static string ToValue(GlPopulationScope scope) => scope switch
    {
        GlPopulationScope.AuditPeriod => AuditPeriod,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "未知的 GL 母體範圍。")
    };

    public static string DisplayName(GlPopulationScope scope) => scope switch
    {
        GlPopulationScope.AuditPeriod => "查核期間",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "未知的 GL 母體範圍。")
    };

    public static bool TryParse(string? value, out GlPopulationScope scope)
    {
        scope = value switch
        {
            AuditPeriod => GlPopulationScope.AuditPeriod,
            _ => default
        };

        return value is AuditPeriod;
    }
}

/// <summary>
/// 只描述一個 GL 母體所需的窄上下文。tag matrix 與其他只做母體裁切的查詢
/// 不需要知道金額倍率、假日或篩選條件，因此不傳整個 <see cref="FilterRuleContext"/>。
/// </summary>
public sealed record GlPopulationContext(
    GlPopulationScope PopulationScope,
    string PeriodStart,
    string PeriodEnd);

/// <summary>
/// filter.preview 的執行上下文與結果。本版為無狀態查詢
/// （COUNT + COUNT DISTINCT + LIMIT 50 預覽），不落地結果
/// （命中落地由 filter.commit 寫入 result_filter_run）。
/// PeriodStart/PeriodEnd 固定界定查核期間母體（專案必填欄位）。
/// </summary>
public sealed record FilterRuleContext(
    int MoneyScale,
    string? LastPeriodStart,
    string PeriodStart,
    string PeriodEnd,
    IReadOnlyList<int>? NonWorkingDays = null,
    GlPopulationScope PopulationScope = GlPopulationScope.AuditPeriod)
{
    public DateParseOptions DateParseOptions { get; init; } = DateParseOptions.Default;

    /// <summary>
    /// typed 條件（type:"typed"）編譯所需的 RDE 欄位 registry（fieldId → value type）。
    /// 預設空集合＝任何 typed 條件在編譯前即 fail loud，不會靜默編出錯誤 SQL。
    /// 來源是目前案件 committed GL mapping 的 RDE definitions（單一權威，不另建第二份目錄）。
    /// </summary>
    public IReadOnlyList<GlRdeFieldMetadata> RdeFields { get; init; } = [];
}

public sealed record FilterPreviewRow(
    string? DocumentNumber,
    string? LineItem,
    string? PostDate,
    string? AccountCode,
    string? AccountName,
    string? DocumentDescription,
    long AmountScaled,
    string DrCr);

public sealed record FilterPreviewResult(
    long Count,
    long VoucherCount,
    IReadOnlyList<FilterPreviewRow> PreviewRows);

public interface IFilterRunRepository
{
    Task<FilterPreviewResult> PreviewAsync(
        string projectId,
        FilterScenarioSpec scenario,
        FilterRuleContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// filter.commit 命中落地的輸入單元：已解析的情境 spec + 其保存位置。
/// 解析（definition JSON → spec）屬 Application 層（FilterScenarioPayloadParser）；Infrastructure
/// 的 materializer 只吃 Domain 型別，不反向依賴 Application（分層規則：Infrastructure 僅引用 Domain）。
/// </summary>
public sealed record MaterializableScenario(int Position, FilterScenarioSpec Spec);

/// <summary>
/// filter.commit 的整批發布邊界。情境定義與命中 entry_id 必須由同一 provider 實作於
/// 同一 transaction 內 replace，失敗或取消時保留前一個完整 revision。
/// </summary>
public sealed record FilterCommitItem(
    SavedFilterScenario Definition,
    FilterScenarioSpec Spec);

public interface IFilterCommitRepository
{
    Task CommitAsync(
        string projectId,
        IReadOnlyList<FilterCommitItem> items,
        FilterRuleContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// filter.commit 命中落地：對每個已存情境把命中的 entry_id 落地到
/// result_filter_run，供 query.filterHitsPage keyset 分頁回取。契約置於 Domain（同 IFilterRunRepository），
/// 供 Application handler 注入；provider 實作與路由在 Infrastructure。
/// </summary>
public interface IFilterRunMaterializer
{
    /// <summary>
    /// replaceAll 為 true 時替換全案並清除篩選失效旗標；false 只替換 scenarios 內的情境位置。
    /// 所選重算不修改其他情境的命中，也不把全案旗標改成已更新。刪寫及旗標更新必須在同一交易。
    /// </summary>
    Task MaterializeAsync(
        string projectId,
        IReadOnlyList<MaterializableScenario> scenarios,
        FilterRuleContext context,
        CancellationToken cancellationToken,
        bool replaceAll = true);
}
