using FsCheck.Xunit;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class GlEffectivePopulationTests
{
    private static readonly DateOnly PeriodStart = new(2025, 1, 1);
    private static readonly DateOnly PeriodEnd = new(2025, 12, 31);

    [Property(Replay = "20260815,105", MaxTest = 256)]
    public bool Classify_BoundedRowsMatchIndependentPartitionOracle(
        int[] daySeeds,
        int[] statusSeeds,
        bool postingStatusMapped,
        bool includeBlank)
    {
        var boundedDays = (daySeeds ?? []).Take(128).ToArray();
        if (boundedDays.Length == 0)
        {
            boundedDays = [0];
        }

        var actualCounts = new int[3];
        var expectedCounts = new int[3];
        var policy = postingStatusMapped
            ? Policy([" Posted ", "closed"], includeBlank)
            : null;

        for (var index = 0; index < boundedDays.Length; index++)
        {
            var daySeed = boundedDays[index];
            var dayCode = (int)((uint)daySeed % 371u);
            DateOnly? postDate = dayCode == 0
                ? null
                : PeriodStart.AddDays(dayCode - 2);
            var statusSeed = statusSeeds is { Length: > 0 }
                ? statusSeeds[index % statusSeeds.Length]
                : daySeed;
            var rawStatus = GeneratedStatus(statusSeed);

            var actual = GlEffectivePopulation.Classify(
                postDate,
                rawStatus,
                PeriodStart,
                PeriodEnd,
                postingStatusMapped,
                policy);
            var expected = IndependentDisposition(
                postDate,
                rawStatus,
                postingStatusMapped,
                includeBlank);

            if (actual.Disposition != expected)
            {
                return false;
            }

            actualCounts[(int)actual.Disposition]++;
            expectedCounts[(int)expected]++;
        }

        return actualCounts.SequenceEqual(expectedCounts)
            && actualCounts.Sum() == boundedDays.Length;
    }

    /// <summary>
    /// 使用者 2026-10-21 裁定日期不符分成三種原因。同一組產生的列逐列比對獨立算出的原因：
    /// 產生的總帳日期涵蓋空白、查核起日前一天、整個期間與迄日之後四天。
    /// </summary>
    [Property(Replay = "20260815,105", MaxTest = 256)]
    public bool Classify_BoundedRowsMatchIndependentReasonOracle(
        int[] daySeeds,
        int[] statusSeeds,
        bool postingStatusMapped,
        bool includeBlank)
    {
        var boundedDays = (daySeeds ?? []).Take(128).ToArray();
        if (boundedDays.Length == 0)
        {
            boundedDays = [0];
        }

        var policy = postingStatusMapped
            ? Policy([" Posted ", "closed"], includeBlank)
            : null;

        for (var index = 0; index < boundedDays.Length; index++)
        {
            var daySeed = boundedDays[index];
            var dayCode = (int)((uint)daySeed % 371u);
            DateOnly? postDate = dayCode == 0
                ? null
                : PeriodStart.AddDays(dayCode - 2);
            var statusSeed = statusSeeds is { Length: > 0 }
                ? statusSeeds[index % statusSeeds.Length]
                : daySeed;
            var rawStatus = GeneratedStatus(statusSeed);

            var actual = GlEffectivePopulation.Classify(
                postDate,
                rawStatus,
                PeriodStart,
                PeriodEnd,
                postingStatusMapped,
                policy);
            if (actual.Reason != IndependentReason(postDate, rawStatus, postingStatusMapped, includeBlank))
            {
                return false;
            }
        }

        return true;
    }

    [Theory]
    [InlineData(null, "is_effective = 1")]
    [InlineData("g", "g.is_effective = 1")]
    public void SqlPredicate_WithOptionalAlias_ReturnsCanonicalEffectivePredicate(
        string? tableAlias,
        string expected)
    {
        Assert.Equal(expected, GlEffectivePopulation.SqlPredicate(tableAlias));
    }

    [Fact]
    public void Classify_WhenPeriodAndPostingStatusBothExclude_PrioritizesPeriod()
    {
        var result = Classify(
            postDate: PeriodStart.AddDays(-1),
            rawPostingStatus: "draft",
            postingStatusMapped: true,
            policy: Policy(["posted"]));

        Assert.Equal(GlEffectivePopulationDisposition.ExcludedByPeriod, result.Disposition);
        Assert.False(result.IsEffective);
        // 使用者 2026-10-21 裁定日期不符分成三種原因，早於查核期間另記；第一次失敗收據 20261021-080930829-9ee6cdc9021744a0bf40903ea77f833a。
        Assert.Equal("before_period", result.StorageReason);
    }

    [Fact]
    public void Classify_WhenPostDateIsNull_ExcludesByPeriod()
    {
        var result = Classify(
            postDate: null,
            rawPostingStatus: "posted",
            postingStatusMapped: true,
            policy: Policy(["posted"]));

        Assert.Equal(GlEffectivePopulationDisposition.ExcludedByPeriod, result.Disposition);
        // 使用者 2026-10-21 裁定日期不符分成三種原因，總帳日期空白另記；建置失敗收據 20261021-080724377-52540d9f361849b9a99477c6ec7ae4cc。
        Assert.Equal(GlEffectivePopulation.BlankPostDateStorageReason, result.StorageReason);
    }

    [Fact]
    public void Classify_OnEitherPeriodBoundary_IsEffective()
    {
        Assert.All(
            new[] { PeriodStart, PeriodEnd },
            boundary =>
            {
                var result = Classify(
                    postDate: boundary,
                    rawPostingStatus: null,
                    postingStatusMapped: false,
                    policy: null);

                Assert.Equal(GlEffectivePopulationDisposition.Effective, result.Disposition);
                Assert.True(result.IsEffective);
                Assert.Null(result.StorageReason);
            });
    }

    [Fact]
    public void Classify_WithMultipleAcceptedValues_TrimsAndIgnoresCase()
    {
        var result = Classify(
            postDate: PeriodStart,
            rawPostingStatus: "  aPpRoVeD  ",
            postingStatusMapped: true,
            policy: Policy([" posted ", "Approved"]));

        Assert.Equal(GlEffectivePopulationDisposition.Effective, result.Disposition);
        Assert.True(result.IsEffective);
        Assert.Null(result.StorageReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t")]
    public void Classify_WhenBlankIsIncluded_AcceptsNullOrWhitespaceStatus(string? rawStatus)
    {
        var result = Classify(
            postDate: PeriodStart,
            rawPostingStatus: rawStatus,
            postingStatusMapped: true,
            policy: Policy([], includeBlank: true));

        Assert.Equal(GlEffectivePopulationDisposition.Effective, result.Disposition);
        Assert.Null(result.StorageReason);
    }

    [Fact]
    public void Classify_WhenStatusIsNotAccepted_ExcludesByPostingStatus()
    {
        var result = Classify(
            postDate: PeriodStart,
            rawPostingStatus: " ",
            postingStatusMapped: true,
            policy: Policy(["posted"], includeBlank: false));

        Assert.Equal(
            GlEffectivePopulationDisposition.ExcludedByPostingStatus,
            result.Disposition);
        Assert.False(result.IsEffective);
        Assert.Equal("posting_status", result.StorageReason);
    }

    [Fact]
    public void Classify_WhenPostingStatusIsUnmapped_UsesOnlyPeriod()
    {
        var withinPeriod = Classify(
            postDate: PeriodStart,
            rawPostingStatus: "not-posted",
            postingStatusMapped: false,
            policy: null);
        var outsidePeriod = Classify(
            postDate: PeriodEnd.AddDays(1),
            rawPostingStatus: "posted",
            postingStatusMapped: false,
            policy: null);

        Assert.Equal(GlEffectivePopulationDisposition.Effective, withinPeriod.Disposition);
        Assert.True(withinPeriod.IsEffective);
        Assert.Equal(
            GlEffectivePopulationDisposition.ExcludedByPeriod,
            outsidePeriod.Disposition);
        // 使用者 2026-10-21 裁定日期不符分成三種原因，晚於查核期間另記；建置失敗收據 20261021-080724377-52540d9f361849b9a99477c6ec7ae4cc。
        Assert.Equal(GlEffectivePopulation.AfterPeriodStorageReason, outsidePeriod.StorageReason);
    }

    [Fact]
    public void NormalizePolicy_TrimsRemovesBlankAndDeduplicatesIgnoringCase()
    {
        var normalized = GlEffectivePopulation.NormalizePolicy(
            postingStatusMapped: true,
            Policy([" Posted ", "", "posted", "  approved  ", "APPROVED", "\t"]));

        Assert.NotNull(normalized);
        Assert.Equal(["Posted", "approved"], normalized.AcceptedValues);
        Assert.False(normalized.IncludeBlank);
    }

    [Fact]
    public void NormalizePolicy_WhenMappedWithoutAnyAcceptedStatus_RejectsPolicy()
    {
        Assert.Throws<ArgumentException>(() =>
            GlEffectivePopulation.NormalizePolicy(
                postingStatusMapped: true,
                Policy(["", "  "], includeBlank: false)));
    }

    [Fact]
    public void NormalizePolicy_WhenMappedWithoutPolicy_RejectsPairing()
    {
        Assert.Throws<ArgumentException>(() =>
            GlEffectivePopulation.NormalizePolicy(
                postingStatusMapped: true,
                policy: null));
    }

    [Fact]
    public void NormalizePolicy_WhenUnmappedWithPolicy_RejectsPairing()
    {
        Assert.Throws<ArgumentException>(() =>
            GlEffectivePopulation.NormalizePolicy(
                postingStatusMapped: false,
                Policy(["posted"])));
    }

    private static GlEffectivePopulationClassification Classify(
        DateOnly? postDate,
        string? rawPostingStatus,
        bool postingStatusMapped,
        GlPostingStatusPolicy? policy) =>
        GlEffectivePopulation.Classify(
            postDate,
            rawPostingStatus,
            PeriodStart,
            PeriodEnd,
            postingStatusMapped,
            policy);

    private static GlPostingStatusPolicy Policy(
        IReadOnlyList<string> acceptedValues,
        bool includeBlank = false) =>
        new(acceptedValues, includeBlank);

    private static string? GeneratedStatus(int seed) => ((uint)seed % 5u) switch
    {
        0 => null,
        1 => " \t ",
        2 => "posted",
        3 => " CLOSED ",
        _ => "draft"
    };

    private static GlEffectivePopulationDisposition IndependentDisposition(
        DateOnly? postDate,
        string? rawStatus,
        bool postingStatusMapped,
        bool includeBlank)
    {
        if (postDate is null
            || postDate.Value < PeriodStart
            || postDate.Value > PeriodEnd)
        {
            return GlEffectivePopulationDisposition.ExcludedByPeriod;
        }

        if (!postingStatusMapped)
        {
            return GlEffectivePopulationDisposition.Effective;
        }

        var normalizedStatus = rawStatus?.Trim() ?? string.Empty;
        var accepted = normalizedStatus.Length == 0
            ? includeBlank
            : string.Equals(normalizedStatus, "posted", StringComparison.OrdinalIgnoreCase)
              || string.Equals(normalizedStatus, "closed", StringComparison.OrdinalIgnoreCase);
        return accepted
            ? GlEffectivePopulationDisposition.Effective
            : GlEffectivePopulationDisposition.ExcludedByPostingStatus;
    }

    private static GlExclusionReason? IndependentReason(
        DateOnly? postDate,
        string? rawStatus,
        bool postingStatusMapped,
        bool includeBlank)
    {
        if (postDate is null)
        {
            return GlExclusionReason.BlankPostDate;
        }

        if (postDate.Value < PeriodStart)
        {
            return GlExclusionReason.BeforePeriod;
        }

        if (postDate.Value > PeriodEnd)
        {
            return GlExclusionReason.AfterPeriod;
        }

        return IndependentDisposition(postDate, rawStatus, postingStatusMapped, includeBlank)
            == GlEffectivePopulationDisposition.Effective
            ? null
            : GlExclusionReason.PostingStatus;
    }
}
