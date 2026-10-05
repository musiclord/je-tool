using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class ReportExportProgramTests
{
    public static TheoryData<
        string,
        ReportArtifactKind[],
        string?,
        string?,
        string?,
        int[],
        bool> IncludedReportActions =>
        new()
        {
            {
                "export.validationArtifacts",
                [
                    ReportArtifactKind.ValidationReport,
                    ReportArtifactKind.InfReport
                ],
                "validation-run",
                null,
                null,
                [],
                true
            },
            {
                "export.prescreenReport",
                [ReportArtifactKind.PrescreenReport],
                null,
                "prescreen-run",
                null,
                [],
                false
            },
            {
                "export.criteriaSelectionReport",
                [ReportArtifactKind.CriteriaSelectionReport],
                "validation-run",
                null,
                "scenario-revision",
                [4, 2],
                false
            }
        };

    [Theory]
    [MemberData(nameof(IncludedReportActions))]
    public async Task IncludedAction_CompletesTypedLifecycleWithExactArtifactPlan(
        string action,
        ReportArtifactKind[] expectedKinds,
        string? expectedValidationRunId,
        string? expectedPrescreenRunId,
        string? expectedScenarioRevision,
        int[] expectedScenarioPositions,
        bool expectedAtomicBatch)
    {
        // 第9批高3；Public首敗100911120：Criteria必須保存本次資料版本；其他報告仍不帶這個來源參照。
        var expectedDataRevision = action == "export.criteriaSelectionReport" ? "41" : null;
        var request = new ReportExportRequest(
            action,
            "project-7",
            ValidationRunId: expectedValidationRunId,
            PrescreenRunId: expectedPrescreenRunId,
            ScenarioRevision: expectedScenarioRevision,
            ScenarioPositions: [4, 2], FilterDataRevision: expectedDataRevision);

        var plan = JetAuditProgram.Plan(request);

        Assert.Same(request, plan.Request);
        Assert.Equal(expectedKinds, plan.ArtifactKinds);
        Assert.Equal(expectedValidationRunId, plan.SourceRef.ValidationRunId);
        Assert.Equal(expectedPrescreenRunId, plan.SourceRef.PrescreenRunId);
        Assert.Equal(expectedScenarioRevision, plan.SourceRef.ScenarioRevision);
        Assert.Equal(expectedDataRevision, plan.SourceRef.FilterDataRevision);
        Assert.Equal(
            expectedScenarioPositions,
            plan.SourceRef.ScenarioPositions ?? []);
        Assert.Equal(expectedAtomicBatch, plan.UseAtomicBatch);

        var port = new RecordingPort();

        var facts = await port.ExecuteAsync(
            plan,
            CancellationToken.None);
        var result = JetAuditProgram.Finalize(plan, facts);

        Assert.Same(plan, port.Plan);
        Assert.Same(plan, result.Plan);
        Assert.Same(facts.Artifacts, result.Artifacts);
        Assert.Equal(expectedKinds, result.Artifacts.Select(item => item.Kind));
    }

    private sealed class RecordingPort : IReportExportFactsPort
    {
        public ReportExportPlan? Plan { get; private set; }

        public Task<ReportExportFacts> ExecuteAsync(
            ReportExportPlan plan,
            CancellationToken cancellationToken)
        {
            Plan = plan;
            var artifacts = plan.ArtifactKinds
                .Select((kind, index) => new ReportArtifact(
                    $"artifact-{index + 1}",
                    kind,
                    $"report-{index + 1}.xlsx",
                    plan.SourceRef,
                    new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero),
                    1,
                    LastWriteUtc: null,
                    Stale: false))
                .ToArray();

            return Task.FromResult(new ReportExportFacts(artifacts));
        }
    }
}
