using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SpreadsheetContentFingerprintTests
{
    private const string HeaderA = "SYNTHETIC-HEADER-A";
    private const string HeaderB = "SYNTHETIC-HEADER-B";
    private const string DataValue = "SYNTHETIC-DATA-VALUE";

    [Fact]
    public void SyntheticWorkbook_CapturesOrderedSheetsRowsColumnsHashesAndHeadersWithoutValues()
    {
        using var directory = new TemporaryContentFingerprintDirectory();
        var workbook = Path.Combine(directory.Path, "synthetic.xlsx");
        var fingerprint = Path.Combine(directory.Path, "synthetic.content.ndjson");
        WriteSyntheticWorkbook(workbook, SyntheticWorkbookOptions.Default);

        var capture = SpreadsheetContentFingerprint.Capture(
            workbook,
            fingerprint,
            "synthetic-report",
            HeaderRules);

        Assert.Equal(2, capture.SheetCount);
        Assert.Equal(4, capture.ContentRowCount);
        Assert.Equal(3, capture.DataRowCount);
        var text = File.ReadAllText(fingerprint);
        Assert.DoesNotContain(HeaderA, text, StringComparison.Ordinal);
        Assert.DoesNotContain(HeaderB, text, StringComparison.Ordinal);
        Assert.DoesNotContain(DataValue, text, StringComparison.Ordinal);

        var records = File.ReadLines(fingerprint)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            Assert.Equal("document", Kind(records[0]));
            Assert.Equal(
                ["Headers", "Second"],
                records
                    .Where(record => Kind(record) == "sheet")
                    .Select(record => record.RootElement.GetProperty("sheet").GetString()));
            Assert.Equal(4, records.Count(record => Kind(record) == "row"));

            var header = Assert.Single(records, record => Kind(record) == "header");
            Assert.Equal("Headers", header.RootElement.GetProperty("sheet").GetString());
            Assert.Equal(1U, header.RootElement.GetProperty("row").GetUInt32());
            Assert.Equal(
                [1U, 2U],
                header.RootElement.GetProperty("cells")
                    .EnumerateArray()
                    .Select(cell => cell.GetProperty("column").GetUInt32()));
            Assert.All(
                header.RootElement.GetProperty("cells").EnumerateArray(),
                cell => Assert.Equal(
                    44,
                    cell.GetProperty("hash").GetString()?.Length));

            var sheetSummaries = records
                .Where(record => Kind(record) == "sheetSummary")
                .ToDictionary(
                    record => record.RootElement.GetProperty("sheet").GetString()!,
                    record => record.RootElement);
            Assert.Equal(3, sheetSummaries["Headers"].GetProperty("contentRows").GetInt64());
            Assert.Equal(2, sheetSummaries["Headers"].GetProperty("dataRows").GetInt64());
            Assert.Equal(2U, sheetSummaries["Headers"].GetProperty("columns").GetUInt32());
            Assert.Equal(1, sheetSummaries["Headers"].GetProperty("headerRows").GetInt32());
            Assert.Equal(1, sheetSummaries["Second"].GetProperty("contentRows").GetInt64());
            Assert.Equal(1U, sheetSummaries["Second"].GetProperty("columns").GetUInt32());
        }
        finally
        {
            foreach (var record in records)
            {
                record.Dispose();
            }
        }
    }

    [Fact]
    public void EquivalentInlineAndSharedStrings_WithAppearanceOnlyChanges_AreByteIdentical()
    {
        using var directory = new TemporaryContentFingerprintDirectory();
        var inlineWorkbook = Path.Combine(directory.Path, "inline.xlsx");
        var sharedWorkbook = Path.Combine(directory.Path, "shared-styled.xlsx");
        var inlineFingerprint = Path.Combine(directory.Path, "inline.ndjson");
        var sharedFingerprint = Path.Combine(directory.Path, "shared.ndjson");
        WriteSyntheticWorkbook(
            inlineWorkbook,
            SyntheticWorkbookOptions.Default with
            {
                UseSharedStrings = false,
                ApplyAlternateStyles = false,
            });
        WriteSyntheticWorkbook(
            sharedWorkbook,
            SyntheticWorkbookOptions.Default with
            {
                UseSharedStrings = true,
                ApplyAlternateStyles = true,
            });

        SpreadsheetContentFingerprint.Capture(
            inlineWorkbook,
            inlineFingerprint,
            "synthetic-report",
            HeaderRules);
        SpreadsheetContentFingerprint.Capture(
            sharedWorkbook,
            sharedFingerprint,
            "synthetic-report",
            HeaderRules);

        Assert.True(SpreadsheetContentFingerprint.FilesEqual(
            inlineFingerprint,
            sharedFingerprint));
        Assert.Null(SpreadsheetContentFingerprint.DescribeFirstDifference(
            inlineFingerprint,
            sharedFingerprint));
    }

    [Fact]
    public void SheetRowColumnHeaderValueAndFormulaMutations_ReportOnlySafePositions()
    {
        using var directory = new TemporaryContentFingerprintDirectory();
        var baselineWorkbook = Path.Combine(directory.Path, "baseline.xlsx");
        var baselineFingerprint = Path.Combine(directory.Path, "baseline.ndjson");
        WriteSyntheticWorkbook(baselineWorkbook, SyntheticWorkbookOptions.Default);
        SpreadsheetContentFingerprint.Capture(
            baselineWorkbook,
            baselineFingerprint,
            "synthetic-report",
            HeaderRules);

        var mutations = new[]
        {
            ("sheet-order", SyntheticWorkbookOptions.Default with { ReverseSheetOrder = true }, "Headers/workbook"),
            ("header-order", SyntheticWorkbookOptions.Default with { ReverseHeaderOrder = true }, "Headers/1"),
            ("deleted-row", SyntheticWorkbookOptions.Default with { DeleteDataRow = true }, "Headers/2"),
            ("column-shift", SyntheticWorkbookOptions.Default with { ShiftDataColumn = true }, "Headers/2"),
            ("value", SyntheticWorkbookOptions.Default with { MutateDataValue = true }, "Headers/2"),
            ("formula", SyntheticWorkbookOptions.Default with { MutateFormula = true }, "Headers/4"),
        };
        foreach (var (id, options, expectedPosition) in mutations)
        {
            var workbook = Path.Combine(directory.Path, $"{id}.xlsx");
            var fingerprint = Path.Combine(directory.Path, $"{id}.ndjson");
            WriteSyntheticWorkbook(workbook, options);
            SpreadsheetContentFingerprint.Capture(
                workbook,
                fingerprint,
                "synthetic-report",
                HeaderRules);

            var difference = SpreadsheetContentFingerprint.DescribeFirstDifference(
                baselineFingerprint,
                fingerprint);

            Assert.NotNull(difference);
            Assert.Equal(expectedPosition, difference.ToString());
            Assert.DoesNotContain(DataValue, difference.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("hash", difference.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ComplementarySameRowFragments_AreMergedButOverlapAndReorderingFailClosed()
    {
        using var directory = new TemporaryContentFingerprintDirectory();
        var combinedWorkbook = Path.Combine(directory.Path, "combined.xlsx");
        var fragmentedWorkbook = Path.Combine(directory.Path, "fragmented.xlsx");
        var combinedFingerprint = Path.Combine(directory.Path, "combined.ndjson");
        var fragmentedFingerprint = Path.Combine(directory.Path, "fragmented.ndjson");
        WriteSyntheticWorkbook(combinedWorkbook, SyntheticWorkbookOptions.Default);
        WriteSyntheticWorkbook(
            fragmentedWorkbook,
            SyntheticWorkbookOptions.Default with { FragmentDataRow = true });
        SpreadsheetContentFingerprint.Capture(
            combinedWorkbook,
            combinedFingerprint,
            "synthetic-report",
            HeaderRules);
        SpreadsheetContentFingerprint.Capture(
            fragmentedWorkbook,
            fragmentedFingerprint,
            "synthetic-report",
            HeaderRules);
        Assert.True(SpreadsheetContentFingerprint.FilesEqual(
            combinedFingerprint,
            fragmentedFingerprint));

        var overlapWorkbook = Path.Combine(directory.Path, "overlap.xlsx");
        WriteSyntheticWorkbook(
            overlapWorkbook,
            SyntheticWorkbookOptions.Default with { OverlapDataRowFragment = true });
        var overlap = Assert.Throws<InvalidDataException>(() =>
            SpreadsheetContentFingerprint.Capture(
                overlapWorkbook,
                Path.Combine(directory.Path, "overlap.ndjson"),
                "synthetic-report",
                HeaderRules));
        Assert.Contains("row-fragment-overlap", overlap.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(DataValue, overlap.Message, StringComparison.Ordinal);

        var reorderedWorkbook = Path.Combine(directory.Path, "reordered.xlsx");
        WriteSyntheticWorkbook(
            reorderedWorkbook,
            SyntheticWorkbookOptions.Default with { ReverseContentRowOrder = true });
        var reordered = Assert.Throws<InvalidDataException>(() =>
            SpreadsheetContentFingerprint.Capture(
                reorderedWorkbook,
                Path.Combine(directory.Path, "reordered.ndjson"),
                "synthetic-report",
                HeaderRules));
        Assert.Contains("row-order", reordered.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(DataValue, reordered.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_IsDeterministicAndRefusesToOverwriteAnExistingFingerprint()
    {
        using var directory = new TemporaryContentFingerprintDirectory();
        var workbook = Path.Combine(directory.Path, "deterministic.xlsx");
        var first = Path.Combine(directory.Path, "first.ndjson");
        var second = Path.Combine(directory.Path, "second.ndjson");
        WriteSyntheticWorkbook(workbook, SyntheticWorkbookOptions.Default);

        SpreadsheetContentFingerprint.Capture(
            workbook,
            first,
            "synthetic-report",
            HeaderRules);
        SpreadsheetContentFingerprint.Capture(
            workbook,
            second,
            "synthetic-report",
            HeaderRules);

        Assert.True(SpreadsheetContentFingerprint.FilesEqual(first, second));
        Assert.Throws<IOException>(() =>
            SpreadsheetContentFingerprint.Capture(
                workbook,
                first,
                "synthetic-report",
                HeaderRules));
        Assert.Empty(Directory.EnumerateFiles(
            directory.Path,
            ".content-fingerprint-shared-*.tmp",
            SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.EnumerateFiles(
            directory.Path,
            ".*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void RepeatedHeaderPrototype_CapturesTheDynamicSecondHeaderWithoutRawValues()
    {
        using var directory = new TemporaryContentFingerprintDirectory();
        var workbook = Path.Combine(directory.Path, "repeated-header.xlsx");
        var fingerprint = Path.Combine(directory.Path, "repeated-header.ndjson");
        WriteRepeatedHeaderWorkbook(workbook);

        SpreadsheetContentFingerprint.Capture(
            workbook,
            fingerprint,
            "synthetic-report",
            sheet => sheet == "FieldInfo"
                ? SpreadsheetContentHeaderRule.FixedWithRepeats(3)
                : SpreadsheetContentHeaderRule.None);

        var headers = File.ReadLines(fingerprint)
            .Select(line => JsonDocument.Parse(line))
            .Where(document => Kind(document) == "header")
            .Select(document =>
            {
                var row = document.RootElement.GetProperty("row").GetUInt32();
                document.Dispose();
                return row;
            })
            .ToArray();
        Assert.Equal([3U, 9U], headers);
        Assert.DoesNotContain(HeaderA, File.ReadAllText(fingerprint), StringComparison.Ordinal);
        Assert.DoesNotContain(HeaderB, File.ReadAllText(fingerprint), StringComparison.Ordinal);
    }

    [Fact]
    public void ExactTextSignature_CapturesDynamicHeaderWithoutRawValuesOrDataRowInflation()
    {
        using var directory = new TemporaryContentFingerprintDirectory();
        var workbook = Path.Combine(directory.Path, "dynamic-header.xlsx");
        var fingerprint = Path.Combine(directory.Path, "dynamic-header.ndjson");
        using (var document = SpreadsheetDocument.Create(
                   workbook,
                   SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(
                new SheetData(
                    HeaderRow(1),
                    new Row(
                        TextCell("A6", "DATE_OF_MAKEUPDAY", null, 0),
                        TextCell("B6", "MAKEUPDAY_DESC", null, 0)) { RowIndex = 6 },
                    new Row(TextCell("A7", DataValue, null, 0)) { RowIndex = 7 }));
            workbookPart.Workbook.AppendChild(new Sheets()).Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Dynamic"
            });
            worksheetPart.Worksheet.Save();
            workbookPart.Workbook.Save();
        }

        SpreadsheetContentFingerprint.Capture(
            workbook,
            fingerprint,
            "synthetic",
            _ => SpreadsheetContentHeaderRule.Fixed(1)
                .WithExactTextSignature(
                    (1, "DATE_OF_MAKEUPDAY"),
                    (2, "MAKEUPDAY_DESC")));

        var lines = File.ReadAllLines(fingerprint)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            Assert.Equal(2, lines.Count(line =>
                line.RootElement.GetProperty("kind").GetString() == "header"));
            var summary = lines.Single(line =>
                line.RootElement.GetProperty("kind").GetString() == "sheetSummary");
            Assert.Equal(3, summary.RootElement.GetProperty("contentRows").GetInt64());
            Assert.Equal(1, summary.RootElement.GetProperty("dataRows").GetInt64());
            Assert.Equal(2, summary.RootElement.GetProperty("headerRows").GetInt32());
        }
        finally
        {
            foreach (var line in lines)
            {
                line.Dispose();
            }
        }

        var text = File.ReadAllText(fingerprint);
        Assert.DoesNotContain("DATE_OF_MAKEUPDAY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MAKEUPDAY_DESC", text, StringComparison.Ordinal);
    }

    private static SpreadsheetContentHeaderRule HeaderRules(string sheetName) =>
        sheetName == "Headers"
            ? SpreadsheetContentHeaderRule.Fixed(1)
            : SpreadsheetContentHeaderRule.None;

    private static string Kind(JsonDocument document) =>
        document.RootElement.GetProperty("kind").GetString()
        ?? throw new InvalidDataException("Synthetic content fingerprint record has no kind.");

    private static void WriteSyntheticWorkbook(
        string path,
        SyntheticWorkbookOptions options)
    {
        var sharedValues = new[] { HeaderA, HeaderB, DataValue, "Second sheet", "mutated" };
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        AddStyles(workbookPart);
        Dictionary<string, int>? shared = null;
        if (options.UseSharedStrings)
        {
            shared = sharedValues
                .Select((value, index) => (value, index))
                .ToDictionary(item => item.value, item => item.index, StringComparer.Ordinal);
            var part = workbookPart.AddNewPart<SharedStringTablePart>();
            part.SharedStringTable = new SharedStringTable(
                sharedValues.Select(value => new SharedStringItem(new Text(value))));
            part.SharedStringTable.Save();
        }

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        var orderedNames = options.ReverseSheetOrder
            ? new[] { "Second", "Headers" }
            : new[] { "Headers", "Second" };
        uint sheetId = 1;
        foreach (var name in orderedNames)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = name == "Headers"
                ? BuildHeadersWorksheet(options, shared)
                : BuildSecondWorksheet(options, shared);
            worksheetPart.Worksheet.Save();
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = sheetId++,
                Name = name,
            });
        }
        workbookPart.Workbook.Save();
    }

    private static Worksheet BuildHeadersWorksheet(
        SyntheticWorkbookOptions options,
        IReadOnlyDictionary<string, int>? shared)
    {
        var style = options.ApplyAlternateStyles ? 1U : 0U;
        var header = new Row { RowIndex = 1 };
        var firstHeader = options.ReverseHeaderOrder ? HeaderB : HeaderA;
        var secondHeader = options.ReverseHeaderOrder ? HeaderA : HeaderB;
        header.Append(
            TextCell("A1", firstHeader, shared, style),
            TextCell("B1", secondHeader, shared, style));

        var data = new Row { RowIndex = 2 };
        var dataReference = options.ShiftDataColumn ? "B2" : "A2";
        data.Append(
            TextCell(
                dataReference,
                options.MutateDataValue ? "mutated" : DataValue,
                shared,
                style),
            NumberCell(options.ShiftDataColumn ? "C2" : "B2", "42", style));

        var formula = new Row { RowIndex = 4 };
        formula.Append(new Cell
        {
            CellReference = "A4",
            CellFormula = new CellFormula(options.MutateFormula ? "B2+1" : "B2"),
            CellValue = new CellValue("42"),
            StyleIndex = style,
        });

        var rows = new List<Row> { header };
        if (!options.DeleteDataRow)
        {
            if (options.FragmentDataRow || options.OverlapDataRowFragment)
            {
                var first = new Row { RowIndex = 2 };
                first.Append((Cell)data.Elements<Cell>().First().CloneNode(true));
                var second = new Row { RowIndex = 2 };
                var secondCell = options.OverlapDataRowFragment
                    ? data.Elements<Cell>().First()
                    : data.Elements<Cell>().Last();
                second.Append((Cell)secondCell.CloneNode(true));
                rows.Add(first);
                rows.Add(second);
            }
            else
            {
                rows.Add(data);
            }
        }
        rows.Add(formula);
        if (options.ReverseContentRowOrder)
        {
            rows = [header, formula, data];
        }

        return new Worksheet(new SheetData(rows));
    }

    private static Worksheet BuildSecondWorksheet(
        SyntheticWorkbookOptions options,
        IReadOnlyDictionary<string, int>? shared)
    {
        var style = options.ApplyAlternateStyles ? 1U : 0U;
        var row = new Row { RowIndex = 3 };
        row.Append(TextCell("A3", "Second sheet", shared, style));
        return new Worksheet(new SheetData(row));
    }

    private static void WriteRepeatedHeaderWorkbook(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var rows = new List<Row>
        {
            RowWithText(1, "A1", "Title"),
            HeaderRow(3),
            RowWithText(4, "A4", "TB data"),
            RowWithText(8, "A8", "GL title"),
            HeaderRow(9),
            RowWithText(10, "A10", "GL data"),
        };
        worksheetPart.Worksheet = new Worksheet(new SheetData(rows));
        worksheetPart.Worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "FieldInfo",
        });
        workbookPart.Workbook.Save();
    }

    private static Row HeaderRow(uint index)
    {
        var row = new Row { RowIndex = index };
        row.Append(
            TextCell($"A{index}", HeaderA, null, 0),
            TextCell($"B{index}", HeaderB, null, 0));
        return row;
    }

    private static Row RowWithText(uint index, string reference, string value)
    {
        var row = new Row { RowIndex = index };
        row.Append(TextCell(reference, value, null, 0));
        return row;
    }

    private static Cell TextCell(
        string reference,
        string value,
        IReadOnlyDictionary<string, int>? shared,
        uint style)
    {
        if (shared is not null)
        {
            return new Cell
            {
                CellReference = reference,
                DataType = CellValues.SharedString,
                CellValue = new CellValue(shared[value].ToString()),
                StyleIndex = style,
            };
        }
        return new Cell
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value)),
            StyleIndex = style,
        };
    }

    private static Cell NumberCell(string reference, string value, uint style) => new()
    {
        CellReference = reference,
        CellValue = new CellValue(value),
        StyleIndex = style,
    };

    private static void AddStyles(WorkbookPart workbookPart)
    {
        var part = workbookPart.AddNewPart<WorkbookStylesPart>();
        part.Stylesheet = new Stylesheet(
            new Fonts(
                new Font(),
                new Font(new Bold())) { Count = 2 },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
            new Borders(new Border()) { Count = 1 },
            new CellStyleFormats(new CellFormat()) { Count = 1 },
            new CellFormats(
                new CellFormat(),
                new CellFormat
                {
                    FontId = 1,
                    FillId = 0,
                    BorderId = 0,
                    NumberFormatId = 0,
                }) { Count = 2 });
        part.Stylesheet.Save();
    }

    private sealed record SyntheticWorkbookOptions(
        bool UseSharedStrings = false,
        bool ApplyAlternateStyles = false,
        bool ReverseSheetOrder = false,
        bool ReverseHeaderOrder = false,
        bool DeleteDataRow = false,
        bool ShiftDataColumn = false,
        bool MutateDataValue = false,
        bool MutateFormula = false,
        bool FragmentDataRow = false,
        bool OverlapDataRowFragment = false,
        bool ReverseContentRowOrder = false)
    {
        internal static SyntheticWorkbookOptions Default { get; } = new();
    }

    private sealed class TemporaryContentFingerprintDirectory : IDisposable
    {
        internal TemporaryContentFingerprintDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"jet-content-fingerprint-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
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
