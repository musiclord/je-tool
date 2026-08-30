using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyAuditParityObservationFactoryTests
{
    [Fact]
    public void FilterTagMatrix_RebindsCompactRuntimePositionsToLegacyScenarioPositions()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "scenarios": [
                { "position": 1, "rowHitCount": 3, "voucherHitCount": 2 },
                { "position": 2, "rowHitCount": 0, "voucherHitCount": 0 }
              ]
            }
            """);
        var legacyIds = new[]
        {
            LegacyFilterScenarioId.FromOrdinal(1),
            LegacyFilterScenarioId.FromOrdinal(4),
        };

        var observations = LegacyAuditParityObservationFactory.ReadFilterScenarios(
            document.RootElement,
            legacyIds);

        Assert.Equal(legacyIds, observations.Keys.OrderBy(id => id.Ordinal));
        Assert.Equal(
            LegacyObservedCountState.ExecutedZero,
            observations[LegacyFilterScenarioId.FromOrdinal(4)].RowCount.State);
        Assert.DoesNotContain(LegacyFilterScenarioId.FromOrdinal(2), observations.Keys);
    }

    [Fact]
    public void FilterTagMatrix_WithIncompleteLegacyIdentityMapFailsClosed()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "scenarios": [
                { "position": 1, "rowHitCount": 0, "voucherHitCount": 0 },
                { "position": 2, "rowHitCount": 0, "voucherHitCount": 0 }
              ]
            }
            """);

        var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
            LegacyAuditParityObservationFactory.ReadFilterScenarios(
                document.RootElement,
                [LegacyFilterScenarioId.FromOrdinal(1)]));

        Assert.Equal("filter.tag-matrix.scenario-identities", error.FieldId);
    }

    [Fact]
    public void PrescreenRulePeriod_DistinguishesExecutedZeroFromNotApplicable()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rulePeriod": {
                "rules": [
                  {
                    "key": "postPeriodApproval",
                    "naReason": null,
                    "hitLines": 0,
                    "hitVouchers": 0
                  },
                  {
                    "key": "nonAuthorizedPreparer",
                    "naReason": "synthetic prerequisite",
                    "hitLines": null,
                    "hitVouchers": null
                  }
                ]
              }
            }
            """);

        var rules = LegacyAuditParityObservationFactory.ReadPrescreen(document.RootElement);

        Assert.Equal(
            LegacyMetricApplicability.Applicable,
            rules[LegacyPrescreenRuleId.PostPeriodApproval].Applicability);
        Assert.Equal(
            LegacyObservedCountState.ExecutedZero,
            rules[LegacyPrescreenRuleId.PostPeriodApproval].RowCount.State);
        Assert.Equal(
            LegacyMetricApplicability.NotApplicable,
            rules[LegacyPrescreenRuleId.NonAuthorizedPreparer].Applicability);
        Assert.Equal(
            LegacyObservedCountState.NotExecuted,
            rules[LegacyPrescreenRuleId.NonAuthorizedPreparer].RowCount.State);
    }

    [Fact]
    public void PrescreenRulePeriod_NotApplicableWithNumericCountsFailsClosed()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rulePeriod": {
                "rules": [
                  {
                    "key": "nonAuthorizedPreparer",
                    "naReason": "synthetic prerequisite",
                    "hitLines": 0,
                    "hitVouchers": 0
                  }
                ]
              }
            }
            """);

        var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
            LegacyAuditParityObservationFactory.ReadPrescreen(document.RootElement));

        Assert.Equal("prescreen.rule-period.na-counts", error.FieldId);
    }

    [Fact]
    public void PrescreenRulePeriod_NullCountsWithoutNotApplicableReasonFailClosed()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rulePeriod": {
                "rules": [
                  {
                    "key": "postPeriodApproval",
                    "naReason": null,
                    "hitLines": null,
                    "hitVouchers": null
                  }
                ]
              }
            }
            """);

        var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
            LegacyAuditParityObservationFactory.ReadPrescreen(document.RootElement));

        Assert.Equal("prescreen.rule-period.hit-lines", error.FieldId);
    }

    [Fact]
    public void AccountMappingReport_CountsWriterDataRowsInsteadOfPopulatedTemplateRows()
    {
        var path = WriteWorkbook(workbook =>
        {
            var mapping = workbook.AddWorksheet("AccountMapping");
            mapping.Cell("A1").Value = "static-title";
            mapping.Cell("A2").Value = "static-instruction";
            mapping.Cell("A3").Value = "header";
            mapping.Cell("A4").Value = "synthetic-account-1";
            mapping.Cell("B4").Value = "synthetic-name-1";
            mapping.Cell("A5").Value = "synthetic-account-2";
            mapping.Cell("B5").Value = "synthetic-name-2";
            StyleOnly(mapping.Cell("A6"));

            var list = workbook.AddWorksheet("List");
            list.Cell("A1").Value = "header";
            for (var row = 2; row <= 6; row++)
            {
                list.Cell(row, 1).Value = $"synthetic-category-{row}";
            }
            StyleOnly(list.Cell("A7"));
        });

        try
        {
            var counts = ReadCounts(LegacyReportKind.AccountMapping, path);

            Assert.Equal(2, counts["AccountMapping"]);
            Assert.Equal(5, counts["List"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData((int)LegacyReportKind.ValidationReport, "V_Report 1", 2, 1)]
    [InlineData((int)LegacyReportKind.InfReport, "可靠性樣本_所有欄位", 2, 1)]
    [InlineData((int)LegacyReportKind.PrescreenReport, "R1", 2, 1)]
    [InlineData((int)LegacyReportKind.CriteriaSelectionReport, "#Criteria Select 1", 2, 1)]
    [InlineData((int)LegacyReportKind.WorkingPaper, WorkpaperSheetCatalog.Step1, 20, 2)]
    [InlineData((int)LegacyReportKind.WorkingPaper, WorkpaperSheetCatalog.Step4 + " (續2)", 13, 1)]
    public void DetailSheetFamilies_CountOnlyContiguousRowsFromTheirWriterBoundary(
        int reportValue,
        string sheetName,
        int firstDataRow,
        int identityColumn)
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet(sheetName);
            for (var row = 1; row < firstDataRow; row++)
            {
                sheet.Cell(row, identityColumn).Value = $"static-{row}";
            }
            sheet.Cell(firstDataRow, identityColumn).Value = "synthetic-data-1";
            sheet.Cell(firstDataRow + 1, identityColumn).Value = "synthetic-data-2";
            StyleOnly(sheet.Cell(firstDataRow + 2, identityColumn));
        });

        try
        {
            var report = (LegacyReportKind)reportValue;
            var counts = ReadCounts(report, path);

            Assert.Equal(2, counts[sheetName]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Source_Quality")]
    [InlineData("Source_Quality (2)")]
    public void SourceQualitySheetFamily_CountsOnlyCompleteContiguousFindingRows(string sheetName)
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet(sheetName);
            SetThreeColumnRow(sheet, 1, "category-header", "row-header", "label-header");
            SetThreeColumnRow(sheet, 2, "nullPostDate", "1", "synthetic-source-1");
            SetThreeColumnRow(sheet, 3, "nullPostDate", "2", "synthetic-source-2");
            StyleOnly(sheet.Cell("A4"));
        });

        try
        {
            var counts = ReadCounts(LegacyReportKind.ValidationReport, path);

            Assert.Equal(2, counts[sheetName]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData((int)LegacyReportKind.ValidationReport, "ValidationReport")]
    [InlineData((int)LegacyReportKind.PrescreenReport, "Pre-screening_Report")]
    [InlineData((int)LegacyReportKind.CriteriaSelectionReport, "Summary Inforamtion")]
    [InlineData((int)LegacyReportKind.WorkingPaper, WorkpaperSheetCatalog.Cover)]
    public void FixedSummarySheets_ReportZeroDataRowsEvenWhenTemplateRowsArePopulated(
        int reportValue,
        string sheetName)
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet(sheetName);
            for (var row = 1; row <= 12; row++)
            {
                sheet.Cell(row, 1).Value = $"static-{row}";
            }
        });

        try
        {
            var counts = ReadCounts((LegacyReportKind)reportValue, path);

            Assert.Equal(0, counts[sheetName]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData((int)LegacyReportKind.InfReport, "INF Testing 可靠性測試", 2)]
    [InlineData((int)LegacyReportKind.WorkingPaper, WorkpaperSheetCatalog.Step2, 1)]
    public void FixedSampleAreas_IgnoreFormattedEmptyRowsAfterExecutedSamples(
        int reportValue,
        string sheetName,
        int identityColumn)
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet(sheetName);
            for (var row = 1; row <= 52; row++)
            {
                sheet.Cell(row, 1).Value = $"static-{row}";
            }
            sheet.Cell(53, identityColumn).Value = "synthetic-sample-1";
            sheet.Cell(54, identityColumn).Value = "synthetic-sample-2";
            StyleOnly(sheet.Cell(55, identityColumn));
            StyleOnly(sheet.Cell(111, identityColumn));
        });

        try
        {
            var counts = ReadCounts((LegacyReportKind)reportValue, path);

            Assert.Equal(2, counts[sheetName]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData((int)LegacyReportKind.ValidationReport)]
    [InlineData((int)LegacyReportKind.WorkingPaper)]
    public void FieldInfoBoundary_CountsBothDataSectionsAndExcludesTitlesHeadersAndBlankRows(
        int reportValue)
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet(MappingMetadataFormat.WorksheetName);
            sheet.Cell("A1").Value = "version";
            sheet.Cell("A2").Value = "tb-title";
            SetTwoColumnRow(sheet, 3, "source-header", "type-header");
            SetTwoColumnRow(sheet, 4, "tb-source-1", "text");
            SetTwoColumnRow(sheet, 5, "tb-source-2", "number");
            MakePhysicalBlankRow(sheet, 6);
            MakePhysicalBlankRow(sheet, 7);
            sheet.Cell("A8").Value = "gl-title";
            SetTwoColumnRow(sheet, 9, "source-header", "type-header");
            SetTwoColumnRow(sheet, 10, "gl-source-1", "date");
            StyleOnly(sheet.Cell("A11"));
        });

        try
        {
            var counts = ReadCounts((LegacyReportKind)reportValue, path);

            Assert.Equal(3, counts[MappingMetadataFormat.WorksheetName]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WorkingPaperCalendarBoundary_CountsHolidayAndMakeupSectionsOnly()
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet(WorkpaperSheetCatalog.CalendarInfo);
            for (var row = 1; row <= 8; row++)
            {
                SetTwoColumnRow(sheet, row, $"static-day-{row}", "static-flag");
            }
            SetThreeColumnRow(sheet, 10, "holiday-header", "name-header", "flag-header");
            SetThreeColumnRow(sheet, 11, "synthetic-date-1", "holiday-1", "Y");
            SetThreeColumnRow(sheet, 12, "synthetic-date-2", "holiday-2", "Y");
            SetTwoColumnRow(sheet, 14, "makeup-header", "name-header");
            SetTwoColumnRow(sheet, 15, "synthetic-date-3", "makeup-1");
            StyleOnly(sheet.Cell("A16"));
        });

        try
        {
            var counts = ReadCounts(LegacyReportKind.WorkingPaper, path);

            Assert.Equal(3, counts[WorkpaperSheetCatalog.CalendarInfo]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WorkingPaperStep3Boundary_CountsOnlyRowsWithBothDynamicColumns()
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet(WorkpaperSheetCatalog.Step3);
            for (var row = 19; row <= 28; row++)
            {
                sheet.Cell(row, 2).Value = $"static-position-{row}";
            }
            sheet.Cell("C19").Value = "synthetic-condition-1";
            sheet.Cell("E19").Value = "synthetic-result-1";
            sheet.Cell("C20").Value = "synthetic-condition-2";
            sheet.Cell("E20").Value = "synthetic-result-2";
        });

        try
        {
            var counts = ReadCounts(LegacyReportKind.WorkingPaper, path);

            Assert.Equal(2, counts[WorkpaperSheetCatalog.Step3]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WorkingPaperStep3Boundary_PartialDynamicRowFailsClosed()
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet(WorkpaperSheetCatalog.Step3);
            sheet.Cell("B19").Value = "static-position";
            sheet.Cell("C19").Value = "synthetic-condition-without-result";
        });

        try
        {
            var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
                LegacyAuditParityObservationFactory.ReadReportDataRows(
                    LegacyReportKind.WorkingPaper,
                    path));

            Assert.Equal("report.data-row-shape", error.FieldId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnknownSheetBoundary_FailsClosedWithoutRenderingSheetNameOrPath()
    {
        var path = WriteWorkbook(workbook =>
            workbook.AddWorksheet("synthetic-unknown-sheet").Cell("A1").Value = "synthetic");

        try
        {
            var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
                LegacyAuditParityObservationFactory.ReadReportDataRows(
                    LegacyReportKind.ValidationReport,
                    path));

            Assert.Equal("report.data-row-boundary", error.FieldId);
            Assert.DoesNotContain("synthetic-unknown-sheet", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(path, error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void JetGeneratedMetadataSheet_IsExcludedOnlyWhenVeryHidden()
    {
        var path = WriteWorkbook(workbook =>
        {
            workbook.AddWorksheet(WorkpaperSheetCatalog.Cover).Cell("A1").Value = "static-cover";
            var metadata = workbook.AddWorksheet(ReportWorkbookMetadataFormat.WorksheetName);
            metadata.Cell("A1").Value = ReportWorkbookMetadataFormat.Marker;
            metadata.Visibility = XLWorksheetVisibility.VeryHidden;
        });

        try
        {
            var counts = ReadCounts(LegacyReportKind.WorkingPaper, path);

            Assert.Equal(0, Assert.Single(counts).Value);
            Assert.DoesNotContain(ReportWorkbookMetadataFormat.WorksheetName, counts.Keys);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void JetGeneratedMetadataSheet_WhenVisibleFailsClosed()
    {
        var path = WriteWorkbook(workbook =>
        {
            workbook.AddWorksheet(WorkpaperSheetCatalog.Cover).Cell("A1").Value = "static-cover";
            workbook.AddWorksheet(ReportWorkbookMetadataFormat.WorksheetName)
                .Cell("A1").Value = ReportWorkbookMetadataFormat.Marker;
        });

        try
        {
            var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
                LegacyAuditParityObservationFactory.ReadReportDataRows(
                    LegacyReportKind.WorkingPaper,
                    path));

            Assert.Equal("report.metadata-sheet-state", error.FieldId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LegacySystemMetadataSheet_EvenWhenVeryHiddenFailsClosed()
    {
        var path = WriteWorkbook(workbook =>
        {
            workbook.AddWorksheet(WorkpaperSheetCatalog.Cover).Cell("A1").Value = "static-cover";
            var metadata = workbook.AddWorksheet(ReportWorkbookMetadataFormat.WorksheetName);
            metadata.Cell("A1").Value = ReportWorkbookMetadataFormat.Marker;
            metadata.Visibility = XLWorksheetVisibility.VeryHidden;
        });

        try
        {
            var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
                LegacyAuditParityObservationFactory.ReadReportDataRows(
                    LegacyReportKind.WorkingPaper,
                    path,
                    LegacyAuditParityReportSource.LegacySystem));

            Assert.Equal("report.data-row-boundary", error.FieldId);
            Assert.DoesNotContain(ReportWorkbookMetadataFormat.WorksheetName, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(path, error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void JetGeneratedValidationV7_FailsClosed()
    {
        var path = WriteWorkbook(workbook =>
            workbook.AddWorksheet("V_Report 7").Cell("A2").Value = "synthetic-retired-detail");

        try
        {
            var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
                LegacyAuditParityObservationFactory.ReadReportDataRows(
                    LegacyReportKind.ValidationReport,
                    path));

            Assert.Equal("report.data-row-boundary", error.FieldId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GapInsideDataArea_FailsClosedInsteadOfSilentlyDroppingLaterRows()
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet("AccountMapping");
            sheet.Cell("A1").Value = "static";
            sheet.Cell("A4").Value = "synthetic-data-1";
            sheet.Cell("B4").Value = "synthetic-name-1";
            sheet.Cell("A6").Value = "synthetic-data-2";
            sheet.Cell("B6").Value = "synthetic-name-2";
        });

        try
        {
            var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
                LegacyAuditParityObservationFactory.ReadReportDataRows(
                    LegacyReportKind.AccountMapping,
                    path));

            Assert.Equal("report.data-row-contiguity", error.FieldId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SampleOutsideFixedWriterArea_FailsClosedInsteadOfExpandingTheBoundary()
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet("INF Testing 可靠性測試");
            sheet.Cell("B53").Value = "synthetic-sample-1";
            sheet.Cell("B113").Value = "synthetic-outside-boundary";
        });

        try
        {
            var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
                LegacyAuditParityObservationFactory.ReadReportDataRows(
                    LegacyReportKind.InfReport,
                    path));

            Assert.Equal("report.data-row-contiguity", error.FieldId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InfReportBoundary_CountsTheSixtiethContiguousLegacySample()
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet("INF Testing 可靠性測試");
            for (var row = 53; row <= 112; row++)
            {
                sheet.Cell(row, 2).Value = $"synthetic-sample-{row}";
            }
        });

        try
        {
            var counts = ReadCounts(LegacyReportKind.InfReport, path);

            Assert.Equal(60, counts["INF Testing 可靠性測試"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AccountMappingReport_PartialMainRowFailsClosed()
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet("AccountMapping");
            sheet.Cell("A4").Value = "synthetic-account-without-name";
        });

        try
        {
            var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
                LegacyAuditParityObservationFactory.ReadReportDataRows(
                    LegacyReportKind.AccountMapping,
                    path));

            Assert.Equal("report.data-row-shape", error.FieldId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AccountMappingList_GapBeforeLaterEntryFailsClosed()
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet("List");
            for (var row = 2; row <= 6; row++)
            {
                sheet.Cell(row, 1).Value = $"synthetic-category-{row}";
            }
            sheet.Cell(8, 1).Value = "synthetic-category-after-gap";
        });

        try
        {
            var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
                LegacyAuditParityObservationFactory.ReadReportDataRows(
                    LegacyReportKind.AccountMapping,
                    path));

            Assert.Equal("report.data-row-contiguity", error.FieldId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AccountMappingList_CountsContiguousTemplateEntriesBeyondFive()
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet("List");
            for (var row = 2; row <= 7; row++)
            {
                sheet.Cell(row, 1).Value = $"synthetic-category-{row}";
            }
        });

        try
        {
            var counts = ReadCounts(LegacyReportKind.AccountMapping, path);

            Assert.Equal(6, counts["List"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LegacyFieldInfoBoundary_UsesOnePhysicalBlankBetweenSections()
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet(MappingMetadataFormat.WorksheetName);
            sheet.Cell("A1").Value = "version";
            sheet.Cell("A2").Value = "tb-title";
            SetTwoColumnRow(sheet, 3, "source-header", "type-header");
            SetTwoColumnRow(sheet, 4, "tb-source", "text");
            MakePhysicalBlankRow(sheet, 5);
            sheet.Cell("A6").Value = "gl-title";
            SetTwoColumnRow(sheet, 7, "source-header", "type-header");
            SetTwoColumnRow(sheet, 8, "gl-source", "date");
        });

        try
        {
            var counts = ReadCounts(
                LegacyReportKind.ValidationReport,
                path,
                LegacyAuditParityReportSource.LegacySystem);

            Assert.Equal(2, counts[MappingMetadataFormat.WorksheetName]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FieldInfoDataRows_AllowBlankTypeMetadataAndOmittedPhysicalSeparatorRows()
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet(MappingMetadataFormat.WorksheetName);
            sheet.Cell("A3").Value = "synthetic-original-field";
            sheet.Cell("B3").Value = "synthetic-type";
            sheet.Cell("A4").Value = "synthetic-tb-field";
            sheet.Cell("A7").Value = "synthetic-gl-title";
            sheet.Cell("A8").Value = "synthetic-original-field";
            sheet.Cell("B8").Value = "synthetic-type";
            sheet.Cell("A9").Value = "synthetic-gl-field";
        });

        try
        {
            var counts = ReadCounts(LegacyReportKind.WorkingPaper, path);

            Assert.Equal(2, counts[MappingMetadataFormat.WorksheetName]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LegacyWorkingPaperStep2_TreatsTemplateSampleAreaAsZeroDataRows()
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet(WorkpaperSheetCatalog.Step2);
            sheet.Cell("A53").Value = "synthetic-template-value";
            sheet.Cell("A111").Value = "synthetic-template-value";
        });

        try
        {
            var counts = ReadCounts(
                LegacyReportKind.WorkingPaper,
                path,
                LegacyAuditParityReportSource.LegacySystem);

            Assert.Equal(0, counts[WorkpaperSheetCatalog.Step2]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LegacyCalendarHoliday_AllowsBlankNameAndUsesDatePlusFlagIdentity()
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet(WorkpaperSheetCatalog.CalendarInfo);
            SetThreeColumnRow(sheet, 10, "date-header", "name-header", "flag-header");
            sheet.Cell("A11").Value = "synthetic-date";
            sheet.Cell("C11").Value = "Y";
        });

        try
        {
            var counts = ReadCounts(
                LegacyReportKind.WorkingPaper,
                path,
                LegacyAuditParityReportSource.LegacySystem);

            Assert.Equal(1, counts[WorkpaperSheetCatalog.CalendarInfo]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LegacyStep11_CountsCompositeRowsButDeduplicatesNormalizedVoucherKeys()
    {
        var path = WriteWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet(WorkpaperSheetCatalog.Step11);
            for (var column = 2; column <= 6; column++)
            {
                sheet.Cell(16, column).Value = $"synthetic-header-{column}";
            }
            SetStep11Row(sheet, 17, " synthetic-voucher ", "synthetic-date-1");
            SetStep11Row(sheet, 18, "synthetic-voucher", "synthetic-date-2");
        });

        try
        {
            var result = LegacyAuditParityObservationFactory.ReadLegacyWorkingPaper(path);

            Assert.Equal(2, Assert.Single(result.DataRows).Value);
            Assert.Equal(1, result.DistinctUnbalancedVoucherCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DuplicatePhysicalRowIndex_FailsClosed()
    {
        var path = WriteStreamingWorkbook("AccountMapping", writer =>
        {
            WriteStreamingRow(
                writer,
                4,
                InlineCell("A4", "synthetic-account-1"),
                InlineCell("B4", "synthetic-name-1"));
            WriteStreamingRow(
                writer,
                4,
                InlineCell("A4", "synthetic-account-2"),
                InlineCell("B4", "synthetic-name-2"));
        });

        try
        {
            var error = Assert.Throws<LegacyAuditParityObservationException>(() =>
                LegacyAuditParityObservationFactory.ReadReportDataRows(
                    LegacyReportKind.AccountMapping,
                    path));

            Assert.Equal("report.row-index-duplicate", error.FieldId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ComplementaryPhysicalRowFragments_MergeIntoOneLogicalRow()
    {
        var path = WriteStreamingWorkbook("AccountMapping", writer =>
        {
            WriteStreamingRow(
                writer,
                4,
                InlineCell("A4", "synthetic-account"));
            WriteStreamingRow(
                writer,
                4,
                InlineCell("B4", "synthetic-name"));
        });

        try
        {
            var counts = ReadCounts(LegacyReportKind.AccountMapping, path);

            Assert.Equal(1, counts["AccountMapping"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LargeSparseContinuationSheet_StreamsPhysicalRowsWithoutChangingBoundary()
    {
        const uint firstFormattingRow = 1_000;
        const uint formattingRowCount = 50_000;
        var sheetName = WorkpaperSheetCatalog.Step4 + " (續2)";
        var path = WriteStreamingWorkbook(sheetName, writer =>
        {
            WriteStreamingRow(writer, 13, InlineCell("A13", "synthetic-data-1"));
            WriteStreamingRow(writer, 14, InlineCell("A14", "synthetic-data-2"));
            for (var offset = 0U; offset < formattingRowCount; offset++)
            {
                writer.WriteElement(new Row
                {
                    RowIndex = firstFormattingRow + offset,
                    CustomHeight = true,
                    Height = 15D,
                });
            }
        });

        try
        {
            var counts = ReadCounts(LegacyReportKind.WorkingPaper, path);

            Assert.Equal(2, counts[sheetName]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IReadOnlyDictionary<string, long> ReadCounts(
        LegacyReportKind report,
        string path,
        LegacyAuditParityReportSource source = LegacyAuditParityReportSource.JetGenerated) =>
        LegacyAuditParityObservationFactory
        .ReadReportDataRows(report, path, source)
        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    private static string WriteWorkbook(Action<XLWorkbook> build)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"jet-parity-report-boundary-{Guid.NewGuid():N}.xlsx");
        using var workbook = new XLWorkbook();
        build(workbook);
        workbook.SaveAs(path);
        return path;
    }

    private static string WriteStreamingWorkbook(
        string sheetName,
        Action<OpenXmlWriter> writeRows)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"jet-parity-report-stream-{Guid.NewGuid():N}.xlsx");
        using var document = SpreadsheetDocument.Create(
            path,
            SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        using (var writer = OpenXmlWriter.Create(worksheetPart))
        {
            writer.WriteStartElement(new Worksheet());
            writer.WriteStartElement(new SheetData());
            writeRows(writer);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = sheetName,
        });
        workbookPart.Workbook.Save();
        return path;
    }

    private static void WriteStreamingRow(
        OpenXmlWriter writer,
        uint rowIndex,
        params Cell[] cells)
    {
        var row = new Row { RowIndex = rowIndex };
        row.Append(cells);
        writer.WriteElement(row);
    }

    private static Cell InlineCell(string reference, string value) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value)),
    };

    private static void SetStep11Row(
        IXLWorksheet sheet,
        int row,
        string voucher,
        string date)
    {
        sheet.Cell(row, 2).Value = voucher;
        sheet.Cell(row, 3).Value = date;
        sheet.Cell(row, 4).Value = 10;
        sheet.Cell(row, 5).Value = 9;
    }

    private static void SetTwoColumnRow(
        IXLWorksheet sheet,
        int row,
        string first,
        string second)
    {
        sheet.Cell(row, 1).Value = first;
        sheet.Cell(row, 2).Value = second;
    }

    private static void SetThreeColumnRow(
        IXLWorksheet sheet,
        int row,
        string first,
        string second,
        string third)
    {
        SetTwoColumnRow(sheet, row, first, second);
        sheet.Cell(row, 3).Value = third;
    }

    private static void MakePhysicalBlankRow(IXLWorksheet sheet, int row)
    {
        sheet.Row(row).Height = 19;
    }

    private static void StyleOnly(IXLCell cell)
    {
        cell.Style.Fill.BackgroundColor = XLColor.LightGray;
    }
}
