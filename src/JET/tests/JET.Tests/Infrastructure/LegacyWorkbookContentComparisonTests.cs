using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyWorkbookContentComparisonTests
{
    [Fact]
    public void Read_EquatesSharedInlineStringsAndNumberLexemesAcrossStorageForms()
    {
        using var directory = new TempDirectory();
        var left = Path.Combine(directory.Path, "left.xlsx");
        var right = Path.Combine(directory.Path, "right.xlsx");
        WriteWorkbook(left, workbook => workbook.Sheet("Data", sheet =>
        {
            sheet.Row(
                InlineStringCell("A1", "alpha"),
                NumberCell("B1", "1230.00"));
        }));
        WriteWorkbook(right, workbook => workbook.Sheet("Data", sheet =>
        {
            sheet.Row(
                SharedStringCell("A1", 0),
                NumberCell("B1", "1.23E+03"));
        }, sharedStrings: ["alpha"]));

        var leftContent = LegacyAuditParityNormalizedWorkbookReader.Read(
            "validation-report",
            left,
            excludeJetMetadataSheet: false);
        var rightContent = LegacyAuditParityNormalizedWorkbookReader.Read(
            "validation-report",
            right,
            excludeJetMetadataSheet: false);

        Assert.Equal(leftContent.ContentDigest(), rightContent.ContentDigest());
        Assert.Empty(LegacyAuditParityNormalizedContentComparator
            .Compare(leftContent, rightContent)
            .Differences);
    }

    [Fact]
    public void Compare_DirectFamily_SeparatesRowCountValuesAndSequenceDimensions()
    {
        using var directory = new TempDirectory();
        var expectedPath = Path.Combine(directory.Path, "expected.xlsx");
        var reorderedPath = Path.Combine(directory.Path, "reordered.xlsx");
        var editedPath = Path.Combine(directory.Path, "edited.xlsx");
        var appendedPath = Path.Combine(directory.Path, "appended.xlsx");
        WriteWorkbook(expectedPath, workbook => workbook.Sheet("V_Report 3", sheet =>
        {
            sheet.Row(InlineStringCell("A1", "H1"), InlineStringCell("B1", "H2"));
            sheet.Row(InlineStringCell("A2", "first"), InlineStringCell("B2", "x"));
            sheet.Row(InlineStringCell("A3", "second"), InlineStringCell("B3", "y"));
        }));
        WriteWorkbook(reorderedPath, workbook => workbook.Sheet("V_Report 3", sheet =>
        {
            sheet.Row(InlineStringCell("A1", "H1"), InlineStringCell("B1", "H2"));
            sheet.Row(InlineStringCell("A2", "second"), InlineStringCell("B2", "y"));
            sheet.Row(InlineStringCell("A3", "first"), InlineStringCell("B3", "x"));
        }));
        WriteWorkbook(editedPath, workbook => workbook.Sheet("V_Report 3", sheet =>
        {
            sheet.Row(InlineStringCell("A1", "H1"), InlineStringCell("B1", "H2"));
            sheet.Row(InlineStringCell("A2", "first"), InlineStringCell("B2", "x"));
            sheet.Row(InlineStringCell("A3", "changed"), InlineStringCell("B3", "y"));
        }));
        WriteWorkbook(appendedPath, workbook => workbook.Sheet("V_Report 3", sheet =>
        {
            sheet.Row(InlineStringCell("A1", "H1"), InlineStringCell("B1", "H2"));
            sheet.Row(InlineStringCell("A2", "first"), InlineStringCell("B2", "x"));
            sheet.Row(InlineStringCell("A3", "second"), InlineStringCell("B3", "y"));
            sheet.Row(InlineStringCell("A4", "third"), InlineStringCell("B4", "z"));
        }));

        var expected = Read(expectedPath);
        var reordered = LegacyAuditParityNormalizedContentComparator.Compare(
            expected,
            Read(reorderedPath));
        var edited = LegacyAuditParityNormalizedContentComparator.Compare(
            expected,
            Read(editedPath));
        var appended = LegacyAuditParityNormalizedContentComparator.Compare(
            expected,
            Read(appendedPath));

        Assert.Contains(reordered.Differences, difference =>
            difference.Dimension == LegacyAuditParityContentDimension.RowValueSequence
            && difference.DifferenceCount == 2);
        Assert.DoesNotContain(reordered.Differences, difference =>
            difference.Dimension == LegacyAuditParityContentDimension.RowValues);
        Assert.Contains(edited.Differences, difference =>
            difference.Dimension == LegacyAuditParityContentDimension.RowValues
            && difference.DifferenceCount == 1);
        Assert.Contains(appended.Differences, difference =>
            difference.Dimension == LegacyAuditParityContentDimension.RowCount
            && difference.DifferenceCount == 1
            && difference.SheetName == "V_Report 3");
        Assert.Contains(appended.Differences, difference =>
            difference.Dimension == LegacyAuditParityContentDimension.RowValues
            && difference.DifferenceCount == 2);
    }

    [Fact]
    public void Compare_DirectFamily_AlignsColumnsByNameAndIsolatesExclusiveColumns()
    {
        using var directory = new TempDirectory();
        var expectedPath = Path.Combine(directory.Path, "expected.xlsx");
        var swappedPath = Path.Combine(directory.Path, "swapped.xlsx");
        var renamedPath = Path.Combine(directory.Path, "renamed.xlsx");
        WriteWorkbook(expectedPath, workbook => workbook.Sheet("V_Report 3", sheet =>
        {
            sheet.Row(InlineStringCell("A1", "H1"), InlineStringCell("B1", "H2"));
            sheet.Row(InlineStringCell("A2", "first"), InlineStringCell("B2", "x"));
        }));
        WriteWorkbook(swappedPath, workbook => workbook.Sheet("V_Report 3", sheet =>
        {
            sheet.Row(InlineStringCell("A1", "H2"), InlineStringCell("B1", "H1"));
            sheet.Row(InlineStringCell("A2", "x"), InlineStringCell("B2", "first"));
        }));
        WriteWorkbook(renamedPath, workbook => workbook.Sheet("V_Report 3", sheet =>
        {
            sheet.Row(InlineStringCell("A1", "H1"), InlineStringCell("B1", "H9"));
            sheet.Row(InlineStringCell("A2", "first"), InlineStringCell("B2", "x"));
        }));

        var expected = Read(expectedPath);
        var swapped = LegacyAuditParityNormalizedContentComparator.Compare(
            expected,
            Read(swappedPath));
        var renamed = LegacyAuditParityNormalizedContentComparator.Compare(
            expected,
            Read(renamedPath));

        // 欄序互換：ColumnHeaders 記 1（僅順序），共有欄值以欄名對齊 → 無值差異。
        Assert.Contains(swapped.Differences, difference =>
            difference.Dimension == LegacyAuditParityContentDimension.ColumnHeaders
            && difference.DifferenceCount == 1);
        Assert.DoesNotContain(swapped.Differences, difference =>
            difference.Dimension == LegacyAuditParityContentDimension.RowValues);
        // 欄名改名：ColumnHeaders 記 2（雙側各一），共有欄 H1 值相等 → 無值差異。
        Assert.Contains(renamed.Differences, difference =>
            difference.Dimension == LegacyAuditParityContentDimension.ColumnHeaders
            && difference.DifferenceCount == 2);
        Assert.DoesNotContain(renamed.Differences, difference =>
            difference.Dimension == LegacyAuditParityContentDimension.RowValues);
    }

    [Theory]
    [InlineData("text", "4,353,170.00", "4353170")]
    [InlineData("text", "-50", "-50")]
    [InlineData("text", "+50", "50")]
    [InlineData("text", "-", "0")]
    [InlineData("text", "2026/1/2", "2026-01-02")]
    [InlineData("text", "2026.01.02", "2026-01-02")]
    [InlineData("text", "114/6/11", "2025-06-11")]
    [InlineData("text", "20260611", "20260611")]
    [InlineData("text", "1140611", "1140611")]
    [InlineData("text", "note-123", "note-123")]
    [InlineData("text", "NT$100", "NT$100")]
    [InlineData("text", "11/05/06", "11/05/06")]
    [InlineData("number", "45123", "45123")]
    public void NormalizeSemanticValue_AppliesOnlyDocumentedImportFormats(
        string kind,
        string value,
        string expected)
    {
        Assert.Equal(
            expected,
            LegacyAuditParityNormalizedWorkbookReader.NormalizeSemanticValue(kind, value));
    }

    [Fact]
    public void Compare_TreatsLegacyZeroForBlankAsStorageOnlyDifference()
    {
        using var directory = new TempDirectory();
        var zeroPath = Path.Combine(directory.Path, "zero-stored.xlsx");
        var blankPath = Path.Combine(directory.Path, "blank-stored.xlsx");
        WriteWorkbook(zeroPath, workbook => workbook.Sheet("Data", sheet =>
        {
            sheet.Row(InlineStringCell("A1", "alpha"), NumberCell("B1", "0"));
        }));
        WriteWorkbook(blankPath, workbook => workbook.Sheet("Data", sheet =>
        {
            sheet.Row(InlineStringCell("A1", "alpha"));
        }));

        var comparison = LegacyAuditParityNormalizedContentComparator.Compare(
            Read(zeroPath),
            Read(blankPath));

        var storage = Assert.Single(comparison.Differences);
        Assert.Equal(LegacyAuditParityContentDimension.CellStorage, storage.Dimension);
    }

    [Fact]
    public void Compare_SeparatesStorageKindFromValueDifferences()
    {
        using var directory = new TempDirectory();
        var textPath = Path.Combine(directory.Path, "text-stored.xlsx");
        var numberPath = Path.Combine(directory.Path, "number-stored.xlsx");
        WriteWorkbook(textPath, workbook => workbook.Sheet("Data", sheet =>
        {
            sheet.Row(InlineStringCell("A1", "123.45"), InlineStringCell("B1", "note"));
        }));
        WriteWorkbook(numberPath, workbook => workbook.Sheet("Data", sheet =>
        {
            sheet.Row(NumberCell("A1", "123.45"), InlineStringCell("B1", "note"));
        }));

        var comparison = LegacyAuditParityNormalizedContentComparator.Compare(
            Read(textPath),
            Read(numberPath));

        var storage = Assert.Single(comparison.Differences);
        Assert.Equal(LegacyAuditParityContentDimension.CellStorage, storage.Dimension);
        Assert.Equal(2, storage.DifferenceCount);
    }

    [Fact]
    public void Compare_ReportsWorksheetSetAndOrderAtWorkbookScope()
    {
        using var directory = new TempDirectory();
        var expectedPath = Path.Combine(directory.Path, "expected.xlsx");
        var extraPath = Path.Combine(directory.Path, "extra.xlsx");
        var swappedPath = Path.Combine(directory.Path, "swapped.xlsx");
        WriteWorkbook(expectedPath, workbook =>
        {
            workbook.Sheet("One", sheet => sheet.Row(InlineStringCell("A1", "x")));
            workbook.Sheet("Two", sheet => sheet.Row(InlineStringCell("A1", "y")));
        });
        WriteWorkbook(extraPath, workbook =>
        {
            workbook.Sheet("One", sheet => sheet.Row(InlineStringCell("A1", "x")));
            workbook.Sheet("Two", sheet => sheet.Row(InlineStringCell("A1", "y")));
            workbook.Sheet("Three", sheet => sheet.Row(InlineStringCell("A1", "z")));
        });
        WriteWorkbook(swappedPath, workbook =>
        {
            workbook.Sheet("Two", sheet => sheet.Row(InlineStringCell("A1", "y")));
            workbook.Sheet("One", sheet => sheet.Row(InlineStringCell("A1", "x")));
        });

        var expected = Read(expectedPath);
        var extra = LegacyAuditParityNormalizedContentComparator.Compare(
            expected,
            Read(extraPath));
        var swapped = LegacyAuditParityNormalizedContentComparator.Compare(
            expected,
            Read(swappedPath));

        Assert.Contains(extra.Differences, difference =>
            difference.Dimension == LegacyAuditParityContentDimension.WorksheetSetAndOrder
            && difference.DifferenceCount == 1
            && difference.SheetName == "*");
        var orderOnly = Assert.Single(swapped.Differences, difference =>
            difference.Dimension == LegacyAuditParityContentDimension.WorksheetSetAndOrder);
        Assert.Equal(1, orderOnly.DifferenceCount);
    }

    [Fact]
    public void Store_RoundTripsCaptureWithDigestVerification()
    {
        using var directory = new TempDirectory();
        var workbookPath = Path.Combine(directory.Path, "capture.xlsx");
        WriteWorkbook(workbookPath, workbook => workbook.Sheet("Data", sheet =>
        {
            sheet.Row(InlineStringCell("A1", "value"), NumberCell("B1", "42"));
        }));
        var capture = Read(workbookPath);
        var capturePath = Path.Combine(directory.Path, "capture.content.json");

        _ = LegacyAuditParityNormalizedContentStore.Write(capturePath, capture);
        var reloaded = LegacyAuditParityNormalizedContentStore.Load(capturePath);

        Assert.Equal(capture.ContentDigest(), reloaded.ContentDigest());
        Assert.Equal(capture.Sheets.Count, reloaded.Sheets.Count);
    }

    [Fact]
    public void Read_ExcludesJetMetadataSheetOnlyWhenRequested()
    {
        using var directory = new TempDirectory();
        var workbookPath = Path.Combine(directory.Path, "metadata.xlsx");
        WriteWorkbook(workbookPath, workbook =>
        {
            workbook.Sheet("Data", sheet => sheet.Row(InlineStringCell("A1", "value")));
            workbook.Sheet(
                JET.Domain.ReportWorkbookMetadataFormat.WorksheetName,
                sheet => sheet.Row(InlineStringCell("A1", "meta")));
        });

        var withMetadata = LegacyAuditParityNormalizedWorkbookReader.Read(
            "validation-report",
            workbookPath,
            excludeJetMetadataSheet: false);
        var withoutMetadata = LegacyAuditParityNormalizedWorkbookReader.Read(
            "validation-report",
            workbookPath,
            excludeJetMetadataSheet: true);

        Assert.Equal(2, withMetadata.Sheets.Count);
        var remaining = Assert.Single(withoutMetadata.Sheets);
        Assert.Equal("Data", remaining.SheetName);
    }

    private static LegacyAuditParityNormalizedWorkbook Read(string path) =>
        LegacyAuditParityNormalizedWorkbookReader.Read(
            "validation-report",
            path,
            excludeJetMetadataSheet: false);

    private static Cell InlineStringCell(string reference, string text) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(text)),
    };

    private static Cell SharedStringCell(string reference, int index) => new()
    {
        CellReference = reference,
        DataType = CellValues.SharedString,
        CellValue = new CellValue(index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    };

    private static Cell NumberCell(string reference, string lexeme) => new()
    {
        CellReference = reference,
        CellValue = new CellValue(lexeme),
    };

    private static void WriteWorkbook(string path, Action<WorkbookBuilder> build)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        var builder = new WorkbookBuilder(workbookPart);
        build(builder);
        workbookPart.Workbook.Save();
    }

    private sealed class WorkbookBuilder(WorkbookPart workbookPart)
    {
        private uint _sheetId = 1;

        internal void Sheet(
            string name,
            Action<SheetBuilder> build,
            IReadOnlyList<string>? sharedStrings = null)
        {
            if (sharedStrings is not null && workbookPart.SharedStringTablePart is null)
            {
                var sharedPart = workbookPart.AddNewPart<SharedStringTablePart>();
                sharedPart.SharedStringTable = new SharedStringTable(
                    sharedStrings.Select(static text => new SharedStringItem(new Text(text))));
                sharedPart.SharedStringTable.Save();
            }

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);
            var builder = new SheetBuilder(sheetData);
            build(builder);
            worksheetPart.Worksheet.Save();
            workbookPart.Workbook.Sheets!.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = _sheetId,
                Name = name,
            });
            _sheetId++;
        }
    }

    private sealed class SheetBuilder(SheetData sheetData)
    {
        private uint _rowIndex = 1;

        internal void Row(params Cell[] cells)
        {
            var row = new Row { RowIndex = _rowIndex };
            foreach (var cell in cells)
            {
                row.Append(cell);
            }
            sheetData.Append(row);
            _rowIndex++;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = Directory.CreateTempSubdirectory("jet-workbook-content-").FullName;
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
