namespace JET.Domain;

/// <summary>
/// 報告來源的有效性世代定義。ScenarioPositions 是同一上游世代中的輸出選擇，
/// 不參與 Criteria／WorkingPaper 的 validity family。
/// </summary>
public static class ReportArtifactValidityPolicy
{
    public static bool HasSameValiditySource(
        ReportArtifactKind kind,
        ReportArtifactSourceRefs left,
        ReportArtifactSourceRefs right)
        => FamilyKey.Create(kind, left) == FamilyKey.Create(kind, right);

    internal readonly record struct FamilyKey(
        ReportArtifactKind Kind,
        string? ValidationRunId,
        string? PrescreenRunId,
        string? ScenarioRevision)
    {
        public static FamilyKey Create(ReportArtifactKind kind, ReportArtifactSourceRefs source) => kind switch
        {
            ReportArtifactKind.ValidationReport
                or ReportArtifactKind.AccountMapping
                or ReportArtifactKind.InfReport
                => new(kind, source.ValidationRunId, null, null),
            ReportArtifactKind.PrescreenReport
                => new(kind, null, source.PrescreenRunId, null),
            ReportArtifactKind.CriteriaSelectionReport
                or ReportArtifactKind.WorkingPaper
                => new(
                    kind,
                    source.ValidationRunId,
                    null,
                    source.ScenarioRevision),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的報告產物種類。")
        };
    }
}

/// <summary>
/// 使用者明示清理時的純 retention policy。所有 stale 都是候選；有效版本每個 validity family
/// 依 generatedUtc、artifactId ordinal 降冪保留三份，且最新有效版永不成為候選。
/// </summary>
public static class ReportArtifactRetentionPolicy
{
    public const int RetainedValidVersionsPerFamily = 3;

    public static IReadOnlyList<ReportArtifactCleanupCandidate> Plan(
        IReadOnlyCollection<ReportArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        var candidates = artifacts
            .Where(artifact => artifact.Stale)
            .Select(artifact => new ReportArtifactCleanupCandidate(
                artifact,
                ReportArtifactCleanupReason.Stale))
            .ToList();

        foreach (var family in artifacts
                     .Where(artifact => !artifact.Stale)
                     .GroupBy(artifact => ReportArtifactValidityPolicy.FamilyKey.Create(
                         artifact.Kind,
                         artifact.SourceRef)))
        {
            var newestFirst = family
                .OrderByDescending(artifact => artifact.GeneratedUtc)
                .ThenByDescending(artifact => artifact.ArtifactId, StringComparer.Ordinal)
                .ToArray();
            var latestValidArtifactId = newestFirst[0].ArtifactId;

            candidates.AddRange(newestFirst
                .Skip(RetainedValidVersionsPerFamily)
                .Where(artifact => !string.Equals(
                    artifact.ArtifactId,
                    latestValidArtifactId,
                    StringComparison.Ordinal))
                .Select(artifact => new ReportArtifactCleanupCandidate(
                    artifact,
                    ReportArtifactCleanupReason.Retention)));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Artifact.GeneratedUtc)
            .ThenByDescending(candidate => candidate.Artifact.ArtifactId, StringComparer.Ordinal)
            .ToArray();
    }
}
