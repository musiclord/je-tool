using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class ReportExportProgramTests
{
    public static TheoryData<
        string,
        ReportKind[],
        string?,
        string?,
        string?,
        int[],
        bool> IncludedReportActions =>
        new()
        {
            {
                "export.validationReports",
                [
                    ReportKind.ValidationReport,
                    ReportKind.InfReport
                ],
                "validation-run",
                null,
                null,
                [],
                true
            },
            {
                "export.prescreenReport",
                [ReportKind.PrescreenReport],
                null,
                "prescreen-run",
                null,
                [],
                false
            },
            {
                "export.criteriaSelectionReport",
                [ReportKind.CriteriaSelectionReport],
                "validation-run",
                null,
                "scenario-revision",
                [4, 2],
                false
            }
        };

    [Theory]
    [MemberData(nameof(IncludedReportActions))]
    public async Task IncludedAction_CompletesTypedLifecycleWithExactReportPlan(
        string action,
        ReportKind[] expectedKinds,
        string? expectedValidationResultId,
        string? expectedPrescreenResultId,
        string? expectedScenarioRevision,
        int[] expectedScenarioPositions,
        bool expectedAtomicBatch)
    {
        // Criteria必須保存本次資料版本；其他報告不帶這個來源參照。
        var expectedDataRevision = action == "export.criteriaSelectionReport" ? "41" : null;
        var request = new ReportExportRequest(
            action,
            "project-7",
            ValidationResultId: expectedValidationResultId,
            PrescreenResultId: expectedPrescreenResultId,
            ScenarioRevision: expectedScenarioRevision,
            ScenarioPositions: [4, 2], FilterDataRevision: expectedDataRevision);

        var plan = JetAuditProgram.Plan(request);

        Assert.Same(request, plan.Request);
        Assert.Equal(expectedKinds, plan.ReportKinds);
        Assert.Equal(expectedValidationResultId, plan.SourceRef.ValidationResultId);
        Assert.Equal(expectedPrescreenResultId, plan.SourceRef.PrescreenResultId);
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
        Assert.Same(facts.Reports, result.Reports);
        Assert.Equal(expectedKinds, result.Reports.Select(item => item.Kind));
    }

    private sealed class RecordingPort : IReportExportFactsPort
    {
        public ReportExportPlan? Plan { get; private set; }

        public Task<ReportExportFacts> ExecuteAsync(
            ReportExportPlan plan,
            CancellationToken cancellationToken)
        {
            Plan = plan;
            var reports = plan.ReportKinds
                .Select((kind, index) => new Report(
                    $"report-{index + 1}",
                    kind,
                    $"report-{index + 1}.xlsx",
                    plan.SourceRef,
                    new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero),
                    1,
                    LastWriteUtc: null,
                    Stale: false))
                .ToArray();

            return Task.FromResult(new ReportExportFacts(reports));
        }
    }
}
