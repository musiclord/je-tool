using System.Globalization;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 寫出舊版 JET 版型的科目配對範本。AccountMapping 工作表的資料從第 4 列開始，A、B 欄鎖定，
/// 只有 C 欄從第 4 列到最後一筆資料不鎖定，並套上分類的下拉選單。
/// 第二張 List 工作表放目前案件的科目分類名稱，不鎖定，審計員可以在清單最後加上新的分類名稱。
/// 寫出時先複製固定範本，再由上往下依序重寫 AccountMapping 與 List 這兩張工作表；範本的其他工作表不能改。
/// </summary>
public sealed class AccountMappingTemplateWriter :
    IAccountMappingTemplateWriter,
    IAccountMappingTemplateProgressWriter,
    IAccountMappingCategoryTemplateWriter,
    IFormalAccountMappingTemplateWriter
{
    internal const int MaxDataRowsPerWorkbook = (int)ExcelWorksheetConstraints.MaxRows - 3;
    private const string SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Spreadsheet = SpreadsheetNamespace;
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
            AccountCategoryBuiltIns.All,
            workbookMetadata: null,
            cancellationToken,
            progress: null);

    Task IAccountMappingTemplateProgressWriter.WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress) =>
        WriteCoreAsync(
            output,
            rows,
            AccountCategoryBuiltIns.All,
            workbookMetadata: null,
            cancellationToken,
            progress);

    Task IAccountMappingCategoryTemplateWriter.WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountCategory> categories,
        CancellationToken cancellationToken) =>
        WriteCoreAsync(output, rows, categories, workbookMetadata: null, cancellationToken, progress: null);

    Task IAccountMappingCategoryTemplateWriter.WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountCategory> categories,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress) =>
        WriteCoreAsync(output, rows, categories, workbookMetadata: null, cancellationToken, progress);

    Task IFormalAccountMappingTemplateWriter.WriteFormalAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountCategory> categories,
        ReportWorkbookMetadata workbookMetadata,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress) =>
        WriteCoreAsync(output, rows, categories, workbookMetadata, cancellationToken, progress);

    private async Task WriteCoreAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountCategory> categories,
        ReportWorkbookMetadata? workbookMetadata,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        AccountCategoryInvariant.ValidateReplacement(categories);
        var orderedCategories = categories
            .OrderBy(item => item.Ordinal)
            .ThenBy(item => item.CategoryId, StringComparer.Ordinal)
            .ToArray();
        if (rows.Count > MaxDataRowsPerWorkbook)
        {
            // 匯入科目配對時只讀一張 AccountMapping 工作表；資料如果自動分到第二張，匯回來時後面幾張會漏掉。
            // 所以列數超過一張工作表放得下的上限時，在開始寫檔之前就丟出例外，不讓列號超出範圍，也不產出只有部分資料的檔案。
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
            cancellationToken,
            FormalWorkbookLayout.AsWritten).ConfigureAwait(false);
    }

    private static IReadOnlyList<SheetStat> FillTemplate(
        DirectTemplateWorkbookEditor editor,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountCategory> categories,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
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
                    ["A2"] = "請在 C 欄選擇標準科目分類；要新增分類，請在 List 工作表的清單最後加上名稱，原有的名稱不要更改；"
                        + "A、B 欄由系統產生，不應修改。"
                },
                SheetProtection: CreateSheetProtection(),
                DataValidations: rows.Count == 0
                    ? null
                    : CreateDataValidations(lastRow),
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
            widths.Observe([row.AccountNumber, row.TemplateName, null]);
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
                TextCell(prototype.Part, rowNumber, "A", rows[index].AccountNumber, prototype.ColumnStyles[1]),
                TextCell(prototype.Part, rowNumber, "B", rows[index].TemplateName, prototype.ColumnStyles[2]),
                BlankCell(rowNumber, "C", prototype.OriginalColumnStyles[3]));
            yield return row;
        }
    }

    private static IEnumerable<XElement> CreateCategoryRows(
        DirectTemplateWorksheetPrototype prototype,
        IReadOnlyList<AccountCategory> categories,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < categories.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowNumber = (uint)index + 2;
            var row = CreateRow(prototype.DynamicRow, rowNumber, preserveFirstRowAttributes: true);
            row.Add(TextCell(
                prototype.Part,
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

    /// <summary>
    /// 科目代號、科目名稱與分類名稱在案件資料裡都有字數上限，遠低於 Excel 每格上限，實際不會截斷；
    /// 仍照一般文字寫出，萬一超過也不會寫出 Excel 打不開的檔案。科目配對範本的文字寫成 inline string。
    /// </summary>
    private static XElement TextCell(
        WorksheetPart part,
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

        ExcelText.SetInlineTextElement(cell, Spreadsheet, value, part);
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

    // 下拉清單的來源和舊版範本一樣取 List 工作表 A 欄第 2 列以下整欄：審計員在清單最後加上的分類名稱，回到 C 欄就選得到。
    private static XElement CreateDataValidations(uint lastRow) =>
        new(
            Spreadsheet + "dataValidations",
            new XAttribute("count", "1"),
            new XElement(
                Spreadsheet + "dataValidation",
                new XAttribute("type", "list"),
                new XAttribute("allowBlank", "1"),
                new XAttribute("showErrorMessage", "1"),
                new XAttribute("sqref", $"C4:C{lastRow}"),
                new XElement(Spreadsheet + "formula1", $"List!$A$2:$A${ExcelWorksheetConstraints.MaxRows}")));

    private static void AddStat(
        List<SheetStat> stats,
        SheetStat stat,
        Action<WorkingPaperProgress>? progress)
    {
        stats.Add(stat);
        progress?.Invoke(new WorkingPaperProgress(stat.SheetName, stats.Count, stat.RowsWritten));
    }
}
