using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// User-approved presentation contract for the six formal workbooks. These
/// assertions intentionally supersede legacy font-family and Field Info grid
/// styling while leaving workbook content and business semantics unchanged.
/// </summary>
public sealed class ReportWorkbookPresentationTests(ReportArtifactExportFixture fixture)
    : IClassFixture<ReportArtifactExportFixture>
{
    private const string CanonicalFontName = "微軟正黑體";
    private const string FieldInfoSheet = "自動化工具-檔案欄位資訊";
    private const string Step12Sheet = "step1-2 分錄編製人員說明";
    private const string Step41Sheet = "step4-1 符合高風險條件傳票明細";

    [Fact]
    public async Task EveryFormalWorkbook_UsesOneCanonicalSpreadsheetFontFamily()
    {
        var reports = await ExportAllFormalReportsAsync();

        Assert.Equal(6, reports.Count);
        foreach (var (kind, path) in reports)
        {
            using var document = SpreadsheetDocument.Open(path, false);
            var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
            var stylesheet = Assert.IsType<Stylesheet>(
                Assert.IsType<WorkbookStylesPart>(workbookPart.WorkbookStylesPart).Stylesheet);
            var fonts = stylesheet.Descendants<Font>().ToArray();

            Assert.NotEmpty(fonts);
            Assert.All(fonts, font => Assert.Equal(
                CanonicalFontName,
                font.FontName?.Val?.Value));
            Assert.Empty(stylesheet.Descendants<FontScheme>());

            var runFonts = workbookPart.WorksheetParts
                .SelectMany(part => part.Worksheet.Descendants<RunFont>())
                .Concat(workbookPart.SharedStringTablePart?.SharedStringTable?
                    .Descendants<RunFont>() ?? [])
                .ToArray();
            Assert.All(runFonts, font => Assert.Equal(CanonicalFontName, font.Val?.Value));

            Assert.True(
                fonts.All(font => font.FontName?.Val?.Value == CanonicalFontName),
                $"Formal workbook '{kind}' contains a non-canonical spreadsheet font.");
        }
    }

    [Fact]
    public async Task WorkingPaper_Step12ManualFields_AreUnlockedWithoutHighlightFill()
    {
        await fixture.ExportCriteriaSelectionReportAsync();
        var response = await fixture.ExportWorkingPaperAsync();
        var path = fixture.ArtifactPath(response.GetProperty("artifact"));
        using var document = SpreadsheetDocument.Open(path, false);

        foreach (var reference in new[] { "C12", "F12", "G12", "H12" })
        {
            var format = CellFormatFor(document, Step12Sheet, reference);
            var fill = FillFor(document, format);

            Assert.True(format.Protection?.Locked?.Value == false, reference);
            Assert.True(IsNoFill(fill), reference);
        }
    }

    [Fact]
    public async Task WorkingPaper_Step41_UsesNoCellRowOrColumnBorders()
    {
        await fixture.ExportCriteriaSelectionReportAsync();
        var response = await fixture.ExportWorkingPaperAsync();
        var path = fixture.ArtifactPath(response.GetProperty("artifact"));
        using var document = SpreadsheetDocument.Open(path, false);
        var worksheet = WorksheetFor(document, Step41Sheet);

        var cells = worksheet.Descendants<Cell>().ToArray();
        Assert.NotEmpty(cells);
        Assert.All(cells, cell => AssertBorderless(
            document,
            cell.StyleIndex?.Value ?? 0U,
            cell.CellReference?.Value ?? "cell"));

        Assert.All(
            worksheet.Descendants<Row>().Where(row => row.StyleIndex is not null),
            row => AssertBorderless(
                document,
                row.StyleIndex!.Value,
                $"row {row.RowIndex?.Value}"));
        Assert.All(
            worksheet.Descendants<Column>().Where(column => column.Style is not null),
            column => AssertBorderless(
                document,
                column.Style!.Value,
                $"column {column.Min?.Value}:{column.Max?.Value}"));
    }

    [Fact]
    public async Task ValidationFieldInfo_UsesTheSameBorderlessVisibleGridAsWorkingPaper()
    {
        var validation = await fixture.ExportValidationArtifactsAsync();
        await fixture.ExportCriteriaSelectionReportAsync();
        var workpaper = await fixture.ExportWorkingPaperAsync();
        var validationPath = fixture.ArtifactPath(FindArtifact(
            validation,
            ReportArtifactKindValues.ValidationReport));
        var workpaperPath = fixture.ArtifactPath(workpaper.GetProperty("artifact"));

        AssertVisibleFieldInfoCellsAreBorderless(validationPath);
        AssertVisibleFieldInfoCellsAreBorderless(workpaperPath);
        AssertVisibleFieldInfoStylesMatch(validationPath, workpaperPath);
    }

    private async Task<IReadOnlyDictionary<string, string>> ExportAllFormalReportsAsync()
    {
        var reports = new Dictionary<string, string>(StringComparer.Ordinal);
        var validation = await fixture.ExportValidationArtifactsAsync();
        foreach (var artifact in validation.GetProperty("artifacts").EnumerateArray())
        {
            reports.Add(
                artifact.GetProperty("kind").GetString()!,
                fixture.ArtifactPath(artifact));
        }

        var prescreen = await fixture.ExportPrescreenReportAsync();
        reports.Add(
            ReportArtifactKindValues.PrescreenReport,
            fixture.ArtifactPath(prescreen.GetProperty("artifact")));

        var criteria = await fixture.ExportCriteriaSelectionReportAsync();
        reports.Add(
            ReportArtifactKindValues.CriteriaSelectionReport,
            fixture.ArtifactPath(criteria.GetProperty("artifact")));

        var workpaper = await fixture.ExportWorkingPaperAsync();
        reports.Add(
            ReportArtifactKindValues.WorkingPaper,
            fixture.ArtifactPath(workpaper.GetProperty("artifact")));

        return reports;
    }

    private static System.Text.Json.JsonElement FindArtifact(
        System.Text.Json.JsonElement response,
        string kind) =>
        response.GetProperty("artifacts").EnumerateArray()
            .Single(artifact => string.Equals(
                artifact.GetProperty("kind").GetString(),
                kind,
                StringComparison.Ordinal));

    private static void AssertVisibleFieldInfoCellsAreBorderless(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var worksheet = WorksheetFor(document, FieldInfoSheet);
        var cells = worksheet.Descendants<Cell>()
            .Where(cell => VisibleColumn(cell.CellReference?.Value) is >= 1 and <= 5)
            .ToArray();

        Assert.NotEmpty(cells);
        Assert.All(cells, cell =>
        {
            var format = CellFormatFor(document, cell);
            var border = BorderFor(document, format);
            Assert.False(
                border.ChildElements.OfType<BorderPropertiesType>()
                    .Any(edge => edge.Style is not null),
                cell.CellReference?.Value);
        });
    }

    private static uint? VisibleColumn(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        uint column = 0;
        foreach (var character in reference)
        {
            if (character is < 'A' or > 'Z')
            {
                break;
            }

            column = checked(column * 26U + (uint)(character - 'A' + 1));
        }
        return column;
    }

    private static void AssertVisibleFieldInfoStylesMatch(
        string validationPath,
        string workpaperPath)
    {
        using var validation = SpreadsheetDocument.Open(validationPath, false);
        using var workpaper = SpreadsheetDocument.Open(workpaperPath, false);
        var validationStyles = VisibleFieldInfoStyles(validation);
        var workpaperStyles = VisibleFieldInfoStyles(workpaper);

        Assert.Equal(workpaperStyles.Keys, validationStyles.Keys);
        foreach (var reference in workpaperStyles.Keys)
        {
            Assert.Equal(workpaperStyles[reference], validationStyles[reference]);
        }
    }

    private static IReadOnlyDictionary<string, string> VisibleFieldInfoStyles(
        SpreadsheetDocument document) =>
        WorksheetFor(document, FieldInfoSheet).Descendants<Cell>()
            .Where(cell => VisibleColumn(cell.CellReference?.Value) is >= 1 and <= 5)
            .ToDictionary(
                cell => cell.CellReference!.Value!,
                cell => EffectiveStyleSignature(document, cell),
                StringComparer.Ordinal);

    private static string EffectiveStyleSignature(
        SpreadsheetDocument document,
        Cell cell)
    {
        var stylesheet = Assert.IsType<Stylesheet>(
            Assert.IsType<WorkbookStylesPart>(document.WorkbookPart!.WorkbookStylesPart)
                .Stylesheet);
        var format = CellFormatFor(document, cell);
        var font = Assert.IsType<Fonts>(stylesheet.Fonts)
            .Elements<Font>()
            .ElementAt(checked((int)(format.FontId?.Value ?? 0U)));
        var fill = FillFor(document, format);
        var border = BorderFor(document, format);
        var alignment = format.Alignment;
        return string.Join(
            "\u001f",
            font.OuterXml,
            fill.OuterXml,
            border.OuterXml,
            format.NumberFormatId?.Value ?? 0U,
            alignment?.Horizontal?.Value,
            alignment?.Vertical?.Value,
            alignment?.WrapText?.Value ?? false,
            alignment?.Indent?.Value ?? 0U,
            alignment?.ShrinkToFit?.Value ?? false,
            format.Protection?.Locked?.Value ?? true,
            format.Protection?.Hidden?.Value ?? false);
    }

    private static CellFormat CellFormatFor(
        SpreadsheetDocument document,
        string sheetName,
        string reference)
    {
        var cell = WorksheetFor(document, sheetName).Descendants<Cell>()
            .Single(item => string.Equals(
                item.CellReference?.Value,
                reference,
                StringComparison.Ordinal));
        return CellFormatFor(document, cell);
    }

    private static CellFormat CellFormatFor(SpreadsheetDocument document, Cell cell)
    {
        var stylesheet = Assert.IsType<Stylesheet>(
            Assert.IsType<WorkbookStylesPart>(document.WorkbookPart!.WorkbookStylesPart)
                .Stylesheet);
        var formats = Assert.IsType<CellFormats>(stylesheet.CellFormats)
            .Elements<CellFormat>()
            .ToArray();
        return formats[checked((int)(cell.StyleIndex?.Value ?? 0U))];
    }

    private static Fill FillFor(SpreadsheetDocument document, CellFormat format)
    {
        var stylesheet = Assert.IsType<Stylesheet>(
            Assert.IsType<WorkbookStylesPart>(document.WorkbookPart!.WorkbookStylesPart)
                .Stylesheet);
        var fills = Assert.IsType<Fills>(stylesheet.Fills).Elements<Fill>().ToArray();
        return fills[checked((int)(format.FillId?.Value ?? 0U))];
    }

    private static Border BorderFor(SpreadsheetDocument document, CellFormat format)
    {
        var stylesheet = Assert.IsType<Stylesheet>(
            Assert.IsType<WorkbookStylesPart>(document.WorkbookPart!.WorkbookStylesPart)
                .Stylesheet);
        var borders = Assert.IsType<Borders>(stylesheet.Borders).Elements<Border>().ToArray();
        return borders[checked((int)(format.BorderId?.Value ?? 0U))];
    }

    private static void AssertBorderless(
        SpreadsheetDocument document,
        uint styleIndex,
        string location)
    {
        var stylesheet = Assert.IsType<Stylesheet>(
            Assert.IsType<WorkbookStylesPart>(document.WorkbookPart!.WorkbookStylesPart)
                .Stylesheet);
        var format = Assert.IsType<CellFormats>(stylesheet.CellFormats)
            .Elements<CellFormat>()
            .ElementAt(checked((int)styleIndex));
        var border = BorderFor(document, format);

        Assert.False(
            border.ChildElements.OfType<BorderPropertiesType>()
                .Any(edge => edge.Style is not null),
            location);
    }

    private static bool IsNoFill(Fill fill) =>
        fill.PatternFill?.PatternType?.Value is null
        || fill.PatternFill.PatternType.Value == PatternValues.None;

    private static Worksheet WorksheetFor(SpreadsheetDocument document, string name)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>()
            .Single(item => string.Equals(item.Name?.Value, name, StringComparison.Ordinal));
        return Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet;
    }
}
