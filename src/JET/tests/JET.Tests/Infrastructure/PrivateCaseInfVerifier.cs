using System.Text.Json;

namespace JET.Tests.Infrastructure;

internal enum PrivateCaseInfVerificationFailure
{
    InvalidEvidence,
}

internal sealed class PrivateCaseInfVerificationException : InvalidOperationException
{
    internal PrivateCaseInfVerificationException(PrivateCaseInfVerificationFailure failure)
        : base($"私人案件的 INF 驗證資料無效（{failure}）。")
    {
        Failure = failure;
    }

    internal PrivateCaseInfVerificationFailure Failure { get; }
}

internal sealed record PrivateCaseInfVerificationFacts(
    long RawPopulationRowCount,
    long EffectivePopulationRowCount,
    long ExcludedPopulationRowCount,
    long ExcludedByPeriodRowCount,
    long ExcludedByPostingStatusRowCount,
    long ReportedSampleSize,
    long WalkedSampleRowCount)
{
    internal static PrivateCaseInfVerificationFacts Capture(
        JsonElement validation,
        long walkedSampleRowCount)
    {
        try
        {
            var population = RequiredObject(validation, "populationSummary");
            var raw = RequiredObject(population, "raw");
            var effective = RequiredObject(population, "effective");
            var excluded = RequiredObject(population, "excluded");
            var inf = RequiredObject(validation, "infSamplingTest");
            return new PrivateCaseInfVerificationFacts(
                RequiredNonNegativeInt64(raw, "rowCount"),
                RequiredNonNegativeInt64(effective, "rowCount"),
                RequiredNonNegativeInt64(excluded, "rowCount"),
                RequiredNonNegativeInt64(excluded, "byPeriodCount"),
                RequiredNonNegativeInt64(excluded, "byPostingStatusCount"),
                RequiredNonNegativeInt64(inf, "sampleSize"),
                NonNegative(walkedSampleRowCount));
        }
        catch (PrivateCaseInfVerificationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or KeyNotFoundException
            or OverflowException)
        {
            throw Error();
        }
    }

    private static JsonElement RequiredObject(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw Error();
        }
        return value;
    }

    private static long RequiredNonNegativeInt64(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result))
        {
            throw Error();
        }
        return NonNegative(result);
    }

    private static long NonNegative(long value) => value >= 0 ? value : throw Error();

    private static PrivateCaseInfVerificationException Error() =>
        new(PrivateCaseInfVerificationFailure.InvalidEvidence);
}

internal sealed class PrivateCaseInfVerificationResult
{
    internal PrivateCaseInfVerificationResult(
        PrivateCaseInfVerificationFacts facts,
        int requiredSampleLimit)
    {
        RawPopulationRowCount = facts.RawPopulationRowCount;
        EffectivePopulationRowCount = facts.EffectivePopulationRowCount;
        ExcludedPopulationRowCount = facts.ExcludedPopulationRowCount;
        ExcludedByPeriodRowCount = facts.ExcludedByPeriodRowCount;
        ExcludedByPostingStatusRowCount = facts.ExcludedByPostingStatusRowCount;
        ReportedSampleSize = facts.ReportedSampleSize;
        WalkedSampleRowCount = facts.WalkedSampleRowCount;
        RequiredSampleLimit = requiredSampleLimit;

        PopulationPartitionMatches = RawPopulationRowCount
            == checked(EffectivePopulationRowCount + ExcludedPopulationRowCount);
        ExcludedPartitionMatches = ExcludedPopulationRowCount
            == checked(ExcludedByPeriodRowCount + ExcludedByPostingStatusRowCount);
        SampleSizeMatchesRule = ReportedSampleSize
            == Math.Min((long)requiredSampleLimit, EffectivePopulationRowCount);
        EffectivePopulationPageMatchesRun = WalkedSampleRowCount == ReportedSampleSize;
    }

    public long RawPopulationRowCount { get; }

    public long EffectivePopulationRowCount { get; }

    public long ExcludedPopulationRowCount { get; }

    public long ExcludedByPeriodRowCount { get; }

    public long ExcludedByPostingStatusRowCount { get; }

    public long ReportedSampleSize { get; }

    public long WalkedSampleRowCount { get; }

    public int RequiredSampleLimit { get; }

    public bool PopulationPartitionMatches { get; }

    public bool ExcludedPartitionMatches { get; }

    public bool SampleSizeMatchesRule { get; }

    public bool EffectivePopulationPageMatchesRun { get; }

    public bool Passed => PopulationPartitionMatches
        && ExcludedPartitionMatches
        && SampleSizeMatchesRule
        && EffectivePopulationPageMatchesRun;

    public override string ToString() => "private case INF verification";
}

internal static class PrivateCaseInfVerifier
{
    internal static PrivateCaseInfVerificationResult Verify(
        PrivateCaseInfVerificationFacts facts,
        PrivateCaseAcceptancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.InfVerification
                != PrivateCaseInfVerificationMode.RulesAndEffectivePopulation
            || policy.InfSampleSize != PrivateCaseAcceptancePolicy.RequiredInfSampleSize
            || facts.RawPopulationRowCount < 0
            || facts.EffectivePopulationRowCount < 0
            || facts.ExcludedPopulationRowCount < 0
            || facts.ExcludedByPeriodRowCount < 0
            || facts.ExcludedByPostingStatusRowCount < 0
            || facts.ReportedSampleSize < 0
            || facts.WalkedSampleRowCount < 0)
        {
            throw new PrivateCaseInfVerificationException(
                PrivateCaseInfVerificationFailure.InvalidEvidence);
        }

        try
        {
            return new PrivateCaseInfVerificationResult(facts, policy.InfSampleSize);
        }
        catch (OverflowException)
        {
            throw new PrivateCaseInfVerificationException(
                PrivateCaseInfVerificationFailure.InvalidEvidence);
        }
    }
}
