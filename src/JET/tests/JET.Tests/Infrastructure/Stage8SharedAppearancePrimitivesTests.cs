using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using JET.Infrastructure;
using Xunit;
using SpreadsheetColor = DocumentFormat.OpenXml.Spreadsheet.Color;
using SpreadsheetFont = DocumentFormat.OpenXml.Spreadsheet.Font;

namespace JET.Tests.Infrastructure;

public sealed class Stage8SharedAppearancePrimitivesTests
{
    private const string SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void WorkbookStylePatcher_ChangesOnlyRequestedComponents_AndAppendsWithoutReindexing()
    {
        using var stream = new MemoryStream();
        using var document = CreateWorkbook(stream, BasicWorksheet());
        var stylesPart = document.WorkbookPart!.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = ComponentFixtureStylesheet();
        stylesPart.Stylesheet.Save();

        var originalFormats = stylesPart.Stylesheet.CellFormats!.Elements<CellFormat>().ToArray();
        var originalOuterXml = originalFormats[0].OuterXml;
        var originalFormatCount = originalFormats.Length;
        var originalFontCount = stylesPart.Stylesheet.Fonts!.Elements<SpreadsheetFont>().Count();
        var originalFillCount = stylesPart.Stylesheet.Fills!.Elements<Fill>().Count();

        var patcher = CreateStylePatcher(stylesPart);
        var patch = CreateStylePatch(
            ("FontName", "Calibri"),
            ("FontSize", 12D),
            ("Bold", true),
            ("FontColorArgb", "FFFF0000"),
            ("FillForegroundArgb", "FF0066FF"));
        var patchedIndex = InvokeStylePatch(patcher, 0U, patch);
        var repeatedIndex = InvokeStylePatch(patcher, 0U, patch);

        Assert.Equal(patchedIndex, repeatedIndex);
        Assert.Equal((uint)originalFormatCount, patchedIndex);
        Assert.Equal(originalFormatCount + 1, stylesPart.Stylesheet.CellFormats!.Elements<CellFormat>().Count());
        Assert.Equal(originalFontCount + 1, stylesPart.Stylesheet.Fonts!.Elements<SpreadsheetFont>().Count());
        Assert.Equal(originalFillCount + 1, stylesPart.Stylesheet.Fills!.Elements<Fill>().Count());
        Assert.Equal(originalOuterXml, stylesPart.Stylesheet.CellFormats!.Elements<CellFormat>().First().OuterXml);

        var prototype = stylesPart.Stylesheet.CellFormats!.Elements<CellFormat>().ElementAt(0);
        var patched = stylesPart.Stylesheet.CellFormats!.Elements<CellFormat>().ElementAt((int)patchedIndex);
        Assert.Equal(prototype.BorderId?.Value, patched.BorderId?.Value);
        Assert.Equal(prototype.NumberFormatId?.Value, patched.NumberFormatId?.Value);
        Assert.Equal(prototype.FormatId?.Value, patched.FormatId?.Value);
        Assert.Equal(prototype.ApplyBorder?.Value, patched.ApplyBorder?.Value);
        Assert.Equal(prototype.ApplyNumberFormat?.Value, patched.ApplyNumberFormat?.Value);
        Assert.Equal(prototype.ApplyProtection?.Value, patched.ApplyProtection?.Value);
        Assert.Equal(prototype.Protection?.OuterXml, patched.Protection?.OuterXml);
        Assert.Equal(prototype.Alignment?.OuterXml, patched.Alignment?.OuterXml);
        Assert.True(patched.ApplyFont?.Value);
        Assert.True(patched.ApplyFill?.Value);

        var font = stylesPart.Stylesheet.Fonts!
            .Elements<SpreadsheetFont>()
            .ElementAt(checked((int)patched.FontId!.Value));
        Assert.Equal("Calibri", font.FontName?.Val?.Value);
        Assert.Equal(12D, font.FontSize?.Val?.Value);
        Assert.NotNull(font.Bold);
        Assert.NotNull(font.Italic);
        Assert.Equal("FFFF0000", font.Color?.Rgb?.Value);

        var fill = stylesPart.Stylesheet.Fills!
            .Elements<Fill>()
            .ElementAt(checked((int)patched.FillId!.Value));
        Assert.Equal(PatternValues.Solid, fill.PatternFill?.PatternType?.Value);
        Assert.Equal("FF0066FF", fill.PatternFill?.ForegroundColor?.Rgb?.Value);

        var cell = document.WorkbookPart.WorksheetParts.Single()
            .Worksheet.Descendants<Cell>().Single();
        cell.StyleIndex = patchedIndex;
        document.WorkbookPart.WorksheetParts.Single().Worksheet.Save();
        Assert.Empty(new OpenXmlValidator().Validate(document));
    }

    [Fact]
    public void DirectTemplateCellEditor_PatchColumn_PreservesWidthPayloadsAndPerCellDifferences()
    {
        using var stream = new MemoryStream();
        var worksheet = new Worksheet(
            new Columns(new Column
            {
                Min = 3,
                Max = 3,
                Width = 40.140625D,
                CustomWidth = true,
                Style = 0
            }),
            new SheetData(
                new Row(
                    InlineTextCell("A1", "allowlisted-value", 0),
                    InlineTextCell("C1", "keep-one", 0)) { RowIndex = 1 },
                new Row(
                    new Cell
                    {
                        CellReference = "C2",
                        StyleIndex = 1,
                        DataType = CellValues.Number,
                        CellValue = new CellValue("42")
                    }) { RowIndex = 2 }));
        using var document = CreateWorkbook(stream, worksheet);
        var stylesPart = document.WorkbookPart!.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = ComponentFixtureStylesheet();
        stylesPart.Stylesheet.Save();

        var originalColumn = (Column)worksheet.GetFirstChild<Columns>()!.Elements<Column>().Single().CloneNode(true);
        var originalPayloads = worksheet.Descendants<Cell>()
            .Where(cell => cell.CellReference!.Value!.StartsWith('C'))
            .ToDictionary(cell => cell.CellReference!.Value!, PayloadWithoutStyle, StringComparer.Ordinal);
        var prototypeFormats = stylesPart.Stylesheet.CellFormats!
            .Elements<CellFormat>()
            .Select(format => (CellFormat)format.CloneNode(true))
            .ToArray();

        var patcher = CreateStylePatcher(stylesPart);
        var editor = CreateCellEditor(
            worksheet,
            ["A1"],
            patcher,
            [3U]);
        var typedEditor = Assert.IsType<DirectTemplateCellEditor>(editor);
        typedEditor.Clear("A1");
        var incomplete = Assert.Throws<InvalidOperationException>(
            typedEditor.EnsureAllAllowedCellsWereEdited);
        Assert.Contains("styleColumns=[3]", incomplete.Message, StringComparison.Ordinal);
        var wrapPatch = CreateStylePatch(("WrapText", true));
        InvokePatchColumn(editor, 3U, wrapPatch);
        var duplicate = Assert.Throws<TargetInvocationException>(
            () => InvokePatchColumn(editor, 3U, wrapPatch));
        Assert.IsType<InvalidOperationException>(duplicate.InnerException);
        typedEditor.EnsureAllAllowedCellsWereEdited();

        var column = worksheet.GetFirstChild<Columns>()!.Elements<Column>().Single();
        Assert.Equal(originalColumn.Min?.Value, column.Min?.Value);
        Assert.Equal(originalColumn.Max?.Value, column.Max?.Value);
        Assert.Equal(originalColumn.Width?.Value, column.Width?.Value);
        Assert.Equal(originalColumn.CustomWidth?.Value, column.CustomWidth?.Value);
        Assert.NotEqual(originalColumn.Style?.Value, column.Style?.Value);

        foreach (var cell in worksheet.Descendants<Cell>()
                     .Where(cell => cell.CellReference!.Value!.StartsWith('C')))
        {
            Assert.Equal(originalPayloads[cell.CellReference!.Value!], PayloadWithoutStyle(cell));
            var originalIndex = cell.CellReference!.Value == "C1" ? 0 : 1;
            var patched = stylesPart.Stylesheet.CellFormats!
                .Elements<CellFormat>()
                .ElementAt(checked((int)cell.StyleIndex!.Value));
            var prototype = prototypeFormats[originalIndex];
            Assert.True(patched.Alignment?.WrapText?.Value);
            Assert.Equal(prototype.Alignment?.Horizontal?.Value, patched.Alignment?.Horizontal?.Value);
            Assert.Equal(prototype.Alignment?.Vertical?.Value, patched.Alignment?.Vertical?.Value);
            Assert.Equal(prototype.Alignment?.Indent?.Value, patched.Alignment?.Indent?.Value);
            Assert.Equal(prototype.Alignment?.ShrinkToFit?.Value, patched.Alignment?.ShrinkToFit?.Value);
            Assert.Equal(prototype.BorderId?.Value, patched.BorderId?.Value);
            Assert.Equal(prototype.NumberFormatId?.Value, patched.NumberFormatId?.Value);
            Assert.Equal(prototype.Protection?.OuterXml, patched.Protection?.OuterXml);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirectTemplateCellEditor_PatchColumn_FailsClosedWithoutDedicatedPrototype(
        bool missingColumnDefinition)
    {
        using var stream = new MemoryStream();
        var worksheet = new Worksheet();
        if (!missingColumnDefinition)
        {
            worksheet.Append(new Columns(new Column
            {
                Min = 2,
                Max = 4,
                Width = 20D,
                CustomWidth = true,
                Style = 0
            }));
        }
        worksheet.Append(new SheetData(
            new Row(InlineTextCell("A1", "value", 0)) { RowIndex = 1 }));
        using var document = CreateWorkbook(stream, worksheet);
        var stylesPart = document.WorkbookPart!.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = ComponentFixtureStylesheet();
        stylesPart.Stylesheet.Save();

        var patcher = CreateStylePatcher(stylesPart);
        var editor = CreateCellEditor(worksheet, ["A1"], patcher, [3U]);
        Assert.IsType<DirectTemplateCellEditor>(editor).Clear("A1");
        var exception = Assert.Throws<TargetInvocationException>(() =>
            InvokePatchColumn(editor, 3U, CreateStylePatch(("WrapText", true))));
        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public void DirectTemplateOverlay_MapsTemplateOnlyCells_AndPreservesTemplatePageSetup()
    {
        using var stream = new MemoryStream();
        using (var document = CreateWorkbook(
                   stream,
                   new Worksheet(
                       new SheetData(
                           new Row(InlineTextCell("B15", "old-15", 0)) { RowIndex = 15 },
                           new Row(InlineTextCell("B16", "template-only", 0)) { RowIndex = 16 },
                           new Row(InlineTextCell("B17", "old-17", 0)) { RowIndex = 17 }),
                       new PageMargins
                       {
                           Left = 0.7,
                           Right = 0.7,
                           Top = 0.75,
                           Bottom = 0.75,
                           Header = 0.3,
                           Footer = 0.3
                       },
                       new PageSetup
                       {
                           Orientation = OrientationValues.Portrait,
                           PaperSize = 9,
                           FitToWidth = 1,
                           FitToHeight = 0
                       })))
        {
            var part = document.WorkbookPart!.WorksheetParts.Single();
            var plan = CreateOverlayPlanWithTemplateStyleMapper(
                (row, column, style) =>
                    column == 2 && row is >= 15 and <= 17 ? 1U : style);
            using var overlay = new DirectTemplateWorksheetOverlayWriter(
                "Synthetic",
                part,
                plan,
                CancellationToken.None);
            overlay.Start(generatedDimension: null, generatedColumns: null);
            overlay.WriteRow(InlineTextRow(15, "B15", "new-15", style: 0));
            overlay.WriteRow(InlineTextRow(17, "B17", "new-17", style: 0));
            XNamespace spreadsheet = SpreadsheetNamespace;
            overlay.Complete(
            [
                new XElement(
                    spreadsheet + "pageSetup",
                    new XAttribute("orientation", "landscape"),
                    new XAttribute("paperSize", "5"))
            ]);
        }

        stream.Position = 0;
        using var read = SpreadsheetDocument.Open(stream, false);
        var result = read.WorkbookPart!.WorksheetParts.Single().Worksheet;
        Assert.Equal("new-15", Cell(result, "B15").InnerText);
        Assert.Equal("template-only", Cell(result, "B16").InnerText);
        Assert.Equal("new-17", Cell(result, "B17").InnerText);
        Assert.All(
            new[] { "B15", "B16", "B17" },
            reference => Assert.Equal(1U, Cell(result, reference).StyleIndex?.Value));
        Assert.Empty(result.Descendants<Pane>());
        var pageSetup = Assert.Single(result.Elements<PageSetup>());
        Assert.Equal(OrientationValues.Portrait, pageSetup.Orientation?.Value);
        Assert.Equal(9U, pageSetup.PaperSize?.Value);
        Assert.Equal(1U, pageSetup.FitToWidth?.Value);
        Assert.Equal(0U, pageSetup.FitToHeight?.Value);
    }

    [Fact]
    public void DirectTemplateOverlay_ExplicitAutoFitSurvivesWhenTemplateColumnIsWider()
    {
        using var stream = new MemoryStream();
        using (var document = CreateWorkbook(
                   stream,
                   new Worksheet(
                       new Columns(new Column
                       {
                           Min = 1,
                           Max = 1,
                           Width = 80D,
                           Style = 0,
                           CustomWidth = true
                       }),
                       new SheetData(
                           new Row(InlineTextCell("A1", "template", 0)) { RowIndex = 1 }))))
        {
            XNamespace spreadsheet = SpreadsheetNamespace;
            var generatedColumns = new XElement(
                spreadsheet + "cols",
                new XElement(
                    spreadsheet + "col",
                    new XAttribute("min", "1"),
                    new XAttribute("max", "1"),
                    new XAttribute("width", "12"),
                    new XAttribute("bestFit", "1"),
                    new XAttribute("customWidth", "1")));
            var part = document.WorkbookPart!.WorksheetParts.Single();
            var plan = CreateOverlayPlanWithTemplateStyleMapper(
                static (_, _, style) => style,
                mergeSourceColumnWidths: true);
            using var overlay = new DirectTemplateWorksheetOverlayWriter(
                "Synthetic",
                part,
                plan,
                CancellationToken.None);
            overlay.Start(generatedDimension: null, generatedColumns);
            overlay.Complete([]);
        }

        stream.Position = 0;
        using var read = SpreadsheetDocument.Open(stream, false);
        var column = read.WorkbookPart!.WorksheetParts.Single()
            .Worksheet.GetFirstChild<Columns>()!.Elements<Column>().Single();
        Assert.Equal(80D, column.Width?.Value);
        Assert.True(column.BestFit?.Value);
        Assert.True(column.CustomWidth?.Value);
    }

    [Theory]
    [InlineData(80D, 12D, 80D)]
    [InlineData(80D, 120D, 120D)]
    public void DirectTemplateRewrite_AutoFitColumns_NeverShrinkAndAlwaysSetBestFit(
        double templateWidth,
        double measuredMinimum,
        double expectedWidth)
    {
        using var stream = new MemoryStream();
        using (var document = CreateWorkbook(
                   stream,
                   new Worksheet(
                       new SheetDimension { Reference = "A1:A1" },
                       new Columns(new Column
                       {
                           Min = 1,
                           Max = 1,
                           Width = templateWidth,
                           Style = 0,
                           CustomWidth = true
                       }),
                       new SheetData(
                           new Row(InlineTextCell("A1", "prototype", 0)) { RowIndex = 1 }))))
        {
            var part = document.WorkbookPart!.WorksheetParts.Single();
            var plan = CreateRewritePlanWithAutoFit(measuredMinimum);
            DirectTemplateWorksheetRewriter.Rewrite(part, plan, CancellationToken.None);
        }

        stream.Position = 0;
        using var read = SpreadsheetDocument.Open(stream, false);
        var column = read.WorkbookPart!.WorksheetParts.Single()
            .Worksheet.GetFirstChild<Columns>()!.Elements<Column>().Single();
        Assert.Equal(expectedWidth, column.Width?.Value);
        Assert.True(column.BestFit?.Value);
        Assert.True(column.CustomWidth?.Value);
    }

    [Fact]
    public void DirectTemplateRewrite_ExplicitEmptyAutoFit_DoesNotMarkMinimumWidthExpansionAsBestFit()
    {
        using var stream = new MemoryStream();
        using (var document = CreateWorkbook(
                   stream,
                   new Worksheet(
                       new SheetDimension { Reference = "A1:A1" },
                       new Columns(new Column
                       {
                           Min = 1,
                           Max = 1,
                           Width = 80D,
                           Style = 0,
                           CustomWidth = true
                       }),
                       new SheetData(
                           new Row(InlineTextCell("A1", "prototype", 0)) { RowIndex = 1 }))))
        {
            var part = document.WorkbookPart!.WorksheetParts.Single();
            var plan = CreateRewritePlan(120D, new HashSet<uint>());
            DirectTemplateWorksheetRewriter.Rewrite(part, plan, CancellationToken.None);
        }

        stream.Position = 0;
        using var read = SpreadsheetDocument.Open(stream, false);
        var column = read.WorkbookPart!.WorksheetParts.Single()
            .Worksheet.GetFirstChild<Columns>()!.Elements<Column>().Single();
        Assert.Equal(120D, column.Width?.Value);
        Assert.Null(column.BestFit);
        Assert.True(column.CustomWidth?.Value);
    }

    [Fact]
    public void ReportSheetWriter_DeterministicAutoFitWidthAndAutomaticRowHeight_ArePinned()
    {
        var widths = new ExcelDisplayWidthTracker(["Header"]);
        widths.Observe(["late-row-very-long-display-value"]);
        var expectedWidth = Assert.Single(widths.Widths);

        using var stream = new MemoryStream();
        using (var document = CreateWorkbook(stream, new Worksheet()))
        {
            var part = document.WorkbookPart!.WorksheetParts.Single();
            using var writer = new ReportSheetWriter(
                "Synthetic",
                part,
                [new ReportColumn(1, 1, expectedWidth, BestFit: true)]);
            writer.WriteDataRow(1, [InlineTextCell("A1", "late-row-very-long-display-value", 0)]);
            writer.CloseAndSummarize(CancellationToken.None);
        }

        stream.Position = 0;
        using var read = SpreadsheetDocument.Open(stream, false);
        var worksheet = read.WorkbookPart!.WorksheetParts.Single().Worksheet;
        var column = Assert.Single(worksheet.GetFirstChild<Columns>()!.Elements<Column>());
        Assert.Equal(expectedWidth, column.Width?.Value);
        Assert.True(column.BestFit?.Value);
        Assert.True(column.CustomWidth?.Value);
        var row = Assert.Single(worksheet.GetFirstChild<SheetData>()!.Elements<Row>());
        Assert.Null(row.Height);
        Assert.Null(row.CustomHeight);
    }

    private static SpreadsheetDocument CreateWorkbook(MemoryStream stream, Worksheet worksheet)
    {
        var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = worksheet;
        workbookPart.Workbook.AppendChild(new Sheets()).Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Synthetic"
        });
        workbookPart.Workbook.Save();
        worksheet.Save();
        return document;
    }

    private static Worksheet BasicWorksheet() => new(
        new SheetData(
            new Row(InlineTextCell("A1", "value", 0)) { RowIndex = 1 }));

    private static Stylesheet ComponentFixtureStylesheet()
    {
        var numberFormats = new NumberingFormats(
            new NumberingFormat { NumberFormatId = 164, FormatCode = "0.000" })
        { Count = 1 };
        var fonts = new Fonts(
            new SpreadsheetFont(
                new Italic(),
                new FontSize { Val = 9D },
                new FontName { Val = "Arial" }))
        { Count = 1 };
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }))
        { Count = 2 };
        var borders = new Borders(
            new Border(
                new LeftBorder { Style = BorderStyleValues.Thin },
                new RightBorder(),
                new TopBorder(),
                new BottomBorder(),
                new DiagonalBorder()),
            new Border(
                new LeftBorder(),
                new RightBorder(),
                new TopBorder(),
                new BottomBorder { Style = BorderStyleValues.Medium },
                new DiagonalBorder()))
        { Count = 2 };
        var styleFormats = new CellStyleFormats(
            new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 })
        { Count = 1 };
        var formats = new CellFormats(
            new CellFormat
            {
                NumberFormatId = 164,
                FontId = 0,
                FillId = 0,
                BorderId = 0,
                FormatId = 0,
                ApplyNumberFormat = true,
                ApplyFont = true,
                ApplyFill = true,
                ApplyBorder = true,
                ApplyAlignment = true,
                ApplyProtection = true,
                Alignment = new Alignment
                {
                    Horizontal = HorizontalAlignmentValues.Right,
                    Vertical = VerticalAlignmentValues.Bottom,
                    WrapText = false,
                    Indent = 2,
                    ShrinkToFit = true
                },
                Protection = new Protection { Locked = false }
            },
            new CellFormat
            {
                NumberFormatId = 0,
                FontId = 0,
                FillId = 0,
                BorderId = 1,
                FormatId = 0,
                ApplyFont = true,
                ApplyBorder = true,
                ApplyAlignment = true,
                ApplyProtection = true,
                Alignment = new Alignment
                {
                    Horizontal = HorizontalAlignmentValues.Center,
                    Vertical = VerticalAlignmentValues.Top,
                    WrapText = false,
                    Indent = 0,
                    ShrinkToFit = false
                },
                Protection = new Protection { Locked = true }
            })
        { Count = 2 };
        return new Stylesheet(numberFormats, fonts, fills, borders, styleFormats, formats);
    }

    private static object CreateStylePatcher(WorkbookStylesPart stylesPart)
    {
        var type = RequiredInfrastructureType("WorkbookStylePatcher");
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(WorkbookStylesPart);
            });
        Assert.NotNull(constructor);
        return constructor!.Invoke([stylesPart]);
    }

    private static object CreateStylePatch(params (string Name, object? Value)[] values)
    {
        var type = RequiredInfrastructureType("WorkbookStylePatch");
        return ConstructByNamedParameters(
            type,
            values.ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase));
    }

    private static uint InvokeStylePatch(object patcher, uint prototypeStyle, object patch)
    {
        var method = patcher.GetType().GetMethod(
            "Patch",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<uint>(method!.Invoke(patcher, [prototypeStyle, patch]));
    }

    private static object CreateCellEditor(
        Worksheet worksheet,
        IReadOnlyCollection<string> allowedCells,
        object patcher,
        IReadOnlyCollection<uint> allowedStyleColumns)
    {
        var constructor = typeof(DirectTemplateCellEditor)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.GetParameters().Any(parameter =>
                string.Equals(parameter.Name, "allowedStyleColumns", StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(constructor);
        return Construct(
            constructor!,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["worksheet"] = worksheet,
                ["allowedCellReferences"] = allowedCells,
                ["stylePatcher"] = patcher,
                ["allowedStyleColumns"] = allowedStyleColumns
            });
    }

    private static void InvokePatchColumn(object editor, uint column, object patch)
    {
        var method = editor.GetType().GetMethod(
            "PatchColumn",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(editor, [column, patch]);
    }

    private static DirectTemplateWorksheetOverlayPlan CreateOverlayPlanWithTemplateStyleMapper(
        Func<uint, uint, uint, uint> mapper,
        bool mergeSourceColumnWidths = false)
    {
        var constructor = typeof(DirectTemplateWorksheetOverlayPlan)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Any(parameter =>
                string.Equals(parameter.Name, "IsDynamicRow", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(
            constructor.GetParameters(),
            parameter => string.Equals(
                parameter.Name,
                "MapTemplateCellStyle",
                StringComparison.OrdinalIgnoreCase));
        return Assert.IsType<DirectTemplateWorksheetOverlayPlan>(Construct(
            constructor,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["IsDynamicRow"] = (Func<uint, bool>)(row => row is 15 or 17),
                ["IsDynamicCell"] = (Func<uint, uint, bool>)((row, column) =>
                    row is 15 or 17 && column == 2),
                ["UseSourcePostSheetDataElements"] = new HashSet<string>(StringComparer.Ordinal),
                ["UseSourceDimension"] = false,
                ["PreserveTemplateExtent"] = false,
                ["UseSourceColumnsWhenTemplateMissing"] = false,
                ["UseSourceColumns"] = false,
                ["MergeSourceColumnWidths"] = mergeSourceColumnWidths,
                ["UseSourceCellStyle"] = (Func<uint, uint, bool>)((_, _) => false),
                ["MapTemplateCellStyle"] = mapper
            }));
    }

    private static DirectTemplateWorksheetRewritePlan CreateRewritePlanWithAutoFit(
        double measuredMinimum) => CreateRewritePlan(measuredMinimum, new HashSet<uint> { 1U });

    private static DirectTemplateWorksheetRewritePlan CreateRewritePlan(
        double measuredMinimum,
        IReadOnlySet<uint> autoFitColumns)
    {
        var constructor = typeof(DirectTemplateWorksheetRewritePlan)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Any(parameter =>
                string.Equals(parameter.Name, "FirstDynamicRow", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(
            constructor.GetParameters(),
            parameter => string.Equals(
                parameter.Name,
                "AutoFitColumns",
                StringComparison.OrdinalIgnoreCase));
        Func<DirectTemplateWorksheetPrototype, CancellationToken, IEnumerable<XElement>> createRows =
            (prototype, _) => [new XElement(prototype.DynamicRow)];
        return Assert.IsType<DirectTemplateWorksheetRewritePlan>(Construct(
            constructor,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["FirstDynamicRow"] = 1U,
                ["DimensionReference"] = "A1:A1",
                ["PrototypeColumns"] = new uint[] { 1 },
                ["MapColumnStyle"] = null,
                ["CreateDynamicRows"] = createRows,
                ["MinimumColumnWidths"] = new Dictionary<uint, double> { [1] = measuredMinimum },
                ["AutoFitColumns"] = autoFitColumns
            }));
    }

    private static Type RequiredInfrastructureType(string name)
    {
        var type = typeof(ReportTemplatePackage).Assembly.GetType($"JET.Infrastructure.{name}");
        Assert.NotNull(type);
        return type!;
    }

    private static object ConstructByNamedParameters(
        Type type,
        IReadOnlyDictionary<string, object?> values)
    {
        var constructor = type
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .FirstOrDefault();
        Assert.NotNull(constructor);
        return Construct(constructor!, values);
    }

    private static object Construct(
        ConstructorInfo constructor,
        IReadOnlyDictionary<string, object?> values)
    {
        var arguments = constructor.GetParameters()
            .Select(parameter => values.TryGetValue(parameter.Name!, out var value)
                ? value
                : parameter.HasDefaultValue
                    ? parameter.DefaultValue
                    : parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null)
            .ToArray();
        return constructor.Invoke(arguments);
    }

    private static Cell InlineTextCell(string reference, string value, uint style) => new()
    {
        CellReference = reference,
        StyleIndex = style,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(
            new Text(value) { Space = SpaceProcessingModeValues.Preserve })
    };

    private static XElement InlineTextRow(uint row, string reference, string value, uint style)
    {
        XNamespace spreadsheet = SpreadsheetNamespace;
        XNamespace xml = XNamespace.Xml;
        return new XElement(
            spreadsheet + "row",
            new XAttribute("r", row.ToString(CultureInfo.InvariantCulture)),
            new XElement(
                spreadsheet + "c",
                new XAttribute("r", reference),
                new XAttribute("s", style.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("t", "inlineStr"),
                new XElement(
                    spreadsheet + "is",
                    new XElement(
                        spreadsheet + "t",
                        new XAttribute(xml + "space", "preserve"),
                        value))));
    }

    private static string PayloadWithoutStyle(Cell cell)
    {
        var clone = (Cell)cell.CloneNode(true);
        clone.StyleIndex = null;
        return clone.OuterXml;
    }

    private static Cell Cell(Worksheet worksheet, string reference) =>
        worksheet.Descendants<Cell>().Single(cell => string.Equals(
            cell.CellReference?.Value,
            reference,
            StringComparison.OrdinalIgnoreCase));
}
