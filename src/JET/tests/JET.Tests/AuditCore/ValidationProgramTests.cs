using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class ValidationProgramTests
{
    [Fact]
    public void Finalize_EligibleSourceControls_CompareWithGlInCore()
    {
        var plan = JetAuditProgram.Plan(Request());
        var facts = Facts() with
        {
            PopulationSummary = PopulationSummary(3, 2, 10_000, 10_000),
            ControlTotals = new ValidationControlTotalsFacts(
                EligibleSourceRowCount: 3,
                EligibleSourceDebitScaled: 10_000,
                EligibleSourceCreditScaled: 9_999)
        };

        var result = JetAuditProgram.Finalize(plan, facts);

        var storedTotals = Assert.IsType<CompletenessStoredTotals>(result.Data.StoredTotals);
        Assert.True(storedTotals.RowCountMatch);
        Assert.False(storedTotals.AmountMatch);
        Assert.Equal(3, storedTotals.EligibleSource.RowCount);
        Assert.Equal(3, storedTotals.Gl.RowCount);
        Assert.Equal(10_000, storedTotals.Gl.TotalDebitScaled);
        Assert.Equal(10_000, storedTotals.Gl.TotalCreditScaled);
    }

    [Fact]
    public void Finalize_SourceRowCountMismatch_DoesNotMatchEqualTargetAndPopulationCounts()
    {
        var plan = JetAuditProgram.Plan(Request());
        var facts = Facts() with
        {
            PopulationSummary = PopulationSummary(3, 2, 10_000, 10_000),
            ControlTotals = new ValidationControlTotalsFacts(
                EligibleSourceRowCount: 99,
                EligibleSourceDebitScaled: 10_000,
                EligibleSourceCreditScaled: 10_000)
        };

        var result = JetAuditProgram.Finalize(plan, facts);

        var storedTotals = Assert.IsType<CompletenessStoredTotals>(result.Data.StoredTotals);
        Assert.False(storedTotals.RowCountMatch);
        Assert.True(storedTotals.AmountMatch);
        Assert.Equal(99, storedTotals.EligibleSource.RowCount);
        Assert.Equal(3, storedTotals.Gl.RowCount);
    }

    [Fact]
    public void Finalize_NullControlTotals_LeavesStoredTotalsUnavailable()
    {
        var result = JetAuditProgram.Finalize(JetAuditProgram.Plan(Request()), Facts());

        Assert.Null(result.Data.StoredTotals);
    }

    [Fact]
    public void Finalize_PopulationPartitionMismatch_FailsClosed()
    {
        var facts = Facts() with
        {
            PopulationSummary = new GlPopulationSummary(
                new GlRawPopulationTotals(10, 10_000, 10_000),
                new ValidationGlTotals(8, 2, 8_000, 8_000, 0),
                new GlExcludedPopulationTotals(1, 1, 0))
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            JetAuditProgram.Finalize(JetAuditProgram.Plan(Request()), facts));

        Assert.Contains("raw／gl／excluded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Finalize_MissingTbMapping_OnlyCompletenessIsNotApplicable()
    {
        var plan = JetAuditProgram.Plan(Request(hasTbMapping: false));

        var result = JetAuditProgram.Finalize(plan, Facts());

        var completeness = Verdict(result, "completeness_test");
        Assert.False(completeness.IsApplicable);
        Assert.Equal("na", completeness.Status);
        Assert.Equal(0, completeness.Count);
        Assert.Equal(ValidationProcedures.MissingTbMappingReason, completeness.NaReason);
        Assert.All(
            result.Manifest.Procedures.Where(item => item.Definition.Slug != "completeness_test"),
            verdict => Assert.True(verdict.IsApplicable));
    }

    [Fact]
    public void Finalize_StoredTotalsMismatch_DoesNotChangeAccountComparisonOutwardStatus()
    {
        var plan = JetAuditProgram.Plan(Request());
        var facts = Facts() with
        {
            CompletenessDiffAccountCount = 0,
            ControlTotals = new ValidationControlTotalsFacts(7, 1, 2)
        };

        var result = JetAuditProgram.Finalize(plan, facts);

        Assert.False(Assert.IsType<CompletenessStoredTotals>(result.Data.StoredTotals).AmountMatch);
        var completeness = Verdict(result, "completeness_test");
        Assert.Equal("na", completeness.Status);
        Assert.Equal(0, completeness.Count);
        Assert.Null(completeness.NaReason);
    }

    [Fact]
    public void Finalize_AmountDistribution_UsesCanonicalOrderAndNonZeroDenominator()
    {
        var facts = Facts() with
        {
            AmountBinCounts =
            [
                new ValidationAmountBinCount("gt10M", 1),
                new ValidationAmountBinCount("zero", 4),
                new ValidationAmountBinCount("lt1k", 1),
                new ValidationAmountBinCount("1k-2k", 1)
            ]
        };

        var result = JetAuditProgram.Finalize(JetAuditProgram.Plan(Request()), facts);
        var bins = result.AmountDistribution.Bins;

        Assert.Equal(ValidationAmountDistributionCatalog.BinKeys, bins.Select(bin => bin.Key));
        Assert.Equal(4, bins[0].Count);
        Assert.Null(bins[0].EcdfPct);
        Assert.Equal(33.3m, bins[1].EcdfPct);
        Assert.Equal(66.7m, bins[2].EcdfPct);
        Assert.Equal(100.0m, bins[^1].EcdfPct);
        Assert.All(bins.Skip(3).SkipLast(1), bin => Assert.Equal(66.7m, bin.EcdfPct));
    }

    [Fact]
    public void Finalize_AmountDistribution_ZeroNonZeroPopulation_LeavesEveryEcdfNull()
    {
        var facts = Facts() with
        {
            AmountBinCounts = [new ValidationAmountBinCount("zero", 7)]
        };

        var result = JetAuditProgram.Finalize(JetAuditProgram.Plan(Request()), facts);

        Assert.Equal(7, result.AmountDistribution.Bins[0].Count);
        Assert.All(result.AmountDistribution.Bins, bin => Assert.Null(bin.EcdfPct));
    }

    // 配對沒確認的情境一律當成已匯入 TB；沒有匯入的原因句另由 WorkflowWithoutTbTests 檢查。
    private static ValidationRequest Request(bool hasTbMapping = true) => new(
        ProjectId: "project",
        HasGlMapping: true,
        HasTbMapping: hasTbMapping,
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        MoneyScale: 10_000,
        SampleSeed: 48_271,
        ResultId: "run",
        GeneratedUtc: new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero),
        SampleSize: 59,
        HasTbImport: true);

    private static ValidationFacts Facts() => new(
        PopulationSummary(0, 0, 0, 0),
        CompletenessDiffAccountCount: 0,
        CompletenessDiffAccounts: [],
        UnbalancedVoucherCount: 0,
        InfSampleCount: 0,
        BlankAccountNumberCount: 0,
        BlankVoucherNumberCount: 0,
        BlankDescriptionCount: 0,
        ApprovalDateNotInPeriodCount: 0,
        SourceQualityFindingCount: 0,
        UnbalancedVouchers: [],
        BlankRecordRows: [],
        ControlTotals: null,
        AmountBinCounts: []);

    private static GlPopulationSummary PopulationSummary(
        long rowCount,
        long voucherCount,
        long debit,
        long credit) => new(
        new GlRawPopulationTotals(rowCount, debit, credit),
        new ValidationGlTotals(rowCount, voucherCount, debit, credit, debit - credit),
        new GlExcludedPopulationTotals(0, 0, 0));

    private static ProcedureVerdict Verdict(ValidationResult result, string slug) =>
        result.Manifest.Procedures.Single(item =>
            string.Equals(item.Definition.Slug, slug, StringComparison.Ordinal));
}
