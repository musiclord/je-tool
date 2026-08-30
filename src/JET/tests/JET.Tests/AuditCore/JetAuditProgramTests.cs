using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class JetAuditProgramTests
{
    [Fact]
    public void Evaluate_WithoutTbMapping_ReturnsSingleSourceNotApplicableReason()
    {
        var verdict = ValidationProcedures.Evaluate(hasTbMapping: false);

        Assert.False(verdict.IsApplicable);
        Assert.Equal("completeness_test", verdict.Definition.Slug);
        Assert.Equal(ValidationProcedures.MissingTbMappingReason, verdict.NaReason);
        Assert.Equal("尚未提交 TB 欄位配對，無法執行完整性測試。", verdict.NaReason);
        Assert.Empty(verdict.Parameters);
    }

    [Fact]
    public void Plan_ValidationFamily_BindsPeriodAndSamplingParameters()
    {
        var snapshot = new AuditCaseSnapshot(
            "project-1", HasGlMapping: true, HasTbMapping: true,
            "2025-01-01", "2025-12-31", 10_000, 48271);
        var parameters = new AuditUserParameters(
            "run-1", new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero), 59);

        var plan = JetAuditProgram.Plan(snapshot, parameters);

        Assert.Equal(4, plan.Procedures.Count);
        Assert.All(plan.Procedures, verdict => Assert.True(verdict.IsApplicable));
        Assert.All(plan.Procedures, verdict =>
        {
            Assert.Equal("2025-01-01", verdict.Parameters["@periodStart"]);
            Assert.Equal("2025-12-31", verdict.Parameters["@periodEnd"]);
        });

        var inf = Assert.Single(plan.Procedures, verdict => verdict.Definition.Slug == "inf_sampling_test");
        Assert.Equal("run-1", inf.Parameters["@runId"]);
        Assert.Equal("48271", inf.Parameters["@seed"]);
        Assert.Equal("59", inf.Parameters["@n"]);
    }

    [Fact]
    public void Finalize_ZeroAndPositiveCounts_PreserveWireStatusSemantics()
    {
        var plan = JetAuditProgram.Plan(
            new AuditCaseSnapshot(
                "project-1", HasGlMapping: true, HasTbMapping: true,
                "2025-01-01", "2025-12-31", 10_000, 48271),
            new AuditUserParameters(
                "run-1", new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero), 59));
        var result = new ValidationRunResult(
            new GlPopulationStats(10, 5, 100, 100, 0),
            PopulationSummary(),
            CompletenessDiffAccountCount: 0,
            CompletenessDiffAccounts: [],
            UnbalancedDocumentCount: 2,
            InfSampleCount: 0,
            NullAccountCount: 1,
            NullDocumentCount: 2,
            NullDescriptionCount: 3,
            OutOfRangeDateCount: 4,
            SourceQualityFindingCount: 5,
            UnbalancedDocuments: [],
            NullRecordRows: [],
            PartA: null);

        var manifest = JetAuditProgram.Finalize(plan, new AuditOutcome(result));

        Assert.Equal("na", Verdict(manifest, "completeness_test").Status);
        Assert.Equal("V", Verdict(manifest, "doc_balance_test").Status);
        Assert.Equal("na", Verdict(manifest, "inf_sampling_test").Status);
        Assert.Equal("V", Verdict(manifest, "null_records_test").Status);
        Assert.Equal(10, Verdict(manifest, "null_records_test").Count);
    }

    [Theory]
    [InlineData(0, "na")]
    [InlineData(7, "V")]
    public void Finalize_CompletenessPartAMismatch_DoesNotChangePartBStatus(
        long partBDifferenceCount,
        string expectedStatus)
    {
        var plan = JetAuditProgram.Plan(
            new AuditCaseSnapshot(
                "project-1", HasGlMapping: true, HasTbMapping: true,
                "2025-01-01", "2025-12-31", 10_000, 48271),
            new AuditUserParameters(
                "run-1", new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero), 59));
        var result = ValidationResult(
            completenessDiffAccountCount: partBDifferenceCount,
            partA: new CompletenessPartA(
                EligibleSource: new CompletenessPopulationTotals(10, 100, 99),
                EffectiveTarget: new CompletenessPopulationTotals(9, 100, 100),
                RowCountMatch: false,
                AmountMatch: false));

        var manifest = JetAuditProgram.Finalize(plan, new AuditOutcome(result));

        var completeness = Verdict(manifest, "completeness_test");
        Assert.Equal(expectedStatus, completeness.Status);
        Assert.Equal(partBDifferenceCount, completeness.Count);
        Assert.Null(completeness.NaReason);
    }

    [Fact]
    public void Finalize_CompletenessWithoutTbMapping_IgnoresStalePartBCount()
    {
        var plan = JetAuditProgram.Plan(
            new AuditCaseSnapshot(
                "project-1", HasGlMapping: true, HasTbMapping: false,
                "2025-01-01", "2025-12-31", 10_000, 48271),
            new AuditUserParameters(
                "run-1", new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero), 59));

        var manifest = JetAuditProgram.Finalize(
            plan,
            new AuditOutcome(ValidationResult(completenessDiffAccountCount: 7, partA: null)));

        var completeness = Verdict(manifest, "completeness_test");
        Assert.Equal("na", completeness.Status);
        Assert.Equal(0, completeness.Count);
        Assert.Equal(ValidationProcedures.MissingTbMappingReason, completeness.NaReason);
    }

    [Fact]
    public void Finalize_NonApplicableValidationProcedure_IgnoresStalePositiveCount()
    {
        var generated = JetAuditProgram.Plan(
            new AuditCaseSnapshot(
                "project-1", HasGlMapping: true, HasTbMapping: true,
                "2025-01-01", "2025-12-31", 10_000, 48271),
            new AuditUserParameters(
                "run-1", new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero), 59));
        var procedures = generated.Procedures
            .Select(verdict => verdict.Definition.Slug == "doc_balance_test"
                ? verdict with { IsApplicable = false, NaReason = "characterization" }
                : verdict)
            .ToArray();
        var plan = generated with { Procedures = Array.AsReadOnly(procedures) };
        var result = ValidationResult(completenessDiffAccountCount: 0, partA: null) with
        {
            UnbalancedDocumentCount = 2
        };

        var manifest = JetAuditProgram.Finalize(plan, new AuditOutcome(result));

        var docBalance = Verdict(manifest, "doc_balance_test");
        Assert.Equal("na", docBalance.Status);
        Assert.Equal(0, docBalance.Count);
        Assert.Equal("characterization", docBalance.NaReason);
    }

    [Fact]
    public void Explain_WithoutTbMapping_UsesSingleSourceNotApplicableReason()
    {
        var plan = JetAuditProgram.Plan(
            new AuditCaseSnapshot(
                "project-1", HasGlMapping: true, HasTbMapping: false,
                "2025-01-01", "2025-12-31", 10_000, 48271),
            new AuditUserParameters(
                "run-1", new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero), 59));
        var outcome = new AuditOutcome(new ValidationRunResult(
            new GlPopulationStats(0, 0, 0, 0, 0),
            PopulationSummary(0, 0, 0, 0),
            CompletenessDiffAccountCount: 0,
            CompletenessDiffAccounts: [],
            UnbalancedDocumentCount: 0,
            InfSampleCount: 0,
            NullAccountCount: 0,
            NullDocumentCount: 0,
            NullDescriptionCount: 0,
            OutOfRangeDateCount: 0,
            SourceQualityFindingCount: 0,
            UnbalancedDocuments: [],
            NullRecordRows: [],
            PartA: null));

        var explanation = JetAuditProgram.Explain(JetAuditProgram.Finalize(plan, outcome));

        Assert.Contains(
            "完整性測試（completeness_test）：na；計數 0；N/A 原因：" +
            ValidationProcedures.MissingTbMappingReason,
            explanation,
            StringComparison.Ordinal);
        Assert.DoesNotContain(ValidationProcedures.MissingTbMappingReason + "。", explanation, StringComparison.Ordinal);
    }

    private static ProcedureVerdict Verdict(AuditRunManifest manifest, string slug) =>
        Assert.Single(manifest.Procedures, verdict => verdict.Definition.Slug == slug);

    private static ValidationRunResult ValidationResult(
        long completenessDiffAccountCount,
        CompletenessPartA? partA) => new(
        new GlPopulationStats(10, 5, 100, 100, 0),
        PopulationSummary(),
        completenessDiffAccountCount,
        CompletenessDiffAccounts: [],
        UnbalancedDocumentCount: 0,
        InfSampleCount: 0,
        NullAccountCount: 0,
        NullDocumentCount: 0,
        NullDescriptionCount: 0,
        OutOfRangeDateCount: 0,
        SourceQualityFindingCount: 0,
        UnbalancedDocuments: [],
        NullRecordRows: [],
        partA);

    private static GlPopulationSummary PopulationSummary(
        long rowCount = 10,
        long voucherCount = 5,
        long debit = 100,
        long credit = 100) => new(
        new GlRawPopulationTotals(rowCount, debit, credit),
        new ValidationEffectivePopulationTotals(rowCount, voucherCount, debit, credit, debit - credit),
        new GlExcludedPopulationTotals(0, 0, 0));
}
