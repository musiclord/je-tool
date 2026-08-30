using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyAuditParityLegacyObservationFactoryTests
{
    [Fact]
    public void PrescreenSummary_ReadsDirectCountsAndRequiresFixedAggregateNotApplicableCells()
    {
        var path = WriteSummary(halfApplicableAggregate: false);
        try
        {
            var result = LegacyAuditParityLegacyObservationFactory.ReadPrescreen(
                LegacyWorkbookSnapshot.Load(path));

            Assert.All(
                LegacyPrescreenParityPlan.DirectRules,
                rule => Assert.Equal(
                    LegacyMetricApplicability.Applicable,
                    result.Rules[rule].Applicability));
            Assert.Equal(
                LegacyMetricApplicability.NotApplicable,
                result.CreatorSummaryApplicability);
            Assert.Equal(
                LegacyMetricApplicability.NotApplicable,
                result.RareAccountsApplicability);
            var blankDescription = result.Rules[LegacyPrescreenRuleId.BlankDescription];
            Assert.Equal(3, blankDescription.RowCount.RequireValue("synthetic-blank-row"));
            Assert.Equal(2, blankDescription.VoucherCount.RequireValue("synthetic-blank-voucher"));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public void PrescreenSummary_HalfNotApplicableAggregateFailsClosed()
    {
        var path = WriteSummary(halfApplicableAggregate: true);
        try
        {
            var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
                LegacyAuditParityLegacyObservationFactory.ReadPrescreen(
                    LegacyWorkbookSnapshot.Load(path)));

            Assert.Equal("legacy-prescreen-creator-summary", error.FieldId);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public void PrescreenSummary_BlankDirectPairRemainsTypedExecutedUnavailable()
    {
        var path = WriteSummary(
            halfApplicableAggregate: false,
            blankDescriptionPair: true);
        try
        {
            var result = LegacyAuditParityLegacyObservationFactory.ReadPrescreen(
                LegacyWorkbookSnapshot.Load(path));

            var counts = result.Rules[LegacyPrescreenRuleId.BlankDescription];
            Assert.Equal(LegacyMetricApplicability.Applicable, counts.Applicability);
            Assert.Equal(LegacyObservedCountState.ExecutedUnavailable, counts.RowCount.State);
            Assert.Equal(LegacyObservedCountState.ExecutedUnavailable, counts.VoucherCount.State);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public void PrescreenSummary_HalfBlankDirectPairFailsClosed()
    {
        var path = WriteSummary(
            halfApplicableAggregate: false,
            halfBlankDescriptionPair: true);
        try
        {
            var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
                LegacyAuditParityLegacyObservationFactory.ReadPrescreen(
                    LegacyWorkbookSnapshot.Load(path)));

            Assert.Equal("legacy-prescreen-BlankDescription", error.FieldId);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public void PrescreenSummary_OverflowVoucherCountRemainsExecutedButUnavailable()
    {
        var path = WriteSummary(
            halfApplicableAggregate: false,
            overflowFirstVoucher: true);
        try
        {
            var result = LegacyAuditParityLegacyObservationFactory.ReadPrescreen(
                LegacyWorkbookSnapshot.Load(path));

            var counts = result.Rules[LegacyPrescreenRuleId.PostPeriodApproval];
            Assert.Equal(LegacyObservedCountState.ExecutedZero, counts.RowCount.State);
            Assert.Equal(LegacyObservedCountState.ExecutedUnavailable, counts.VoucherCount.State);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public void UnbalancedVoucherOracle_IgnoresValidationRawDetailCountAndDeduplicatesStep11()
    {
        var validationPath = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Name = "ValidationReport";
            sheet.Cell(25, 4).Value = 7;
        });
        var workingPaperPath = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Name = WorkpaperSheetCatalog.Step11;
            for (var column = 2; column <= 6; column++)
            {
                sheet.Cell(16, column).Value = $"synthetic-header-{column}";
            }
            WriteStep11Row(sheet, 17, " synthetic-voucher ", "synthetic-date-1");
            WriteStep11Row(sheet, 18, "synthetic-voucher", "synthetic-date-2");
        });
        try
        {
            var validation = LegacyWorkbookSnapshot.Load(validationPath);
            var summary = validation.FindSingleSheet(
                sheet => sheet.Name.Equals("ValidationReport", StringComparison.Ordinal),
                "synthetic-validation-summary");

            var result = LegacyAuditParityLegacyObservationFactory
                .ReadUnbalancedVoucherObservation(summary, workingPaperPath);

            Assert.Equal(2, Assert.Single(result.DataRows).Value);
            Assert.Equal(1, result.DistinctUnbalancedVoucherCount);
        }
        finally
        {
            TestWorkbookBuilder.Delete(validationPath);
            TestWorkbookBuilder.Delete(workingPaperPath);
        }
    }

    [Fact]
    public void InfMembership_DataAfterLegacyFixedAreaFailsClosed()
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Name = "INF Testing 可靠性測試";
            sheet.Cell(53, 6).Value = 1;
            sheet.Cell(113, 2).Value = "synthetic-outside-boundary";
        });
        try
        {
            var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
                LegacyAuditParityLegacyObservationFactory.ReadInf(
                    LegacyWorkbookSnapshot.Load(path)));

            Assert.Equal("legacy-inf-outside-writer-area", error.FieldId);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public void InfMembership_AllowsTheSixtiethSampleWrittenByIdeaScript()
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Name = "INF Testing 可靠性測試";
            for (var row = 53; row <= 112; row++)
            {
                sheet.Cell(row, 2).Value = $"synthetic-document-{row}";
                sheet.Cell(row, 6).Value = row;
            }
        });
        try
        {
            var observation = LegacyAuditParityLegacyObservationFactory.ReadInf(
                LegacyWorkbookSnapshot.Load(path));

            Assert.Equal(60, observation.ReportedSampleSize);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public void InfMembership_NormalizesApostrophePrefixedLegacyDates()
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Name = "INF Testing 可靠性測試";
            sheet.Cell(53, 2).Value = "synthetic-document";
            sheet.Cell(53, 6).Value = 1;
            sheet.Cell(53, 7).Value = " '2025/3/5 ";
            sheet.Cell(53, 8).Value = "'2025-03-06";
        });
        try
        {
            var observation = LegacyAuditParityLegacyObservationFactory.ReadInf(
                LegacyWorkbookSnapshot.Load(path));

            Assert.Equal(1, observation.ReportedSampleSize);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public void FilterScenario_OverflowVoucherCountRemainsExecutedButUnavailable()
    {
        var scenarios = LegacyAuditParityLegacyObservationFactory.ReadFilterScenarios(
        [
            new LegacyScenarioCount(Position: 1, VoucherCount: null, RowCount: 7),
        ]);

        var counts = Assert.Single(scenarios).Value;
        Assert.Equal(LegacyMetricApplicability.Applicable, counts.Applicability);
        Assert.Equal(LegacyObservedCountState.ExecutedNonZero, counts.RowCount.State);
        Assert.Equal(LegacyObservedCountState.ExecutedUnavailable, counts.VoucherCount.State);
    }

    private static string WriteSummary(
        bool halfApplicableAggregate,
        bool overflowFirstVoucher = false,
        bool blankDescriptionPair = false,
        bool halfBlankDescriptionPair = false) =>
        TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Name = "Pre-screening_Report";
            for (var row = 8; row <= 11; row++)
            {
                sheet.Cell(row, 5).Value = row - 8;
                sheet.Cell(row, 6).Value = row - 8;
            }
            if (!blankDescriptionPair)
            {
                sheet.Cell(17, 5).Value = 2;
            }
            if (!blankDescriptionPair && !halfBlankDescriptionPair)
            {
                sheet.Cell(17, 6).Value = 3;
            }
            if (overflowFirstVoucher)
            {
                sheet.Cell(8, 5).Value = "Over 2,500";
            }
            sheet.Cell(12, 5).Value = "N/A";
            sheet.Cell(12, 6).Value = halfApplicableAggregate ? "0" : "N/A";
            sheet.Cell(13, 5).Value = "N/A";
            sheet.Cell(13, 6).Value = "N/A";
        });

    private static void WriteStep11Row(
        ClosedXML.Excel.IXLWorksheet sheet,
        int row,
        string voucher,
        string date)
    {
        sheet.Cell(row, 2).Value = voucher;
        sheet.Cell(row, 3).Value = date;
        sheet.Cell(row, 4).Value = 10;
        sheet.Cell(row, 5).Value = 9;
    }
}
