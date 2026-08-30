namespace JET.Domain;

/// <summary>
/// 投影結果。Errors 非空時代表 repository 已 rollback、target 未寫入任何資料。
/// </summary>
public sealed record ProjectionResult(
    int ProjectedRowCount,
    IReadOnlyList<RowProjectionError> Errors)
{
    /// <summary>
    /// 全來源列掃描所得的錯誤總數。Errors 只保存有界樣本，不能拿樣本數冒充總數。
    /// </summary>
    public int TotalErrorCount { get; init; } = Errors.Count;

    /// <summary>GL 投影的有效母體控制總數；TB 投影及失敗結果維持 null。</summary>
    public GlEffectivePopulationTotals? EffectivePopulation { get; init; }

    /// <summary>
    /// 非阻斷的提交後提醒（投影已成功）。目前用於「必填文字欄整欄空白，疑似配錯欄」
    /// （如重複標頭中的空白欄）。預設空；前端在配對提交成功後一併呈現給使用者。
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// GL 投影同一交易內形成的互斥母體控制總數。RawRowCount 等於實際保留於 target 的列數；
/// Effective + 兩類 exclusion 必須精確分割 raw。
/// </summary>
public sealed record GlEffectivePopulationTotals(
    long RawRowCount,
    long EffectiveRowCount,
    long ExcludedByPeriodCount,
    long ExcludedByPostingStatusCount,
    long EffectiveDebitScaled,
    long EffectiveCreditScaled);

/// <summary>staging → target 投影的非權威進度快照。</summary>
public sealed record ProjectionProgress(long RowsProcessed);

public interface IGlRepository
{
    Task<ProjectionResult> ProjectStagingToTargetAsync(
        string projectId,
        string batchId,
        GlMappingSpec spec,
        int moneyScale,
        DateParseOptions dateOptions,
        DateOnly periodStart,
        DateOnly periodEnd,
        bool postingStatusMapped,
        GlPostingStatusPolicy? postingStatusPolicy,
        DateTimeOffset committedUtc,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null);
}

/// <summary>
/// 舊有 repository 單元測試的 source-compatibility 入口。Production mapping lifecycle 不得使用；
/// 它只支援未配對 posting status 的既有 fixture，並以全日期域表達「fixture 未宣告案件期間」。
/// 新 provider 仍必須實作完整有效母體介面，不能藉此遺漏 policy。
/// </summary>
internal static class GlRepositoryFixtureCompatibilityExtensions
{
    internal static Task<ProjectionResult> ProjectStagingToTargetAsync(
        this IGlRepository repository,
        string projectId,
        string batchId,
        GlMappingSpec spec,
        int moneyScale,
        DateParseOptions dateOptions,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Mapping.TryGetValue(GlMappingKeys.PostingStatus, out var sourceColumn)
            && !string.IsNullOrWhiteSpace(sourceColumn))
        {
            throw new InvalidOperationException(
                "舊 fixture projection 入口不得用於已配對 posting status 的 mapping。");
        }

        return repository.ProjectStagingToTargetAsync(
            projectId,
            batchId,
            spec,
            moneyScale,
            dateOptions,
            DateOnly.MinValue,
            DateOnly.MaxValue,
            postingStatusMapped: false,
            postingStatusPolicy: null,
            committedUtc: DateTimeOffset.UnixEpoch,
            cancellationToken,
            progress);
    }
}

public interface ITbRepository
{
    Task<ProjectionResult> ProjectStagingToTargetAsync(
        string projectId,
        string batchId,
        TbMappingSpec spec,
        int moneyScale,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null);
}
