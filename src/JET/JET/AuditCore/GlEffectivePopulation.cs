using JET.Domain;

namespace JET.AuditCore;

internal enum GlEffectivePopulationDisposition
{
    Effective,
    ExcludedByPeriod,
    ExcludedByPostingStatus
}

internal readonly record struct GlEffectivePopulationClassification(
    GlEffectivePopulationDisposition Disposition)
{
    internal bool IsEffective => Disposition == GlEffectivePopulationDisposition.Effective;

    internal string? StorageReason => Disposition switch
    {
        GlEffectivePopulationDisposition.Effective => null,
        GlEffectivePopulationDisposition.ExcludedByPeriod =>
            GlEffectivePopulation.PeriodStorageReason,
        GlEffectivePopulationDisposition.ExcludedByPostingStatus =>
            GlEffectivePopulation.PostingStatusStorageReason,
        _ => throw new ArgumentOutOfRangeException(
            nameof(Disposition),
            Disposition,
            "未知的 GL 有效母體分類。")
    };
}

/// <summary>投影熱路徑重用的 posting-status matcher；建構一次，逐列 O(1) 查找。</summary>
internal sealed class GlEffectivePopulationMatcher
{
    private readonly HashSet<string> acceptedValues;

    internal GlEffectivePopulationMatcher(GlPostingStatusPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        IncludeBlank = policy.IncludeBlank;
        acceptedValues = new HashSet<string>(
            policy.AcceptedValues,
            StringComparer.OrdinalIgnoreCase);
    }

    internal bool IncludeBlank { get; }

    internal bool Accepts(string normalizedStatus) => acceptedValues.Contains(normalizedStatus);
}

/// <summary>
/// GL 有效分錄母體的 provider-neutral 單一契約。投影以日期優先形成互斥分類；
/// 所有下游 SQL 只消費已落地的 <c>is_effective</c>，不重做期間或過帳政策。
/// </summary>
internal static class GlEffectivePopulation
{
    internal const string PeriodStorageReason = "period";
    internal const string PostingStatusStorageReason = "posting_status";

    /// <summary>
    /// 下游查詢共用的唯一有效母體 SQL 述詞。Alias 只接受呼叫端持有的 bare table alias。
    /// </summary>
    internal static string SqlPredicate(string? tableAlias = null)
    {
        if (tableAlias is null)
        {
            return "is_effective = 1";
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(tableAlias);
        return $"{tableAlias}.is_effective = 1";
    }

    /// <summary>
    /// 驗證 mapping 與 posting policy 的配對，並產生可直接保存及逐列分類的 canonical policy。
    /// Accepted values 保留第一個出現的 casing／順序，先 trim、去空，再以 OrdinalIgnoreCase 去重。
    /// </summary>
    internal static GlPostingStatusPolicy? NormalizePolicy(
        bool postingStatusMapped,
        GlPostingStatusPolicy? policy)
    {
        if (!postingStatusMapped)
        {
            if (policy is not null)
            {
                throw new ArgumentException(
                    "未配對過帳狀態欄位時不得提供 postingStatusPolicy。",
                    nameof(policy));
            }

            return null;
        }

        if (policy is null)
        {
            throw new ArgumentException(
                "已配對過帳狀態欄位時必須提供 postingStatusPolicy。",
                nameof(policy));
        }

        var acceptedValues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceValues = policy.AcceptedValues ?? [];

        foreach (var rawValue in sourceValues)
        {
            var normalizedValue = rawValue?.Trim();
            if (string.IsNullOrEmpty(normalizedValue) || !seen.Add(normalizedValue))
            {
                continue;
            }

            acceptedValues.Add(normalizedValue);
        }

        if (!policy.IncludeBlank && acceptedValues.Count == 0)
        {
            throw new ArgumentException(
                "postingStatusPolicy 至少需要一個非空白 accepted value，或設定 includeBlank。",
                nameof(policy));
        }

        return new GlPostingStatusPolicy(acceptedValues, policy.IncludeBlank);
    }

    /// <summary>
    /// 先判斷空白／期外過帳日，再判斷過帳狀態，確保每列只落入一個 disposition。
    /// 日期邊界為閉區間；未配對過帳狀態欄位時，期內列一律有效。
    /// </summary>
    internal static GlEffectivePopulationClassification Classify(
        DateOnly? postDate,
        string? rawPostingStatus,
        DateOnly periodStart,
        DateOnly periodEnd,
        bool postingStatusMapped,
        GlPostingStatusPolicy? policy)
    {
        if (periodStart > periodEnd)
        {
            throw new ArgumentException("GL 有效母體期間起日不得晚於迄日。", nameof(periodStart));
        }

        var normalizedPolicy = NormalizePolicy(postingStatusMapped, policy);
        return ClassifyNormalized(
            postDate,
            rawPostingStatus,
            periodStart,
            periodEnd,
            postingStatusMapped,
            normalizedPolicy);
    }

    /// <summary>
    /// 投影熱路徑使用已由 plan 正規化的 policy，避免逐列重新配置集合。
    /// </summary>
    internal static GlEffectivePopulationClassification ClassifyNormalized(
        DateOnly? postDate,
        string? rawPostingStatus,
        DateOnly periodStart,
        DateOnly periodEnd,
        bool postingStatusMapped,
        GlPostingStatusPolicy? normalizedPolicy) =>
        ClassifyWithMatcher(
            postDate,
            rawPostingStatus,
            periodStart,
            periodEnd,
            postingStatusMapped,
            normalizedPolicy is null ? null : new GlEffectivePopulationMatcher(normalizedPolicy));

    internal static GlEffectivePopulationClassification ClassifyWithMatcher(
        DateOnly? postDate,
        string? rawPostingStatus,
        DateOnly periodStart,
        DateOnly periodEnd,
        bool postingStatusMapped,
        GlEffectivePopulationMatcher? matcher)
    {
        if (periodStart > periodEnd)
        {
            throw new ArgumentException("GL 有效母體期間起日不得晚於迄日。", nameof(periodStart));
        }

        if (postingStatusMapped && matcher is null)
        {
            throw new ArgumentException(
                "已配對過帳狀態欄位時必須提供 canonical postingStatusPolicy。",
                nameof(matcher));
        }

        if (postDate is null || postDate.Value < periodStart || postDate.Value > periodEnd)
        {
            return new GlEffectivePopulationClassification(
                GlEffectivePopulationDisposition.ExcludedByPeriod);
        }

        if (!postingStatusMapped)
        {
            return new GlEffectivePopulationClassification(
                GlEffectivePopulationDisposition.Effective);
        }

        var normalizedStatus = rawPostingStatus?.Trim() ?? string.Empty;
        var accepted = normalizedStatus.Length == 0
            ? matcher!.IncludeBlank
            : matcher!.Accepts(normalizedStatus);

        return new GlEffectivePopulationClassification(
            accepted
                ? GlEffectivePopulationDisposition.Effective
                : GlEffectivePopulationDisposition.ExcludedByPostingStatus);
    }
}
