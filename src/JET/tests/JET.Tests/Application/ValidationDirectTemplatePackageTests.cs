using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Validation 固定範本的 production writer 驗收。門檻 count 與 fake page 實際列數刻意
/// 解耦，避免測試真的配置一萬列，並讓錯誤的「先查明細再決定 summary-only」實作必然失敗。
/// </summary>
public sealed class ValidationDirectTemplatePackageTests
{
    private const string FieldInfoSheet = "自動化工具-檔案欄位資訊";
    private const string GuideSheet = "完整性測試出現差異時之指引";

    [Theory]
    [InlineData(10_000, true)]
    [InlineData(10_001, false)]
    [InlineData(1_234_289, false)]
    public async Task NullCategoryBoundaries_EmitAtTenThousandAndNeverReadOversizedDetails(
        long savedCount,
        bool expectedDetailSheets)
    {
        var repositories = new ValidationRepositoryFakes
        {
            ThrowOnValidationDetailRead = !expectedDetailSheets
        };
        var projection = Projection(
            nullAccountCount: savedCount,
            nullDocumentCount: savedCount,
            nullDescriptionCount: savedCount,
            outOfRangeDateCount: savedCount);
        var plan = Plan(projection, unbalancedDetailRowCount: 0);

        var bytes = await WritePlannedAsync(
            repositories,
            projection,
            plan,
            new FieldInfoProjection([], []));

        using var workbook = Workbook(bytes);
        foreach (var sheetName in new[]
                 {
                     "V_Report 1", "V_Report 2", "V_Report 3", "V_Report 4"
                 })
        {
            Assert.Equal(
                expectedDetailSheets,
                workbook.Worksheets.Any(sheet => sheet.Name == sheetName));
        }

        Assert.Equal(expectedDetailSheets ? 4 : 0, repositories.NullPageCalls);
        Assert.Equal(expectedDetailSheets ? 4 : 0, repositories.RawRowCalls);
        Assert.Equal(0, repositories.UnbalancedPageCalls);
        Assert.Contains("V_Report 5", workbook.Worksheets.Select(sheet => sheet.Name));
        AssertOpenXmlValid(bytes);
    }

    [Theory]
    [InlineData(9_999, true)]
    [InlineData(10_000, false)]
    public async Task UnbalancedRawDetailBoundary_IsIndependentFromDistinctVoucherSummary(
        long rawDetailRowCount,
        bool expectedDetailSheet)
    {
        var repositories = new ValidationRepositoryFakes
        {
            ThrowOnValidationDetailRead = !expectedDetailSheet
        };
        var projection = Projection(unbalancedDocumentCount: 2);
        var plan = Plan(projection, rawDetailRowCount);

        var bytes = await WritePlannedAsync(
            repositories,
            projection,
            plan,
            new FieldInfoProjection([], []));

        using var workbook = Workbook(bytes);
        Assert.Equal(
            expectedDetailSheet,
            workbook.Worksheets.Any(sheet => sheet.Name == "V_Report 6"));
        Assert.Equal(2, workbook.Worksheet("ValidationReport").Cell("D25").GetValue<int>());
        Assert.Equal(expectedDetailSheet ? 1 : 0, repositories.UnbalancedPageCalls);
        Assert.Equal(expectedDetailSheet ? 1 : 0, repositories.RawRowCalls);
        Assert.Equal(
            rawDetailRowCount,
            plan.RequireDetail(ValidationReportDetailKind.UnbalancedGlEntry).Count);
        AssertOpenXmlValid(bytes);
    }

    [Fact]
    public async Task DirectValidationPackage_PreservesFixedOracleAndOrdersDetailSheets()
    {
        var repositories = new ValidationRepositoryFakes();
        var projection = Projection(
            nullAccountCount: 1,
            nullDocumentCount: 1,
            nullDescriptionCount: 1,
            outOfRangeDateCount: 1,
            unbalancedDocumentCount: 1);
        var plan = Plan(projection, unbalancedDetailRowCount: 1);
        var templateBytes = await ValidationTemplateBytesAsync();

        var outputBytes = await WritePlannedAsync(
            repositories,
            projection,
            plan,
            new FieldInfoProjection([], []));

        using (var workbook = Workbook(outputBytes))
        {
            Assert.Equal(
                new[]
                {
                    "ValidationReport",
                    FieldInfoSheet,
                    GuideSheet,
                    "V_Report 1",
                    "V_Report 2",
                    "V_Report 3",
                    "V_Report 4",
                    "V_Report 5",
                    "V_Report 6",
                    "Source_Quality"
                },
                workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
            Assert.Equal("Validation Report", workbook.Worksheet("ValidationReport").Cell("B1").GetString());
            Assert.True(workbook.Worksheet("ValidationReport").Cell("E3").IsEmpty());
            var visibleValues = workbook.Worksheets
                .SelectMany(sheet => sheet.CellsUsed())
                .Select(cell => cell.GetString())
                .ToArray();
            Assert.DoesNotContain(visibleValues, value =>
                value.Contains("Run ID", StringComparison.OrdinalIgnoreCase)
                || value.Contains(
                    "validation-run-id-must-not-be-visible",
                    StringComparison.Ordinal));

            Assert.All(new[] { "V_Report 1", "V_Report 2", "V_Report 6" }, sheetName =>
            {
                var rawDetail = workbook.Worksheet(sheetName);
                Assert.All(new[] { "A1", "A2" }, reference =>
                {
                    var style = rawDetail.Cell(reference).Style;
                    Assert.Equal("微軟正黑體", style.Font.FontName);
                    Assert.Equal(10D, style.Font.FontSize);
                    Assert.False(style.Font.Bold);
                    Assert.Equal(XLFillPatternValues.None, style.Fill.PatternType);
                    Assert.Equal(XLAlignmentVerticalValues.Bottom, style.Alignment.Vertical);
                    Assert.Equal(XLBorderStyleValues.None, style.Border.LeftBorder);
                    Assert.Equal(XLBorderStyleValues.None, style.Border.RightBorder);
                    Assert.Equal(XLBorderStyleValues.None, style.Border.TopBorder);
                    Assert.Equal(XLBorderStyleValues.None, style.Border.BottomBorder);
                });
            });
        }

        using var template = Open(templateBytes);
        using var output = Open(outputBytes);
        var templateSummary = WorksheetPartFor(template, "ValidationReport");
        var outputSummary = WorksheetPartFor(output, "ValidationReport");
        Assert.Equal(
            Assert.Single(templateSummary.Worksheet.Elements<Drawing>()).OuterXml,
            Assert.Single(outputSummary.Worksheet.Elements<Drawing>()).OuterXml);
        AssertPartTreesEqual(
            WorksheetPartFor(template, GuideSheet),
            WorksheetPartFor(output, GuideSheet),
            includeRoot: true);
        AssertPartTreesEqual(
            templateSummary,
            outputSummary,
            includeRoot: false);
        AssertValidationPackageAllowedDiff(templateBytes, outputBytes);
        var outputSheets = output.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().ToArray();
        var activeTab = Assert.Single(
            output.WorkbookPart.Workbook.BookViews!.Elements<WorkbookView>()).ActiveTab!.Value;
        Assert.Equal(GuideSheet, outputSheets[checked((int)activeTab)].Name?.Value);
        AssertOpenXmlValid(outputBytes);
    }

    [Fact]
    public async Task FormalValidation_SourceQualityUsesFullRepositoryBeyondFiftyRowSummarySample()
    {
        var fullFindings = Enumerable.Range(1, 51)
            .Select(index => new SourceQualityFindingRow(
                "nullPostDate",
                index,
                "synthetic-source.xlsx [GL]",
                $"DOC-{index}",
                "1000",
                null,
                "blank posting date",
                index))
            .ToArray();
        var summarySample = fullFindings.Take(50).ToArray();
        var repositories = new ValidationRepositoryFakes
        {
            SourceQualityRows = fullFindings,
            TargetGlDefinitions =
            [
                new LegacyFieldDefinition(
                    1,
                    "DOCUMENT_DATE_JE",
                    "source date",
                    LegacyFieldKind.Date,
                    null,
                    null)
            ]
        };
        var projection = Projection(
            sourceQualityFindingCount: fullFindings.Length,
            sourceQualitySampleRows: summarySample);
        var plan = Plan(projection, unbalancedDetailRowCount: 0);

        var result = await WriteFormalPlannedResultAsync(
            repositories,
            projection,
            plan,
            new FieldInfoProjection([], []),
            new LegacyReportWriterOptions(17));
        var bytes = result.Bytes;

        Assert.Equal(50, projection.SourceQualitySampleRows.Count);
        Assert.Equal(1, repositories.SourceQualityPageCalls);
        using var workbook = Workbook(bytes);
        string[] headers =
        [
            "CATEGORY",
            "SOURCE_ROW_NUMBER",
            "SOURCE_LABEL",
            "DOCUMENT_NUMBER",
            "ACCOUNT_CODE",
            "POST_DATE",
            "DESCRIPTION"
        ];
        var sheetNames = Enumerable.Range(1, 4)
            .Select(part => LegacyReportWriter.SeriesName("Source_Quality", part))
            .ToArray();
        var expectedRowsPerSheet = new[] { 16L, 16L, 16L, 3L };
        Assert.Equal(
            sheetNames,
            workbook.Worksheets
                .Where(sheet => sheet.Name.StartsWith("Source_Quality", StringComparison.Ordinal))
                .Select(sheet => sheet.Name)
                .ToArray());
        var actualRows = new List<string[]>();
        for (var sheetIndex = 0; sheetIndex < sheetNames.Length; sheetIndex++)
        {
            var sourceQuality = workbook.Worksheet(sheetNames[sheetIndex]);
            Assert.Equal(headers, Enumerable.Range(1, headers.Length)
                .Select(column => sourceQuality.Cell(1, column).GetString())
                .ToArray());
            Assert.Equal(
                checked((int)expectedRowsPerSheet[sheetIndex] + 1),
                sourceQuality.LastRowUsed()!.RowNumber());
            for (var row = 2; row <= sourceQuality.LastRowUsed()!.RowNumber(); row++)
            {
                actualRows.Add(Enumerable.Range(1, headers.Length)
                    .Select(column => sourceQuality.Cell(row, column).GetString())
                    .ToArray());
            }
        }

        var expectedRows = fullFindings.Select(finding => new[]
        {
            finding.Category,
            finding.SourceRowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            finding.SourceLabel,
            finding.DocumentNumber ?? string.Empty,
            finding.AccountCode ?? string.Empty,
            finding.PostDate ?? string.Empty,
            finding.Description ?? string.Empty
        }).ToArray();
        Assert.Equal(expectedRows.Length, actualRows.Count);
        for (var index = 0; index < expectedRows.Length; index++)
        {
            Assert.Equal(expectedRows[index], actualRows[index]);
        }
        Assert.Equal(
            actualRows.Count,
            actualRows.Select(row => string.Join('\u001f', row)).Distinct(StringComparer.Ordinal).Count());

        var sourceQualityStats = result.Stats.SheetStats
            .Where(stat => stat.SheetName.StartsWith("Source_Quality", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(sheetNames, sourceQualityStats.Select(stat => stat.SheetName).ToArray());
        Assert.Equal(expectedRowsPerSheet, sourceQualityStats.Select(stat => stat.RowsWritten).ToArray());
        Assert.Equal(bytes.LongLength, result.Stats.BytesWritten);
        AssertOpenXmlValid(bytes);
    }

    [Fact]
    public async Task Stage8TrackO_ValidationRegistry_AllTwentyOneEntriesMatchSyntheticWorkbook()
    {
        var repositories = new ValidationRepositoryFakes
        {
            CompletenessRows =
            [
                new CompletenessDiffAccount(
                    "1101",
                    "Cash",
                    10_000,
                    10_000,
                    0,
                    false)
            ]
        };
        var projection = Projection(
            nullAccountCount: 1,
            nullDocumentCount: 1,
            nullDescriptionCount: 1,
            outOfRangeDateCount: 1,
            unbalancedDocumentCount: 1,
            completenessDiffAccountCount: 1);
        var fieldInfo = JetAuditProgram.ProjectFieldInfo(
        [
            new LegacyFieldDefinition(
                1,
                "TB_TEXT_TB",
                "TB source",
                LegacyFieldKind.Text,
                20,
                null)
        ],
        [
            new LegacyFieldDefinition(
                1,
                "GL_TEXT_JE",
                "GL source",
                LegacyFieldKind.Text,
                20,
                null)
        ]);

        var bytes = await WritePlannedAsync(
            repositories,
            projection,
            Plan(projection, unbalancedDetailRowCount: 1),
            fieldInfo);

        using var document = Open(bytes);
        var mismatches = new List<string>();
        var summary = WorksheetPartFor(document, "ValidationReport").Worksheet;
        foreach (var expected in new[]
                 {
                     (Cell: "D20", Id: "validation-summary-missing-account-fill"),
                     (Cell: "D21", Id: "validation-summary-missing-voucher-fill"),
                     (Cell: "D22", Id: "validation-summary-blank-description-fill"),
                     (Cell: "D23", Id: "validation-summary-post-period-fill"),
                     (Cell: "D24", Id: "validation-summary-completeness-fill"),
                     (Cell: "D25", Id: "validation-summary-unbalanced-voucher-fill")
                 })
        {
            if (!string.Equals(
                    EffectiveFillArgb(document, summary, expected.Cell),
                    "FFFFFF00",
                    StringComparison.Ordinal))
            {
                mismatches.Add($"{expected.Id}: ValidationReport!{expected.Cell} fill.foregroundArgb");
            }
        }

        foreach (var expected in new[]
                 {
                     (Sheet: "V_Report 1", Id: "validation-missing-account-detail-autofit"),
                     (Sheet: "V_Report 2", Id: "validation-missing-voucher-detail-autofit"),
                     (Sheet: "V_Report 3", Id: "validation-blank-description-detail-autofit"),
                     (Sheet: "V_Report 4", Id: "validation-post-period-detail-autofit"),
                     (Sheet: "V_Report 5", Id: "validation-completeness-detail-autofit"),
                     (Sheet: "V_Report 6", Id: "validation-unbalanced-voucher-detail-autofit")
                 })
        {
            var worksheet = WorksheetPartFor(document, expected.Sheet).Worksheet;
            if (!FirstTwentyColumnsAreAutoFit(worksheet))
            {
                mismatches.Add($"{expected.Id}: {expected.Sheet}!columns 1-20 column.autoFit");
            }
        }

        var fieldInfoSheet = WorksheetPartFor(document, FieldInfoSheet).Worksheet;
        AddFieldInfoHeaderMismatches(
            document,
            fieldInfoSheet,
            row: 3,
            idPrefix: "validation-field-info-tb-header",
            expectedFill: "FFF0F000",
            mismatches);
        AddFieldInfoHeaderMismatches(
            document,
            fieldInfoSheet,
            row: 8,
            idPrefix: "validation-field-info-gl-header",
            expectedFill: "FFE6E600",
            mismatches);
        if (!ColumnsAreAutoFit(fieldInfoSheet, 1, 5))
        {
            mismatches.Add(
                "validation-field-info-autofit: 自動化工具-檔案欄位資訊!columns A-E column.autoFit");
        }

        Assert.True(
            mismatches.Count == 0,
            "Stage 8 Track O Validation registry diff:\n" + string.Join("\n", mismatches));
    }

    [Fact]
    public async Task Stage8TrackO_D25Fill_UsesFinalizedRawDetailGate_NotDisplayedVoucherCount()
    {
        var repositories = new ValidationRepositoryFakes();
        var projection = Projection(unbalancedDocumentCount: 0);

        var bytes = await WritePlannedAsync(
            repositories,
            projection,
            Plan(projection, unbalancedDetailRowCount: 1),
            new FieldInfoProjection([], []));

        using var document = Open(bytes);
        var summary = WorksheetPartFor(document, "ValidationReport").Worksheet;
        Assert.Equal(0m, ReadNumber(summary, "D25"));
        Assert.Equal(
            "FFFFFF00",
            EffectiveFillArgb(document, summary, "D25"));
        _ = WorksheetPartFor(document, "V_Report 6");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_000)]
    public async Task Stage8TrackO_SummaryConditionalFills_PreserveTemplateFill_WhenConditionsAreOff(
        long unbalancedDetailRowCount)
    {
        var repositories = new ValidationRepositoryFakes();
        var projection = Projection();
        var templateBytes = await ValidationTemplateBytesAsync();
        var outputBytes = await WritePlannedAsync(
            repositories,
            projection,
            Plan(projection, unbalancedDetailRowCount),
            new FieldInfoProjection([], []));

        using var template = Open(templateBytes);
        using var output = Open(outputBytes);
        var templateSummary = WorksheetPartFor(template, "ValidationReport").Worksheet;
        var outputSummary = WorksheetPartFor(output, "ValidationReport").Worksheet;
        var mismatches = new List<string>();

        foreach (var reference in new[] { "D20", "D21", "D22", "D23", "D24", "D25" })
        {
            var templateFill = EffectiveFillArgb(template, templateSummary, reference);
            var outputFill = EffectiveFillArgb(output, outputSummary, reference);
            if (!string.Equals(templateFill, outputFill, StringComparison.Ordinal))
            {
                mismatches.Add(
                    $"ValidationReport!{reference}: expected template fill {templateFill ?? "<none>"}, "
                    + $"actual {outputFill ?? "<none>"}");
            }

            if (string.Equals(outputFill, "FFFFFF00", StringComparison.Ordinal))
            {
                mismatches.Add(
                    $"ValidationReport!{reference}: off-state must not apply the conditional yellow fill");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            $"Stage 8 Track O Validation off-state diff (raw detail count "
            + $"{unbalancedDetailRowCount}):\n{string.Join("\n", mismatches)}");
    }

    [Fact]
    public async Task RawNullAccountDetail_AutoFitsFromLateContinuationAndDoesNotWrapOrIndent()
    {
        const int rowCount = 17;
        var longCjk = new string('寬', 40);
        var rows = Enumerable.Range(0, rowCount)
            .Select(index => new NullRecordRow(
                $"DOC-{index + 1}",
                "1101",
                "2025-03-01",
                "row",
                false,
                false,
                false,
                false,
                1_000 + index))
            .ToArray();
        var rawRows = rows.ToDictionary(
            row => row.EntryId,
            row => JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["短欄"] = "x",
                ["長中文欄"] = row.EntryId == rows[^1].EntryId ? longCjk : "短",
                ["大額顯示"] = row.EntryId == rows[^1].EntryId
                    ? "-9,876,543,210,987.6543"
                    : "1"
            }));
        var repositories = new ValidationRepositoryFakes
        {
            GlColumns = ["短欄", "長中文欄", "大額顯示"],
            NullRows = rows,
            RawRowsByEntryId = rawRows
        };
        var projection = Projection(nullAccountCount: rowCount);
        var plan = Plan(projection, unbalancedDetailRowCount: 0);
        var progress = new List<WorkpaperProgress>();

        var bytes = await WritePlannedAsync(
            repositories,
            projection,
            plan,
            new FieldInfoProjection([], []),
            new LegacyReportWriterOptions(17),
            progress.Add,
            progressRowInterval: 1);

        using var document = Open(bytes);
        var firstName = "V_Report 1";
        var secondName = LegacyReportWriter.SeriesName(firstName, 2);
        var first = WorksheetPartFor(document, firstName).Worksheet;
        var second = WorksheetPartFor(document, secondName).Worksheet;
        var firstColumns = Assert.IsType<Columns>(first.GetFirstChild<Columns>());
        var secondColumns = Assert.IsType<Columns>(second.GetFirstChild<Columns>());
        var firstCjkWidth = ColumnFor(firstColumns, 2).Width!.Value;
        var secondCjkWidth = ColumnFor(secondColumns, 2).Width!.Value;
        var firstAmountColumn = ColumnFor(firstColumns, 3);
        var firstAmountWidth = firstAmountColumn.Width!.Value;

        Assert.Equal(firstCjkWidth, secondCjkWidth, 6);
        Assert.InRange(firstCjkWidth, 94, 255);
        Assert.InRange(firstAmountWidth, 29, 255);
        Assert.True(firstAmountColumn.BestFit?.Value == true);
        Assert.True(firstAmountColumn.CustomWidth?.Value == true);
        AssertUnwrappedCells(document, firstName, "A1", "A2");
        AssertUnwrappedCells(document, secondName, "B2", "C2");
        var rawProgress = progress
            .Where(update => update.SheetName == firstName || update.SheetName == secondName)
            .ToArray();
        Assert.Equal(0, rawProgress[0].RowsWritten);
        var firstCompletionIndex = Array.FindIndex(
            rawProgress,
            update => update.SheetName == firstName
                      && update.SheetsCompleted > rawProgress[0].SheetsCompleted);
        var secondStartIndex = Array.FindIndex(
            rawProgress,
            update => update.SheetName == secondName);
        Assert.True(firstCompletionIndex >= 0);
        Assert.True(secondStartIndex > firstCompletionIndex);
        AssertOpenXmlValid(bytes);
    }

    [Fact]
    public async Task InfAllFields_AutoFitsUsingAllFiftyNineSamplesAndNoWrapStyles()
    {
        const int sampleCount = 59;
        var longCjk = new string('欄', 40);
        var samples = Enumerable.Range(0, sampleCount)
            .Select(index => new InfSampleRow(
                $"DOC-{index + 1}",
                "1101",
                "現金",
                10_000,
                0,
                "2025-03-01",
                "2025-03-02",
                "tester",
                "approver",
                "sample",
                2_000 + index))
            .ToArray();
        var rawRows = samples.ToDictionary(
            row => row.EntryId,
            row => JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["短欄"] = "x",
                ["長中文欄"] = row.EntryId == samples[^1].EntryId ? longCjk : "短",
                ["大額顯示"] = row.EntryId == samples[^1].EntryId
                    ? "-9,876,543,210,987.6543"
                    : "1"
            }));
        var repositories = new ValidationRepositoryFakes
        {
            GlColumns = ["短欄", "長中文欄", "大額顯示"],
            InfRows = samples,
            RawRowsByEntryId = rawRows
        };
        await using var output = new MemoryStream();

        await repositories.CreateWriter().WriteAsync(
            output,
            new InfReportContext(
                Context().Project,
                "inf-run",
                new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero)),
            CancellationToken.None);

        var bytes = output.ToArray();
        using var document = Open(bytes);
        const string sheetName = "可靠性樣本_所有欄位";
        var worksheet = WorksheetPartFor(document, sheetName).Worksheet;
        var columns = Assert.IsType<Columns>(worksheet.GetFirstChild<Columns>());
        var amountColumn = ColumnFor(columns, 3);

        Assert.InRange(ColumnFor(columns, 2).Width!.Value, 94, 255);
        Assert.InRange(amountColumn.Width!.Value, 29, 255);
        Assert.True(amountColumn.BestFit?.Value == true);
        Assert.True(amountColumn.CustomWidth?.Value == true);
        Assert.Equal(
            60U,
            worksheet.GetFirstChild<SheetData>()!.Elements<Row>().Last().RowIndex!.Value);
        AssertUnwrappedCells(document, sheetName, "A1", "A2", "B60", "C60");
        AssertOpenXmlValid(bytes);
    }

    [Fact]
    public async Task InfMain_AutoFitsAllFiftyNineSamplesWithoutShrinkingTemplateOrWrappingDescription()
    {
        const int sampleCount = 59;
        var longCjk = new string('摘', 40);
        var samples = Enumerable.Range(0, sampleCount)
            .Select(index => new InfSampleRow(
                $"DOC-{index + 1}",
                "1101",
                "現金",
                index == sampleCount - 1 ? long.MaxValue : 10_000,
                0,
                "2025-03-01",
                "2025-03-02",
                "tester",
                "approver",
                index == sampleCount - 1 ? longCjk : "sample",
                3_000 + index))
            .ToArray();
        var rawRows = samples.ToDictionary(
            row => row.EntryId,
            _ => JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["RAW_COLUMN"] = "raw"
            }));
        var repositories = new ValidationRepositoryFakes
        {
            GlColumns = ["RAW_COLUMN"],
            InfRows = samples,
            RawRowsByEntryId = rawRows
        };
        await using var output = new MemoryStream();

        await repositories.CreateWriter().WriteAsync(
            output,
            new InfReportContext(
                Context().Project,
                "inf-run",
                new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero)),
            CancellationToken.None);

        var bytes = output.ToArray();
        using var document = Open(bytes);
        const string sheetName = "INF Testing 可靠性測試";
        var worksheet = WorksheetPartFor(document, sheetName).Worksheet;
        var columns = Assert.IsType<Columns>(worksheet.GetFirstChild<Columns>());
        var templateMinimums = new[]
        {
            11.81640625D,
            15.36328125D,
            16.08984375D,
            17.453125D,
            10.6328125D,
            19.08984375D,
            19.08984375D,
            16.08984375D,
            17.453125D,
            43.36328125D,
            26D
        };

        for (var index = 0; index < templateMinimums.Length; index++)
        {
            Assert.True(
                ColumnFor(columns, checked((uint)index + 2)).Width!.Value
                >= templateMinimums[index]);
        }
        Assert.InRange(ColumnFor(columns, 6).Width!.Value, 23, 255);
        Assert.InRange(ColumnFor(columns, 11).Width!.Value, 94, 255);
        AssertUnwrappedCells(
            document,
            sheetName,
            "F51",
            "K51",
            "F111",
            "K53",
            "K111");
        AssertOpenXmlValid(bytes);
    }

    [Fact]
    public async Task Stage8TrackO_InfNumberFormat_UsesTargetTextKindAndLeavesNumberAndUnusedRowsUnchanged()
    {
        var sample = new InfSampleRow(
            "00123",
            "00123",
            "Account",
            12_300,
            0,
            "2025-03-01",
            "2025-03-02",
            "tester",
            "approver",
            "sample",
            8_001);
        var repositories = new ValidationRepositoryFakes
        {
            GlColumns = ["RAW_COLUMN"],
            InfRows = [sample],
            RawRowsByEntryId = new Dictionary<long, string>
            {
                [sample.EntryId] = "{\"RAW_COLUMN\":\"raw\"}"
            },
            TargetGlDefinitions =
            [
                new LegacyFieldDefinition(
                    1,
                    "傳票號碼_JE",
                    "document source",
                    LegacyFieldKind.Text,
                    5,
                    null),
                new LegacyFieldDefinition(
                    2,
                    "傳票金額_JE",
                    "amount source",
                    LegacyFieldKind.Number,
                    null,
                    2),
                new LegacyFieldDefinition(
                    3,
                    "未對映文字_JE",
                    "unmapped source",
                    LegacyFieldKind.Text,
                    20,
                    null)
            ]
        };
        await using var output = new MemoryStream();

        await repositories.CreateWriter().WriteAsync(
            output,
            new InfReportContext(
                Context().Project,
                "inf-run",
                new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero)),
            CancellationToken.None);

        using var document = Open(output.ToArray());
        var worksheet = WorksheetPartFor(document, "INF Testing 可靠性測試").Worksheet;
        Assert.Equal(
            "@",
            EffectiveNumberFormatCode(document, worksheet, "B53"));
        Assert.Equal(
            "builtin:2",
            EffectiveNumberFormatCode(document, worksheet, "F53"));
        Assert.Equal(
            "builtin:2",
            EffectiveNumberFormatCode(document, worksheet, "B54"));
    }

    [Fact]
    public async Task CompletenessDetail_AutoFitsFormattedAmountsAndDoesNotWrapOrIndent()
    {
        var repositories = new ValidationRepositoryFakes
        {
            CompletenessRows =
            [
                new CompletenessDiffAccount(
                    "LONG-ACCOUNT-CODE",
                    new string('科', 40),
                    long.MaxValue,
                    long.MinValue,
                    long.MinValue,
                    false)
            ]
        };
        var projection = Projection();
        var plan = Plan(projection, unbalancedDetailRowCount: 0);

        var bytes = await WritePlannedAsync(
            repositories,
            projection,
            plan,
            new FieldInfoProjection([], []));

        using var document = Open(bytes);
        const string sheetName = "V_Report 5";
        var worksheet = WorksheetPartFor(document, sheetName).Worksheet;
        var columns = Assert.IsType<Columns>(worksheet.GetFirstChild<Columns>());

        Assert.InRange(ColumnFor(columns, 2).Width!.Value, 94, 255);
        Assert.InRange(ColumnFor(columns, 5).Width!.Value, 31, 255);
        AssertUnwrappedCells(document, sheetName, "A1", "A2", "B2", "E2");
        AssertOpenXmlValid(bytes);
    }

    [Fact]
    public async Task FieldInfo_UsesLegacyPhysicalLayoutHiddenRoundTripAndFullDataAutoFitCap()
    {
        var repositories = new ValidationRepositoryFakes();
        var longDescription = new string('欄', 300);
        var fieldInfo = JetAuditProgram.ProjectFieldInfo(
        [
            new LegacyFieldDefinition(1, "TB_TEXT_TB", longDescription, LegacyFieldKind.Text, 300, null),
            new LegacyFieldDefinition(2, "TB_NUMBER", null, LegacyFieldKind.Number, null, 4),
            new LegacyFieldDefinition(3, "TB_DATE_TB", null, LegacyFieldKind.Date, null, null),
            new LegacyFieldDefinition(4, "TB_TIME", null, LegacyFieldKind.Time, null, null)
        ],
        [
            new LegacyFieldDefinition(1, "GL_TEXT_JE", null, LegacyFieldKind.Text, 40, null),
            new LegacyFieldDefinition(2, "GL_NUMBER_JE_S", null, LegacyFieldKind.Number, null, 2),
            new LegacyFieldDefinition(3, "GL_DATE_je", null, LegacyFieldKind.Date, null, null),
            new LegacyFieldDefinition(4, "GL_TIME", null, LegacyFieldKind.Time, null, null)
        ]);

        var projection = Projection();
        var bytes = await WritePlannedAsync(
            repositories,
            projection,
            Plan(projection, unbalancedDetailRowCount: 0),
            fieldInfo);

        using (var workbook = Workbook(bytes))
        {
            var sheet = workbook.Worksheet(FieldInfoSheet);
            Assert.Equal("V.2019", sheet.Cell("A1").GetString());
            Assert.Equal("TB檔案配對前後欄位對照表", sheet.Cell("A2").GetString());
            Assert.Equal(
                new[] { "配對前欄位名稱", "欄位型態", "文字長度", "小數位數", "配對後欄位名稱" },
                Row(sheet, 3));
            Assert.Equal(longDescription, sheet.Cell("A4").GetString());
            Assert.Equal(
                new[] { "文字型態", "數字型態", "日期型態", "時間型態" },
                sheet.Range("B4:B7").Cells().Select(cell => cell.GetString()).ToArray());
            Assert.Equal(
                new[] { longDescription, "TB_NUMBER", "TB_DATE_TB", "TB_TIME" },
                sheet.Range("A4:A7").Cells().Select(cell => cell.GetString()).ToArray());
            Assert.Equal(300, sheet.Cell("C4").GetValue<int>());
            Assert.True(sheet.Cell("D4").IsEmpty());
            Assert.True(sheet.Cell("C5").IsEmpty());
            Assert.Equal(4, sheet.Cell("D5").GetValue<int>());
            Assert.Equal("TB_TEXT_TB", sheet.Cell("E4").GetString());
            Assert.True(sheet.Cell("E5").IsEmpty());
            Assert.Equal("TB_DATE_TB", sheet.Cell("E6").GetString());
            Assert.True(sheet.Cell("E7").IsEmpty());

            Assert.All(sheet.Range("A8:E8").Cells(), cell => Assert.True(cell.IsEmpty()));
            Assert.All(sheet.Range("A9:E9").Cells(), cell => Assert.True(cell.IsEmpty()));
            Assert.Equal("GL檔案配對前後欄位對照表", sheet.Cell("A10").GetString());
            Assert.Equal(Row(sheet, 3), Row(sheet, 11));
            Assert.Equal(
                new[] { "GL_TEXT_JE", "GL_NUMBER_JE_S", "GL_DATE_je", "GL_TIME" },
                sheet.Range("A12:A15").Cells().Select(cell => cell.GetString()).ToArray());
            Assert.Equal(
                new[] { "文字型態", "數字型態", "日期型態", "時間型態" },
                sheet.Range("B12:B15").Cells().Select(cell => cell.GetString()).ToArray());
            Assert.Equal(40, sheet.Cell("C12").GetValue<int>());
            Assert.Equal(2, sheet.Cell("D13").GetValue<int>());
            Assert.True(sheet.Cell("C13").IsEmpty());
            Assert.True(sheet.Cell("D12").IsEmpty());
            Assert.Equal("GL_TEXT_JE", sheet.Cell("E12").GetString());
            Assert.Equal("GL_NUMBER_JE_S", sheet.Cell("E13").GetString());
            Assert.True(sheet.Cell("E14").IsEmpty());
            Assert.True(sheet.Cell("E15").IsEmpty());

            Assert.True(sheet.Column(6).IsHidden);
            Assert.True(sheet.Column(7).IsHidden);
            Assert.True(sheet.Column(8).IsHidden);
            Assert.Equal(MappingMetadataFormat.Marker, sheet.Cell("F1").GetString());
            Assert.Equal(MappingMetadataFormat.CurrentVersion, sheet.Cell("G1").GetValue<int>());
            var metadata = MappingMetadataCodec.Decode(
                MappingMetadataFormat.CurrentVersion,
                sheet.Cell("H1").GetString());
            Assert.Equal("APPROVAL_DATE", metadata.Gl.Mapping[GlMappingKeys.DocDate]);
            Assert.Equal("ACCOUNT_CODE", metadata.Tb.Mapping[TbMappingKeys.AccNum]);
            Assert.True(sheet.Protection.IsProtected);
        }

        using var document = Open(bytes);
        var worksheet = WorksheetPartFor(document, FieldInfoSheet).Worksheet;
        foreach (var blankRowIndex in new uint[] { 8, 9 })
        {
            var blankRow = worksheet.GetFirstChild<SheetData>()!.Elements<Row>()
                .Single(row => row.RowIndex?.Value == blankRowIndex);
            Assert.Empty(blankRow.Elements<Cell>());
        }
        Assert.Equal(
            0U,
            worksheet.Descendants<Cell>().Single(cell => cell.CellReference?.Value == "H1")
                .StyleIndex?.Value);
        var columns = Assert.IsType<Columns>(worksheet.GetFirstChild<Columns>());
        var visibleColumns = Enumerable.Range(1, 5)
            .Select(index => ColumnFor(columns, (uint)index))
            .ToArray();
        Assert.All(visibleColumns, column =>
        {
            Assert.True(column.BestFit?.Value == true);
            Assert.True(column.CustomWidth?.Value == true);
            Assert.InRange(column.Width?.Value ?? 0, 1, 255);
        });
        Assert.Equal(255d, visibleColumns[0].Width?.Value);
        var hiddenMetadata = ColumnFor(columns, 6);
        Assert.Equal(8U, hiddenMetadata.Max?.Value);
        Assert.True(hiddenMetadata.Hidden?.Value == true);
        AssertOpenXmlValid(bytes);
    }

    private static async Task<byte[]> WritePlannedAsync(
        ValidationRepositoryFakes repositories,
        ValidationReportProjection projection,
        ValidationReportPlan plan,
        FieldInfoProjection fieldInfo,
        LegacyReportWriterOptions? options = null,
        Action<WorkpaperProgress>? progress = null,
        int progressRowInterval = 10_000)
    {
        var writer = repositories.CreateWriter(options, progressRowInterval);
        await using var output = new MemoryStream();
        await ((IPlannedValidationReportWriter)writer).WritePlannedAsync(
            output,
            Context(),
            projection,
            plan,
            fieldInfo,
            "tester",
            CancellationToken.None,
            progress);
        return output.ToArray();
    }

    private static async Task<byte[]> WriteFormalPlannedAsync(
        ValidationRepositoryFakes repositories,
        ValidationReportProjection projection,
        ValidationReportPlan plan,
        FieldInfoProjection fieldInfo)
    {
        var result = await WriteFormalPlannedResultAsync(
            repositories,
            projection,
            plan,
            fieldInfo);
        return result.Bytes;
    }

    private static async Task<(byte[] Bytes, ExportStats Stats)> WriteFormalPlannedResultAsync(
        ValidationRepositoryFakes repositories,
        ValidationReportProjection projection,
        ValidationReportPlan plan,
        FieldInfoProjection fieldInfo,
        LegacyReportWriterOptions? options = null)
    {
        var writer = repositories.CreateWriter(options);
        var glMapping = new CommittedMapping(
            DatasetKind.Gl,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.DocDate] = "DOCUMENT_DATE_JE"
            },
            GlAmountModeNames.Signed,
            "batch-Gl",
            DateTimeOffset.UnixEpoch);
        var metadata = new ReportWorkbookMetadata(
            "2025-01-01",
            "2025-12-31",
            TaxonomyRevision: 1,
            glMapping,
            TbMapping: null);

        await using var output = new MemoryStream();
        var stats = await ((IFormalPlannedValidationReportWriter)writer).WriteFormalPlannedAsync(
            output,
            Context(),
            projection,
            plan,
            fieldInfo,
            metadata,
            "tester",
            CancellationToken.None);
        return (output.ToArray(), stats);
    }

    private static ValidationReportContext Context() => new(
        new ReportDocumentContext(
            "project-validation-direct",
            "測試公司",
            "2025-01-01",
            "2025-12-31",
            "2024-01-01",
            10_000),
        "validation-run-id-must-not-be-visible",
        new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero),
        "{}");

    private static ValidationReportProjection Projection(
        long nullAccountCount = 0,
        long nullDocumentCount = 0,
        long nullDescriptionCount = 0,
        long outOfRangeDateCount = 0,
        long unbalancedDocumentCount = 0,
        long completenessDiffAccountCount = 0,
        long sourceQualityFindingCount = 0,
        IReadOnlyList<SourceQualityFindingRow>? sourceQualitySampleRows = null) =>
        new(
            Net: 0,
            TotalDebit: 100,
            TotalCredit: 100,
            GlRowCount: 2,
            CompletenessDiffAccountCount: completenessDiffAccountCount,
            UnbalancedDocumentCount: unbalancedDocumentCount,
            NullAccountCount: nullAccountCount,
            NullDocumentCount: nullDocumentCount,
            NullDescriptionCount: nullDescriptionCount,
            OutOfRangeDateCount: outOfRangeDateCount,
            SourceQualityFindingCount: sourceQualityFindingCount,
            SourceQualitySampleRows: sourceQualitySampleRows ?? []);

    private static ValidationReportPlan Plan(
        ValidationReportProjection projection,
        long unbalancedDetailRowCount) =>
        JetAuditProgram.Finalize(
            JetAuditProgram.Plan(new ValidationReportRequest(
                "project-validation-direct",
                "2025-01-01",
                "2025-12-31",
                projection.CompletenessDiffAccountCount,
                projection.NullAccountCount,
                projection.NullDocumentCount,
                projection.NullDescriptionCount,
                projection.OutOfRangeDateCount)),
            new ValidationReportPlanningFacts(unbalancedDetailRowCount));

    private static async Task<byte[]> ValidationTemplateBytesAsync()
    {
        await using var stream = new MemoryStream();
        await ReportTemplateCatalog.Default.CopyToAsync(
            ReportTemplateCatalog.Validation,
            stream,
            CancellationToken.None);
        return stream.ToArray();
    }

    private static XLWorkbook Workbook(byte[] bytes) =>
        new(new MemoryStream(bytes, writable: false));

    private static SpreadsheetDocument Open(byte[] bytes) =>
        SpreadsheetDocument.Open(new MemoryStream(bytes, writable: false), false);

    private static string[] Row(IXLWorksheet sheet, int row) =>
        Enumerable.Range(1, 5).Select(column => sheet.Cell(row, column).GetString()).ToArray();

    private static WorksheetPart WorksheetPartFor(
        SpreadsheetDocument document,
        string sheetName)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>()
            .Single(item => string.Equals(item.Name?.Value, sheetName, StringComparison.Ordinal));
        return Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!.Value!));
    }

    private static Column ColumnFor(Columns columns, uint index) =>
        columns.Elements<Column>().Single(column =>
            column.Min?.Value <= index && column.Max?.Value >= index);

    private static bool FirstTwentyColumnsAreAutoFit(Worksheet worksheet) =>
        ColumnsAreAutoFit(worksheet, 1, 20);

    private static bool ColumnsAreAutoFit(
        Worksheet worksheet,
        uint first,
        uint last)
    {
        var columns = worksheet.GetFirstChild<Columns>();
        if (columns is null)
        {
            return false;
        }

        for (var index = first; index <= last; index++)
        {
            var column = columns.Elements<Column>().SingleOrDefault(item =>
                item.Min?.Value <= index && item.Max?.Value >= index);
            if (column is null
                || column.Width is null
                || column.BestFit?.Value != true
                || column.CustomWidth?.Value != true)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddFieldInfoHeaderMismatches(
        SpreadsheetDocument document,
        Worksheet worksheet,
        uint row,
        string idPrefix,
        string expectedFill,
        List<string> mismatches)
    {
        var cells = Enumerable.Range(1, 5)
            .Select(column => ReportSheetWriter.Reference((uint)column, row))
            .ToArray();
        var fonts = cells
            .Select(reference => EffectiveFont(document, worksheet, reference))
            .ToArray();
        if (fonts.Any(font => font.Bold is null))
        {
            mismatches.Add($"{idPrefix}-bold: row {row} A:E font.bold");
        }
        if (fonts.Any(font => !string.Equals(
                font.FontName?.Val?.Value,
                "微軟正黑體",
                StringComparison.Ordinal)))
        {
            mismatches.Add($"{idPrefix}-font: row {row} A:E font.name");
        }
        if (fonts.Any(font => font.FontSize?.Val?.Value != 12D))
        {
            mismatches.Add($"{idPrefix}-size: row {row} A:E font.size");
        }
        if (cells.Any(reference => !string.Equals(
                EffectiveFillArgb(document, worksheet, reference),
                expectedFill,
                StringComparison.Ordinal)))
        {
            mismatches.Add($"{idPrefix}-fill: row {row} A:E fill.foregroundArgb");
        }
    }

    private static DocumentFormat.OpenXml.Spreadsheet.Font EffectiveFont(
        SpreadsheetDocument document,
        Worksheet worksheet,
        string reference)
    {
        var format = EffectiveCellFormat(document, worksheet, reference);
        return document.WorkbookPart!.WorkbookStylesPart!.Stylesheet.Fonts!
            .Elements<DocumentFormat.OpenXml.Spreadsheet.Font>()
            .ElementAt(checked((int)(format.FontId?.Value ?? 0U)));
    }

    private static string? EffectiveFillArgb(
        SpreadsheetDocument document,
        Worksheet worksheet,
        string reference)
    {
        var format = EffectiveCellFormat(document, worksheet, reference);
        return document.WorkbookPart!.WorkbookStylesPart!.Stylesheet.Fills!
            .Elements<Fill>()
            .ElementAt(checked((int)(format.FillId?.Value ?? 0U)))
            .PatternFill?
            .ForegroundColor?
            .Rgb?
            .Value?
            .ToUpperInvariant();
    }

    private static string EffectiveNumberFormatCode(
        SpreadsheetDocument document,
        Worksheet worksheet,
        string reference)
    {
        var format = EffectiveCellFormat(document, worksheet, reference);
        var id = format.NumberFormatId?.Value ?? 0U;
        if (id == 0U)
        {
            return "General";
        }
        if (id == 49U)
        {
            return "@";
        }

        return document.WorkbookPart!.WorkbookStylesPart!.Stylesheet.NumberingFormats?
            .Elements<NumberingFormat>()
            .SingleOrDefault(item => item.NumberFormatId?.Value == id)
            ?.FormatCode?
            .Value
            ?? $"builtin:{id}";
    }

    private static CellFormat EffectiveCellFormat(
        SpreadsheetDocument document,
        Worksheet worksheet,
        string reference)
    {
        var cell = worksheet.Descendants<Cell>()
            .Single(item => string.Equals(
                item.CellReference?.Value,
                reference,
                StringComparison.OrdinalIgnoreCase));
        return document.WorkbookPart!.WorkbookStylesPart!.Stylesheet.CellFormats!
            .Elements<CellFormat>()
            .ElementAt(checked((int)(cell.StyleIndex?.Value ?? 0U)));
    }

    private static decimal ReadNumber(Worksheet worksheet, string reference)
    {
        var value = worksheet.Descendants<Cell>()
            .Single(item => string.Equals(
                item.CellReference?.Value,
                reference,
                StringComparison.OrdinalIgnoreCase))
            .CellValue?
            .Text;
        return decimal.Parse(
            value ?? throw new InvalidDataException($"Cell {reference} has no numeric value."),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AssertUnwrappedCells(
        SpreadsheetDocument document,
        string sheetName,
        params string[] references)
    {
        var worksheet = WorksheetPartFor(document, sheetName).Worksheet;
        var formats = document.WorkbookPart!.WorkbookStylesPart!.Stylesheet
            .CellFormats!
            .Elements<CellFormat>()
            .ToArray();
        foreach (var reference in references)
        {
            var cell = worksheet.Descendants<Cell>()
                .Single(item => item.CellReference?.Value == reference);
            var style = formats[checked((int)(cell.StyleIndex?.Value ?? 0U))];
            var alignment = Assert.IsType<Alignment>(style.Alignment);
            Assert.False(alignment.WrapText?.Value ?? false);
            Assert.Equal(0U, alignment.Indent?.Value ?? 0U);
            Assert.False(alignment.ShrinkToFit?.Value ?? false);
        }
    }

    private static void AssertPartTreesEqual(
        OpenXmlPart expectedRoot,
        OpenXmlPart actualRoot,
        bool includeRoot)
    {
        var expected = SnapshotPartTree(expectedRoot, includeRoot);
        var actual = SnapshotPartTree(actualRoot, includeRoot);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var name in expected.Keys)
        {
            Assert.True(
                expected[name].SequenceEqual(actual[name]),
                $"Fixed template part changed: {name}");
        }
    }

    private static IReadOnlyDictionary<string, byte[]> SnapshotPartTree(
        OpenXmlPart root,
        bool includeRoot)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var pending = new Queue<OpenXmlPart>();
        var seen = new HashSet<Uri>();
        if (includeRoot)
        {
            pending.Enqueue(root);
        }
        else
        {
            foreach (var child in root.Parts.Select(pair => pair.OpenXmlPart))
            {
                pending.Enqueue(child);
            }
        }

        while (pending.TryDequeue(out var part))
        {
            if (!seen.Add(part.Uri))
            {
                continue;
            }

            using var input = part.GetStream(FileMode.Open, FileAccess.Read);
            using var copy = new MemoryStream();
            input.CopyTo(copy);
            result.Add(part.Uri.ToString(), copy.ToArray());
            foreach (var child in part.Parts.Select(pair => pair.OpenXmlPart))
            {
                pending.Enqueue(child);
            }
        }

        return result;
    }

    private static void AssertValidationPackageAllowedDiff(
        byte[] templateBytes,
        byte[] outputBytes)
    {
        var template = ValidationPackageSnapshot.Capture(templateBytes);
        var output = ValidationPackageSnapshot.Capture(outputBytes);
        var summaryPart = template.ResolveWorksheetPart("ValidationReport");
        var fieldInfoPart = template.ResolveWorksheetPart(FieldInfoSheet);
        var explanationPart = template.ResolveWorksheetPart("step1-3 完整性測試之差異說明");

        var removed = template.PartNames.Except(output.PartNames, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var expectedRemoved = template.RelationshipClosure(explanationPart);
        expectedRemoved.Add(explanationPart);
        Assert.True(
            expectedRemoved.SetEquals(removed),
            $"Removed Validation package parts were: {string.Join(", ", removed.Order(StringComparer.Ordinal))}");

        var added = output.PartNames.Except(template.PartNames, StringComparer.Ordinal).ToArray();
        Assert.Equal(7, added.Length);
        Assert.All(added, name => Assert.Matches(@"^xl/worksheets/sheet\d+\.xml$", name));

        var changedCommon = template.PartNames.Intersect(output.PartNames, StringComparer.Ordinal)
            .Where(name => !template.Bytes(name).SequenceEqual(output.Bytes(name)))
            .ToHashSet(StringComparer.Ordinal);
        var expectedChangedCommon = new HashSet<string>(StringComparer.Ordinal)
        {
            "[Content_Types].xml",
            "xl/workbook.xml",
            "xl/_rels/workbook.xml.rels",
            "xl/sharedStrings.xml",
            "xl/styles.xml",
            summaryPart,
            fieldInfoPart
        };
        Assert.True(
            expectedChangedCommon.SetEquals(changedCommon),
            $"Changed Validation package parts were: {string.Join(", ", changedCommon.Order(StringComparer.Ordinal))}");

        AssertStylesAppendOnly(
            template.Xml("xl/styles.xml"),
            output.Xml("xl/styles.xml"));
    }

    private static void AssertStylesAppendOnly(XDocument template, XDocument output)
    {
        XNamespace spreadsheet =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var expectedDeltas = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // Stage 9 appends the closed ExportDatabase raw-page family. The
            // Field Info repair adds a two-style borderless base plus its
            // shared version/TB/GL appearance patches.
            ["numFmts"] = 30,
            ["fonts"] = 14,
            ["fills"] = 16,
            ["borders"] = 3,
            ["cellStyleXfs"] = 2,
            // Generated writer formats plus the Stage-8 component patches
            // and the Stage-9 ExportDatabase family remain append-only.
            ["cellXfs"] = 62
        };
        var actualDeltas = expectedDeltas.Keys.ToDictionary(
            name => name,
            name =>
            {
                var sourceCount = template.Root!.Element(spreadsheet + name)?.Elements().Count() ?? 0;
                var outputCount = output.Root!.Element(spreadsheet + name)?.Elements().Count()
                    ?? throw new InvalidDataException($"Output styles has no {name}.");
                return outputCount - sourceCount;
            },
            StringComparer.Ordinal);
        Assert.True(
            expectedDeltas.SequenceEqual(actualDeltas),
            $"Style deltas were: {string.Join(", ", actualDeltas.Select(pair => $"{pair.Key}={pair.Value}"))}");
        foreach (var (name, expectedDelta) in expectedDeltas)
        {
            var sourceElement = template.Root!.Element(spreadsheet + name);
            var outputElement = output.Root!.Element(spreadsheet + name)
                ?? throw new InvalidDataException($"Output styles has no {name}.");
            var sourceChildren = sourceElement?.Elements().ToArray() ?? [];
            var outputChildren = outputElement.Elements().ToArray();
            Assert.Equal(sourceChildren.Length + expectedDelta, outputChildren.Length);
            for (var index = 0; index < sourceChildren.Length; index++)
            {
                if (name == "fonts")
                {
                    Assert.Equal(
                        CanonicalFontWithoutFamily(sourceChildren[index], spreadsheet),
                        CanonicalFontWithoutFamily(outputChildren[index], spreadsheet));
                }
                else
                {
                    Assert.Equal(
                        Canonical(sourceChildren[index]),
                        Canonical(outputChildren[index]));
                }
            }
        }

        var outputFonts = output.Root!.Element(spreadsheet + "fonts")!
            .Elements(spreadsheet + "font").ToArray();
        Assert.All(outputFonts, font =>
        {
            Assert.Equal(
                "微軟正黑體",
                (string?)font.Element(spreadsheet + "name")?.Attribute("val"));
            Assert.Null(font.Element(spreadsheet + "scheme"));
        });

        var sourceRemainder = template.Root!.Elements()
            .Where(element => !expectedDeltas.ContainsKey(element.Name.LocalName))
            .ToArray();
        var outputRemainder = output.Root!.Elements()
            .Where(element => !expectedDeltas.ContainsKey(element.Name.LocalName))
            .ToArray();
        Assert.Equal(
            sourceRemainder.Select(element => element.Name),
            outputRemainder.Select(element => element.Name));
        for (var index = 0; index < sourceRemainder.Length; index++)
        {
            Assert.Equal(
                Canonical(sourceRemainder[index]),
                Canonical(outputRemainder[index]));
        }
    }

    private static string Canonical(XElement element) =>
        CanonicalElement(element).ToString(System.Xml.Linq.SaveOptions.DisableFormatting);

    private static string CanonicalFontWithoutFamily(
        XElement font,
        XNamespace spreadsheet)
    {
        var copy = new XElement(font);
        copy.Element(spreadsheet + "name")?.Remove();
        copy.Element(spreadsheet + "scheme")?.Remove();
        return Canonical(copy);
    }

    private static XElement CanonicalElement(XElement element) =>
        new(
            element.Name,
            element.Attributes()
                .Where(attribute => !attribute.IsNamespaceDeclaration)
                .OrderBy(attribute => attribute.Name.NamespaceName, StringComparer.Ordinal)
                .ThenBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
                .Select(attribute => new XAttribute(attribute.Name, attribute.Value)),
            element.Nodes().Select<XNode, XNode>(node => node switch
            {
                XElement child => CanonicalElement(child),
                XCData data => new XCData(data.Value),
                XText text => new XText(text.Value),
                XComment comment => new XComment(comment.Value),
                XProcessingInstruction instruction =>
                    new XProcessingInstruction(instruction.Target, instruction.Data),
                _ => throw new InvalidDataException(
                    $"Unsupported XML node type '{node.NodeType}' in Validation package oracle.")
            }));

    private sealed class ValidationPackageSnapshot
    {
        private static readonly XNamespace Spreadsheet =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace OfficeRelationships =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackageRelationships =
            "http://schemas.openxmlformats.org/package/2006/relationships";
        private readonly IReadOnlyDictionary<string, byte[]> _parts;

        private ValidationPackageSnapshot(IReadOnlyDictionary<string, byte[]> parts)
        {
            _parts = parts;
            PartNames = parts.Keys.Order(StringComparer.Ordinal).ToArray();
        }

        internal IReadOnlyList<string> PartNames { get; }

        internal byte[] Bytes(string name) => _parts[name];

        internal XDocument Xml(string name)
        {
            using var stream = new MemoryStream(_parts[name], writable: false);
            return XDocument.Load(stream, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        }

        internal string ResolveWorksheetPart(string sheetName)
        {
            var workbook = Xml("xl/workbook.xml");
            var relationshipId = (string?)workbook.Descendants(Spreadsheet + "sheet")
                .Single(sheet => string.Equals(
                    (string?)sheet.Attribute("name"),
                    sheetName,
                    StringComparison.Ordinal))
                .Attribute(OfficeRelationships + "id")
                ?? throw new InvalidDataException($"Worksheet '{sheetName}' has no relationship id.");
            var target = (string?)Xml("xl/_rels/workbook.xml.rels").Root!
                .Elements(PackageRelationships + "Relationship")
                .Single(relationship => string.Equals(
                    (string?)relationship.Attribute("Id"),
                    relationshipId,
                    StringComparison.Ordinal))
                .Attribute("Target")
                ?? throw new InvalidDataException($"Worksheet '{sheetName}' has no relationship target.");
            return ResolveTarget("xl/workbook.xml", target);
        }

        internal HashSet<string> RelationshipClosure(string rootPart)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            pending.Enqueue(rootPart);
            while (pending.TryDequeue(out var part))
            {
                var relationshipPart = RelationshipPartName(part);
                if (!_parts.ContainsKey(relationshipPart) || !result.Add(relationshipPart))
                {
                    continue;
                }
                foreach (var relationship in Xml(relationshipPart).Root!
                             .Elements(PackageRelationships + "Relationship")
                             .Where(item => !string.Equals(
                                 (string?)item.Attribute("TargetMode"),
                                 "External",
                                 StringComparison.OrdinalIgnoreCase)))
                {
                    var target = (string?)relationship.Attribute("Target")
                        ?? throw new InvalidDataException("Package relationship has no target.");
                    var resolved = ResolveTarget(part, target);
                    if (result.Add(resolved))
                    {
                        pending.Enqueue(resolved);
                    }
                }
            }
            return result;
        }

        internal static ValidationPackageSnapshot Capture(byte[] bytes)
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
            {
                using var part = entry.Open();
                using var copy = new MemoryStream();
                part.CopyTo(copy);
                parts.Add(entry.FullName.Replace('\\', '/'), copy.ToArray());
            }
            return new ValidationPackageSnapshot(parts);
        }

        private static string RelationshipPartName(string partName)
        {
            var separator = partName.LastIndexOf('/');
            var directory = separator < 0 ? string.Empty : partName[..separator];
            var fileName = separator < 0 ? partName : partName[(separator + 1)..];
            return string.IsNullOrEmpty(directory)
                ? $"_rels/{fileName}.rels"
                : $"{directory}/_rels/{fileName}.rels";
        }

        private static string ResolveTarget(string sourcePart, string target) =>
            Uri.UnescapeDataString(new Uri(
                    new Uri($"https://package.invalid/{sourcePart}"),
                    target)
                .AbsolutePath
                .TrimStart('/'));
    }

    private static void AssertOpenXmlValid(byte[] bytes)
    {
        using var document = Open(bytes);
        Assert.Empty(new OpenXmlValidator().Validate(document));
    }

    private sealed class ValidationRepositoryFakes :
        ICompletenessAccountPageRepository,
        ICompletenessDiffPageRepository,
        IUnbalancedGlEntryPageRepository,
        INullRecordsPageRepository,
        IInfSamplePageRepository,
        IPrescreenPageRepository,
        IFilterHitsPageRepository,
        IRawGlExportRepository,
        IImportRepository,
        IMappingStateStore,
        ICreatorSummaryExportRepository,
        IAccountUsageExportRepository,
        ITagMatrixScenariosRepository,
        ILegacyFieldDefinitionFactsPort,
        IResultPageRdeValuesPort,
        ISourceQualityPageRepository
    {
        private static readonly IReadOnlyDictionary<string, string> GlMapping =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.DocDate] = "APPROVAL_DATE"
            };

        private static readonly IReadOnlyDictionary<string, string> TbMapping =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TbMappingKeys.AccNum] = "ACCOUNT_CODE"
            };

        internal bool ThrowOnValidationDetailRead { get; init; }

        internal IReadOnlyList<string> GlColumns { get; init; } = ["RAW_COLUMN"];

        internal IReadOnlyList<NullRecordRow>? NullRows { get; init; }

        internal IReadOnlyList<InfSampleRow>? InfRows { get; init; }

        internal IReadOnlyList<CompletenessDiffAccount>? CompletenessRows { get; init; }

        internal IReadOnlyList<LegacyFieldDefinition> TargetGlDefinitions { get; init; } = [];

        internal IReadOnlyDictionary<long, string>? RawRowsByEntryId { get; init; }

        internal IReadOnlyList<SourceQualityFindingRow>? SourceQualityRows { get; init; }

        internal int NullPageCalls { get; private set; }

        internal int UnbalancedPageCalls { get; private set; }

        internal int RawRowCalls { get; private set; }

        internal int SourceQualityPageCalls { get; private set; }

        internal LegacyReportWriter CreateWriter(
            LegacyReportWriterOptions? options = null,
            int progressRowInterval = 10_000)
        {
            var writerOptions = options ?? new LegacyReportWriterOptions();
            if (TargetGlDefinitions.Count > 0)
            {
                var fieldDefinitionConstructor = typeof(LegacyReportWriter)
                    .GetConstructors(
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic)
                    .SingleOrDefault(constructor =>
                    {
                        var parameters = constructor.GetParameters();
                        return parameters.Any(parameter =>
                                   parameter.ParameterType == typeof(ILegacyFieldDefinitionFactsPort))
                               && parameters.Any(parameter =>
                                   parameter.ParameterType == typeof(LegacyReportWriterOptions));
                    });
                if (fieldDefinitionConstructor is not null)
                {
                    object? ResolveArgument(Type type)
                    {
                        if (type == typeof(ILegacyFieldDefinitionFactsPort))
                        {
                            return this;
                        }
                        if (type == typeof(ITagMatrixRowPageRepository))
                        {
                            return null;
                        }
                        if (type == typeof(LegacyReportWriterOptions))
                        {
                            return writerOptions;
                        }
                        if (type == typeof(int))
                        {
                            return progressRowInterval;
                        }
                        if (type.IsInstanceOfType(this))
                        {
                            return this;
                        }

                        throw new InvalidOperationException(
                            $"Unsupported LegacyReportWriter constructor parameter {type.FullName}.");
                    }

                    var arguments = fieldDefinitionConstructor.GetParameters()
                        .Select(parameter => ResolveArgument(parameter.ParameterType))
                        .ToArray();
                    return Assert.IsType<LegacyReportWriter>(
                        fieldDefinitionConstructor.Invoke(arguments));
                }
            }

            return new LegacyReportWriter(
                (ICompletenessAccountPageRepository)this,
                (ICompletenessDiffPageRepository)this,
                (IUnbalancedGlEntryPageRepository)this,
                (INullRecordsPageRepository)this,
                (IInfSamplePageRepository)this,
                (IPrescreenPageRepository)this,
                (IRawGlExportRepository)this,
                (IImportRepository)this,
                (IMappingStateStore)this,
                (ICreatorSummaryExportRepository)this,
                (IAccountUsageExportRepository)this,
                (ITagMatrixScenariosRepository)this,
                writerOptions,
                progressRowInterval);
        }

        public Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
            string projectId,
            int moneyScale,
            string periodStart,
            string periodEnd,
            PageRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PageResult<CompletenessDiffAccount>(
                CompletenessRows ?? [],
                null));

        Task<PageResult<CompletenessDiffAccount>> ICompletenessDiffPageRepository.GetPageAsync(
            string projectId,
            int moneyScale,
            string periodStart,
            string periodEnd,
            PageRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PageResult<CompletenessDiffAccount>(
                CompletenessRows ?? [],
                null));

        public Task<PageResult<long>> GetEntryIdsPageAsync(
            string projectId,
            string periodStart,
            string periodEnd,
            PageRequest request,
            CancellationToken cancellationToken)
        {
            UnbalancedPageCalls++;
            ThrowIfForbidden();
            return Task.FromResult(new PageResult<long>([901], null));
        }

        public Task<PageResult<NullRecordRow>> GetPageAsync(
            string projectId,
            NullRecordCategory category,
            string periodStart,
            string periodEnd,
            PageRequest request,
            CancellationToken cancellationToken)
        {
            NullPageCalls++;
            ThrowIfForbidden();
            if (NullRows is not null)
            {
                return Task.FromResult(new PageResult<NullRecordRow>(NullRows, null));
            }
            var entryId = 100 + (long)category;
            return Task.FromResult(new PageResult<NullRecordRow>(
                [new NullRecordRow("DOC-1", "1101", "2025-03-01", "row", false, false, false, false, entryId)],
                null));
        }

        public Task<IReadOnlyDictionary<long, string>> FetchJsonByEntryIdsAsync(
            string projectId,
            IReadOnlyList<long> entryIds,
            CancellationToken cancellationToken)
        {
            RawRowCalls++;
            ThrowIfForbidden();
            if (RawRowsByEntryId is not null)
            {
                return Task.FromResult<IReadOnlyDictionary<long, string>>(
                    entryIds.ToDictionary(
                        entryId => entryId,
                        entryId => RawRowsByEntryId[entryId]));
            }
            return Task.FromResult<IReadOnlyDictionary<long, string>>(
                entryIds.ToDictionary(
                    entryId => entryId,
                    entryId => $"{{\"RAW_COLUMN\":\"row-{entryId}\"}}"));
        }

        public Task<ImportBatchInfo?> GetLatestBatchAsync(
            string projectId,
            DatasetKind kind,
            CancellationToken cancellationToken) =>
            Task.FromResult<ImportBatchInfo?>(new ImportBatchInfo(
                $"batch-{kind}",
                kind,
                kind == DatasetKind.Gl ? "gl-source.xlsx" : "tb-source.xlsx",
                DateTimeOffset.UnixEpoch,
                1,
                kind == DatasetKind.Gl ? GlColumns : ["TB_COLUMN"],
                []));

        public Task<CommittedMapping?> FindAsync(
            string projectId,
            DatasetKind kind,
            CancellationToken cancellationToken) =>
            Task.FromResult<CommittedMapping?>(new CommittedMapping(
                kind,
                kind == DatasetKind.Gl ? GlMapping : TbMapping,
                kind == DatasetKind.Gl ? GlAmountModeNames.Signed : TbChangeModeNames.Direct,
                $"batch-{kind}",
                DateTimeOffset.UnixEpoch));

        public Task<ImportBatchResult> ReplaceBatchAsync(
            string projectId,
            DatasetKind kind,
            ImportSourceDescriptor source,
            IReadOnlyList<string> columns,
            IAsyncEnumerable<StagingRow> rows,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ImportBatchResult> AppendToBatchAsync(
            string projectId,
            DatasetKind kind,
            ImportSourceDescriptor source,
            IReadOnlyList<string> columns,
            IAsyncEnumerable<StagingRow> rows,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(
            string projectId,
            CommittedMapping mapping,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PageResult<InfSampleRow>> GetPageAsync(
            string projectId,
            string runId,
            int moneyScale,
            PageRequest request,
            CancellationToken cancellationToken) =>
            InfRows is not null
                ? Task.FromResult(new PageResult<InfSampleRow>(InfRows, null))
                : throw new NotSupportedException();

        public Task<PageResult<PrescreenHitRow>> GetPageAsync(
            string projectId,
            string ruleKey,
            FilterRuleContext context,
            PageRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PrescreenHitCounts> GetCountsAsync(
            string projectId,
            string ruleKey,
            FilterRuleContext context,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PageResult<FilterHitRow>> GetPageAsync(
            string projectId,
            int scenarioPosition,
            int moneyScale,
            PageRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        Task<IReadOnlyList<CreatorSummaryExportRow>> ICreatorSummaryExportRepository.FetchAllAsync(
            string projectId,
            string periodStart,
            string periodEnd,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        Task<IReadOnlyList<AccountUsageExportRow>> IAccountUsageExportRepository.FetchAllAsync(
            string projectId,
            string periodStart,
            string periodEnd,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)>> GetCountsAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        Task<IReadOnlyList<LegacyFieldDefinition>> ILegacyFieldDefinitionFactsPort.ReadAsync(
            string projectId,
            DatasetKind kind,
            LegacyFieldDefinitionScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LegacyFieldDefinition>>(
                kind == DatasetKind.Gl && scope == LegacyFieldDefinitionScope.Target
                    ? TargetGlDefinitions
                    : []);

        Task<IReadOnlyList<ResultPageRdeValue>> IResultPageRdeValuesPort.ReadAsync(
            string projectId,
            IReadOnlyList<long> entryIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResultPageRdeValue>>([]);

        Task<PageResult<SourceQualityFindingRow>> ISourceQualityPageRepository.GetPageAsync(
            string projectId,
            PageRequest request,
            CancellationToken cancellationToken)
        {
            SourceQualityPageCalls++;
            return Task.FromResult(new PageResult<SourceQualityFindingRow>(
                SourceQualityRows ?? [],
                null));
        }

        private void ThrowIfForbidden()
        {
            if (ThrowOnValidationDetailRead)
            {
                throw new InvalidOperationException(
                    "Summary-only validation detail repositories must not be called.");
            }
        }
    }
}
