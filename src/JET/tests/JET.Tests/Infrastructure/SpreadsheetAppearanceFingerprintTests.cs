using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;
using SpreadsheetColor = DocumentFormat.OpenXml.Spreadsheet.Color;
using SpreadsheetFont = DocumentFormat.OpenXml.Spreadsheet.Font;

namespace JET.Tests.Infrastructure;

public sealed class SpreadsheetAppearanceFingerprintTests
{
    private static readonly string[] ReportTemplateNames =
    [
        "AccountMapping.xlsx",
        "CriteriaSelectionReport.xlsx",
        "INFReport.xlsx",
        "PrescreeningReport.xlsx",
        "ValidationReport.xlsx",
        "WorkingPaper.xlsx"
    ];

    [Fact]
    public void SyntheticWorkbook_ProducesTheHandCalculatedAppearanceFingerprint()
    {
        using var directory = new TemporaryFingerprintDirectory();
        var path = Path.Combine(directory.Path, "synthetic.xlsx");
        WriteSyntheticWorkbook(path, bold: true);

        var fingerprint = SpreadsheetAppearanceFingerprint.Capture(path);

        Assert.DoesNotContain(fingerprint.Entries, entry => entry.Location == "$workbook");
        Assert.Equal("Arial", fingerprint.Value("Synthetic!A1", "font.name"));
        Assert.Equal("12", fingerprint.Value("Synthetic!A1", "font.size"));
        Assert.Equal("true", fingerprint.Value("Synthetic!A1", "font.bold"));
        Assert.Equal("true", fingerprint.Value("Synthetic!A1", "font.italic"));
        Assert.Equal("FFFF0000", fingerprint.Value("Synthetic!A1", "font.colorArgb"));
        Assert.Equal("solid", fingerprint.Value("Synthetic!A1", "fill.pattern"));
        Assert.Equal("FF75A3D1", fingerprint.Value("Synthetic!A1", "fill.foregroundArgb"));
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            Assert.Equal("thin", fingerprint.Value("Synthetic!A1", $"border.{side}.style"));
            Assert.Equal("FF0000FF", fingerprint.Value("Synthetic!A1", $"border.{side}.colorArgb"));
        }
        Assert.Equal("0.0000", fingerprint.Value("Synthetic!A1", "numberFormat.code"));
        Assert.Equal("center", fingerprint.Value("Synthetic!A1", "alignment.horizontal"));
        Assert.Equal("top", fingerprint.Value("Synthetic!A1", "alignment.vertical"));
        Assert.Equal("true", fingerprint.Value("Synthetic!A1", "alignment.wrapText"));
        Assert.Equal("1", fingerprint.Value("Synthetic!A1", "alignment.indent"));
        Assert.Equal("true", fingerprint.Value("Synthetic!A1", "alignment.shrinkToFit"));
        Assert.Equal("false", fingerprint.Value("Synthetic!A1", "protection.locked"));
        Assert.Equal(
            "_(* #,##0.00_);_(* \\(#,##0.00\\);_(* \"-\"??_);_(@_)",
            fingerprint.Value("Synthetic!B2", "numberFormat.code"));

        Assert.Equal("18.5", fingerprint.Value("Synthetic!column:2-2", "width"));
        Assert.Equal("true", fingerprint.Value("Synthetic!column:2-2", "hidden"));
        Assert.Equal("25", fingerprint.Value("Synthetic!row:1", "height"));
        Assert.Equal("true", fingerprint.Value("Synthetic!merge:A1:B1", "present"));
        Assert.Equal("false", fingerprint.Value("Synthetic!sheetView:1", "showGridLines"));
        Assert.Equal("125", fingerprint.Value("Synthetic!sheetView:1", "zoomScale"));
        Assert.Equal("frozen", fingerprint.Value("Synthetic!sheetView:1.pane", "state"));
        Assert.Equal("1", fingerprint.Value("Synthetic!sheetView:1.pane", "ySplit"));
        Assert.Equal("A2", fingerprint.Value("Synthetic!sheetView:1.pane", "topLeftCell"));
        Assert.Equal("landscape", fingerprint.Value("Synthetic!pageSetup", "orientation"));
        Assert.Equal("9", fingerprint.Value("Synthetic!pageSetup", "paperSize"));
        Assert.Null(fingerprint.Value("Synthetic!pageSetup", "fitToWidth"));
        Assert.Equal("true", fingerprint.Value("Synthetic!sheetProtection", "sheet"));
        Assert.DoesNotContain("ABCD", fingerprint.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SYNTHETIC-PAGESETUP-ATTRIBUTE-SENTINEL",
            fingerprint.Json,
            StringComparison.Ordinal);

        var expectedEntries = new[]
        {
            Entry("Synthetic!A1", "alignment.horizontal", "center"),
            Entry("Synthetic!A1", "alignment.indent", "1"),
            Entry("Synthetic!A1", "alignment.shrinkToFit", "true"),
            Entry("Synthetic!A1", "alignment.vertical", "top"),
            Entry("Synthetic!A1", "alignment.wrapText", "true"),
            Entry("Synthetic!A1", "border.bottom.colorArgb", "FF0000FF"),
            Entry("Synthetic!A1", "border.bottom.style", "thin"),
            Entry("Synthetic!A1", "border.left.colorArgb", "FF0000FF"),
            Entry("Synthetic!A1", "border.left.style", "thin"),
            Entry("Synthetic!A1", "border.right.colorArgb", "FF0000FF"),
            Entry("Synthetic!A1", "border.right.style", "thin"),
            Entry("Synthetic!A1", "border.top.colorArgb", "FF0000FF"),
            Entry("Synthetic!A1", "border.top.style", "thin"),
            Entry("Synthetic!A1", "fill.foregroundArgb", "FF75A3D1"),
            Entry("Synthetic!A1", "fill.pattern", "solid"),
            Entry("Synthetic!A1", "font.bold", "true"),
            Entry("Synthetic!A1", "font.colorArgb", "FFFF0000"),
            Entry("Synthetic!A1", "font.italic", "true"),
            Entry("Synthetic!A1", "font.name", "Arial"),
            Entry("Synthetic!A1", "font.size", "12"),
            Entry("Synthetic!A1", "numberFormat.code", "0.0000"),
            Entry("Synthetic!A1", "protection.locked", "false"),
            Entry("Synthetic!B2", "numberFormat.code",
                "_(* #,##0.00_);_(* \\(#,##0.00\\);_(* \"-\"??_);_(@_)"),
            Entry("Synthetic!cellStream", "cellCount", "2"),
            Entry(
                "Synthetic!cellStream",
                "sha256",
                "8D0F866A7FE12E8F9B3F6E82520307281BE6A8CB07074E73538E54EC16F79055"),
            Entry("Synthetic!column:2-2", "hidden", "true"),
            Entry("Synthetic!column:2-2", "width", "18.5"),
            Entry("Synthetic!merge:A1:B1", "present", "true"),
            Entry("Synthetic!pageSetup", "fitToHeight", "0"),
            Entry("Synthetic!pageSetup", "orientation", "landscape"),
            Entry("Synthetic!pageSetup", "paperSize", "9"),
            Entry("Synthetic!row:1", "height", "25"),
            Entry("Synthetic!sheetProtection", "objects", "true"),
            Entry("Synthetic!sheetProtection", "scenarios", "true"),
            Entry("Synthetic!sheetProtection", "sheet", "true"),
            Entry("Synthetic!sheetView:1", "showGridLines", "false"),
            Entry("Synthetic!sheetView:1", "zoomScale", "125"),
            Entry("Synthetic!sheetView:1.pane", "activePane", "bottomLeft"),
            Entry("Synthetic!sheetView:1.pane", "state", "frozen"),
            Entry("Synthetic!sheetView:1.pane", "topLeftCell", "A2"),
            Entry("Synthetic!sheetView:1.pane", "ySplit", "1")
        };
        Assert.Equal(
            expectedEntries
                .OrderBy(entry => entry.Location, StringComparer.Ordinal)
                .ThenBy(entry => entry.Property, StringComparer.Ordinal),
            fingerprint.Entries);
    }

    [Fact]
    public void Fingerprint_IsValueBlindAndUtilitySourceKeepsTheBoundaryExplicit()
    {
        using var directory = new TemporaryFingerprintDirectory();
        var path = Path.Combine(directory.Path, "value-isolation.xlsx");
        WriteSyntheticWorkbook(path, bold: true);

        var fingerprint = SpreadsheetAppearanceFingerprint.Capture(path);

        Assert.DoesNotContain("SYNTHETIC-INLINE-SENTINEL", fingerprint.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC-SHARED-SENTINEL", fingerprint.Json, StringComparison.Ordinal);

        var sourcePath = Path.Combine(
            TestRepositoryPaths.RepositoryRoot,
            "src",
            "JET",
            "tests",
            "JET.Tests",
            "Infrastructure",
            "SpreadsheetAppearanceFingerprint.cs");
        var source = File.ReadAllText(sourcePath);
        Assert.DoesNotContain("Cell" + "Value", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Inline" + "String", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Shared" + "StringTable", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UtilitySource_CellBranchReadsOnlyAddressAndStyleThenSkipsTheWholeSubtree()
    {
        var sourcePath = Path.Combine(
            TestRepositoryPaths.RepositoryRoot,
            "src",
            "JET",
            "tests",
            "JET.Tests",
            "Infrastructure",
            "SpreadsheetAppearanceFingerprint.cs");
        var source = File.ReadAllText(sourcePath);

        var cellBranchStart = source.IndexOf("case \"c\":", StringComparison.Ordinal);
        var cellBranchEnd = source.IndexOf(
            "case \"col\":",
            cellBranchStart,
            StringComparison.Ordinal);
        Assert.True(cellBranchStart >= 0 && cellBranchEnd > cellBranchStart);
        var cellBranch = source[cellBranchStart..cellBranchEnd];
        Assert.Contains("CaptureCellAppearance(", cellBranch, StringComparison.Ordinal);
        Assert.Contains("reader.Skip();", cellBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("reader.Read", cellBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("reader.Value", cellBranch, StringComparison.Ordinal);

        var readCellStart = source.IndexOf(
            "private static CellDescriptor ReadCell(",
            StringComparison.Ordinal);
        var readCellEnd = source.IndexOf(
            "private static ColumnDescriptor ReadColumn(",
            readCellStart,
            StringComparison.Ordinal);
        Assert.True(readCellStart >= 0 && readCellEnd > readCellStart);
        var readCellSource = source[readCellStart..readCellEnd];
        var attributes = Regex.Matches(
                readCellSource,
                @"GetAttribute\(""(?<name>[^""]+)""\)",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["name"].Value)
            .ToArray();

        Assert.Equal(new[] { "r", "s" }, attributes);
        Assert.DoesNotContain("reader.Read", readCellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("reader.Value", readCellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadElementContentAs", readCellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadSubtree", readCellSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFirstDifference_ReportsTheCellPropertyAndBothValues()
    {
        using var directory = new TemporaryFingerprintDirectory();
        var expectedPath = Path.Combine(directory.Path, "expected.xlsx");
        var actualPath = Path.Combine(directory.Path, "actual.xlsx");
        WriteSyntheticWorkbook(expectedPath, bold: true);
        WriteSyntheticWorkbook(actualPath, bold: false);

        var difference = SpreadsheetAppearanceFingerprint
            .Capture(expectedPath)
            .DescribeFirstDifference(SpreadsheetAppearanceFingerprint.Capture(actualPath));

        Assert.Equal(
            "Synthetic!A1 font.bold expected=true actual=false",
            difference);
    }

    [Fact]
    public void CellStreamHash_CoversCellsBeyondTheBoundedExplicitRowWindow()
    {
        using var directory = new TemporaryFingerprintDirectory();
        var expectedPath = Path.Combine(directory.Path, "stream-expected.xlsx");
        var actualPath = Path.Combine(directory.Path, "stream-actual.xlsx");
        WriteCellStreamWorkbook(expectedPath, styledLastCell: false);
        WriteCellStreamWorkbook(actualPath, styledLastCell: true);

        var expected = SpreadsheetAppearanceFingerprint.Capture(expectedPath);
        var actual = SpreadsheetAppearanceFingerprint.Capture(actualPath);

        Assert.Equal("250", expected.Value("Stream!cellStream", "cellCount"));
        Assert.Null(expected.Value("Stream!A250", "font.name"));
        Assert.Null(actual.Value("Stream!A250", "font.name"));
        Assert.NotEqual(
            expected.Value("Stream!cellStream", "sha256"),
            actual.Value("Stream!cellStream", "sha256"));
        Assert.Contains(
            "Stream!cellStream sha256",
            expected.DescribeFirstDifference(actual),
            StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC-CELL-VALUE", expected.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC-CELL-VALUE", actual.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFirstDifference_UsesTheEffectiveNonstandardWorkbookBaseline()
    {
        using var directory = new TemporaryFingerprintDirectory();
        var expectedPath = Path.Combine(directory.Path, "baseline-expected.xlsx");
        var actualPath = Path.Combine(directory.Path, "baseline-actual.xlsx");
        WriteBaselineWorkbook(expectedPath, useCellOverride: false);
        WriteBaselineWorkbook(actualPath, useCellOverride: true);

        var difference = SpreadsheetAppearanceFingerprint
            .Capture(expectedPath)
            .DescribeFirstDifference(SpreadsheetAppearanceFingerprint.Capture(actualPath));

        Assert.Equal(
            "Baseline!A1 font.name expected=Arial actual=Calibri",
            difference);
    }

    [Fact]
    public void EquivalentColumnRuns_AreCanonicalAndApplyTheirEffectiveStyleToCells()
    {
        using var directory = new TemporaryFingerprintDirectory();
        var combinedPath = Path.Combine(directory.Path, "columns-combined.xlsx");
        var splitPath = Path.Combine(directory.Path, "columns-split.xlsx");
        WriteColumnRunWorkbook(combinedPath, split: false);
        WriteColumnRunWorkbook(splitPath, split: true);

        var combined = SpreadsheetAppearanceFingerprint.Capture(combinedPath);
        var split = SpreadsheetAppearanceFingerprint.Capture(splitPath);

        Assert.Equal(combined.JsonBytes, split.JsonBytes);
        Assert.Equal("18.5", combined.Value("Columns!column:2-3", "width"));
        Assert.Equal("true", combined.Value("Columns!column:2-3", "hidden"));
        Assert.Equal("true", combined.Value("Columns!column:2-3", "style.font.bold"));
        Assert.Equal("true", combined.Value("Columns!B1", "font.bold"));
    }

    [Fact]
    public void MissingOrEmptyStylesheet_UsesDefaultsAndCapturesNonstandardSheetDefaults()
    {
        using var directory = new TemporaryFingerprintDirectory();
        var missingPath = Path.Combine(directory.Path, "no-styles.xlsx");
        var emptyPath = Path.Combine(directory.Path, "empty-styles.xlsx");
        WriteWorkbookWithoutStyles(missingPath, addEmptyStylesPart: false);
        WriteWorkbookWithoutStyles(emptyPath, addEmptyStylesPart: true);

        var missing = SpreadsheetAppearanceFingerprint.Capture(missingPath);
        var empty = SpreadsheetAppearanceFingerprint.Capture(emptyPath);

        Assert.Equal(
            [
                Entry("NoStyles!cellStream", "cellCount", "1"),
                Entry(
                    "NoStyles!cellStream",
                    "sha256",
                    "B10470E36C781862D424593B9313801D2FD5B2840410164A261C5466E430551B"),
                Entry("NoStyles!sheetFormat", "defaultRowHeight", "20"),
            ],
            missing.Entries);
        Assert.Equal(missing.JsonBytes, empty.JsonBytes);
    }

    [Fact]
    public void ExplicitPageSetupDefaults_AreEquivalentToOmittedDefaults()
    {
        using var directory = new TemporaryFingerprintDirectory();
        var omittedPath = Path.Combine(directory.Path, "page-defaults-omitted.xlsx");
        var explicitPath = Path.Combine(directory.Path, "page-defaults-explicit.xlsx");
        WritePageSetupDefaultsWorkbook(omittedPath, explicitDefaults: false);
        WritePageSetupDefaultsWorkbook(explicitPath, explicitDefaults: true);

        var omitted = SpreadsheetAppearanceFingerprint.Capture(omittedPath);
        var explicitDefaults = SpreadsheetAppearanceFingerprint.Capture(explicitPath);

        Assert.Equal(omitted.JsonBytes, explicitDefaults.JsonBytes);
        Assert.Equal("0", explicitDefaults.Value("PageDefaults!cellStream", "cellCount"));
        Assert.Equal(
            "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
            explicitDefaults.Value("PageDefaults!cellStream", "sha256"));
        Assert.Equal(2, explicitDefaults.Entries.Count);
    }

    [Fact]
    public void RowStyle_OnlyAppliesWhenCustomFormatIsTrue()
    {
        using var directory = new TemporaryFingerprintDirectory();
        var stalePath = Path.Combine(directory.Path, "row-style-stale.xlsx");
        var appliedPath = Path.Combine(directory.Path, "row-style-applied.xlsx");
        WriteRowStyleWorkbook(stalePath, customFormat: false);
        WriteRowStyleWorkbook(appliedPath, customFormat: true);

        var stale = SpreadsheetAppearanceFingerprint.Capture(stalePath);
        var applied = SpreadsheetAppearanceFingerprint.Capture(appliedPath);

        Assert.Null(stale.Value("Rows!row:1", "style.font.name"));
        Assert.Null(stale.Value("Rows!A1", "font.name"));
        Assert.Equal("Calibri", applied.Value("Rows!row:1", "style.font.name"));
        Assert.Equal("Calibri", applied.Value("Rows!A1", "font.name"));
    }

    [Fact]
    public void SixReportTemplates_ProduceByteIdenticalJsonOnRepeatedCapture()
    {
        var dataRoot = Path.Combine(TestRepositoryPaths.RepositoryRoot, "data");

        foreach (var templateName in ReportTemplateNames)
        {
            var path = Path.Combine(dataRoot, templateName);
            Assert.True(File.Exists(path), $"Missing report template: data/{templateName}");

            var first = SpreadsheetAppearanceFingerprint.Capture(path);
            var second = SpreadsheetAppearanceFingerprint.Capture(path);

            Assert.NotEmpty(first.Entries);
            Assert.Equal(first.JsonBytes, second.JsonBytes);
            Assert.Null(first.DescribeFirstDifference(second));
        }
    }

    private static void WriteSyntheticWorkbook(string path, bool bold)
    {
        using var document = SpreadsheetDocument.Create(
            path,
            DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        var themePart = workbookPart.AddNewPart<ThemePart>();
        using (var themeStream = themePart.GetStream(FileMode.Create, FileAccess.Write))
        using (var writer = new StreamWriter(themeStream, new UTF8Encoding(false), leaveOpen: false))
        {
            writer.Write(SyntheticThemeXml);
        }

        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = BuildStylesheet(bold);
        stylesPart.Stylesheet.Save();

        var sharedStringsPart = workbookPart.AddNewPart<SharedStringTablePart>();
        sharedStringsPart.SharedStringTable = new SharedStringTable(
            new SharedStringItem(new Text("SYNTHETIC-SHARED-SENTINEL")));
        sharedStringsPart.SharedStringTable.Save();

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetView = new SheetView
        {
            WorkbookViewId = 0U,
            ShowGridLines = false,
            ZoomScale = 125U
        };
        sheetView.Append(new Pane
        {
            State = PaneStateValues.Frozen,
            VerticalSplit = 1D,
            TopLeftCell = "A2",
            ActivePane = PaneValues.BottomLeft
        });
        var sheetData = new SheetData(
            new Row(
                new Cell
                {
                    CellReference = "A1",
                    StyleIndex = 1U,
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new Text("SYNTHETIC-INLINE-SENTINEL"))
                })
            {
                RowIndex = 1U,
                Height = 25D,
                CustomHeight = true
            },
            new Row(
                new Cell
                {
                    CellReference = "B2",
                    StyleIndex = 2U,
                    DataType = CellValues.SharedString,
                    CellValue = new CellValue("0")
                })
            {
                RowIndex = 2U
            });
        var pageSetup = new PageSetup
        {
            Orientation = OrientationValues.Landscape,
            PaperSize = 9U,
            FitToWidth = 1U,
            FitToHeight = 0U
        };
        pageSetup.SetAttribute(new OpenXmlAttribute(
            string.Empty,
            "syntheticUnsafe",
            string.Empty,
            "SYNTHETIC-PAGESETUP-ATTRIBUTE-SENTINEL"));
        worksheetPart.Worksheet = new Worksheet(
            new SheetViews(sheetView),
            new SheetFormatProperties { DefaultRowHeight = 15D },
            new Columns(new Column
            {
                Min = 2U,
                Max = 2U,
                Width = 18.5D,
                Hidden = true,
                CustomWidth = true
            }),
            sheetData,
            new SheetProtection
            {
                Sheet = true,
                Objects = true,
                Scenarios = true,
                Password = "ABCD"
            },
            new MergeCells(new MergeCell { Reference = "A1:B1" }),
            pageSetup);
        worksheetPart.Worksheet.Save();

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "Synthetic"
        });
        workbookPart.Workbook.Save();
    }

    private static void WriteBaselineWorkbook(string path, bool useCellOverride)
    {
        using var document = SpreadsheetDocument.Create(
            path,
            DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = BuildBaselineStylesheet();
        stylesPart.Stylesheet.Save();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(
            new SheetData(
                new Row(
                    new Cell
                    {
                        CellReference = "A1",
                        StyleIndex = useCellOverride ? 1U : 0U
                    })
                { RowIndex = 1U }));
        worksheetPart.Worksheet.Save();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "Baseline"
        });
        workbookPart.Workbook.Save();
    }

    private static void WriteColumnRunWorkbook(string path, bool split)
    {
        using var document = SpreadsheetDocument.Create(
            path,
            DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var themePart = workbookPart.AddNewPart<ThemePart>();
        using (var themeStream = themePart.GetStream(FileMode.Create, FileAccess.Write))
        using (var writer = new StreamWriter(themeStream, new UTF8Encoding(false), leaveOpen: false))
        {
            writer.Write(SyntheticThemeXml);
        }

        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = BuildStylesheet(bold: true);
        stylesPart.Stylesheet.Save();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var columns = split
            ? new Columns(
                StyledColumn(2U, 2U),
                StyledColumn(3U, 3U))
            : new Columns(StyledColumn(2U, 3U));
        worksheetPart.Worksheet = new Worksheet(
            new SheetFormatProperties { DefaultRowHeight = 15D },
            columns,
            new SheetData(
                new Row(new Cell { CellReference = "B1" }) { RowIndex = 1U }));
        worksheetPart.Worksheet.Save();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "Columns"
        });
        workbookPart.Workbook.Save();
    }

    private static Column StyledColumn(uint min, uint max) => new()
    {
        Min = min,
        Max = max,
        Width = 18.5D,
        Hidden = true,
        Style = 1U,
        CustomWidth = true
    };

    private static void WriteWorkbookWithoutStyles(string path, bool addEmptyStylesPart)
    {
        using var document = SpreadsheetDocument.Create(
            path,
            DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        if (addEmptyStylesPart)
        {
            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = new Stylesheet();
            stylesPart.Stylesheet.Save();
        }

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(
            new SheetFormatProperties { DefaultRowHeight = 20D },
            new SheetData(
                new Row(new Cell { CellReference = "A1" }) { RowIndex = 1U }));
        worksheetPart.Worksheet.Save();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "NoStyles"
        });
        workbookPart.Workbook.Save();
    }

    private static void WritePageSetupDefaultsWorkbook(string path, bool explicitDefaults)
    {
        using var document = SpreadsheetDocument.Create(
            path,
            DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var worksheet = new Worksheet(new SheetData());
        if (explicitDefaults)
        {
            worksheet.Append(new PageSetup
            {
                PaperSize = 1U,
                Scale = 100U,
                FirstPageNumber = 1U,
                FitToWidth = 1U,
                FitToHeight = 1U,
                PageOrder = PageOrderValues.DownThenOver,
                Orientation = OrientationValues.Default,
                UsePrinterDefaults = true,
                BlackAndWhite = false,
                Draft = false,
                CellComments = CellCommentsValues.None,
                UseFirstPageNumber = false,
                Errors = PrintErrorValues.Displayed,
                HorizontalDpi = 600U,
                VerticalDpi = 600U,
                Copies = 1U
            });
        }

        worksheetPart.Worksheet = worksheet;
        worksheetPart.Worksheet.Save();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "PageDefaults"
        });
        workbookPart.Workbook.Save();
    }

    private static void WriteRowStyleWorkbook(string path, bool customFormat)
    {
        using var document = SpreadsheetDocument.Create(
            path,
            DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = BuildBaselineStylesheet();
        stylesPart.Stylesheet.Save();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(
            new SheetData(
                new Row(new Cell { CellReference = "A1" })
                {
                    RowIndex = 1U,
                    StyleIndex = 1U,
                    CustomFormat = customFormat
                }));
        worksheetPart.Worksheet.Save();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "Rows"
        });
        workbookPart.Workbook.Save();
    }

    private static void WriteCellStreamWorkbook(string path, bool styledLastCell)
    {
        using var document = SpreadsheetDocument.Create(
            path,
            DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = BuildBaselineStylesheet();
        stylesPart.Stylesheet.Save();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var rows = Enumerable.Range(1, 250)
            .Select(index => new Row(
                new Cell
                {
                    CellReference = $"A{index}",
                    StyleIndex = index == 250 && styledLastCell ? 1U : 0U,
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new Text("SYNTHETIC-CELL-VALUE")),
                })
            {
                RowIndex = (uint)index,
            })
            .ToArray();
        worksheetPart.Worksheet = new Worksheet(new SheetData(rows));
        worksheetPart.Worksheet.Save();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "Stream",
        });
        workbookPart.Workbook.Save();
    }

    private static Stylesheet BuildStylesheet(bool bold)
    {
        var styledFont = new SpreadsheetFont(
            new Bold { Val = bold },
            new Italic(),
            new FontSize { Val = 12D },
            new SpreadsheetColor { Indexed = 2U },
            new FontName { Val = "Arial" });

        var borderColor = new SpreadsheetColor { Indexed = 4U };
        var borders = new Borders(
            new Border(
                new LeftBorder(),
                new RightBorder(),
                new TopBorder(),
                new BottomBorder(),
                new DiagonalBorder()),
            new Border(
                new LeftBorder(borderColor.CloneNode(true)) { Style = BorderStyleValues.Thin },
                new RightBorder(borderColor.CloneNode(true)) { Style = BorderStyleValues.Thin },
                new TopBorder(borderColor.CloneNode(true)) { Style = BorderStyleValues.Thin },
                new BottomBorder(borderColor.CloneNode(true)) { Style = BorderStyleValues.Thin },
                new DiagonalBorder()))
        { Count = 2U };

        return new Stylesheet(
            new NumberingFormats(
                new NumberingFormat { NumberFormatId = 164U, FormatCode = "0.0000" })
            { Count = 1U },
            new Fonts(
                new SpreadsheetFont(
                    new FontSize { Val = 11D },
                    new SpreadsheetColor { Rgb = "FF000000" },
                    new FontName { Val = "Calibri" }),
                styledFont)
            { Count = 2U },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(
                    new ForegroundColor { Theme = 4U, Tint = 0.4D },
                    new BackgroundColor { Indexed = 64U })
                { PatternType = PatternValues.Solid }))
            { Count = 3U },
            borders,
            new CellStyleFormats(
                new CellFormat { FontId = 0U, FillId = 0U, BorderId = 0U, NumberFormatId = 0U })
            { Count = 1U },
            new CellFormats(
                new CellFormat
                {
                    FormatId = 0U,
                    FontId = 0U,
                    FillId = 0U,
                    BorderId = 0U,
                    NumberFormatId = 0U
                },
                new CellFormat
                {
                    FormatId = 0U,
                    FontId = 1U,
                    FillId = 2U,
                    BorderId = 1U,
                    NumberFormatId = 164U,
                    ApplyFont = true,
                    ApplyFill = true,
                    ApplyBorder = true,
                    ApplyNumberFormat = true,
                    ApplyAlignment = true,
                    ApplyProtection = true,
                    Alignment = new Alignment
                    {
                        Horizontal = HorizontalAlignmentValues.Center,
                        Vertical = VerticalAlignmentValues.Top,
                        WrapText = true,
                        Indent = 1U,
                        ShrinkToFit = true
                    },
                    Protection = new Protection { Locked = false }
                },
                new CellFormat
                {
                    FormatId = 0U,
                    FontId = 0U,
                    FillId = 0U,
                    BorderId = 0U,
                    NumberFormatId = 43U,
                    ApplyNumberFormat = true
                })
            { Count = 3U },
            new CellStyles(new CellStyle { Name = "Normal", FormatId = 0U, BuiltinId = 0U })
            { Count = 1U });
    }

    private static Stylesheet BuildBaselineStylesheet() => new(
        new Fonts(
            new SpreadsheetFont(
                new FontSize { Val = 11D },
                new SpreadsheetColor { Rgb = "FF000000" },
                new FontName { Val = "Arial" }),
            new SpreadsheetFont(
                new FontSize { Val = 11D },
                new SpreadsheetColor { Rgb = "FF000000" },
                new FontName { Val = "Calibri" }))
        { Count = 2U },
        new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }))
        { Count = 2U },
        new Borders(
            new Border(
                new LeftBorder(),
                new RightBorder(),
                new TopBorder(),
                new BottomBorder(),
                new DiagonalBorder()))
        { Count = 1U },
        new CellStyleFormats(
            new CellFormat { FontId = 0U, FillId = 0U, BorderId = 0U, NumberFormatId = 0U })
        { Count = 1U },
        new CellFormats(
            new CellFormat { FontId = 0U, FillId = 0U, BorderId = 0U, NumberFormatId = 0U },
            new CellFormat
            {
                FontId = 1U,
                FillId = 0U,
                BorderId = 0U,
                NumberFormatId = 0U,
                ApplyFont = true
            })
        { Count = 2U },
        new CellStyles(new CellStyle { Name = "Normal", FormatId = 0U, BuiltinId = 0U })
        { Count = 1U });

    private static AppearanceEntry Entry(string location, string property, string value) =>
        new(location, property, value);

    private const string SyntheticThemeXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Synthetic">
          <a:themeElements>
            <a:clrScheme name="Synthetic">
              <a:dk1><a:srgbClr val="000000"/></a:dk1>
              <a:lt1><a:srgbClr val="FFFFFF"/></a:lt1>
              <a:dk2><a:srgbClr val="1F497D"/></a:dk2>
              <a:lt2><a:srgbClr val="EEECE1"/></a:lt2>
              <a:accent1><a:srgbClr val="336699"/></a:accent1>
              <a:accent2><a:srgbClr val="C0504D"/></a:accent2>
              <a:accent3><a:srgbClr val="9BBB59"/></a:accent3>
              <a:accent4><a:srgbClr val="8064A2"/></a:accent4>
              <a:accent5><a:srgbClr val="4BACC6"/></a:accent5>
              <a:accent6><a:srgbClr val="F79646"/></a:accent6>
              <a:hlink><a:srgbClr val="0000FF"/></a:hlink>
              <a:folHlink><a:srgbClr val="800080"/></a:folHlink>
            </a:clrScheme>
            <a:fontScheme name="Synthetic">
              <a:majorFont><a:latin typeface="Cambria"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>
              <a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont>
            </a:fontScheme>
            <a:fmtScheme name="Synthetic">
              <a:fillStyleLst/><a:lnStyleLst/><a:effectStyleLst/><a:bgFillStyleLst/>
            </a:fmtScheme>
          </a:themeElements>
        </a:theme>
        """;

    private sealed class TemporaryFingerprintDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"jet-appearance-{Guid.NewGuid():N}");

        internal TemporaryFingerprintDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
