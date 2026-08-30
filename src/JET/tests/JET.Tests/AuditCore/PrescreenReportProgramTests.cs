using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class PrescreenReportProgramTests
{
    private static readonly PrescreenReportDetailKind[] DetailKinds =
    [
        PrescreenReportDetailKind.PostPeriodApproval,
        PrescreenReportDetailKind.SuspiciousKeywords,
        PrescreenReportDetailKind.UnexpectedAccountPair,
        PrescreenReportDetailKind.TrailingZeros,
        PrescreenReportDetailKind.BlankDescription
    ];

    [Theory]
    [InlineData(0, (int)PrescreenReportDetailDisposition.Omit)]
    [InlineData(9_999, (int)PrescreenReportDetailDisposition.Emit)]
    [InlineData(10_000, (int)PrescreenReportDetailDisposition.SummaryOnly)]
    public void Finalize_UsesExclusiveTenThousandRowBoundary(
        long rowHitCount,
        int expected)
    {
        var voucherHitCount = rowHitCount == 0 ? 0 : 1;
        var plan = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request()),
            Facts(voucherHitCount, rowHitCount));

        Assert.All(DetailKinds, kind =>
        {
            var detail = plan.RequireDetail(kind);
            Assert.Equal((PrescreenReportDetailDisposition)expected, detail.Disposition);
            Assert.Equal(
                new PrescreenHitCounts(voucherHitCount, rowHitCount),
                detail.Counts);
            Assert.Equal(
                expected == (int)PrescreenReportDetailDisposition.Emit,
                detail.Emit);
            Assert.Equal(
                expected == (int)PrescreenReportDetailDisposition.SummaryOnly,
                detail.IsSummaryOnly);
        });
    }

    [Fact]
    public void Finalize_UsesRowCountRatherThanVoucherCountForThreshold()
    {
        var counts = Counts(voucherHitCount: 0, rowHitCount: 0);
        counts[PrescreenReportDetailKind.PostPeriodApproval] =
            new PrescreenHitCounts(VoucherHitCount: 1, RowHitCount: 10_000);

        var plan = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request()),
            new PrescreenReportPlanningFacts(counts));

        var detail = plan.RequireDetail(PrescreenReportDetailKind.PostPeriodApproval);
        Assert.Equal(1L, detail.Counts?.VoucherHitCount);
        Assert.Equal(10_000L, detail.Counts?.RowHitCount);
        Assert.Equal(
            PrescreenReportDetailDisposition.SummaryOnly,
            detail.Disposition);
    }

    [Fact]
    public void Finalize_NotApplicableFamilyDoesNotEnterFacts()
    {
        const string naReason = "未設定核准日期欄位";
        var counts = Counts(voucherHitCount: 0, rowHitCount: 0);
        counts.Remove(PrescreenReportDetailKind.PostPeriodApproval);

        var plan = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request(postPeriodApprovalNaReason: naReason)),
            new PrescreenReportPlanningFacts(counts));

        var detail = plan.RequireDetail(PrescreenReportDetailKind.PostPeriodApproval);
        Assert.False(detail.IsApplicable);
        Assert.Equal(naReason, detail.NaReason);
        Assert.Null(detail.Counts);
        Assert.Equal(
            PrescreenReportDetailDisposition.NotApplicable,
            detail.Disposition);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void Finalize_RejectsNegativeVoucherOrRowCounts(
        long voucherHitCount,
        long rowHitCount)
    {
        var counts = Counts(voucherHitCount: 0, rowHitCount: 0);
        counts[PrescreenReportDetailKind.PostPeriodApproval] =
            new PrescreenHitCounts(voucherHitCount, rowHitCount);

        Assert.Throws<ArgumentOutOfRangeException>(() => JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request()),
            new PrescreenReportPlanningFacts(counts)));
    }

    [Fact]
    public void Finalize_RejectsMissingApplicableFacts()
    {
        var counts = Counts(voucherHitCount: 0, rowHitCount: 0);
        counts.Remove(PrescreenReportDetailKind.BlankDescription);

        Assert.Throws<InvalidOperationException>(() => JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request()),
            new PrescreenReportPlanningFacts(counts)));
    }

    [Fact]
    public void Finalize_RejectsFactsForNotApplicableFamily()
    {
        var plan = JetAuditProgram.Plan(Request(
            suspiciousKeywordsNaReason: "未提供可疑關鍵字"));

        Assert.Throws<InvalidOperationException>(() => JetAuditProgram.Finalize(
            plan,
            Facts(voucherHitCount: 0, rowHitCount: 0)));
    }

    [Fact]
    public void Finalize_IsSingleUse()
    {
        var facts = Facts(voucherHitCount: 0, rowHitCount: 0);
        var finalized = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request()),
            facts);

        Assert.Throws<InvalidOperationException>(() =>
            JetAuditProgram.Finalize(finalized, facts));
    }

    [Fact]
    public void RequireDetailAndExplain_RejectUnfinalizedPlan()
    {
        var plan = JetAuditProgram.Plan(Request());

        Assert.Throws<InvalidOperationException>(() =>
            plan.RequireDetail(PrescreenReportDetailKind.PostPeriodApproval));
        Assert.Throws<InvalidOperationException>(() => JetAuditProgram.Explain(plan));
    }

    [Fact]
    public async Task ExecuteAsync_PassesUnfinalizedPlanAndCancellationTokenToFactsPort()
    {
        var plan = JetAuditProgram.Plan(Request());
        var expected = Facts(voucherHitCount: 3, rowHitCount: 7);
        var port = new RecordingFactsPort(expected);
        using var source = new CancellationTokenSource();

        var facts = await JetAuditProgram.ExecuteAsync(plan, port, source.Token);

        Assert.Same(plan, port.Plan);
        Assert.NotNull(port.Plan);
        Assert.False(port.Plan.IsFinalized);
        Assert.Equal(source.Token, port.CancellationToken);
        Assert.Equal(1, port.Calls);
        Assert.Same(expected, facts);
    }

    private static PrescreenReportRequest Request(
        string? postPeriodApprovalNaReason = null,
        string? suspiciousKeywordsNaReason = null,
        string? unexpectedAccountPairNaReason = null,
        string? trailingZerosNaReason = null,
        string? blankDescriptionNaReason = null) =>
        new(
            ProjectId: "project-1",
            RuleContext: new FilterRuleContext(
                MoneyScale: 100,
                LastPeriodStart: null,
                PeriodStart: "2025-01-01",
                PeriodEnd: "2025-12-31"),
            PostPeriodApprovalNaReason: postPeriodApprovalNaReason,
            SuspiciousKeywordsNaReason: suspiciousKeywordsNaReason,
            UnexpectedAccountPairNaReason: unexpectedAccountPairNaReason,
            TrailingZerosNaReason: trailingZerosNaReason,
            BlankDescriptionNaReason: blankDescriptionNaReason);

    private static PrescreenReportPlanningFacts Facts(
        long voucherHitCount,
        long rowHitCount) =>
        new(Counts(voucherHitCount, rowHitCount));

    private static Dictionary<PrescreenReportDetailKind, PrescreenHitCounts> Counts(
        long voucherHitCount,
        long rowHitCount) =>
        DetailKinds.ToDictionary(
            kind => kind,
            _ => new PrescreenHitCounts(voucherHitCount, rowHitCount));

    private sealed class RecordingFactsPort(PrescreenReportPlanningFacts result)
        : IPrescreenReportPlanningFactsPort
    {
        internal PrescreenReportPlan? Plan { get; private set; }

        internal CancellationToken CancellationToken { get; private set; }

        internal int Calls { get; private set; }

        public Task<PrescreenReportPlanningFacts> ExecuteAsync(
            PrescreenReportPlan plan,
            CancellationToken cancellationToken)
        {
            Plan = plan;
            CancellationToken = cancellationToken;
            Calls++;
            return Task.FromResult(result);
        }
    }
}
