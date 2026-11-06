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
        Assert.Equal(6, result.ReportCount);
        Assert.Equal(LegacyAuditParityJourneyContract.FullActionOrder, result.ActionNames);
        Assert.True(result.ValidationCaptured);
        Assert.True(result.PrescreenCaptured);
        Assert.True(result.FilterCaptured);
        Assert.True(result.HighRiskSummaryCaptured);
        Assert.NotNull(result.InfVerification);
        Assert.True(result.InfVerification.Passed);
        Assert.Equal(
            Math.Min(
                (long)PrivateCaseAcceptancePolicy.RequiredInfSampleSize,
                result.InfVerification.GlRowCount),
            result.InfVerification.ReportedSampleSize);
        Assert.Equal(
            result.InfVerification.ReportedSampleSize,
            result.InfVerification.WalkedSampleRowCount);
        Assert.NotNull(result.ScenarioVerificationFacts);
        Assert.Single(result.ScenarioVerificationFacts.Counts);
        Assert.NotNull(result.ScenarioDiagnosticFacts);
        Assert.Single(result.ScenarioDiagnosticFacts.Counts);
        Assert.All(result.Reports, report => Assert.True(File.Exists(report.FullPath)));

        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(prepared.Input.ClientName, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(
            prepared.Input.GlSources[0].FilePath,
            serialized,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("case-A", serialized, StringComparison.Ordinal);
    }

    // 新增測試（裁定 2026-11-06-01）：私人案件核對接上三段後續操作時，每段匯出後都在下一段開始前交給比對，
    // 段落順序與 PrivateCaseAcceptanceRunner 相同，每段都帶齊六份報告、抽樣與情境計數。用合成資料驗證接線。
    [Fact]
    public async Task RunAsync_WithFollowUps_HandsEachExportToVerificationBeforeNextSegment()
    {
        using var fixture = LegacyAuditParityJourneyTests.SyntheticJourneyFixture.Create();
        using var host = new HandlerTestHost();
        var prepared = Convert(fixture.Input);
        var segments = new List<(LegacyAuditParityFollowUp? FollowUp, IReadOnlyList<string> Actions)>();

        var result = await PrivateCaseJourney.RunAsync(
            host,
            prepared,
            LegacyAuditParityProvider.Sqlite,
            LegacyAuditParityCheckpoint.Export,
            followUps: PrivateCaseAcceptanceRunner.FollowUps,
            afterEachExport: segment =>
            {
                Assert.Equal(LegacyAuditParityCheckpoint.Export, segment.CompletedCheckpoint);
                Assert.Equal(6, segment.ReportCount);
                Assert.NotNull(segment.InfVerification);
                Assert.True(segment.InfVerification.Passed);
                Assert.NotNull(segment.ScenarioVerificationFacts);
                Assert.Single(segment.ScenarioVerificationFacts.Counts);
                Assert.NotNull(segment.ScenarioDiagnosticFacts);
                Assert.Single(segment.ScenarioDiagnosticFacts.Counts);
                // 這一段的報告在下一段開始前仍在案件資料夾裡，比對端才取得到。
                Assert.All(segment.Reports, report => Assert.True(File.Exists(report.FullPath)));
                segments.Add((segment.FollowUp, segment.ActionNames));
                return Task.CompletedTask;
            });

        Assert.Equal(LegacyAuditParityJourneyContract.FullActionOrder, result.ActionNames);
        Assert.Equal(
            new LegacyAuditParityFollowUp?[] { null }
                .Concat(PrivateCaseAcceptanceRunner.FollowUps.Select(static followUp => (LegacyAuditParityFollowUp?)followUp)),
            segments.Select(static segment => segment.FollowUp));
        foreach (var (followUp, actions) in segments.Skip(1))
        {
            Assert.Equal(
                LegacyAuditParityJourneyContract.FollowUpActionOrder(followUp!.Value),
                LegacyAuditParityJourneyContract.NormalizePaginatedActionOrder(actions));
        }
    }

    [Fact]
    public async Task RunAsync_FollowUpsWithoutVerification_AreRejected()
    {
        using var fixture = LegacyAuditParityJourneyTests.SyntheticJourneyFixture.Create();
        using var host = new HandlerTestHost();

        await Assert.ThrowsAsync<ArgumentException>(() => PrivateCaseJourney.RunAsync(
            host,
            Convert(fixture.Input),
            LegacyAuditParityProvider.Sqlite,
            LegacyAuditParityCheckpoint.Export,
            followUps: PrivateCaseAcceptanceRunner.FollowUps));
    }

    private static PrivateCasePreparedJourneyInput Convert(
        LegacyAuditParityJourneyInput input)
    {
        var converted = new PrivateCaseJourneyInput(
            "local-case-01",
            input.ProjectCode,
            input.ClientName,
            input.OperatorId,
            input.PeriodStart,
            input.PeriodEnd,
            input.PrepStartDate!,
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
