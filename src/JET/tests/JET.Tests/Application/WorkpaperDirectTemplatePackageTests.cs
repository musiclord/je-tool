using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class WorkpaperDirectTemplatePackageTests
{
    private static readonly string[] DynamicSheets =
    [
        WorkpaperSheetCatalog.Cover,
        WorkpaperSheetCatalog.Step1,
        WorkpaperSheetCatalog.Step11,
        WorkpaperSheetCatalog.Step12,
        WorkpaperSheetCatalog.Step13,
        WorkpaperSheetCatalog.Step2,
        WorkpaperSheetCatalog.Step3,
        WorkpaperSheetCatalog.Step4,
        WorkpaperSheetCatalog.Step41,
        WorkpaperSheetCatalog.FieldInfo,
        WorkpaperSheetCatalog.CalendarInfo,
        WorkpaperSheetCatalog.AccountMapping
    ];

    [Fact]
    public async Task FourteenSheetExport_ChangesOnlyDynamicWorksheetsAndStyles()
    {
        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            "WorkingPaper.xlsx");
        var templateBytes = await File.ReadAllBytesAsync(templatePath);
        Assert.Equal(
            "DBD81FC7C06028BA15149EB3F72BD64224D1F604683E9CDC152BA5686DC8A380",
            Convert.ToHexString(SHA256.HashData(templateBytes)));

        var outputBytes = await ExportFourteenSheetWorkingPaperAsync();
        WorkpaperPackageDiff.AssertAllowed(templateBytes, outputBytes);
        var evidencePath = Environment.GetEnvironmentVariable(
            "JET_WORKPAPER_DIRECT_EVIDENCE_PATH");
        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(Path.GetFullPath(evidencePath))!);
            await File.WriteAllBytesAsync(evidencePath, outputBytes);
        }

        var mutated = WorkpaperPackageDiff.MutateWorkbookPart(outputBytes);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => WorkpaperPackageDiff.AssertAllowed(templateBytes, mutated));
    }

    [Fact]
    public async Task Step13WithoutDifference_RemovesRelationshipClosureAndPreservesLocalDefinedNameOwners()
    {
        var templateBytes = await ReadTemplateBytesAsync();
        var outputBytes = await ExportWorkingPaperAsync(
            hasCompletenessDifference: false,
            includeReferenceContinuations: false,
            continuationRowLimit: null);

        WorkpaperPackageDiff.AssertWorksheetRemoval(
            templateBytes,
            outputBytes,
            WorkpaperSheetCatalog.Step13);
    }

    [Fact]
    public async Task ReferenceContinuations_AddCompleteTopologyAndPreserveUnauthorizedExistingParts()
    {
        var templateBytes = await ReadTemplateBytesAsync();
        var outputBytes = await ExportWorkingPaperAsync(
            hasCompletenessDifference: true,
            includeReferenceContinuations: true,
            continuationRowLimit: 25);

        WorkpaperPackageDiff.AssertReferenceContinuationTopology(
            templateBytes,
            outputBytes);
    }

    private static Task<byte[]> ExportFourteenSheetWorkingPaperAsync() =>
        ExportWorkingPaperAsync(
            hasCompletenessDifference: true,
            includeReferenceContinuations: false,
            continuationRowLimit: null);

    private static async Task<byte[]> ReadTemplateBytesAsync()
    {
        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            "WorkingPaper.xlsx");
        var templateBytes = await File.ReadAllBytesAsync(templatePath);
        Assert.Equal(
            "DBD81FC7C06028BA15149EB3F72BD64224D1F604683E9CDC152BA5686DC8A380",
            Convert.ToHexString(SHA256.HashData(templateBytes)));
        return templateBytes;
    }

    private static async Task<byte[]> ExportWorkingPaperAsync(
        bool hasCompletenessDifference,
        bool includeReferenceContinuations,
        uint? continuationRowLimit)
    {
        using var host = new HandlerTestHost();
        var holidays = includeReferenceContinuations
            ? Enumerable.Range(1, 18).Select(day => $"2025-01-{day:00}").ToArray()
            : null;
        var makeupDays = includeReferenceContinuations
            ? Enumerable.Range(1, 18).Select(day => $"2025-02-{day:00}").ToArray()
            : null;
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(
                "傳票號碼", "傳票日期", "核准日期", "科目代號",
                "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow(
                    "JV-001", "2025-03-05", "2026-01-02", "1101",
                    "現金", "調整分錄", "2000000.00", 1)
                .AddRow(
                    "JV-001", "2025-03-05", "2026-01-02", "4101",
                     "收入", null, "2000000.00", 0),
            lastPeriodStart: "2025-12-31",
            holidays: holidays,
            makeupDays: makeupDays,
            validateForDownstream: true);

        if (includeReferenceContinuations)
        {
            await ImportAccountMappingsAsync(host, rowCount: 27);
        }

        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = new[]
                {
                    new
                    {
                        name = "受控測試情境",
                        rationale = "原始摘要含調整",
                        groups = new[]
                        {
                            new
                            {
                                join = "AND",
                                rules = new[]
                                {
                                    new
                                    {
                                        join = "AND",
                                        type = "customKeywords",
                                        keywords = "調整"
                                    }
                                }
                            }
                        }
                    }
                }
            }));
        _ = await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationRunId = validation
                    .GetProperty("resultRef")
                    .GetProperty("runId")
                    .GetString(),
                prescreenRunId = prescreen
                    .GetProperty("resultRef")
                    .GetProperty("runId")
                    .GetString(),
                revision = committed
                    .GetProperty("resultRef")
                    .GetProperty("revision")
                    .GetString()
            }));

        if (hasCompletenessDifference || continuationRowLimit is not null)
        {
            var context = new WorkpaperContext(
                projectId,
                "測試自含案件",
                "2025-01-01",
                "2025-12-31",
                "2025-12-31",
                ProjectDocument.DefaultMoneyScale,
                validation.GetProperty("resultRef").GetProperty("runId").GetString()!,
                committed.GetProperty("resultRef").GetProperty("revision").GetString()!,
                [1]);
            await using var output = new MemoryStream();
            var stats = await BuildWriter(
                    host,
                    continuationRowLimit is uint rowLimit
                        ? new WorkpaperWriterOptions(rowLimit)
                        : new WorkpaperWriterOptions(),
                    hasCompletenessDifference)
                .WriteAsync(output, context, CancellationToken.None);
            Assert.Equal(output.Length, stats.BytesWritten);
            return output.ToArray();
        }

        var response = await host.DispatchAsync(
            "export.workpaperStream",
            JsonSerializer.Serialize(new
            {
                validationRunId = validation
                    .GetProperty("resultRef")
                    .GetProperty("runId")
                    .GetString(),
                prescreenRunId = prescreen
                    .GetProperty("resultRef")
                    .GetProperty("runId")
                    .GetString(),
                scenarioRevision = committed
                    .GetProperty("resultRef")
                    .GetProperty("revision")
                    .GetString(),
                scenarioPositions = new[] { 1 }
            }));
        var path = Path.Combine(
            host.ProjectsRoot,
            projectId,
            response.GetProperty("artifact").GetProperty("fileName").GetString()!);
        return await File.ReadAllBytesAsync(path);
    }

    private static WorkpaperWriter BuildWriter(
        HandlerTestHost host,
        WorkpaperWriterOptions options,
        bool hasCompletenessDifference)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        ICompletenessDiffPageRepository completenessDiffs = hasCompletenessDifference
            ? new FixedCompletenessDiffPageRepository()
            : new LocalCompletenessDiffPageRepository(database);
        return new WorkpaperWriter(
            new LocalCompletenessAccountPageRepository(database),
            completenessDiffs,
            new LocalDocBalancePageRepository(database),
            new LocalCreatorSummaryExportRepository(database),
            new LocalInfSamplePageRepository(database),
            new LocalFilterScenarioStore(database),
            new LocalTagMatrixScenariosRepository(database),
            new LocalTagMatrixVoucherPageRepository(database),
            new LocalTagMatrixRowPageRepository(database),
            new LocalMappingStateStore(database),
            new LocalCalendarExportRepository(database),
            new LocalAccountMappingExportRepository(database),
            options,
            rawRows: new LocalRawGlExportRepository(database),
            accountMappingStateStore: new LocalAccountMappingRepository(database));
    }

    private sealed class FixedCompletenessDiffPageRepository : ICompletenessDiffPageRepository
    {
        public Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
            string projectId,
            int moneyScale,
            string periodStart,
            string periodEnd,
            PageRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<CompletenessDiffAccount> rows =
            [
                new(
                    AccountCode: "1101",
                    AccountName: "現金",
                    TbAmountScaled: 19_000_000_000,
                    GlAmountScaled: 20_000_000_000,
                    DiffScaled: -1_000_000_000,
                    NotInTb: false)
            ];
            return Task.FromResult(new PageResult<CompletenessDiffAccount>(rows, null));
        }
    }

    private static async Task ImportAccountMappingsAsync(
        HandlerTestHost host,
        int rowCount)
    {
        var mappingFile = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "GL_NUMBER";
            sheet.Cell(1, 2).Value = "GL_NAME";
            sheet.Cell(1, 3).Value = "STANDARDIZED_ACCOUNT_NAME";
            for (var index = 1; index <= rowCount; index++)
            {
                sheet.Cell(index + 1, 1).Value = $"MAP-{index:000}";
                sheet.Cell(index + 1, 2).Value = $"續頁科目{index:000}";
                sheet.Cell(index + 1, 3).Value = "Cash";
            }
        });

        try
        {
            await host.DispatchAsync(
                "import.accountMapping.fromFile",
                JsonSerializer.Serialize(new
                {
                    filePath = mappingFile,
                    fileName = "workpaper-package-continuation-account-mapping.xlsx"
                }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(mappingFile);
        }
    }

    private static class WorkpaperPackageDiff
    {
        private static readonly XNamespace Spreadsheet =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace OfficeRelationships =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackageRelationships =
            "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace ContentTypes =
            "http://schemas.openxmlformats.org/package/2006/content-types";
        private const string WorksheetRelationshipType =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";
        private const string WorksheetContentType =
            "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
        internal static void AssertAllowed(byte[] templateBytes, byte[] outputBytes)
        {
            var template = PackageSnapshot.Capture(templateBytes);
            var output = PackageSnapshot.Capture(outputBytes);
            var coverEmbeddedDocumentParts = CoverEmbeddedDocumentParts(template);
            Assert.Equal(
                template.PartNames
                    .Except(coverEmbeddedDocumentParts, StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
                output.PartNames);

            var allowed = DynamicSheets
                .Select(template.ResolveWorksheetPart)
                .Concat(
                [
                    "[Content_Types].xml",
                    PackageSnapshot.RelationshipPartName(
                        template.ResolveWorksheetPart(WorkpaperSheetCatalog.Cover)),
                    "xl/sharedStrings.xml",
                    "xl/styles.xml"
                ])
                .ToHashSet(StringComparer.Ordinal);
            var changed = output.PartNames
                .Where(name => !template.Bytes(name).SequenceEqual(output.Bytes(name)))
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(
                allowed.SetEquals(changed),
                $"Changed package parts were: {string.Join(
                    ", ",
                    changed.Order(StringComparer.Ordinal))}");
        }

        internal static void AssertWorksheetRemoval(
            byte[] templateBytes,
            byte[] outputBytes,
            string removedSheetName)
        {
            AssertCanonicalMetadataWorksheet(outputBytes);
            var template = PackageSnapshot.Capture(templateBytes);
            var output = PackageSnapshot.Capture(outputBytes);
            var coverEmbeddedDocumentParts = CoverEmbeddedDocumentParts(template);
            var removedSheet = template.Worksheets.Single(sheet => string.Equals(
                sheet.Name,
                removedSheetName,
                StringComparison.Ordinal));

            var expectedRemoved = template.RelationshipClosure(removedSheet.PartName);
            expectedRemoved.Add(removedSheet.PartName);
            expectedRemoved.UnionWith(coverEmbeddedDocumentParts);
            var actualRemoved = template.PartNames
                .Except(output.PartNames, StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(
                expectedRemoved.SetEquals(actualRemoved),
                $"Removed WorkingPaper parts were: {string.Join(
                    ", ",
                    actualRemoved.Order(StringComparer.Ordinal))}");
            var metadataSheet = output.Worksheets.Single(sheet => string.Equals(
                sheet.Name,
                ReportWorkbookMetadataFormat.WorksheetName,
                StringComparison.Ordinal));
            Assert.Equal("xl/worksheets/sheet15.xml", metadataSheet.PartName);
            Assert.Equal(
                new[] { metadataSheet.PartName },
                output.PartNames.Except(template.PartNames, StringComparer.Ordinal));

            var expectedSheets = template.Worksheets
                .Where(sheet => !string.Equals(
                    sheet.Name,
                    removedSheetName,
                    StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(
                expectedSheets,
                output.Worksheets.Where(sheet => !string.Equals(
                    sheet.Name,
                    ReportWorkbookMetadataFormat.WorksheetName,
                    StringComparison.Ordinal)).ToArray());
            Assert.Equal(metadataSheet, output.Worksheets[^1]);

            var expectedRelationships = template.WorkbookRelationships
                .Where(relationship => !string.Equals(
                    relationship.Id,
                    removedSheet.RelationshipId,
                    StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(
                expectedRelationships,
                output.WorkbookRelationships.Where(relationship => !string.Equals(
                    relationship.Id,
                    metadataSheet.RelationshipId,
                    StringComparison.Ordinal)).ToArray());
            var metadataRelationship = output.WorkbookRelationships.Single(relationship =>
                string.Equals(
                    relationship.Id,
                    metadataSheet.RelationshipId,
                    StringComparison.Ordinal));
            Assert.Equal(WorksheetRelationshipType, metadataRelationship.Type);
            Assert.Null(metadataRelationship.TargetMode);

            AssertContentTypesAfterRemoval(
                template,
                output,
                expectedRemoved,
                metadataSheet.PartName);
            var expectedLocalNames = template.LocalDefinedNames
                .Where(name => !string.Equals(
                    name.Owner,
                    removedSheetName,
                    StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(expectedLocalNames, output.LocalDefinedNames);

            AssertWorksheetTopologyComplete(output);
            AssertExistingPartsUnchangedExcept(
                template,
                output,
                DynamicSheets
                    .Where(name => !string.Equals(name, removedSheetName, StringComparison.Ordinal))
                    .Select(template.ResolveWorksheetPart)
                    .Concat(
                    [
                        "[Content_Types].xml",
                        "xl/workbook.xml",
                        "xl/_rels/workbook.xml.rels",
                        PackageSnapshot.RelationshipPartName(
                            template.ResolveWorksheetPart(WorkpaperSheetCatalog.Cover)),
                        "xl/sharedStrings.xml",
                        "xl/styles.xml"
                    ]));
        }

        internal static void AssertReferenceContinuationTopology(
            byte[] templateBytes,
            byte[] outputBytes)
        {
            var template = PackageSnapshot.Capture(templateBytes);
            var output = PackageSnapshot.Capture(outputBytes);
            var coverEmbeddedDocumentParts = CoverEmbeddedDocumentParts(template);
            Assert.Equal(
                coverEmbeddedDocumentParts.Order(StringComparer.Ordinal),
                template.PartNames
                    .Except(output.PartNames, StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal));

            var calendarContinuations = output.ContinuationWorksheets(
                WorkpaperSheetCatalog.CalendarInfo);
            var accountMappingContinuations = output.ContinuationWorksheets(
                WorkpaperSheetCatalog.AccountMapping);
            Assert.NotEmpty(calendarContinuations);
            Assert.NotEmpty(accountMappingContinuations);
            var continuations = calendarContinuations
                .Concat(accountMappingContinuations)
                .ToArray();

            var templateSheetNames = template.Worksheets
                .Select(sheet => sheet.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Equal(
                continuations.Select(sheet => sheet.Name).Order(StringComparer.Ordinal),
                output.Worksheets
                    .Where(sheet => !templateSheetNames.Contains(sheet.Name))
                    .Select(sheet => sheet.Name)
                    .Order(StringComparer.Ordinal));

            var expectedAdded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var continuation in continuations)
            {
                expectedAdded.Add(continuation.PartName);
                expectedAdded.Add(PackageSnapshot.RelationshipPartName(continuation.PartName));

                var baseName = calendarContinuations.Contains(continuation)
                    ? WorkpaperSheetCatalog.CalendarInfo
                    : WorkpaperSheetCatalog.AccountMapping;
                var prototypePart = template.ResolveWorksheetPart(baseName);
                var expectedRelationships =
                    template.ResolvedRelationshipsForPart(prototypePart);
                Assert.NotEmpty(expectedRelationships);
                Assert.Equal(
                    expectedRelationships,
                    output.ResolvedRelationshipsForPart(continuation.PartName));
            }

            var actualAdded = output.PartNames
                .Except(template.PartNames, StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(
                expectedAdded.SetEquals(actualAdded),
                $"Added WorkingPaper parts were: {string.Join(
                    ", ",
                    actualAdded.Order(StringComparer.Ordinal))}");

            var retainedOutputSheets = output.Worksheets
                .Where(sheet => templateSheetNames.Contains(sheet.Name))
                .ToArray();
            Assert.Equal(template.Worksheets, retainedOutputSheets);
            var templateRelationshipIds = template.WorkbookRelationships
                .Select(relationship => relationship.Id)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Equal(
                template.WorkbookRelationships,
                output.WorkbookRelationships
                    .Where(relationship => templateRelationshipIds.Contains(relationship.Id))
                    .ToArray());
            Assert.Equal(
                continuations.Select(sheet => sheet.RelationshipId).Order(StringComparer.Ordinal),
                output.WorkbookRelationships
                    .Where(relationship => !templateRelationshipIds.Contains(relationship.Id))
                    .Select(relationship => relationship.Id)
                    .Order(StringComparer.Ordinal));

            AssertContentTypesForAddedWorksheets(
                template,
                output,
                continuations.Select(sheet => sheet.PartName));
            AssertWorksheetTopologyComplete(output);
            AssertExistingPartsUnchangedExcept(
                template,
                output,
                DynamicSheets
                    .Select(template.ResolveWorksheetPart)
                    .Concat(
                    [
                        "[Content_Types].xml",
                        "xl/workbook.xml",
                        "xl/_rels/workbook.xml.rels",
                        PackageSnapshot.RelationshipPartName(
                            template.ResolveWorksheetPart(WorkpaperSheetCatalog.Cover)),
                        "xl/sharedStrings.xml",
                        "xl/styles.xml"
                    ]));
        }

        private static void AssertContentTypesAfterRemoval(
            PackageSnapshot template,
            PackageSnapshot output,
            IReadOnlySet<string> removedParts,
            string metadataPart)
        {
            Assert.Equal(
                Tokens(template.ContentTypeDefaults),
                Tokens(output.ContentTypeDefaults));
            var expectedOverrides = template.ContentTypeOverrides
                .Where(pair => !removedParts.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            expectedOverrides.Add(metadataPart, WorksheetContentType);
            Assert.Equal(
                Tokens(expectedOverrides),
                Tokens(output.ContentTypeOverrides));
            output.AssertContentTypeOverridesResolve();
        }

        private static void AssertCanonicalMetadataWorksheet(byte[] outputBytes)
        {
            using var stream = new MemoryStream(outputBytes, writable: false);
            using var document = SpreadsheetDocument.Open(stream, false);
            Assert.Empty(new OpenXmlValidator().Validate(document));
            var workbookPart = document.WorkbookPart
                ?? throw new InvalidDataException("WorkingPaper output has no workbook part.");
            var metadataSheet = workbookPart.Workbook.Sheets!.Elements<Sheet>().Single(sheet =>
                string.Equals(
                    sheet.Name?.Value,
                    ReportWorkbookMetadataFormat.WorksheetName,
                    StringComparison.Ordinal));
            Assert.Equal(SheetStateValues.VeryHidden, metadataSheet.State?.Value);

            var worksheet = Assert.IsType<WorksheetPart>(
                workbookPart.GetPartById(metadataSheet.Id!.Value!)).Worksheet;
            var rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToArray();
            Assert.NotEmpty(rows);
            Assert.Equal(ReportWorkbookMetadataFormat.Marker, MetadataCellText(rows[0], "A1"));
            Assert.Equal(
                ReportWorkbookMetadataFormat.CurrentVersion.ToString(CultureInfo.InvariantCulture),
                MetadataCellText(rows[0], "B1"));

            var chunkCount = int.Parse(
                MetadataCellText(rows[0], "C1"),
                CultureInfo.InvariantCulture);
            Assert.True(chunkCount > 0);
            Assert.Equal(chunkCount + 1, rows.Length);
            var chunks = new string[chunkCount];
            for (var index = 0; index < chunkCount; index++)
            {
                var row = rows[index + 1];
                var rowIndex = checked((uint)index + 2U);
                Assert.Equal(rowIndex, row.RowIndex?.Value);
                Assert.True(row.Hidden?.Value ?? false);
                Assert.Equal(
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    MetadataCellText(row, $"A{rowIndex}"));
                chunks[index] = MetadataCellText(row, $"B{rowIndex}");
                Assert.InRange(
                    chunks[index].Length,
                    1,
                    ReportWorkbookMetadataFormat.MaximumChunkLength);
                if (index < chunkCount - 1)
                {
                    Assert.Equal(
                        ReportWorkbookMetadataFormat.MaximumChunkLength,
                        chunks[index].Length);
                }
            }

            using var payload = JsonDocument.Parse(string.Concat(chunks));
            Assert.Equal(
                ReportWorkbookMetadataFormat.CurrentVersion,
                payload.RootElement.GetProperty("formatVersion").GetInt32());
        }

        private static string MetadataCellText(Row row, string reference)
        {
            var cell = row.Elements<Cell>().Single(cell => string.Equals(
                cell.CellReference?.Value,
                reference,
                StringComparison.Ordinal));
            return cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? string.Empty;
        }

        private static void AssertContentTypesForAddedWorksheets(
            PackageSnapshot template,
            PackageSnapshot output,
            IEnumerable<string> addedWorksheetParts)
        {
            var added = addedWorksheetParts.ToHashSet(StringComparer.Ordinal);
            var coverEmbeddedDocumentParts = CoverEmbeddedDocumentParts(template);
            Assert.Equal(
                Tokens(template.ContentTypeDefaults),
                Tokens(output.ContentTypeDefaults));
            Assert.Equal(
                Tokens(template.ContentTypeOverrides
                    .Where(pair => !coverEmbeddedDocumentParts.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)),
                Tokens(output.ContentTypeOverrides
                    .Where(pair => !added.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
            Assert.Equal(
                added.Order(StringComparer.Ordinal),
                output.ContentTypeOverrides.Keys
                    .Except(
                        template.ContentTypeOverrides.Keys
                            .Where(key => !coverEmbeddedDocumentParts.Contains(key)),
                        StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal));
            Assert.All(
                added,
                part => Assert.Equal(WorksheetContentType, output.ContentTypeOverrides[part]));
            output.AssertContentTypeOverridesResolve();
        }

        private static IReadOnlySet<string> CoverEmbeddedDocumentParts(
            PackageSnapshot template) =>
            template.RelationshipClosureForWorksheetElements(
                WorkpaperSheetCatalog.Cover,
                "drawing",
                "legacyDrawing",
                "oleObjects");

        private static void AssertWorksheetTopologyComplete(PackageSnapshot snapshot)
        {
            Assert.Equal(
                snapshot.Worksheets.Count,
                snapshot.Worksheets.Select(sheet => sheet.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(
                snapshot.Worksheets.Count,
                snapshot.Worksheets.Select(sheet => sheet.RelationshipId).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(
                snapshot.Worksheets.Count,
                snapshot.Worksheets.Select(sheet => sheet.PartName).Distinct(StringComparer.Ordinal).Count());

            var worksheetRelationships = snapshot.WorkbookRelationships
                .Where(relationship => string.Equals(
                    relationship.Type,
                    WorksheetRelationshipType,
                    StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(
                snapshot.Worksheets.Select(sheet => sheet.RelationshipId).Order(StringComparer.Ordinal),
                worksheetRelationships.Select(relationship => relationship.Id).Order(StringComparer.Ordinal));
            foreach (var sheet in snapshot.Worksheets)
            {
                Assert.Contains(sheet.PartName, snapshot.PartNames);
                Assert.Equal(WorksheetContentType, snapshot.ContentTypeForPart(sheet.PartName));
            }
            snapshot.AssertInternalRelationshipTargetsResolve();
            snapshot.AssertContentTypeOverridesResolve();
        }

        private static void AssertExistingPartsUnchangedExcept(
            PackageSnapshot template,
            PackageSnapshot output,
            IEnumerable<string> allowedChangedParts)
        {
            var allowed = allowedChangedParts.ToHashSet(StringComparer.Ordinal);
            var unauthorized = template.PartNames
                .Intersect(output.PartNames, StringComparer.Ordinal)
                .Where(name => !allowed.Contains(name))
                .Where(name => !template.Bytes(name).SequenceEqual(output.Bytes(name)))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.True(
                unauthorized.Length == 0,
                $"Unauthorized existing WorkingPaper parts changed: {string.Join(", ", unauthorized)}");
        }

        private static string[] Tokens(IReadOnlyDictionary<string, string> values) =>
            values
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}\u001f{pair.Value}")
                .ToArray();

        internal static byte[] MutateWorkbookPart(byte[] packageBytes)
        {
            using var stream = new MemoryStream();
            stream.Write(packageBytes);
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
            {
                var entry = archive.GetEntry("xl/workbook.xml")
                    ?? throw new InvalidDataException("WorkingPaper workbook part is missing.");
                XDocument workbook;
                using (var input = entry.Open())
                {
                    workbook = XDocument.Load(input, LoadOptions.PreserveWhitespace);
                }
                entry.Delete();
                workbook.Root!.SetAttributeValue(
                    "unauthorizedDirectTemplateProbe",
                    "changed");
                var replacement = archive.CreateEntry("xl/workbook.xml");
                using var output = replacement.Open();
                workbook.Save(output, SaveOptions.DisableFormatting);
            }
            return stream.ToArray();
        }

        internal sealed record WorksheetDescriptor(
            string Name,
            string SheetId,
            string RelationshipId,
            string PartName);

        internal sealed record PackageRelationshipDescriptor(
            string Id,
            string Type,
            string Target,
            string? TargetMode);

        internal sealed record LocalDefinedNameDescriptor(
            string Name,
            string Formula,
            string Owner);

        private sealed class PackageSnapshot
        {
            private readonly IReadOnlyDictionary<string, byte[]> _parts;

            private PackageSnapshot(IReadOnlyDictionary<string, byte[]> parts)
            {
                _parts = parts;
                PartNames = parts.Keys.Order(StringComparer.Ordinal).ToArray();
            }

            internal IReadOnlyList<string> PartNames { get; }

            internal byte[] Bytes(string name) => _parts[name];

            internal IReadOnlyList<WorksheetDescriptor> Worksheets =>
                ReadWorksheets();

            internal IReadOnlyList<PackageRelationshipDescriptor> WorkbookRelationships =>
                ReadRelationships("xl/workbook.xml");

            internal IReadOnlyList<LocalDefinedNameDescriptor> LocalDefinedNames =>
                ReadLocalDefinedNames();

            internal IReadOnlyDictionary<string, string> ContentTypeDefaults =>
                Xml("[Content_Types].xml")
                    .Root!
                    .Elements(ContentTypes + "Default")
                    .ToDictionary(
                        element => (string?)element.Attribute("Extension")
                            ?? throw new InvalidDataException(
                                "WorkingPaper content-type default has no extension."),
                        element => (string?)element.Attribute("ContentType")
                            ?? throw new InvalidDataException(
                                "WorkingPaper content-type default has no content type."),
                        StringComparer.OrdinalIgnoreCase);

            internal IReadOnlyDictionary<string, string> ContentTypeOverrides =>
                Xml("[Content_Types].xml")
                    .Root!
                    .Elements(ContentTypes + "Override")
                    .ToDictionary(
                        element => NormalizePartName(
                            (string?)element.Attribute("PartName")
                            ?? throw new InvalidDataException(
                                "WorkingPaper content-type override has no part name.")),
                        element => (string?)element.Attribute("ContentType")
                            ?? throw new InvalidDataException(
                                "WorkingPaper content-type override has no content type."),
                        StringComparer.Ordinal);

            internal string ResolveWorksheetPart(string sheetName) =>
                Worksheets.Single(sheet => string.Equals(
                    sheet.Name,
                    sheetName,
                    StringComparison.Ordinal)).PartName;

            internal IReadOnlyList<WorksheetDescriptor> ContinuationWorksheets(string baseName)
            {
                var byName = Worksheets.ToDictionary(
                    sheet => sheet.Name,
                    StringComparer.Ordinal);
                var result = new List<WorksheetDescriptor>();
                for (var pageNumber = 2; ; pageNumber++)
                {
                    var name = ExcelWorksheetConstraints.ContinuationSheetName(
                        baseName,
                        pageNumber);
                    if (!byName.TryGetValue(name, out var sheet))
                    {
                        break;
                    }
                    result.Add(sheet);
                }
                return result;
            }

            internal IReadOnlyList<PackageRelationshipDescriptor> RelationshipsForPart(
                string sourcePart) =>
                ReadRelationships(sourcePart);

            internal IReadOnlyList<PackageRelationshipDescriptor>
                ResolvedRelationshipsForPart(string sourcePart) =>
                ReadRelationships(sourcePart)
                    .Select(relationship => string.Equals(
                            relationship.TargetMode,
                            "External",
                            StringComparison.OrdinalIgnoreCase)
                        ? relationship
                        : relationship with
                        {
                            Target = ResolveTarget(sourcePart, relationship.Target)
                        })
                    .ToArray();

            internal HashSet<string> RelationshipClosure(string rootPart)
            {
                var result = new HashSet<string>(StringComparer.Ordinal);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var pending = new Queue<string>();
                pending.Enqueue(rootPart);
                while (pending.TryDequeue(out var sourcePart))
                {
                    if (!visited.Add(sourcePart))
                    {
                        continue;
                    }

                    var relationshipPart = RelationshipPartName(sourcePart);
                    if (!_parts.ContainsKey(relationshipPart))
                    {
                        continue;
                    }
                    result.Add(relationshipPart);
                    foreach (var relationship in ReadRelationships(sourcePart)
                                 .Where(relationship => !string.Equals(
                                     relationship.TargetMode,
                                     "External",
                                     StringComparison.OrdinalIgnoreCase)))
                    {
                        var target = ResolveTarget(sourcePart, relationship.Target);
                        if (result.Add(target))
                        {
                            pending.Enqueue(target);
                        }
                    }
                }
                return result;
            }

            internal HashSet<string> RelationshipClosureForWorksheetElements(
                string sheetName,
                params string[] elementLocalNames)
            {
                var sourcePart = ResolveWorksheetPart(sheetName);
                var localNames = elementLocalNames.ToHashSet(StringComparer.Ordinal);
                var relationshipIds = Xml(sourcePart).Root!
                    .Descendants()
                    .Where(element => localNames.Contains(element.Name.LocalName))
                    .SelectMany(element => element.DescendantsAndSelf())
                    .Attributes(OfficeRelationships + "id")
                    .Select(attribute => attribute.Value)
                    .ToHashSet(StringComparer.Ordinal);
                if (relationshipIds.Count == 0)
                {
                    throw new InvalidDataException(
                        "WorkingPaper cover has no embedded-document relationship IDs.");
                }

                var relationships = ReadRelationships(sourcePart)
                    .ToDictionary(relationship => relationship.Id, StringComparer.Ordinal);
                var result = new HashSet<string>(StringComparer.Ordinal);
                foreach (var relationshipId in relationshipIds)
                {
                    if (!relationships.TryGetValue(relationshipId, out var relationship)
                        || string.Equals(
                            relationship.TargetMode,
                            "External",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "WorkingPaper cover embedded-document relationship is invalid.");
                    }
                    var target = ResolveTarget(sourcePart, relationship.Target);
                    result.Add(target);
                    result.UnionWith(RelationshipClosure(target));
                }
                return result;
            }

            internal string ContentTypeForPart(string partName)
            {
                if (ContentTypeOverrides.TryGetValue(partName, out var contentType))
                {
                    return contentType;
                }

                var extension = Path.GetExtension(partName).TrimStart('.');
                if (ContentTypeDefaults.TryGetValue(extension, out contentType))
                {
                    return contentType;
                }
                throw new InvalidDataException(
                    $"WorkingPaper part '{partName}' has no content type.");
            }

            internal void AssertContentTypeOverridesResolve()
            {
                foreach (var partName in ContentTypeOverrides.Keys)
                {
                    Assert.True(
                        _parts.ContainsKey(partName),
                        $"Content-type override points to missing part '{partName}'.");
                }
            }

            internal void AssertInternalRelationshipTargetsResolve()
            {
                foreach (var relationshipPart in PartNames.Where(
                             name => name.EndsWith(".rels", StringComparison.Ordinal)))
                {
                    var sourcePart = SourcePartForRelationshipPart(relationshipPart);
                    foreach (var relationship in ReadRelationshipPart(relationshipPart)
                                 .Where(relationship => !string.Equals(
                                     relationship.TargetMode,
                                     "External",
                                     StringComparison.OrdinalIgnoreCase)))
                    {
                        var target = ResolveTarget(sourcePart, relationship.Target);
                        Assert.True(
                            _parts.ContainsKey(target),
                            $"Relationship '{relationshipPart}#{relationship.Id}' points to missing part '{target}'.");
                    }
                }
            }

            internal static PackageSnapshot Capture(byte[] bytes)
            {
                using var input = new MemoryStream(bytes, writable: false);
                using var archive = new ZipArchive(input, ZipArchiveMode.Read);
                var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var entry in archive.Entries.Where(
                             entry => !string.IsNullOrEmpty(entry.Name)))
                {
                    using var part = entry.Open();
                    using var copy = new MemoryStream();
                    part.CopyTo(copy);
                    parts.Add(entry.FullName.Replace('\\', '/'), copy.ToArray());
                }
                return new PackageSnapshot(parts);
            }

            internal static string RelationshipPartName(string partName)
            {
                var separator = partName.LastIndexOf('/');
                var directory = separator < 0 ? string.Empty : partName[..separator];
                var fileName = separator < 0 ? partName : partName[(separator + 1)..];
                return string.IsNullOrEmpty(directory)
                    ? $"_rels/{fileName}.rels"
                    : $"{directory}/_rels/{fileName}.rels";
            }

            private IReadOnlyList<WorksheetDescriptor> ReadWorksheets()
            {
                var relationships = WorkbookRelationships.ToDictionary(
                    relationship => relationship.Id,
                    StringComparer.Ordinal);
                return Xml("xl/workbook.xml")
                    .Descendants(Spreadsheet + "sheet")
                    .Select(sheet =>
                    {
                        var name = (string?)sheet.Attribute("name")
                            ?? throw new InvalidDataException(
                                "WorkingPaper worksheet has no name.");
                        var sheetId = (string?)sheet.Attribute("sheetId")
                            ?? throw new InvalidDataException(
                                $"WorkingPaper worksheet '{name}' has no sheet id.");
                        var relationshipId =
                            (string?)sheet.Attribute(OfficeRelationships + "id")
                            ?? throw new InvalidDataException(
                                $"WorkingPaper worksheet '{name}' has no relationship id.");
                        if (!relationships.TryGetValue(
                                relationshipId,
                                out var relationship))
                        {
                            throw new InvalidDataException(
                                $"WorkingPaper worksheet '{name}' has no workbook relationship.");
                        }
                        if (!string.Equals(
                                relationship.Type,
                                WorksheetRelationshipType,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                $"WorkingPaper worksheet '{name}' relationship is not a worksheet.");
                        }
                        return new WorksheetDescriptor(
                            name,
                            sheetId,
                            relationshipId,
                            ResolveTarget("xl/workbook.xml", relationship.Target));
                    })
                    .ToArray();
            }

            private IReadOnlyList<LocalDefinedNameDescriptor> ReadLocalDefinedNames()
            {
                var sheets = Worksheets;
                return Xml("xl/workbook.xml")
                    .Descendants(Spreadsheet + "definedName")
                    .Where(element => element.Attribute("localSheetId") is not null)
                    .Select(element =>
                    {
                        var rawIndex = (string?)element.Attribute("localSheetId");
                        if (!uint.TryParse(rawIndex, out var index) || index >= sheets.Count)
                        {
                            throw new InvalidDataException(
                                $"WorkingPaper local defined name '{(string?)element.Attribute("name")}' has an invalid owner.");
                        }
                        return new LocalDefinedNameDescriptor(
                            (string?)element.Attribute("name")
                                ?? throw new InvalidDataException(
                                    "WorkingPaper local defined name has no name."),
                            element.Value,
                            sheets[checked((int)index)].Name);
                    })
                    .OrderBy(name => name.Name, StringComparer.Ordinal)
                    .ThenBy(name => name.Owner, StringComparer.Ordinal)
                    .ThenBy(name => name.Formula, StringComparer.Ordinal)
                    .ToArray();
            }

            private IReadOnlyList<PackageRelationshipDescriptor> ReadRelationships(
                string sourcePart)
            {
                var relationshipPart = RelationshipPartName(sourcePart);
                return _parts.ContainsKey(relationshipPart)
                    ? ReadRelationshipPart(relationshipPart)
                    : [];
            }

            private IReadOnlyList<PackageRelationshipDescriptor> ReadRelationshipPart(
                string relationshipPart) =>
                Xml(relationshipPart)
                    .Root!
                    .Elements(PackageRelationships + "Relationship")
                    .Select(element => new PackageRelationshipDescriptor(
                        (string?)element.Attribute("Id")
                            ?? throw new InvalidDataException(
                                $"WorkingPaper relationship part '{relationshipPart}' has an entry without id."),
                        (string?)element.Attribute("Type")
                            ?? throw new InvalidDataException(
                                $"WorkingPaper relationship part '{relationshipPart}' has an entry without type."),
                        (string?)element.Attribute("Target")
                            ?? throw new InvalidDataException(
                                $"WorkingPaper relationship part '{relationshipPart}' has an entry without target."),
                        (string?)element.Attribute("TargetMode")))
                    .OrderBy(relationship => relationship.Id, StringComparer.Ordinal)
                    .ToArray();

            private static string SourcePartForRelationshipPart(string relationshipPart)
            {
                if (string.Equals(
                        relationshipPart,
                        "_rels/.rels",
                        StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                const string marker = "/_rels/";
                var markerIndex = relationshipPart.LastIndexOf(
                    marker,
                    StringComparison.Ordinal);
                if (markerIndex < 0
                    || !relationshipPart.EndsWith(".rels", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Invalid package relationship part name '{relationshipPart}'.");
                }
                var directory = relationshipPart[..markerIndex];
                var fileName = relationshipPart[
                    (markerIndex + marker.Length)..(relationshipPart.Length - ".rels".Length)];
                return $"{directory}/{fileName}";
            }

            private static string ResolveTarget(string sourcePart, string target)
            {
                var baseUri = string.IsNullOrEmpty(sourcePart)
                    ? new Uri("https://package.invalid/")
                    : new Uri($"https://package.invalid/{sourcePart}");
                return NormalizePartName(
                    Uri.UnescapeDataString(new Uri(baseUri, target).AbsolutePath));
            }

            private static string NormalizePartName(string partName) =>
                partName.Replace('\\', '/').TrimStart('/');

            private XDocument Xml(string name)
            {
                using var input = new MemoryStream(_parts[name], writable: false);
                return XDocument.Load(input, LoadOptions.PreserveWhitespace);
            }
        }
    }
}
