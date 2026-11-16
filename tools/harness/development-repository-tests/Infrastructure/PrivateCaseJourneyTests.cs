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

    // 新增測試：私人案件核對接上三段後續操作時，每段匯出後都在下一段開始前交給比對，
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

    // 清單宣告攸關資料元素欄位、情境用它篩選時，確認 GL 配對要一併送出這個欄位，
    // 儲存情境前把情境裡的欄位名稱換成後端產生的代號；三段後續操作也一樣。兩列裡只有一列的值是 alpha，
    // 所以每一段都只命中一列。用合成資料驗證接線。
    [Fact]
    public async Task RunAsync_ScenarioOnDeclaredRdeField_FiltersByThatFieldInEverySegment()
    {
        using var fixture = LegacyAuditParityJourneyTests.SyntheticJourneyFixture.Create(withUnmappedGlColumn: true);
        using var host = new HandlerTestHost();
        var converted = Convert(fixture.Input);
        var prepared = converted with
        {
            Input = converted.Input with
            {
                GlRdeFields =
                [
                    new PrivateCaseRdeField(
                        LegacyAuditParityJourneyTests.SyntheticJourneyFixture.UnmappedGlColumn,
                        "合成自訂欄位",
                        "text",
                        "合成自訂_JE"),
                ],
                FilterScenarios = JsonSerializer.SerializeToElement(new object[]
                {
                    new
                    {
                        name = "合成自訂欄位條件",
                        rationale = "合成 journey",
                        groups = new object[]
                        {
                            new
                            {
                                join = "AND",
                                conditions = new object[]
                                {
                                    new { join = "AND", type = "fieldValue", fieldId = "合成自訂欄位",
                                        @operator = "in", values = new[] { "ALPHA" } },
                                },
                            },
                        },
                    },
                }),
            },
        };
        var segments = new List<(LegacyAuditParityFollowUp? FollowUp, long RowCount, int DiagnosticCount)>();

        var result = await PrivateCaseJourney.RunAsync(
            host,
            prepared,
            LegacyAuditParityProvider.Sqlite,
            LegacyAuditParityCheckpoint.Export,
            followUps: PrivateCaseAcceptanceRunner.FollowUps,
            afterEachExport: segment =>
            {
                segments.Add((
                    segment.FollowUp,
                    Assert.Single(segment.ScenarioVerificationFacts!.Counts).RowCount,
                    segment.ScenarioDiagnosticFacts!.Counts.Count));
                return Task.CompletedTask;
            });

        Assert.Equal(LegacyAuditParityJourneyContract.FullActionOrder, result.ActionNames);
        Assert.Equal(PrivateCaseAcceptanceRunner.FollowUps.Count + 1, segments.Count);
        Assert.All(segments, segment => Assert.Equal(1, segment.RowCount));
        Assert.All(segments, segment => Assert.Equal(1, segment.DiagnosticCount));
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
