using JET.AuditCore;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class ValidationReportProgramTests
{
    [Theory]
    [InlineData(0, (int)ValidationReportDetailDisposition.Omit)]
    [InlineData(1, (int)ValidationReportDetailDisposition.Emit)]
    [InlineData(9_999, (int)ValidationReportDetailDisposition.Emit)]
    [InlineData(10_000, (int)ValidationReportDetailDisposition.Emit)]
    [InlineData(10_001, (int)ValidationReportDetailDisposition.SummaryOnly)]
    [InlineData(1_234_289, (int)ValidationReportDetailDisposition.SummaryOnly)]
    public void Finalize_NullRecordCategories_UseInclusiveTenThousandBoundary(
        long count,
        int expected)
    {
        var plan = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request(
                nullAccountCount: count,
                nullDocumentCount: count,
                nullDescriptionCount: count,
                outOfRangeDateCount: count)),
            new ValidationReportPlanningFacts(UnbalancedDetailRowCount: 0));

        Assert.Equal((ValidationReportDetailDisposition)expected, plan.RequireDetail(ValidationReportDetailKind.NullAccount).Disposition);
        Assert.Equal((ValidationReportDetailDisposition)expected, plan.RequireDetail(ValidationReportDetailKind.NullDocument).Disposition);
        Assert.Equal((ValidationReportDetailDisposition)expected, plan.RequireDetail(ValidationReportDetailKind.NullDescription).Disposition);
        Assert.Equal((ValidationReportDetailDisposition)expected, plan.RequireDetail(ValidationReportDetailKind.OutOfRangeApprovalDate).Disposition);
        Assert.Equal(6, plan.Details.Count);
    }

    [Theory]
    [InlineData(0, (int)ValidationReportDetailDisposition.Omit)]
    [InlineData(1, (int)ValidationReportDetailDisposition.Emit)]
    [InlineData(9_999, (int)ValidationReportDetailDisposition.Emit)]
    [InlineData(10_000, (int)ValidationReportDetailDisposition.SummaryOnly)]
    [InlineData(10_001, (int)ValidationReportDetailDisposition.SummaryOnly)]
    public void Finalize_UnbalancedRawRows_UseExclusiveTenThousandBoundary(
        long detailRowCount,
        int expected)
    {
        var plan = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request()),
            new ValidationReportPlanningFacts(detailRowCount));

        var detail = plan.RequireDetail(ValidationReportDetailKind.UnbalancedGlEntry);
        Assert.Equal(detailRowCount, detail.Count);
        Assert.Equal((ValidationReportDetailDisposition)expected, detail.Disposition);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void Finalize_CompletenessSheetAlwaysEmitsAndExplanationTracksSavedDiffCount(
        long completenessDiffCount,
        bool expectedExplanation)
    {
        var plan = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request(completenessDiffAccountCount: completenessDiffCount)),
            new ValidationReportPlanningFacts(UnbalancedDetailRowCount: 0));

        var completeness = plan.RequireDetail(ValidationReportDetailKind.CompletenessAccount);
        Assert.Equal(ValidationReportDetailDisposition.Emit, completeness.Disposition);
        Assert.Equal(completenessDiffCount, completeness.Count);
        Assert.Equal(expectedExplanation, plan.EmitCompletenessExplanation);
    }

    [Fact]
    public void Finalize_RejectsNegativeSavedOrProviderCounts()
    {
        var negativeSaved = JetAuditProgram.Plan(Request(nullAccountCount: -1));
        var valid = JetAuditProgram.Plan(Request());

        Assert.Throws<ArgumentOutOfRangeException>(() => JetAuditProgram.Finalize(
            negativeSaved,
            new ValidationReportPlanningFacts(UnbalancedDetailRowCount: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => JetAuditProgram.Finalize(
            valid,
            new ValidationReportPlanningFacts(UnbalancedDetailRowCount: -1)));
    }

    [Fact]
    public void Finalize_IsSingleUseAndRequireDetailRejectsUnfinalizedPlan()
    {
        var unfinalized = JetAuditProgram.Plan(Request());
        var finalized = JetAuditProgram.Finalize(
            unfinalized,
            new ValidationReportPlanningFacts(UnbalancedDetailRowCount: 0));

        Assert.Throws<InvalidOperationException>(() =>
            unfinalized.RequireDetail(ValidationReportDetailKind.NullAccount));
        Assert.Throws<InvalidOperationException>(() => JetAuditProgram.Finalize(
            finalized,
            new ValidationReportPlanningFacts(UnbalancedDetailRowCount: 0)));
    }

    private static ValidationReportRequest Request(
        long completenessDiffAccountCount = 0,
        long nullAccountCount = 0,
        long nullDocumentCount = 0,
        long nullDescriptionCount = 0,
        long outOfRangeDateCount = 0) =>
        new(
            ProjectId: "project-1",
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            CompletenessDiffAccountCount: completenessDiffAccountCount,
            NullAccountCount: nullAccountCount,
            NullDocumentCount: nullDocumentCount,
            NullDescriptionCount: nullDescriptionCount,
            OutOfRangeDateCount: outOfRangeDateCount);
}
