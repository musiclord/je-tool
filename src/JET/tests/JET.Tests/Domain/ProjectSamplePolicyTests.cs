using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class ProjectSamplePolicyTests
{
    [Fact]
    public void EffectiveSampleSeed_LegacyDocumentWithoutPersistedSeed_UsesDomainFallback()
    {
        var document = Document(sampleSeed: null);

        Assert.Equal(48_271, document.EffectiveSampleSeed);
        Assert.Equal(ProjectDocument.LegacySampleSeed, document.EffectiveSampleSeed);
    }

    [Fact]
    public void EffectiveSampleSeed_PersistedSeed_WinsOverLegacyFallback()
    {
        var document = Document(sampleSeed: 123_456);

        Assert.Equal(123_456, document.EffectiveSampleSeed);
    }

    private static ProjectDocument Document(long? sampleSeed) => new(
        ProjectId: "project",
        ProjectCode: "P",
        EntityName: "Entity",
        OperatorId: "operator",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastAccountingPeriodDate: null,
        MoneyScale: ProjectDocument.DefaultMoneyScale,
        RoundingMode: ProjectDocument.DefaultRoundingMode,
        CreatedUtc: DateTimeOffset.UnixEpoch,
        CurrentStep: 1,
        SchemaVersion: ProjectDocument.CurrentSchemaVersion,
        SampleSeed: sampleSeed);
}
