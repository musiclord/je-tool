using JET.Domain;

namespace JET.AuditCore;

internal enum GlEffectivePopulationDisposition
{
    Effective,
    ExcludedByPeriod,
    ExcludedByPostingStatus
}

/// <summary>
/// 一列 GL 的分類：<see cref="Reason"/> 是 null 表示列入 GL，否則是未納入原因。
/// GL 範圍、控制總數與驗證摘要只看由原因推出的 <see cref="Disposition"/>；日期不符的三種原因合計就是日期不符
/// （使用者 2026-10-21 裁定分開標示）。
/// </summary>
internal readonly record struct GlEffectivePopulationClassification(GlExclusionReason? Reason)
{
    internal static GlEffectivePopulationClassification Effective => default;

    internal GlEffectivePopulationDisposition Disposition => Reason switch
    {
        null => GlEffectivePopulationDisposition.Effective,
        GlExclusionReason.BlankPostDate or GlExclusionReason.BeforePeriod or GlExclusionReason.AfterPeriod =>
            GlEffectivePopulationDisposition.ExcludedByPeriod,
        GlExclusionReason.PostingStatus => GlEffectivePopulationDisposition.ExcludedByPostingStatus,
        _ => throw new InvalidOperationException(
            "未知的 GL 分類：每一列只能列入 GL 或 GL(未納入測試)。")
    };

    internal bool IsEffective => Reason is null;

    internal string? StorageReason => Reason is { } reason ? GlEffectivePopulation.StorageReasonOf(reason) : null;
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
    internal const string BlankPostDateStorageReason = "blank_post_date";
    internal const string BeforePeriodStorageReason = "before_period";
    internal const string AfterPeriodStorageReason = "after_period";
    internal const string PostingStatusStorageReason = "posting_status";

    /// <summary><c>target_gl_entry.exclusion_reason</c> 的儲存值；四種原因各一個，列入 GL 的列存 NULL。</summary>
    internal static string StorageReasonOf(GlExclusionReason reason) => reason switch
    {
        GlExclusionReason.BlankPostDate => BlankPostDateStorageReason,
        GlExclusionReason.BeforePeriod => BeforePeriodStorageReason,
        GlExclusionReason.AfterPeriod => AfterPeriodStorageReason,
        GlExclusionReason.PostingStatus => PostingStatusStorageReason,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "未知的 GL 未納入原因。")
    };

    /// <summary>讀回儲存值；不是四種之一就是資料庫被改壞，直接丟出，不猜測原因。</summary>
    internal static GlExclusionReason ReasonOfStorage(string? storageReason) => storageReason switch
    {
        BlankPostDateStorageReason => GlExclusionReason.BlankPostDate,
        BeforePeriodStorageReason => GlExclusionReason.BeforePeriod,
        AfterPeriodStorageReason => GlExclusionReason.AfterPeriod,
        PostingStatusStorageReason => GlExclusionReason.PostingStatus,
        _ => throw new InvalidOperationException(
            $"未知的 GL 排除原因 storage token '{storageReason ?? "<null>"}'。")
    };

    /// <summary>
    /// 日期不符這組原因的 SQL 述詞：由 <see cref="GlEffectivePopulationClassification.Disposition"/> 是日期不符的原因產生，
    /// C# 的分組和 SQL 只有這一個來源（使用者 2026-10-21 裁定分開標示，驗證摘要的日期不符仍是三種原因的合計）。
    /// 儲存值是內部常數，直接嵌成字面值。
    /// </summary>
    internal static string ExcludedByPeriodSqlPredicate() =>
        "exclusion_reason IN (" + string.Join(", ", Enum.GetValues<GlExclusionReason>()
            .Where(static reason => new GlEffectivePopulationClassification(reason).Disposition
                == GlEffectivePopulationDisposition.ExcludedByPeriod)
            .Select(static reason => "'" + StorageReasonOf(reason) + "'")) + ")";

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
    /// 先判斷總帳日期空白、早於查核起日或晚於查核迄日，再判斷過帳狀態，確保每列只落入一個 disposition。
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
            throw new ArgumentException("GL 的查核期間起日不得晚於迄日。", nameof(periodStart));
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
            throw new ArgumentException("GL 的查核期間起日不得晚於迄日。", nameof(periodStart));
        }

        if (postingStatusMapped && matcher is null)
        {
            throw new ArgumentException(
                "已配對過帳狀態欄位時必須提供 canonical postingStatusPolicy。",
                nameof(matcher));
        }

        if (postDate is null)
        {
            return new GlEffectivePopulationClassification(GlExclusionReason.BlankPostDate);
        }

        if (postDate.Value < periodStart)
        {
            return new GlEffectivePopulationClassification(GlExclusionReason.BeforePeriod);
        }

        if (postDate.Value > periodEnd)
        {
            return new GlEffectivePopulationClassification(GlExclusionReason.AfterPeriod);
        }

        if (!postingStatusMapped)
        {
            return GlEffectivePopulationClassification.Effective;
        }

        var normalizedStatus = rawPostingStatus?.Trim() ?? string.Empty;
        var accepted = normalizedStatus.Length == 0
            ? matcher!.IncludeBlank
            : matcher!.Accepts(normalizedStatus);

        return accepted
            ? GlEffectivePopulationClassification.Effective
            : new GlEffectivePopulationClassification(GlExclusionReason.PostingStatus);
    }
}
