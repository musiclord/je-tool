using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyAuditParityObservationsTests
{
    [Fact]
    public void CompletenessControls_PreserveUnexecutedBooleanAndCountStates()
    {
        var observation = new LegacyCompletenessObservation(
            differenceAccountCount: 0,
            partASourceRowCount: null,
            partATargetRowCount: 0,
            partATotalDebit: 0m,
            partATotalCredit: 0m,
            partARowCountMatch: null,
            partAAmountMatch: null);

        Assert.Equal(LegacyObservedCountState.NotExecuted, observation.PartASourceRowCount.State);
        Assert.Equal(LegacyObservedCountState.ExecutedZero, observation.PartATargetRowCount.State);
        Assert.Equal(LegacyObservedBooleanState.NotExecuted, observation.PartARowCountMatch.State);
        Assert.Throws<LegacyAuditParityComparisonCompletenessException>(() =>
            observation.PartARowCountMatch.RequireValue(
                LegacyAuditParityMetricIds.PartARowCountMatch));
    }

    [Fact]
    public void ObservedCount_DistinguishesNotExecutedUnavailableZeroAndPositive()
    {
        var missing = LegacyObservedCount.NotExecuted;
        var unavailable = LegacyObservedCount.ExecutedUnavailable;
        var zero = LegacyObservedCount.Executed(0);
        var positive = LegacyObservedCount.Executed(7);

        Assert.Equal(LegacyObservedCountState.NotExecuted, missing.State);
        Assert.Null(missing.Value);
        Assert.False(missing.WasExecuted);
        Assert.False(missing.HasExactValue);
        Assert.Equal(LegacyObservedCountState.ExecutedUnavailable, unavailable.State);
        Assert.Null(unavailable.Value);
        Assert.True(unavailable.WasExecuted);
        Assert.False(unavailable.HasExactValue);
        Assert.Equal(LegacyObservedCountState.ExecutedZero, zero.State);
        Assert.Equal(0, zero.Value);
        Assert.True(zero.HasExactValue);
        Assert.Equal(LegacyObservedCountState.ExecutedNonZero, positive.State);
        Assert.Equal(7, positive.Value);
        Assert.True(positive.HasExactValue);
        Assert.Throws<LegacyAuditParityComparisonCompletenessException>(() =>
            missing.RequireValue(LegacyAuditParityMetricIds.UnbalancedVoucherCount));
        Assert.Throws<LegacyAuditParityComparisonCompletenessException>(() =>
            unavailable.RequireValue(LegacyAuditParityMetricIds.UnbalancedVoucherCount));
        Assert.Throws<ArgumentException>(() => LegacyObservedCount.Reload(
            LegacyObservedCountState.ExecutedUnavailable,
            1));
    }

    [Fact]
    public void RowVoucherCounts_RejectsPartiallyExecutedPair()
    {
        Assert.Throws<ArgumentException>(() => new LegacyRowVoucherCounts(
            LegacyObservedCount.NotExecuted,
            LegacyObservedCount.Executed(0)));
    }

    [Fact]
    public void ProviderComparison_WhenBothSidesDidNotExecuteMetric_FailsClosed()
    {
        var left = ProviderObservation(
            LegacyAuditParityProvider.Sqlite,
            missingRule: LegacyPrescreenRuleId.PostPeriodApproval);
        var right = ProviderObservation(
            LegacyAuditParityProvider.DuckDb,
            missingRule: LegacyPrescreenRuleId.PostPeriodApproval);

        var error = Assert.Throws<LegacyAuditParityComparisonCompletenessException>(() =>
            LegacyAuditParityComparator.CompareProviders([left, right]));

        Assert.Equal(
            LegacyAuditParityMetricIds.PrescreenRowCount(LegacyPrescreenRuleId.PostPeriodApproval),
            error.MetricId);
    }

    [Fact]
    public void ReportComparison_NotExecutedAndExecutedZeroFailClosed()
    {
        var family = LegacyAuditParityReportFamily.WorkingPaperCompleteness;
        var sheet = LegacyAuditParityFingerprints.SheetName(
            LegacyAuditParityReportMetrics.BaseSheetName(family));
        var missing = LegacyReportObservation.FromFingerprintObservations(
        [
            new KeyValuePair<string, LegacyObservedCount>(
                sheet,
                LegacyObservedCount.NotExecuted),
        ]);
        var zero = LegacyReportObservation.FromFingerprintObservations(
        [
            new KeyValuePair<string, LegacyObservedCount>(
                sheet,
                LegacyObservedCount.Executed(0)),
        ]);
        var metricId = LegacyAuditParityReportMetrics.DataRowCount(family);

        var error = Assert.Throws<LegacyAuditParityComparisonCompletenessException>(() =>
            LegacyAuditParityReportMetrics.Difference(
                LegacyReportKind.WorkingPaper,
                missing,
                zero));

        Assert.Equal(metricId, error.MetricId);
    }

    [Fact]
    public void ReportComparison_ContinuationWithoutBaseFailsClosed()
    {
        var family = LegacyAuditParityReportFamily.WorkingPaperRiskVoucherMatrix;
        var invalid = Report(
            (LegacyAuditParityReportMetrics.ContinuationSheetName(
                family,
                part: 2,
                localizedSuffix: false), 1));

        var error = Assert.Throws<LegacyAuditParityReportMetricsException>(() =>
            LegacyAuditParityReportMetrics.Difference(
                LegacyReportKind.WorkingPaper,
                invalid,
                Report((LegacyAuditParityReportMetrics.BaseSheetName(family), 1))));

        Assert.Equal("continuation-sequence", error.FieldId);
    }

    [Theory]
    [InlineData((int)LegacyAuditParityReportFamily.WorkingPaperReliability)]
    [InlineData((int)LegacyAuditParityReportFamily.WorkingPaperRiskConditionSummary)]
    public void ReportCatalog_FixedCapacityWorkingPaperFamiliesRejectContinuation(int familyValue)
    {
        var family = (LegacyAuditParityReportFamily)familyValue;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LegacyAuditParityReportMetrics.ContinuationSheetName(
                family,
                part: 2,
                localizedSuffix: true));
    }

    [Fact]
    public void ReportComparison_AbsentConditionalFamilyAndExecutedRowsEmitBothDimensions()
    {
        var family = LegacyAuditParityReportFamily.WorkingPaperCompletenessDifference;
        var otherFamily = LegacyAuditParityReportFamily.WorkingPaperCover;
        var absent = Report((LegacyAuditParityReportMetrics.BaseSheetName(otherFamily), 0));
        var present = Report(
            (LegacyAuditParityReportMetrics.BaseSheetName(otherFamily), 0),
            (LegacyAuditParityReportMetrics.BaseSheetName(family), 3));

        var differences = LegacyAuditParityReportMetrics.Difference(
            LegacyReportKind.WorkingPaper,
            absent,
            present);

        Assert.Collection(
            differences,
            difference =>
            {
                Assert.Equal(LegacyAuditParityReportMetrics.SheetCount(family), difference.MetricId);
                Assert.Equal(1, difference.DifferenceCount);
            },
            difference =>
            {
                Assert.Equal(LegacyAuditParityReportMetrics.DataRowCount(family), difference.MetricId);
                Assert.Equal(3, difference.DifferenceCount);
            });
    }

    [Fact]
    public void ReportComparison_UnknownPhysicalSheetFailsClosedWithoutEchoingItsName()
    {
        var family = LegacyAuditParityReportFamily.WorkingPaperRiskVoucherMatrix;
        var invalid = Report(("synthetic-unknown-physical-sheet", 1));

        var error = Assert.Throws<LegacyAuditParityReportMetricsException>(() =>
            LegacyAuditParityReportMetrics.Difference(
                LegacyReportKind.WorkingPaper,
                invalid,
                Report((LegacyAuditParityReportMetrics.BaseSheetName(family), 1))));

        Assert.Equal("unknown-sheet.working_paper", error.FieldId);
        Assert.DoesNotContain(
            "synthetic-unknown-physical-sheet",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReportComparison_DualContinuationSuffixesFailClosed()
    {
        var family = LegacyAuditParityReportFamily.WorkingPaperRiskVoucherMatrix;
        var invalid = Report(
            (LegacyAuditParityReportMetrics.BaseSheetName(family), 1),
            (LegacyAuditParityReportMetrics.ContinuationSheetName(
                family,
                part: 2,
                localizedSuffix: false), 1),
            (LegacyAuditParityReportMetrics.ContinuationSheetName(
                family,
                part: 2,
                localizedSuffix: true), 1));

        var error = Assert.Throws<LegacyAuditParityReportMetricsException>(() =>
            LegacyAuditParityReportMetrics.Difference(
                LegacyReportKind.WorkingPaper,
                invalid,
                Report((LegacyAuditParityReportMetrics.BaseSheetName(family), 3))));

        Assert.Equal("continuation-sequence", error.FieldId);
    }

    [Fact]
    public void ReportComparison_ContinuationGapFailsClosed()
    {
        var family = LegacyAuditParityReportFamily.WorkingPaperRiskVoucherMatrix;
        var invalid = Report(
            (LegacyAuditParityReportMetrics.BaseSheetName(family), 1),
            (LegacyAuditParityReportMetrics.ContinuationSheetName(
                family,
                part: 3,
                localizedSuffix: false), 1));

        var error = Assert.Throws<LegacyAuditParityReportMetricsException>(() =>
            LegacyAuditParityReportMetrics.Difference(
                LegacyReportKind.WorkingPaper,
                invalid,
                Report((LegacyAuditParityReportMetrics.BaseSheetName(family), 2))));

        Assert.Equal("continuation-sequence", error.FieldId);
    }

    [Fact]
    public void CriteriaReportMetric_UsesOriginalLegacyScenarioOrdinal()
    {
        var family = LegacyAuditParityReportFamily.CriteriaScenario004;
        var left = Report((LegacyAuditParityReportMetrics.BaseSheetName(family), 1));
        var right = Report((LegacyAuditParityReportMetrics.BaseSheetName(family), 3));

        var difference = Assert.Single(LegacyAuditParityReportMetrics.Difference(
            LegacyReportKind.CriteriaSelectionReport,
            left,
            right));

        Assert.Equal("report.criteria_selection_report.scenario.004.data_row_count", difference.MetricId);
        Assert.Equal(2, difference.DifferenceCount);
    }

    [Fact]
    public void Artifact_RoundTripsDeidentifiedMetricsAndProducesSameComparisonInput()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"artifact-{Guid.NewGuid():N}");
        var sqlite = ProviderObservation(LegacyAuditParityProvider.Sqlite);
        var duckDb = ProviderObservation(LegacyAuditParityProvider.DuckDb);
        var provenance = new LegacyAuditParityMappingProvenance(
        [
            new(
                LegacyMappingDataset.Gl,
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingResolutionSource.NormalizedHeaderIdentity),
            new(
                LegacyMappingDataset.Tb,
                LegacyMappingSlot.TbAccountNumber,
                LegacyMappingResolutionSource.WorkingPaperExplicitMapping),
        ]);

        var path = LegacyAuditParityObservationArtifactStore.Write(workspace, sqlite, provenance);
        var reloaded = LegacyAuditParityObservationArtifactStore.Load(path);

        Assert.Empty(LegacyAuditParityComparator.CompareProviders([sqlite, duckDb]).Entries);
        Assert.Empty(LegacyAuditParityComparator.CompareProviders([reloaded.Observation, duckDb]).Entries);
        Assert.True(reloaded.MappingProvenance.UsesHeaderBindingFallback);
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("sensitive-member", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-sheet", json, StringComparison.Ordinal);
        Assert.DoesNotContain("987654321.125", json, StringComparison.Ordinal);
        Assert.DoesNotContain("filePath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Artifact_WithUnknownProperty_IsRejected()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"artifact-invalid-{Guid.NewGuid():N}");
        var path = LegacyAuditParityObservationArtifactStore.Write(
            workspace,
            ProviderObservation(LegacyAuditParityProvider.Sqlite),
            new LegacyAuditParityMappingProvenance([]));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var malformed = document.RootElement.GetRawText().TrimEnd('}') + ",\"unknown\":true}";
        File.WriteAllText(path, malformed);

        Assert.Throws<LegacyAuditParityObservationArtifactException>(() =>
            LegacyAuditParityObservationArtifactStore.Load(path));
    }

    [Fact]
    public void Artifact_RoundTripsExecutedUnavailableWithoutCollapsingExecutionState()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"artifact-unavailable-{Guid.NewGuid():N}");
        var path = LegacyAuditParityObservationArtifactStore.Write(
            workspace,
            ProviderObservation(
                LegacyAuditParityProvider.Sqlite,
                filterVoucherCount: LegacyObservedCount.ExecutedUnavailable),
            new LegacyAuditParityMappingProvenance([]));

        var reloaded = LegacyAuditParityObservationArtifactStore.Load(path);
        var voucher = reloaded.Observation.Metrics.FilterScenarios[
            LegacyFilterScenarioId.FromOrdinal(1)].VoucherCount;

        Assert.Equal(LegacyObservedCountState.ExecutedUnavailable, voucher.State);
        Assert.True(voucher.WasExecuted);
        Assert.False(voucher.HasExactValue);
        Assert.Null(voucher.Value);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var persisted = json["metrics"]!["filterScenarios"]!.AsArray()[0]!["voucherCount"]!.AsObject();
        Assert.Equal("ExecutedUnavailable", persisted["state"]!.GetValue<string>());
        Assert.True(persisted.ContainsKey("value"));
        Assert.Null(persisted["value"]);
    }

    [Fact]
    public void Artifact_ExecutedUnavailableWithNumericValue_IsRejected()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"artifact-unavailable-invalid-{Guid.NewGuid():N}");
        var path = LegacyAuditParityObservationArtifactStore.Write(
            workspace,
            ProviderObservation(
                LegacyAuditParityProvider.Sqlite,
                filterVoucherCount: LegacyObservedCount.ExecutedUnavailable),
            new LegacyAuditParityMappingProvenance([]));
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var persisted = json["metrics"]!["filterScenarios"]!.AsArray()[0]!["voucherCount"]!.AsObject();
        persisted["value"] = 1;
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Assert.Throws<LegacyAuditParityObservationArtifactException>(() =>
            LegacyAuditParityObservationArtifactStore.Load(path));
    }

    [Fact]
    public void CapturedProviderFilterPositions_RebindAndRoundTripWithoutChangingCounts()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"artifact-filter-rebind-{Guid.NewGuid():N}");
        var original = ProviderObservation(LegacyAuditParityProvider.Sqlite);
        var rebound = LegacyAuditParityFilterScenarioBindings.RebindCapturedProviderObservation(
            original,
            [LegacyFilterScenarioId.FromOrdinal(4)]);

        Assert.DoesNotContain(
            LegacyFilterScenarioId.FromOrdinal(1),
            rebound.Metrics.FilterScenarios.Keys);
        var reboundCounts = Assert.Single(rebound.Metrics.FilterScenarios).Value;
        Assert.Equal(3, reboundCounts.RowCount.RequireValue(
            LegacyAuditParityMetricIds.FilterRowCount(LegacyFilterScenarioId.FromOrdinal(4))));
        Assert.Equal(2, reboundCounts.VoucherCount.RequireValue(
            LegacyAuditParityMetricIds.FilterVoucherCount(LegacyFilterScenarioId.FromOrdinal(4))));

        var path = LegacyAuditParityObservationArtifactStore.Write(
            workspace,
            rebound,
            new LegacyAuditParityMappingProvenance([]));
        var reloaded = LegacyAuditParityObservationArtifactStore.Load(path);
        Assert.Equal(
            [LegacyFilterScenarioId.FromOrdinal(4)],
            reloaded.Observation.Metrics.FilterScenarios.Keys);
        var criteriaSheets = reloaded.Observation.Metrics.Reports[
            LegacyReportKind.CriteriaSelectionReport].FingerprintedSheets;
        Assert.Contains(
            criteriaSheets,
            sheet => sheet.Key == LegacyAuditParityFingerprints.SheetName("#Criteria Select 4"));
        Assert.DoesNotContain(
            criteriaSheets,
            sheet => sheet.Key == LegacyAuditParityFingerprints.SheetName("#Criteria Select 1"));
    }

    private static LegacyReportObservation Report(params (string Name, long Rows)[] sheets) =>
        new(sheets.Select(sheet =>
            new KeyValuePair<string, long>(sheet.Name, sheet.Rows)));

    private static LegacyAuditParityObservation ProviderObservation(
        LegacyAuditParityProvider provider,
        LegacyPrescreenRuleId? missingRule = null,
        LegacyObservedCount? filterVoucherCount = null)
    {
        var rules = LegacyPrescreenRuleCatalog.All.ToDictionary(
            rule => rule,
            rule => rule == missingRule
                ? new LegacyRowVoucherCounts(
                    LegacyObservedCount.NotExecuted,
                    LegacyObservedCount.NotExecuted)
                : new LegacyRowVoucherCounts(3, 2));
        var reports = Enum.GetValues<LegacyReportKind>().ToDictionary(
            kind => kind,
            kind => Report((
                LegacyAuditParityReportMetrics.BaseSheetName(
                    RepresentativeReportFamily(kind)),
                3)));
        var metrics = new LegacyAuditParityMetrics(
            new LegacyCompletenessObservation(
                0,
                20,
                20,
                987654321.125m,
                987654321.125m,
                partARowCountMatch: true,
                partAAmountMatch: true),
            unbalancedVoucherCount: 0,
            new LegacyInfObservation(
                1,
                [LegacyInfMemberKey.Create("sensitive-member", "2")]),
            rules,
            new Dictionary<LegacyFilterScenarioId, LegacyRowVoucherCounts>
            {
                [LegacyFilterScenarioId.FromOrdinal(1)] = new(
                    LegacyObservedCount.Executed(3),
                    filterVoucherCount ?? LegacyObservedCount.Executed(2)),
            },
            reports,
            new LegacyRowVoucherCounts(3, 2));
        return LegacyAuditParityObservation.FromProvider(LegacyParityCase.CaseA, provider, metrics);
    }

    private static LegacyAuditParityReportFamily RepresentativeReportFamily(
        LegacyReportKind report) => report switch
    {
        LegacyReportKind.ValidationReport =>
            LegacyAuditParityReportFamily.ValidationCompletenessReconciliation,
        LegacyReportKind.AccountMapping =>
            LegacyAuditParityReportFamily.AccountMappingMain,
        LegacyReportKind.InfReport =>
            LegacyAuditParityReportFamily.InfReliability,
        LegacyReportKind.PrescreenReport =>
            LegacyAuditParityReportFamily.PrescreenPostPeriodApprovalDetail,
        LegacyReportKind.CriteriaSelectionReport =>
            LegacyAuditParityReportFamily.CriteriaScenario001,
        LegacyReportKind.WorkingPaper =>
            LegacyAuditParityReportFamily.WorkingPaperCompleteness,
        _ => throw new ArgumentOutOfRangeException(nameof(report)),
    };
}
