using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class JetAuditProgramTests
{
    // 驗證計畫只接受目前的 INF 抽樣演算法版本；沒有版本的快照代表舊版 JET 建立的案件，會被拒絕。
    private const int CurrentInfSamplingVersion = JetAuditProgram.CurrentInfSamplingAlgorithmVersion;

    [Fact]
    public void Evaluate_WithoutTbMapping_ReturnsSingleSourceNotApplicableReason()
    {
        // 裁定 2026-10-29-16：沒有 TB 時要分辨沒有匯入與配對還沒確認，這裡是已匯入 TB、配對還沒確認的情況。
        // 第一次失敗：Focused 收據 20261030-233634558-aa5dc6c98193412894b1d29b6fad6a84。
        var verdict = ValidationProcedures.Evaluate(hasTbMapping: false, hasTbImport: true);

        Assert.False(verdict.IsApplicable);
        Assert.Equal("completeness_test", verdict.Definition.Slug);
        Assert.Equal(ValidationProcedures.MissingTbMappingReason, verdict.NaReason);
        // 裁定 2026-10-02-01：畫面不再說「提交」欄位配對，原因改用「完成」；斷言改鎖新句子。
        // 確認配對用語統一（裁定 2026-10-04-01）；仍鎖定完整原因與單一 N/A 判定。
        // 第一次失敗：20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d。
        // 裁定 2026-10-30-16 確認兩種原因句，預期值換成新句子；同一個收據也是這裡的第一次失敗。
        // 裁定 2026-11-01-06 把這句的「還沒確認」改成「尚未確認」，其餘字不動；第一次失敗：Focused 收據 20261101-062343383-804cee29d9d9428f9e5f502ea4e7ae94。
        Assert.Equal("TB 欄位配對尚未確認，無法執行 GL 與 TB 逐科目比對。", verdict.NaReason);
        Assert.Equal(
            "沒有匯入 TB，無法執行 GL 與 TB 逐科目比對。",
            ValidationProcedures.Evaluate(hasTbMapping: false).NaReason);
    }

    [Fact]
    public void Plan_ValidationFamily_SelectsFourApplicableProcedures()
    {
        var snapshot = new AuditProjectSnapshot(
            "project-1", HasGlMapping: true, HasTbMapping: true,
            "2025-01-01", "2025-12-31", 10_000, 48271,
                SampleSeedVersion: CurrentInfSamplingVersion);
        var parameters = new AuditUserParameters(
            "run-1", new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero), 59);

        var plan = JetAuditProgram.Plan(snapshot, parameters);

        Assert.Equal(4, plan.Procedures.Count);
        Assert.All(plan.Procedures, verdict => Assert.True(verdict.IsApplicable));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void Plan_ValidationWithLegacySampleSeedVersion_RejectsAsOldProject(int? version)
    {
        var snapshot = new AuditProjectSnapshot(
            "project-1", HasGlMapping: true, HasTbMapping: true,
            "2025-01-01", "2025-12-31", 10_000, 48271,
            SampleSeedVersion: version);
        var parameters = new AuditUserParameters(
            "run-1", new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero), 59);

        var exception = Assert.Throws<JetActionException>(() => JetAuditProgram.Plan(snapshot, parameters));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, exception.Code);
        // 裁定 2026-10-14-01 確認了這句的用字（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。開案時已擋下舊版案件，
        // 驗證計畫照正常操作走不到這裡：訊息改成非預期錯誤那一句，檢查代號記進支援日誌；錯誤代碼不變，仍拒絕執行。
        Assert.Equal(JetPayloadRejection.UnexpectedErrorMessage, exception.Message);
        Assert.Equal("inf_algorithm_version_legacy", JetPayloadRejection.CheckOf(exception));
    }

    [Fact]
    public void Finalize_ZeroAndPositiveCounts_PreserveWireStatusSemantics()
    {
        var plan = JetAuditProgram.Plan(
            new AuditProjectSnapshot(
                "project-1", HasGlMapping: true, HasTbMapping: true,
                "2025-01-01", "2025-12-31", 10_000, 48271,
                SampleSeedVersion: CurrentInfSamplingVersion),
            new AuditUserParameters(
                "run-1", new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero), 59));
        var result = new ValidationRunResult(
            new GlPopulationStats(10, 5, 100, 100, 0),
            PopulationSummary(),
            CompletenessDiffAccountCount: 0,
            CompletenessDiffAccounts: [],
            UnbalancedVoucherCount: 2,
            InfSampleCount: 0,
            NullAccountCount: 1,
            NullDocumentCount: 2,
            NullDescriptionCount: 3,
            ApprovalDateNotInPeriodCount: 4,
            SourceQualityFindingCount: 5,
            UnbalancedVouchers: [],
            BlankRecordRows: [],
            StoredTotals: null);

        var manifest = JetAuditProgram.Finalize(plan, new AuditOutcome(result));

        Assert.Equal("na", Verdict(manifest, "completeness_test").Status);
        Assert.Equal("V", Verdict(manifest, "voucher_balance_test").Status);
        Assert.Equal("na", Verdict(manifest, "inf_sampling_test").Status);
        Assert.Equal("V", Verdict(manifest, "blank_records_test").Status);
        Assert.Equal(10, Verdict(manifest, "blank_records_test").Count);
    }

    [Theory]
    [InlineData(0, "na")]
    [InlineData(7, "V")]
    public void Finalize_CompletenessStoredTotalsMismatch_DoesNotChangeAccountComparisonStatus(
        long accountComparisonDifferenceCount,
        string expectedStatus)
    {
        var plan = JetAuditProgram.Plan(
            new AuditProjectSnapshot(
                "project-1", HasGlMapping: true, HasTbMapping: true,
                "2025-01-01", "2025-12-31", 10_000, 48271,
                SampleSeedVersion: CurrentInfSamplingVersion),
            new AuditUserParameters(
                "run-1", new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero), 59));
        var result = ValidationResult(
            completenessDiffAccountCount: accountComparisonDifferenceCount,
            storedTotals: new CompletenessStoredTotals(
                EligibleSource: new CompletenessPopulationTotals(10, 100, 99),
                Gl: new CompletenessPopulationTotals(9, 100, 100),
                RowCountMatch: false,
                AmountMatch: false));

        var manifest = JetAuditProgram.Finalize(plan, new AuditOutcome(result));

        var completeness = Verdict(manifest, "completeness_test");
        Assert.Equal(expectedStatus, completeness.Status);
        Assert.Equal(accountComparisonDifferenceCount, completeness.Count);
        Assert.Null(completeness.NaReason);
    }

    [Fact]
    public void Finalize_CompletenessWithoutTbMapping_IgnoresStaleAccountComparisonCount()
    {
        var plan = JetAuditProgram.Plan(
            new AuditProjectSnapshot(
                "project-1", HasGlMapping: true, HasTbMapping: false,
                "2025-01-01", "2025-12-31", 10_000, 48271,
                SampleSeedVersion: CurrentInfSamplingVersion,
                // 已匯入 TB、配對還沒確認；沒有這個事實時原因會變成沒有匯入 TB（第一次失敗：Focused 收據 20261030-233634558-aa5dc6c98193412894b1d29b6fad6a84）。
                HasTbImport: true),
            new AuditUserParameters(
                "run-1", new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero), 59));

        var manifest = JetAuditProgram.Finalize(
            plan,
            new AuditOutcome(ValidationResult(completenessDiffAccountCount: 7, storedTotals: null)));

        var completeness = Verdict(manifest, "completeness_test");
        Assert.Equal("na", completeness.Status);
        Assert.Equal(0, completeness.Count);
        Assert.Equal(ValidationProcedures.MissingTbMappingReason, completeness.NaReason);
    }

    [Fact]
    public void Finalize_NonApplicableValidationProcedure_IgnoresStalePositiveCount()
    {
        var generated = JetAuditProgram.Plan(
            new AuditProjectSnapshot(
                "project-1", HasGlMapping: true, HasTbMapping: true,
                "2025-01-01", "2025-12-31", 10_000, 48271,
                SampleSeedVersion: CurrentInfSamplingVersion),
            new AuditUserParameters(
                "run-1", new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero), 59));
        var procedures = generated.Procedures
            .Select(verdict => verdict.Definition.Slug == "voucher_balance_test"
                ? verdict with { IsApplicable = false, NaReason = "characterization" }
                : verdict)
            .ToArray();
        var plan = generated with { Procedures = Array.AsReadOnly(procedures) };
        var result = ValidationResult(completenessDiffAccountCount: 0, storedTotals: null) with
        {
            UnbalancedVoucherCount = 2
        };

        var manifest = JetAuditProgram.Finalize(plan, new AuditOutcome(result));

        var voucherBalance = Verdict(manifest, "voucher_balance_test");
        Assert.Equal("na", voucherBalance.Status);
        Assert.Equal(0, voucherBalance.Count);
        Assert.Equal("characterization", voucherBalance.NaReason);
    }

    private static ProcedureVerdict Verdict(AuditRunManifest manifest, string slug) =>
        Assert.Single(manifest.Procedures, verdict => verdict.Definition.Slug == slug);

    private static ValidationRunResult ValidationResult(
        long completenessDiffAccountCount,
        CompletenessStoredTotals? storedTotals) => new(
        new GlPopulationStats(10, 5, 100, 100, 0),
        PopulationSummary(),
        completenessDiffAccountCount,
        CompletenessDiffAccounts: [],
        UnbalancedVoucherCount: 0,
        InfSampleCount: 0,
        NullAccountCount: 0,
        NullDocumentCount: 0,
        NullDescriptionCount: 0,
        ApprovalDateNotInPeriodCount: 0,
        SourceQualityFindingCount: 0,
        UnbalancedVouchers: [],
        BlankRecordRows: [],
        storedTotals);

    private static GlPopulationSummary PopulationSummary(
        long rowCount = 10,
        long voucherCount = 5,
        long debit = 100,
        long credit = 100) => new(
        new GlRawPopulationTotals(rowCount, debit, credit),
        new ValidationGlTotals(rowCount, voucherCount, debit, credit, debit - credit),
        new GlExcludedPopulationTotals(0, 0, 0));
}
