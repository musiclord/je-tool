using JET.Domain;

namespace JET.AuditCore;

internal enum GlExcludedPopulationDisposition
{
    Gl,
    ExcludedByPeriod,
    ExcludedByPostingStatus
}

/// <summary>
/// 一列 GL 的分類：<see cref="Reason"/> 是 null 表示列入 GL，否則是未納入原因。
/// GL 範圍、控制總數與驗證摘要只看由原因推出的 <see cref="Disposition"/>；日期不符的四種原因合計就是日期不符
/// （四種原因分開標示）。
/// </summary>
internal readonly record struct GlExcludedPopulationClassification(GlExclusionReason? Reason)
{
    internal static GlExcludedPopulationClassification Gl => default;

    internal GlExcludedPopulationDisposition Disposition => Reason switch
    {
        null => GlExcludedPopulationDisposition.Gl,
        GlExclusionReason.BlankGlDate or GlExclusionReason.UnrecognizedGlDate
            or GlExclusionReason.BeforePeriod or GlExclusionReason.AfterPeriod =>
            GlExcludedPopulationDisposition.ExcludedByPeriod,
        GlExclusionReason.PostingStatus => GlExcludedPopulationDisposition.ExcludedByPostingStatus,
        _ => throw new InvalidOperationException(
            "未知的 GL 分類：每一列只能列入 GL 或 GL(未納入測試)。")
    };

    internal bool IsExcluded => Reason is not null;

    internal string? StorageReason => Reason is { } reason ? GlExcludedPopulation.StorageReasonOf(reason) : null;
}

/// <summary>判斷過帳狀態值是否在接受的清單裡。整理分錄時每一列都要查，所以只建立一次，之後每列查一次雜湊表。</summary>
internal sealed class GlExcludedPopulationMatcher
{
    private readonly HashSet<string> _acceptedValues;

    internal GlExcludedPopulationMatcher(GlPostingStatusPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        IncludeBlank = policy.IncludeBlank;
        _acceptedValues = new HashSet<string>(
            policy.AcceptedValues,
            StringComparer.OrdinalIgnoreCase);
    }

    internal bool IncludeBlank { get; }

    internal bool Accepts(string normalizedStatus) => _acceptedValues.Contains(normalizedStatus);
}

/// <summary>
/// 分錄列入 GL 或 GL(未納入測試) 的規則只定義在這裡，不綁特定資料庫。整理分錄時先看總帳日期、再看過帳狀態，
/// 每一列只會有一種分類。之後的 SQL 一律只讀存好的 <c>is_excluded</c>，不重新判斷查核期間或過帳狀態。
/// </summary>
internal static class GlExcludedPopulation
{
    internal const string BlankGlDateStorageReason = "blank_gl_date";
    internal const string UnrecognizedGlDateStorageReason = "unrecognized_gl_date";
    internal const string BeforePeriodStorageReason = "before_period";
    internal const string AfterPeriodStorageReason = "after_period";
    internal const string PostingStatusStorageReason = "posting_status";

    /// <summary><c>gl.exclusion_reason</c> 的儲存值；五種原因各一個，列入 GL 的列存 NULL。</summary>
    internal static string StorageReasonOf(GlExclusionReason reason) => reason switch
    {
        GlExclusionReason.BlankGlDate => BlankGlDateStorageReason,
        GlExclusionReason.UnrecognizedGlDate => UnrecognizedGlDateStorageReason,
        GlExclusionReason.BeforePeriod => BeforePeriodStorageReason,
        GlExclusionReason.AfterPeriod => AfterPeriodStorageReason,
        GlExclusionReason.PostingStatus => PostingStatusStorageReason,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "未知的 GL 未納入原因。")
    };

    /// <summary>讀回儲存值；不是五種之一就是資料庫被改壞，直接丟出，不猜測原因。</summary>
    internal static GlExclusionReason ReasonOfStorage(string? storageReason) => storageReason switch
    {
        BlankGlDateStorageReason => GlExclusionReason.BlankGlDate,
        UnrecognizedGlDateStorageReason => GlExclusionReason.UnrecognizedGlDate,
        BeforePeriodStorageReason => GlExclusionReason.BeforePeriod,
        AfterPeriodStorageReason => GlExclusionReason.AfterPeriod,
        PostingStatusStorageReason => GlExclusionReason.PostingStatus,
        _ => throw new InvalidOperationException(
            $"未知的 GL 排除原因 storage token '{storageReason ?? "<null>"}'。")
    };

    /// <summary>
    /// 日期不符這組原因的 SQL 述詞：由 <see cref="GlExcludedPopulationClassification.Disposition"/> 是日期不符的原因產生，
    /// C# 的分組和 SQL 只有這一個來源（四種原因分開標示；驗證摘要的日期不符仍是四種原因的合計）。
    /// 儲存值是內部常數，直接嵌成字面值。
    /// </summary>
    internal static string ExcludedByPeriodSqlPredicate() =>
        "exclusion_reason IN (" + string.Join(", ", Enum.GetValues<GlExclusionReason>()
            .Where(static reason => new GlExcludedPopulationClassification(reason).Disposition
                == GlExcludedPopulationDisposition.ExcludedByPeriod)
            .Select(static reason => "'" + StorageReasonOf(reason) + "'")) + ")";

    /// <summary>
    /// 之後的查詢要「只看納入測試的分錄」時共用的 SQL 條件，只有這一個定義。
    /// <paramref name="tableAlias"/> 只能是呼叫端自己寫在 SQL 裡的資料表別名，不帶其他內容。
    /// </summary>
    internal static string SqlPredicate(string? tableAlias = null)
    {
        if (tableAlias is null)
        {
            return "is_excluded = 0";
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(tableAlias);
        return $"{tableAlias}.is_excluded = 0";
    }

    /// <summary>
    /// 查詢要「只看未納入測試的分錄」時共用的 SQL 條件，只有這一個定義；資料預覽的未納入測試頁與資料驗證的筆數都用它。
    /// <paramref name="tableAlias"/> 只能是呼叫端自己寫在 SQL 裡的資料表別名，不帶其他內容。
    /// </summary>
    internal static string ExcludedSqlPredicate(string? tableAlias = null)
    {
        if (tableAlias is null)
        {
            return "is_excluded = 1";
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(tableAlias);
        return $"{tableAlias}.is_excluded = 1";
    }

    /// <summary>
    /// 檢查「有沒有配對過帳狀態欄位」和「有沒有提供過帳狀態設定」是否一致，並整理出可以直接保存、逐列分類用的設定。
    /// 接受的值先去掉頭尾空白並丟掉空字串，再不分大小寫去掉重複；重複的值保留第一次出現的寫法與順序。
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
    /// 先判斷總帳日期無法辨識、空白、早於查核起日或晚於查核迄日，再判斷過帳狀態，確保每列只落入一個 disposition。
    /// 日期邊界為閉區間；未配對過帳狀態欄位時，期內列一律有效。
    /// </summary>
    internal static GlExcludedPopulationClassification Classify(
        DateOnly? glDate,
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
            glDate,
            rawPostingStatus,
            periodStart,
            periodEnd,
            postingStatusMapped,
            normalizedPolicy);
    }

    /// <summary>
    /// 傳入的過帳狀態設定已經由 <see cref="NormalizePolicy"/> 整理過，這裡不再整理一次。
    /// 每次呼叫仍會建立一個 <see cref="GlExcludedPopulationMatcher"/>；要逐列分類時改用
    /// <see cref="ClassifyWithMatcher"/>，重用同一個。
    /// </summary>
    internal static GlExcludedPopulationClassification ClassifyNormalized(
        DateOnly? glDate,
        string? rawPostingStatus,
        DateOnly periodStart,
        DateOnly periodEnd,
        bool postingStatusMapped,
        GlPostingStatusPolicy? normalizedPolicy) =>
        ClassifyWithMatcher(
            glDate,
            rawPostingStatus,
            periodStart,
            periodEnd,
            postingStatusMapped,
            normalizedPolicy is null ? null : new GlExcludedPopulationMatcher(normalizedPolicy));

    internal static GlExcludedPopulationClassification ClassifyWithMatcher(
        DateOnly? glDate,
        string? rawPostingStatus,
        DateOnly periodStart,
        DateOnly periodEnd,
        bool postingStatusMapped,
        GlExcludedPopulationMatcher? matcher,
        bool glDateUnrecognized = false)
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

        // 來源有內容卻讀不成日期，和空白分開記，審計員才知道要回頭看來源寫了什麼。
        if (glDateUnrecognized)
        {
            return new GlExcludedPopulationClassification(GlExclusionReason.UnrecognizedGlDate);
        }

        if (glDate is null)
        {
            return new GlExcludedPopulationClassification(GlExclusionReason.BlankGlDate);
        }

        if (glDate.Value < periodStart)
        {
            return new GlExcludedPopulationClassification(GlExclusionReason.BeforePeriod);
        }

        if (glDate.Value > periodEnd)
        {
            return new GlExcludedPopulationClassification(GlExclusionReason.AfterPeriod);
        }

        if (!postingStatusMapped)
        {
            return GlExcludedPopulationClassification.Gl;
        }

        var normalizedStatus = rawPostingStatus?.Trim() ?? string.Empty;
        var accepted = normalizedStatus.Length == 0
            ? matcher!.IncludeBlank
            : matcher!.Accepts(normalizedStatus);

        return accepted
            ? GlExcludedPopulationClassification.Gl
            : new GlExcludedPopulationClassification(GlExclusionReason.PostingStatus);
    }
}
