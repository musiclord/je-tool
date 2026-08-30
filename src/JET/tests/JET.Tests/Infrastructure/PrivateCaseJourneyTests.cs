using System.Text.Json;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseJourneyTests
{
    [Fact]
    public async Task RunAsync_CompleteSyntheticInput_UsesProductActionsWithoutLegacyCaseObservation()
    {
        using var fixture = LegacyAuditParityJourneyTests.SyntheticJourneyFixture.Create();
        using var host = new HandlerTestHost();
        var prepared = Convert(fixture.Input);

        var result = await PrivateCaseJourney.RunAsync(
            host,
            prepared,
            LegacyAuditParityProvider.Sqlite,
            LegacyAuditParityCheckpoint.Export);

        Assert.Equal("local-case-01", result.CaseAlias);
        Assert.Equal(LegacyAuditParityCheckpoint.Export, result.CompletedCheckpoint);
        Assert.Equal(6, result.ArtifactCount);
        Assert.Equal(LegacyAuditParityJourneyContract.FullActionOrder, result.ActionNames);
        Assert.True(result.ValidationCaptured);
        Assert.True(result.PrescreenCaptured);
        Assert.True(result.FilterCaptured);
        Assert.True(result.TagMatrixCaptured);
        Assert.NotNull(result.InfVerification);
        Assert.True(result.InfVerification.Passed);
        Assert.Equal(
            Math.Min(
                (long)PrivateCaseAcceptancePolicy.RequiredInfSampleSize,
                result.InfVerification.EffectivePopulationRowCount),
            result.InfVerification.ReportedSampleSize);
        Assert.Equal(
            result.InfVerification.ReportedSampleSize,
            result.InfVerification.WalkedSampleRowCount);
        Assert.NotNull(result.ScenarioVerificationFacts);
        Assert.Single(result.ScenarioVerificationFacts.Counts);
        Assert.NotNull(result.ScenarioDiagnosticFacts);
        Assert.Single(result.ScenarioDiagnosticFacts.Counts);
        Assert.All(result.Artifacts, artifact => Assert.True(File.Exists(artifact.FullPath)));

        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(prepared.Input.EntityName, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(
            prepared.Input.GlSources[0].FilePath,
            serialized,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("case-A", serialized, StringComparison.Ordinal);
    }

    private static PrivateCasePreparedJourneyInput Convert(
        LegacyAuditParityJourneyInput input)
    {
        var converted = new PrivateCaseJourneyInput(
            "local-case-01",
            input.ProjectCode,
            input.EntityName,
            input.OperatorId,
            input.PeriodStart,
            input.PeriodEnd,
            input.LastPeriodStart!,
            input.SampleSeed,
            input.GlSources.Select(source => new PrivateCaseJourneyImportSource(
                    source.FilePath,
                    source.FileName!,
                    source.SheetName!))
                .ToArray(),
            input.GlMapping,
            input.GlAmountMode,
            input.TbSources.Select(source => new PrivateCaseJourneyImportSource(
                    source.FilePath,
                    source.FileName!,
                    source.SheetName!))
                .ToArray(),
            input.TbMapping,
            input.TbChangeMode,
            ConvertReference(input.AccountMappingFile!),
            ConvertReference(input.AuthorizedPreparerFile!),
            ConvertReference(input.HolidayFile!),
            ConvertReference(input.MakeupDayFile!),
            [],
            input.FilterScenarios);
        return new PrivateCasePreparedJourneyInput(
            converted,
            PrivateSourceCount: 4,
            GeneratedReferenceCount: 2);
    }

    private static PrivateCaseJourneyReferenceFile ConvertReference(
        LegacyAuditParityReferenceFile reference) => new(
            reference.FilePath,
            reference.FileName!);
}
