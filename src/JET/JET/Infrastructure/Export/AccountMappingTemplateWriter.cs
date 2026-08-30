using System.Globalization;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// legacy AccountMapping 版型：資料自第 4 列開始，A/B 鎖定，只有 C4:C(last) 解鎖並套分類下拉；
/// 第二張 List 保存目前 project taxonomy labels。production 先複製固定範本，再 forward-only 重寫兩張授權 worksheet。
/// </summary>
public sealed class AccountMappingTemplateWriter :
    IAccountMappingTemplateWriter,
    IAccountMappingTemplateProgressWriter,
    IAccountMappingTaxonomyTemplateWriter,
    IFormalAccountMappingTemplateWriter
{
    internal const int MaxDataRowsPerWorkbook = (int)ExcelWorksheetConstraints.MaxRows - 3;
    private const string SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Spreadsheet = SpreadsheetNamespace;
    private static readonly XNamespace Xml = XNamespace.Xml;
    private readonly ReportTemplateCatalog _templates;

    public AccountMappingTemplateWriter()
        : this(ReportTemplateCatalog.Default)
    {
    }

    internal AccountMappingTemplateWriter(ReportTemplateCatalog templates)
    {
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
    }

    public Task WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        CancellationToken cancellationToken) =>
        WriteCoreAsync(
            output,
            rows,
            AccountTaxonomyBuiltIns.All,
            workbookMetadata: null,
            cancellationToken,
            progress: null);

    Task IAccountMappingTemplateProgressWriter.WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress) =>
        WriteCoreAsync(
            output,
            rows,
            AccountTaxonomyBuiltIns.All,
            workbookMetadata: null,
            cancellationToken,
            progress);

    Task IAccountMappingTaxonomyTemplateWriter.WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountTaxonomyCategory> categories,
        CancellationToken cancellationToken) =>
        WriteCoreAsync(output, rows, categories, workbookMetadata: null, cancellationToken, progress: null);

    Task IAccountMappingTaxonomyTemplateWriter.WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountTaxonomyCategory> categories,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress) =>
        WriteCoreAsync(output, rows, categories, workbookMetadata: null, cancellationToken, progress);

    Task IFormalAccountMappingTemplateWriter.WriteFormalAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountTaxonomyCategory> categories,
        ReportWorkbookMetadata workbookMetadata,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress) =>
        WriteCoreAsync(output, rows, categories, workbookMetadata, cancellationToken, progress);

    private async Task WriteCoreAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountTaxonomyCategory> categories,
        ReportWorkbookMetadata? workbookMetadata,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        AccountTaxonomyInvariant.ValidateReplacement(categories);
        var orderedCategories = categories
            .OrderBy(item => item.Ordinal)
            .ThenBy(item => item.CategoryId, StringComparer.Ordinal)
            .ToArray();
        if (rows.Count > MaxDataRowsPerWorkbook)
        {
            // AccountMapping 的匯入契約只接受單一權威工作表；自動分頁會讓回匯遺漏後續頁。
            // 因此在建立 package 前明確拒絕，而不是讓 uint 列號越界或產出部分檔案。
            throw new ArgumentOutOfRangeException(
                nameof(rows),
                $"AccountMapping supports at most {MaxDataRowsPerWorkbook:N0} data rows in one workbook.");
        }

        _ = await ReportTemplatePackage.FillDirectAsync(
            _templates,
            ReportTemplateCatalog.AccountMapping,
            output,
            ["AccountMapping", "List"],
            (editor, ct) =>
            {
                var stats = FillTemplate(editor, rows, orderedCategories, ct, progress);
                if (workbookMetadata is not null)
                {
                    ReportWorkbookMetadataWorksheet.Append(editor.Document, workbookMetadata, ct);
                }
                return stats;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<SheetStat> FillTemplate(
        DirectTemplateWorkbookEditor editor,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountTaxonomyCategory> categories,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var stats = new List<SheetStat>(2);
        var lastRow = checked((uint)rows.Count + 3);
        var minimumColumnWidths = MeasureAccountMappingColumns(
            rows,
            cancellationToken);
        editor.RewriteWorksheet(
            "AccountMapping",
            new DirectTemplateWorksheetRewritePlan(
                FirstDynamicRow: 4,
                DimensionReference: $"A1:D{Math.Max(4U, lastRow)}",
                PrototypeColumns: [1, 2, 3],
                MapColumnStyle: (_, style) => editor.EnsureProtectionStyle(style, locked: true),
                CreateDynamicRows: (prototype, ct) =>
                    CreateAccountMappingRows(prototype, rows, ct),
                InlineStringCellOverrides: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["A2"] = "請在 C 欄選擇標準科目分類；允許分類僅限 "
                        + string.Join("、", categories.Select(item => item.Label))
                        + "；A、B 欄由系統產生，不應修改。"
                },
                SheetProtection: CreateSheetProtection(),
                DataValidations: rows.Count == 0
                    ? null
                    : CreateDataValidations(lastRow, checked((uint)categories.Count + 1)),
                RemoveTopLevelElements: new HashSet<string>(StringComparer.Ordinal)
                {
                    "extLst"
                },
                MinimumColumnWidths: minimumColumnWidths),
            cancellationToken);
        AddStat(stats, new SheetStat("AccountMapping", rows.Count), progress);

        cancellationToken.ThrowIfCancellationRequested();
        editor.RewriteWorksheet(
            "List",
            new DirectTemplateWorksheetRewritePlan(
                FirstDynamicRow: 2,
                DimensionReference: $"A1:A{categories.Count + 1}",
                PrototypeColumns: [1],
                MapColumnStyle: null,
                CreateDynamicRows: (prototype, ct) => CreateCategoryRows(prototype, categories, ct)),
            cancellationToken);
        AddStat(stats, new SheetStat("List", categories.Count), progress);
        return stats;
    }

    private static IReadOnlyDictionary<uint, double> MeasureAccountMappingColumns(
        IReadOnlyList<AccountMappingTemplateRow> rows,
        CancellationToken cancellationToken)
    {
        var widths = new ExcelDisplayWidthTracker(
            ["GL_Number", "GL_Name", "Standardized Account Name*"]);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            widths.Observe([row.AccountCode, row.AccountName, null]);
        }

        return widths.Widths
            .Select((width, index) => (
                Column: checked((uint)index + 1),
                Width: width))
            .ToDictionary(item => item.Column, item => item.Width);
    }

    private static IEnumerable<XElement> CreateAccountMappingRows(
        DirectTemplateWorksheetPrototype prototype,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowNumber = (uint)index + 4;
            var row = CreateRow(prototype.DynamicRow, rowNumber, preserveFirstRowAttributes: index == 0);
            row.Add(
                TextCell(rowNumber, "A", rows[index].AccountCode, prototype.ColumnStyles[1]),
                TextCell(rowNumber, "B", rows[index].AccountName, prototype.ColumnStyles[2]),
                BlankCell(rowNumber, "C", prototype.OriginalColumnStyles[3]));
            yield return row;
        }
    }

    private static IEnumerable<XElement> CreateCategoryRows(
        DirectTemplateWorksheetPrototype prototype,
        IReadOnlyList<AccountTaxonomyCategory> categories,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < categories.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowNumber = (uint)index + 2;
            var row = CreateRow(prototype.DynamicRow, rowNumber, preserveFirstRowAttributes: true);
            row.Add(TextCell(
                rowNumber,
                "A",
                categories[index].Label,
                prototype.OriginalColumnStyles[1]));
            yield return row;
        }
    }

    private static XElement CreateRow(
        XElement prototype,
        uint rowNumber,
        bool preserveFirstRowAttributes)
    {
        var row = new XElement(Spreadsheet + "row");
        foreach (var attribute in prototype.Attributes())
        {
            if (attribute.Name.LocalName == "r")
            {
                continue;
            }
            if (!preserveFirstRowAttributes && attribute.Name.LocalName != "spans")
            {
                continue;
            }
            row.Add(new XAttribute(attribute));
        }
        row.SetAttributeValue("r", rowNumber.ToString(CultureInfo.InvariantCulture));
        return row;
    }

    private static XElement TextCell(
        uint rowNumber,
        string column,
        string? value,
        uint style)
    {
        var cell = BlankCell(rowNumber, column, style);
        if (string.IsNullOrEmpty(value))
        {
            return cell;
        }

        cell.SetAttributeValue("t", "inlineStr");
        cell.Add(new XElement(
            Spreadsheet + "is",
            new XElement(
                Spreadsheet + "t",
                new XAttribute(Xml + "space", "preserve"),
                value)));
        return cell;
    }

    private static XElement BlankCell(
        uint rowNumber,
        string column,
        uint style) =>
        new(
            Spreadsheet + "c",
            new XAttribute("r", $"{column}{rowNumber}"),
            new XAttribute("s", style.ToString(CultureInfo.InvariantCulture)));

    private static XElement CreateSheetProtection() =>
        new(
            Spreadsheet + "sheetProtection",
            new XAttribute("sheet", "1"),
            new XAttribute("objects", "1"),
            new XAttribute("scenarios", "1"),
            new XAttribute("selectLockedCells", "0"),
            new XAttribute("selectUnlockedCells", "0"));

    private static XElement CreateDataValidations(uint lastRow, uint lastListRow) =>
        new(
            Spreadsheet + "dataValidations",
            new XAttribute("count", "1"),
            new XElement(
                Spreadsheet + "dataValidation",
                new XAttribute("type", "list"),
                new XAttribute("allowBlank", "1"),
                new XAttribute("showErrorMessage", "1"),
                new XAttribute("sqref", $"C4:C{lastRow}"),
                new XElement(Spreadsheet + "formula1", $"List!$A$2:$A${lastListRow}")));

    private static void AddStat(
        List<SheetStat> stats,
        SheetStat stat,
        Action<WorkpaperProgress>? progress)
    {
        stats.Add(stat);
        progress?.Invoke(new WorkpaperProgress(stat.SheetName, stats.Count, stat.RowsWritten));
    }
}
