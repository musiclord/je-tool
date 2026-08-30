using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SpreadsheetPhysicalLayoutFingerprintTests
{
    [Fact]
    public void SyntheticWorkbook_CapturesPhysicalLayoutAndIsDeterministic()
    {
        using var directory = new TempProjectRoot();
        var path = Path.Combine(directory.Path, "physical-layout.xlsx");
        WriteWorkbook(
            path,
            LayoutFeature.BestFit |
            LayoutFeature.CustomWidth |
            LayoutFeature.Pane |
            LayoutFeature.PageSetup |
            LayoutFeature.PageMargins,
            "SYNTHETIC-LAYOUT-SENTINEL",
            includeSecondExplicitRow: true);

        var first = SpreadsheetPhysicalLayoutFingerprint.Capture(path);
        var second = SpreadsheetPhysicalLayoutFingerprint.Capture(path);

        Assert.Equal(first.JsonBytes, second.JsonBytes);
        Assert.Null(first.DescribeFirstDifference(second));
        Assert.Equal("Layout", first.Value("$workbook!sheet:0001", "name"));
        Assert.Equal("Tail", first.Value("$workbook!sheet:0002", "name"));
        Assert.Equal("true", first.Value("Layout!column:0001:1-1", "bestFit"));
        Assert.Equal("true", first.Value("Layout!column:0001:1-1", "customWidth"));
        Assert.Equal("auto", first.Value("Layout!row:0000001:1", "heightMode"));
        Assert.Equal("false", first.Value("Layout!row:0000001:1", "customHeight"));
        Assert.Equal("explicit", first.Value("Layout!row:0000002:2", "heightMode"));
        Assert.Equal("true", first.Value("Layout!row:0000002:2", "customHeight"));
        Assert.Equal("15", first.Value("Layout!row:0000002:2", "height"));
        Assert.Equal("true", first.Value("Layout!sheetView:1.pane", "present"));
        Assert.Equal("frozen", first.Value("Layout!sheetView:1.pane", "state"));
        Assert.Equal("1", first.Value("Layout!sheetView:1.pane", "ySplit"));
        Assert.Equal("true", first.Value("Layout!pageSetup", "present"));
        Assert.Equal("1", first.Value("Layout!pageSetup", "paperSize"));
        Assert.Equal("100", first.Value("Layout!pageSetup", "scale"));
        Assert.Equal("default", first.Value("Layout!pageSetup", "orientation"));
        Assert.Equal("false", first.Value("Layout!pageSetup", "blackAndWhite"));
        Assert.Equal("true", first.Value("Layout!pageMargins", "present"));
        Assert.Equal("0.7", first.Value("Layout!pageMargins", "left"));
        Assert.Equal("0.3", first.Value("Layout!pageMargins", "header"));
    }

    [Theory]
    [InlineData(LayoutFeature.BestFit, "Layout!column:0001:1-1")]
    [InlineData(LayoutFeature.CustomWidth, "Layout!column:0001:1-1")]
    [InlineData(LayoutFeature.ExplicitFirstRow, "Layout!row:0000001:1")]
    [InlineData(LayoutFeature.Pane, "Layout!sheetView:1.pane")]
    [InlineData(LayoutFeature.PageSetup, "Layout!pageSetup")]
    [InlineData(LayoutFeature.PageMargins, "Layout!pageMargins")]
    public void PhysicalMutation_IsDetectedEvenWhenCellAppearanceIsUnchanged(
        LayoutFeature mutation,
        string expectedLocation)
    {
        using var directory = new TempProjectRoot();
        var baselinePath = Path.Combine(directory.Path, "baseline.xlsx");
        var actualPath = Path.Combine(directory.Path, "actual.xlsx");
        WriteWorkbook(baselinePath, LayoutFeature.None, "SAME-VALUE");
        WriteWorkbook(actualPath, mutation, "SAME-VALUE");

        var difference = SpreadsheetPhysicalLayoutFingerprint
            .Capture(baselinePath)
            .DescribeFirstDifference(SpreadsheetPhysicalLayoutFingerprint.Capture(actualPath));

        Assert.NotNull(difference);
        Assert.Contains(expectedLocation, difference, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_IsValueBlindAndSourceSkipsCellSubtrees()
    {
        using var directory = new TempProjectRoot();
        var firstPath = Path.Combine(directory.Path, "first.xlsx");
        var secondPath = Path.Combine(directory.Path, "second.xlsx");
        const string firstSentinel = "SYNTHETIC-FIRST-PHYSICAL-SENTINEL";
        const string secondSentinel = "SYNTHETIC-SECOND-PHYSICAL-SENTINEL";
        WriteWorkbook(firstPath, LayoutFeature.PageSetup, firstSentinel);
        WriteWorkbook(secondPath, LayoutFeature.PageSetup, secondSentinel);

        var first = SpreadsheetPhysicalLayoutFingerprint.Capture(firstPath);
        var second = SpreadsheetPhysicalLayoutFingerprint.Capture(secondPath);

        Assert.Equal(first.JsonBytes, second.JsonBytes);
        Assert.DoesNotContain(firstSentinel, first.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(secondSentinel, second.Json, StringComparison.Ordinal);

        var sourcePath = Path.Combine(
            TestRepositoryPaths.RepositoryRoot,
            "src",
            "JET",
            "tests",
            "JET.Tests",
            "Infrastructure",
            "SpreadsheetPhysicalLayoutFingerprint.cs");
        var source = File.ReadAllText(sourcePath);
        Assert.DoesNotContain("Cell" + "Value", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Inline" + "String", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Shared" + "StringTable", source, StringComparison.Ordinal);
    }

    private static void WriteWorkbook(
        string path,
        LayoutFeature features,
        string cellText,
        bool includeSecondExplicitRow = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var document = SpreadsheetDocument.Create(
            path,
            DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());

        var layoutPart = workbookPart.AddNewPart<WorksheetPart>();
        layoutPart.Worksheet = CreateLayoutWorksheet(
            features,
            cellText,
            includeSecondExplicitRow);
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(layoutPart),
            SheetId = 1U,
            Name = "Layout"
        });

        var tailPart = workbookPart.AddNewPart<WorksheetPart>();
        tailPart.Worksheet = new Worksheet(
            new SheetData(new Row { RowIndex = 1U }));
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(tailPart),
            SheetId = 2U,
            Name = "Tail"
        });

        workbookPart.Workbook.Save();
    }

    private static Worksheet CreateLayoutWorksheet(
        LayoutFeature features,
        string cellText,
        bool includeSecondExplicitRow)
    {
        var view = new SheetView { WorkbookViewId = 0U };
        if (features.HasFlag(LayoutFeature.Pane))
        {
            view.Append(new Pane
            {
                State = PaneStateValues.Frozen,
                VerticalSplit = 1D,
                TopLeftCell = "A2",
                ActivePane = PaneValues.BottomLeft
            });
        }

        var column = new Column
        {
            Min = 1U,
            Max = 1U,
            Width = 12D,
            BestFit = features.HasFlag(LayoutFeature.BestFit),
            CustomWidth = features.HasFlag(LayoutFeature.CustomWidth)
        };
        var firstRow = new Row { RowIndex = 1U };
        if (features.HasFlag(LayoutFeature.ExplicitFirstRow))
        {
            firstRow.Height = 15D;
            firstRow.CustomHeight = true;
        }
        firstRow.Append(new Cell
        {
            CellReference = "A1",
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(cellText))
        });

        var sheetData = new SheetData(firstRow);
        if (includeSecondExplicitRow)
        {
            sheetData.Append(new Row
            {
                RowIndex = 2U,
                Height = 15D,
                CustomHeight = true
            });
        }

        var worksheet = new Worksheet(
            new SheetViews(view),
            new Columns(column),
            sheetData);
        if (features.HasFlag(LayoutFeature.PageMargins))
        {
            worksheet.Append(new PageMargins
            {
                Left = 0.7D,
                Right = 0.7D,
                Top = 0.75D,
                Bottom = 0.75D,
                Header = 0.3D,
                Footer = 0.3D
            });
        }
        if (features.HasFlag(LayoutFeature.PageSetup))
        {
            worksheet.Append(new PageSetup
            {
                PaperSize = 1U,
                Scale = 100U,
                FitToWidth = 1U,
                FitToHeight = 1U,
                Orientation = OrientationValues.Default,
                BlackAndWhite = false
            });
        }

        return worksheet;
    }

    [Flags]
    public enum LayoutFeature
    {
        None = 0,
        BestFit = 1 << 0,
        CustomWidth = 1 << 1,
        ExplicitFirstRow = 1 << 2,
        Pane = 1 << 3,
        PageSetup = 1 << 4,
        PageMargins = 1 << 5
    }
}
