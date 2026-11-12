using FsCheck.Xunit;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class GlExcludedPopulationTests
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
            DateOnly? glDate = dayCode == 0
                ? null
                : PeriodStart.AddDays(dayCode - 2);
            var statusSeed = statusSeeds is { Length: > 0 }
                ? statusSeeds[index % statusSeeds.Length]
                : daySeed;
            var rawStatus = GeneratedStatus(statusSeed);

            var actual = GlExcludedPopulation.Classify(
                glDate,
                rawStatus,
                PeriodStart,
                PeriodEnd,
                postingStatusMapped,
                policy);
            var expected = IndependentDisposition(
                glDate,
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
    /// 日期不符分成三種原因。同一組產生的列逐列比對獨立算出的原因：
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
            DateOnly? glDate = dayCode == 0
                ? null
                : PeriodStart.AddDays(dayCode - 2);
            var statusSeed = statusSeeds is { Length: > 0 }
                ? statusSeeds[index % statusSeeds.Length]
                : daySeed;
            var rawStatus = GeneratedStatus(statusSeed);

            var actual = GlExcludedPopulation.Classify(
                glDate,
                rawStatus,
                PeriodStart,
                PeriodEnd,
                postingStatusMapped,
                policy);
            if (actual.Reason != IndependentReason(glDate, rawStatus, postingStatusMapped, includeBlank))
            {
                return false;
            }
        }

        return true;
    }

    [Theory]
    [InlineData(null, "is_excluded = 0")]
    [InlineData("g", "g.is_excluded = 0")]
    public void SqlPredicate_WithOptionalAlias_ReturnsCanonicalEffectivePredicate(
        string? tableAlias,
        string expected)
    {
        Assert.Equal(expected, GlExcludedPopulation.SqlPredicate(tableAlias));
    }

    // 日期不符這組原因由分類的處置產生。驗證摘要的數字由
    // QueryDataPreviewHandlerTests 的三種日期原因案例核對；這裡核對條件涵蓋的原因。
    [Fact]
    public void ExcludedByPeriodSqlPredicate_CoversExactlyTheReasonsWhoseDispositionIsDateMismatch()
    {
        Assert.Equal(
            "exclusion_reason IN ('blank_gl_date', 'before_period', 'after_period')",
            GlExcludedPopulation.ExcludedByPeriodSqlPredicate());
        var byPeriod = Enum.GetValues<GlExclusionReason>()
            .Where(reason => new GlExcludedPopulationClassification(reason).Disposition == GlExcludedPopulationDisposition.ExcludedByPeriod)
            .ToArray();
        Assert.Equal([GlExclusionReason.BlankGlDate, GlExclusionReason.BeforePeriod, GlExclusionReason.AfterPeriod], byPeriod);
    }

    [Fact]
    public void Classify_WhenPeriodAndPostingStatusBothExclude_PrioritizesPeriod()
    {
        var result = Classify(
            glDate: PeriodStart.AddDays(-1),
            rawPostingStatus: "draft",
            postingStatusMapped: true,
            policy: Policy(["posted"]));

        Assert.Equal(GlExcludedPopulationDisposition.ExcludedByPeriod, result.Disposition);
        Assert.True(result.IsExcluded);
        // 日期不符分成三種原因，早於查核期間另記。
        Assert.Equal("before_period", result.StorageReason);
    }

    [Fact]
    public void Classify_WhenGlDateIsNull_ExcludesByPeriod()
    {
        var result = Classify(
            glDate: null,
            rawPostingStatus: "posted",
            postingStatusMapped: true,
            policy: Policy(["posted"]));

        Assert.Equal(GlExcludedPopulationDisposition.ExcludedByPeriod, result.Disposition);
        // 日期不符分成三種原因，總帳日期空白另記。
        Assert.Equal(GlExcludedPopulation.BlankGlDateStorageReason, result.StorageReason);
    }

    [Fact]
    public void Classify_OnEitherPeriodBoundary_IsGl()
    {
        Assert.All(
            new[] { PeriodStart, PeriodEnd },
            boundary =>
            {
                var result = Classify(
                    glDate: boundary,
                    rawPostingStatus: null,
                    postingStatusMapped: false,
                    policy: null);

                Assert.Equal(GlExcludedPopulationDisposition.Gl, result.Disposition);
                Assert.False(result.IsExcluded);
                Assert.Null(result.StorageReason);
            });
    }

    [Fact]
    public void Classify_WithMultipleAcceptedValues_TrimsAndIgnoresCase()
    {
        var result = Classify(
            glDate: PeriodStart,
            rawPostingStatus: "  aPpRoVeD  ",
            postingStatusMapped: true,
            policy: Policy([" posted ", "Approved"]));

        Assert.Equal(GlExcludedPopulationDisposition.Gl, result.Disposition);
        Assert.False(result.IsExcluded);
        Assert.Null(result.StorageReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t")]
    public void Classify_WhenBlankIsIncluded_AcceptsNullOrWhitespaceStatus(string? rawStatus)
    {
        var result = Classify(
            glDate: PeriodStart,
            rawPostingStatus: rawStatus,
            postingStatusMapped: true,
            policy: Policy([], includeBlank: true));

        Assert.Equal(GlExcludedPopulationDisposition.Gl, result.Disposition);
        Assert.Null(result.StorageReason);
    }

    [Fact]
    public void Classify_WhenStatusIsNotAccepted_ExcludesByPostingStatus()
    {
        var result = Classify(
            glDate: PeriodStart,
            rawPostingStatus: " ",
            postingStatusMapped: true,
            policy: Policy(["posted"], includeBlank: false));

        Assert.Equal(
            GlExcludedPopulationDisposition.ExcludedByPostingStatus,
            result.Disposition);
        Assert.True(result.IsExcluded);
        Assert.Equal("posting_status", result.StorageReason);
    }

    [Fact]
    public void Classify_WhenPostingStatusIsUnmapped_UsesOnlyPeriod()
    {
        var withinPeriod = Classify(
            glDate: PeriodStart,
            rawPostingStatus: "not-posted",
            postingStatusMapped: false,
            policy: null);
        var outsidePeriod = Classify(
            glDate: PeriodEnd.AddDays(1),
            rawPostingStatus: "posted",
            postingStatusMapped: false,
            policy: null);

        Assert.Equal(GlExcludedPopulationDisposition.Gl, withinPeriod.Disposition);
        Assert.False(withinPeriod.IsExcluded);
        Assert.Equal(
            GlExcludedPopulationDisposition.ExcludedByPeriod,
            outsidePeriod.Disposition);
        // 日期不符分成三種原因，晚於查核期間另記。
        Assert.Equal(GlExcludedPopulation.AfterPeriodStorageReason, outsidePeriod.StorageReason);
    }

    [Fact]
    public void NormalizePolicy_TrimsRemovesBlankAndDeduplicatesIgnoringCase()
    {
        var normalized = GlExcludedPopulation.NormalizePolicy(
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
            GlExcludedPopulation.NormalizePolicy(
                postingStatusMapped: true,
                Policy(["", "  "], includeBlank: false)));
    }

    [Fact]
    public void NormalizePolicy_WhenMappedWithoutPolicy_RejectsPairing()
    {
        Assert.Throws<ArgumentException>(() =>
            GlExcludedPopulation.NormalizePolicy(
                postingStatusMapped: true,
                policy: null));
    }

    [Fact]
    public void NormalizePolicy_WhenUnmappedWithPolicy_RejectsPairing()
    {
        Assert.Throws<ArgumentException>(() =>
            GlExcludedPopulation.NormalizePolicy(
                postingStatusMapped: false,
                Policy(["posted"])));
    }

    private static GlExcludedPopulationClassification Classify(
        DateOnly? glDate,
        string? rawPostingStatus,
        bool postingStatusMapped,
        GlPostingStatusPolicy? policy) =>
        GlExcludedPopulation.Classify(
            glDate,
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

    private static GlExcludedPopulationDisposition IndependentDisposition(
        DateOnly? glDate,
        string? rawStatus,
        bool postingStatusMapped,
        bool includeBlank)
    {
        if (glDate is null
            || glDate.Value < PeriodStart
            || glDate.Value > PeriodEnd)
        {
            return GlExcludedPopulationDisposition.ExcludedByPeriod;
        }

        if (!postingStatusMapped)
        {
            return GlExcludedPopulationDisposition.Gl;
        }

        var normalizedStatus = rawStatus?.Trim() ?? string.Empty;
        var accepted = normalizedStatus.Length == 0
            ? includeBlank
            : string.Equals(normalizedStatus, "posted", StringComparison.OrdinalIgnoreCase)
              || string.Equals(normalizedStatus, "closed", StringComparison.OrdinalIgnoreCase);
        return accepted
            ? GlExcludedPopulationDisposition.Gl
            : GlExcludedPopulationDisposition.ExcludedByPostingStatus;
    }

    private static GlExclusionReason? IndependentReason(
        DateOnly? glDate,
        string? rawStatus,
        bool postingStatusMapped,
        bool includeBlank)
    {
        if (glDate is null)
        {
            return GlExclusionReason.BlankGlDate;
        }

        if (glDate.Value < PeriodStart)
        {
            return GlExclusionReason.BeforePeriod;
        }

        if (glDate.Value > PeriodEnd)
        {
            return GlExclusionReason.AfterPeriod;
        }

        return IndependentDisposition(glDate, rawStatus, postingStatusMapped, includeBlank)
            == GlExcludedPopulationDisposition.Gl
            ? null
            : GlExclusionReason.PostingStatus;
    }
}
