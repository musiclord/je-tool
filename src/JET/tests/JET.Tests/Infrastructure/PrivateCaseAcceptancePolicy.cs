using System.Text.Json.Serialization;

namespace JET.Tests.Infrastructure;

internal enum PrivateCaseReportComparisonMode
{
    ContentAndAppearance = 1,
}

internal enum PrivateCaseInfVerificationMode
{
    RulesAndEffectivePopulation = 1,
}

internal enum PrivateCaseCleanupMode
{
    AlwaysDelete = 1,
}

internal enum PrivateCaseAcceptancePolicyFailure
{
    UnsupportedPolicy,
}

internal sealed class PrivateCaseAcceptancePolicyException : InvalidOperationException
{
    internal PrivateCaseAcceptancePolicyException(PrivateCaseAcceptancePolicyFailure failure)
        : base($"私人案件驗收規則無效（{failure}）。")
    {
        Failure = failure;
    }

    internal PrivateCaseAcceptancePolicyFailure Failure { get; }
}

/// <summary>
/// Phase 6 的固定驗收規則。這些值來自使用者決定，不接受環境變數或執行參數改寫。
/// </summary>
internal sealed class PrivateCaseAcceptancePolicy
{
    internal const int RequiredInfSampleSize = 59;

    private PrivateCaseAcceptancePolicy(
        PrivateCaseReportComparisonMode reportComparison,
        PrivateCaseInfVerificationMode infVerification,
        PrivateCaseCleanupMode cleanupMode,
        int infSampleSize)
    {
        ReportComparison = reportComparison;
        InfVerification = infVerification;
        CleanupMode = cleanupMode;
        InfSampleSize = infSampleSize;
    }

    internal static PrivateCaseAcceptancePolicy Current { get; } = Create(
        PrivateCaseReportComparisonMode.ContentAndAppearance,
        PrivateCaseInfVerificationMode.RulesAndEffectivePopulation,
        PrivateCaseCleanupMode.AlwaysDelete,
        RequiredInfSampleSize);

    public string ReportComparisonId => "content-and-appearance";

    public string InfVerificationId => "rules-and-effective-population";

    public string CleanupId => "always-delete";

    public int InfSampleSize { get; }

    [JsonIgnore]
    internal PrivateCaseReportComparisonMode ReportComparison { get; }

    [JsonIgnore]
    internal PrivateCaseInfVerificationMode InfVerification { get; }

    [JsonIgnore]
    internal PrivateCaseCleanupMode CleanupMode { get; }

    internal static PrivateCaseAcceptancePolicy Create(
        PrivateCaseReportComparisonMode reportComparison,
        PrivateCaseInfVerificationMode infVerification,
        PrivateCaseCleanupMode cleanupMode,
        int infSampleSize)
    {
        if (reportComparison != PrivateCaseReportComparisonMode.ContentAndAppearance
            || infVerification != PrivateCaseInfVerificationMode.RulesAndEffectivePopulation
            || cleanupMode != PrivateCaseCleanupMode.AlwaysDelete
            || infSampleSize != RequiredInfSampleSize)
        {
            throw new PrivateCaseAcceptancePolicyException(
                PrivateCaseAcceptancePolicyFailure.UnsupportedPolicy);
        }

        return new PrivateCaseAcceptancePolicy(
            reportComparison,
            infVerification,
            cleanupMode,
            infSampleSize);
    }

    public override string ToString() => "private case acceptance policy";
}
