using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 報告版本清理的規格 oracle：P4 使用者核定 1A。stale 全選；其餘按 validity family
/// 保留 generatedUtc／artifactId 降冪的前三版，且最新有效版另受硬守衛。
/// </summary>
public sealed class ReportArtifactRetentionPolicyTests
{
    private static readonly DateTimeOffset BaseUtc =
        new(2026, 7, 11, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Plan_StaleArtifacts_SelectsEveryStaleArtifact()
    {
        // 等價分割：stale 不參與 validity family 的保留計數，無論時間或種類都必須入選。
        var artifacts = new[]
        {
            Artifact("a", ReportArtifactKind.ValidationReport, BaseUtc.AddMinutes(2), Stale: true),
            Artifact("b", ReportArtifactKind.WorkingPaper, BaseUtc.AddMinutes(1), Stale: true),
            Artifact("c", ReportArtifactKind.ValidationReport, BaseUtc, Stale: false)
        };

        var candidates = ReportArtifactRetentionPolicy.Plan(artifacts);

        Assert.Equal(["a", "b"], candidates.Select(item => item.Artifact.ArtifactId));
        Assert.All(candidates, item => Assert.Equal(ReportArtifactCleanupReason.Stale, item.Reason));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    public void Plan_ValidFamilyBoundaries_RetainsLatestThree(int artifactCount, int expectedCandidateCount)
    {
        // BVA：保留門檻的下鄰／邊界／上鄰分別是 1、3、4。
        var artifacts = Enumerable.Range(1, artifactCount)
            .Select(index => Artifact(index.ToString(), generatedUtc: BaseUtc.AddMinutes(index)))
            .ToArray();

        var candidates = ReportArtifactRetentionPolicy.Plan(artifacts);

        Assert.Equal(expectedCandidateCount, candidates.Count);
    }

    [Fact]
    public void Plan_SameTimestamp_UsesArtifactIdOrdinalDescendingAsTieBreaker()
    {
        var artifacts = new[]
        {
            Artifact("a", generatedUtc: BaseUtc),
            Artifact("d", generatedUtc: BaseUtc),
            Artifact("b", generatedUtc: BaseUtc),
            Artifact("c", generatedUtc: BaseUtc)
        };

        var candidates = ReportArtifactRetentionPolicy.Plan(artifacts);

        Assert.Equal("a", Assert.Single(candidates).Artifact.ArtifactId);
    }

    [Fact]
    public void Plan_WorkingPaperPositionsDiffer_StillUsesOneValidityFamily()
    {
        var artifacts = Enumerable.Range(1, 4)
            .Select(index => Artifact(
                index.ToString(),
                ReportArtifactKind.WorkingPaper,
                BaseUtc.AddMinutes(index),
                source: Source(positions: [index])))
            .ToArray();

        var candidates = ReportArtifactRetentionPolicy.Plan(artifacts);

        Assert.Equal("1", Assert.Single(candidates).Artifact.ArtifactId);
    }

    [Fact]
    public void Plan_DifferentRelevantRunIds_SeparatesValidityFamilies()
    {
        var artifacts = Enumerable.Range(1, 4)
            .Select(index => Artifact(
                index.ToString(),
                generatedUtc: BaseUtc.AddMinutes(index),
                source: Source(validationRunId: $"validation-{index}")))
            .ToArray();

        Assert.Empty(ReportArtifactRetentionPolicy.Plan(artifacts));
    }

    [Fact]
    public void Plan_NewestArtifactIsStale_ProtectsNewestValidArtifactAndSelectsStale()
    {
        var artifacts = new[]
        {
            Artifact("stale-newest", generatedUtc: BaseUtc.AddMinutes(5), Stale: true),
            Artifact("valid-latest", generatedUtc: BaseUtc.AddMinutes(4)),
            Artifact("valid-2", generatedUtc: BaseUtc.AddMinutes(3)),
            Artifact("valid-3", generatedUtc: BaseUtc.AddMinutes(2)),
            Artifact("valid-oldest", generatedUtc: BaseUtc.AddMinutes(1))
        };

        var candidates = ReportArtifactRetentionPolicy.Plan(artifacts);

        Assert.Equal(["stale-newest", "valid-oldest"], candidates.Select(item => item.Artifact.ArtifactId));
        Assert.DoesNotContain(candidates, item => item.Artifact.ArtifactId == "valid-latest");
    }

    [Theory]
    [InlineData(ReportArtifactKind.ValidationReport)]
    [InlineData(ReportArtifactKind.AccountMapping)]
    [InlineData(ReportArtifactKind.InfReport)]
    public void HasSameValiditySource_ValidationFamily_IgnoresUnrelatedReferences(ReportArtifactKind kind)
    {
        var left = Source(validationRunId: "validation-1", prescreenRunId: "prescreen-a", positions: [1]);
        var right = Source(validationRunId: "validation-1", prescreenRunId: "prescreen-b", positions: [2]);

        Assert.True(ReportArtifactValidityPolicy.HasSameValiditySource(kind, left, right));
    }

    [Theory]
    [InlineData(ReportArtifactKind.CriteriaSelectionReport)]
    [InlineData(ReportArtifactKind.WorkingPaper)]
    public void HasSameValiditySource_ScenarioFamily_IgnoresPositionsAndPrescreen(
        ReportArtifactKind kind)
    {
        var left = Source(positions: [1]);
        var same = Source(positions: [2, 3]);
        var differentPrescreen = Source(prescreenRunId: "prescreen-2", positions: [1]);
        var differentRevision = Source(scenarioRevision: "revision-2", positions: [1]);

        Assert.True(ReportArtifactValidityPolicy.HasSameValiditySource(
            kind, left, same));
        Assert.True(ReportArtifactValidityPolicy.HasSameValiditySource(
            kind, left, differentPrescreen));
        Assert.False(ReportArtifactValidityPolicy.HasSameValiditySource(
            kind, left, differentRevision));
    }

    [Fact]
    public void HasSameValiditySource_PrescreenFamily_UsesOnlyPrescreenRunId()
    {
        var left = Source(validationRunId: "validation-a", prescreenRunId: "prescreen-1");
        var same = Source(validationRunId: "validation-b", prescreenRunId: "prescreen-1");
        var different = Source(validationRunId: "validation-a", prescreenRunId: "prescreen-2");

        Assert.True(ReportArtifactValidityPolicy.HasSameValiditySource(
            ReportArtifactKind.PrescreenReport, left, same));
        Assert.False(ReportArtifactValidityPolicy.HasSameValiditySource(
            ReportArtifactKind.PrescreenReport, left, different));
    }

    [Fact]
    public void HasSameValiditySource_ValidationFamily_DifferentValidationRunIsDifferentFamily()
    {
        var left = Source(validationRunId: "validation-1");
        var right = Source(validationRunId: "validation-2");

        Assert.False(ReportArtifactValidityPolicy.HasSameValiditySource(
            ReportArtifactKind.ValidationReport, left, right));
    }

    private static ReportArtifact Artifact(
        string artifactId,
        ReportArtifactKind kind = ReportArtifactKind.ValidationReport,
        DateTimeOffset? generatedUtc = null,
        bool Stale = false,
        ReportArtifactSourceRefs? source = null)
        => new(
            artifactId,
            kind,
            $"{artifactId}.xlsx",
            source ?? Source(),
            generatedUtc ?? BaseUtc,
            10,
            new string('0', 64),
            Stale);

    private static ReportArtifactSourceRefs Source(
        string validationRunId = "validation-1",
        string prescreenRunId = "prescreen-1",
        string scenarioRevision = "revision-1",
        IReadOnlyList<int>? positions = null)
        => new(validationRunId, prescreenRunId, scenarioRevision, positions ?? [1]);
}
