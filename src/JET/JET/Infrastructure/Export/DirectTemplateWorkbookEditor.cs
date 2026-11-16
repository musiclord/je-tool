using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace JET.Infrastructure;

/// <summary>
/// 直接在範本的複本上填資料時，限制哪些地方可以改。只能改寫建構時列出的既有工作表，每一張只能取用一次；
/// 改儲存格時只能改事先列出的那幾格；改寫空白工作表時不新增 part；
/// 鎖定或不鎖定的樣式只能從範本既有的 cellXf 複製出來，再改保護設定。
/// 新增的資料工作表要用的樣式另外由 <see cref="WorkbookStyleMap"/> 接在樣式表最後面，範本既有的樣式索引不能移動。
/// </summary>
internal sealed class DirectTemplateWorkbookEditor
{
    private readonly SpreadsheetDocument _document;
    private readonly WorkbookPart _workbookPart;
    private readonly HashSet<string> _allowedWorksheets;
    private readonly HashSet<string> _rewrittenWorksheets = new(StringComparer.Ordinal);
    private readonly Dictionary<(uint StyleIndex, bool Locked), uint> _protectionStyles = [];
    private WorkbookStylePatcher? _stylePatcher;

    internal DirectTemplateWorkbookEditor(
        SpreadsheetDocument document,
        IReadOnlyCollection<string> allowedWorksheetNames)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(allowedWorksheetNames);
        _document = document;
        _workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("Template workbook has no workbook part.");
        _allowedWorksheets = allowedWorksheetNames.ToHashSet(StringComparer.Ordinal);
        if (_allowedWorksheets.Count == 0
            || _allowedWorksheets.Any(string.IsNullOrWhiteSpace)
            || _allowedWorksheets.Count != allowedWorksheetNames.Count)
        {
            throw new ArgumentException(
                "Direct-template worksheet allowlist must be non-empty and unique.",
                nameof(allowedWorksheetNames));
        }

        var available = Sheets
            .Elements<Sheet>()
            .Select(sheet => sheet.Name?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        var missing = _allowedWorksheets.Where(name => !available.Contains(name)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"Template workbook is missing allowed worksheet(s): {string.Join(", ", missing)}.");
        }
    }

    internal uint EnsureProtectionStyle(uint prototypeStyleIndex, bool locked)
    {
        if (_protectionStyles.TryGetValue((prototypeStyleIndex, locked), out var cached))
        {
            return cached;
        }

        var stylesPart = _workbookPart.WorkbookStylesPart
            ?? throw new InvalidDataException("Template workbook has no styles part.");
        var stylesheet = stylesPart.Stylesheet
            ?? throw new InvalidDataException("Template workbook has no stylesheet.");
        var cellFormats = stylesheet.CellFormats
            ?? throw new InvalidDataException("Template stylesheet has no cell formats.");
        var formats = cellFormats.Elements<CellFormat>().ToArray();
        if (prototypeStyleIndex >= formats.Length)
        {
            throw new InvalidDataException(
                $"Template style prototype {prototypeStyleIndex} does not exist.");
        }

        var prototype = formats[prototypeStyleIndex];
        var prototypeLocked = prototype.Protection?.Locked?.Value ?? true;
        if (prototypeLocked == locked)
        {
            _protectionStyles[(prototypeStyleIndex, locked)] = prototypeStyleIndex;
            return prototypeStyleIndex;
        }

        var clone = (CellFormat)prototype.CloneNode(true);
        clone.ApplyProtection = true;
        clone.Protection = new Protection { Locked = locked };
        var result = (uint)formats.Length;
        cellFormats.Append(clone);
        cellFormats.Count = (uint)cellFormats.ChildElements.Count;
        stylesheet.Save();
        _protectionStyles[(prototypeStyleIndex, locked)] = result;
        return result;
    }

    internal void RewriteWorksheet(
        string worksheetName,
        DirectTemplateWorksheetRewritePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var part = TakeWorksheetPartForRewrite(worksheetName);
        DirectTemplateWorksheetRewriter.Rewrite(part, plan, cancellationToken);
    }

    /// <summary>
    /// 修改列數固定的範本工作表。只能改 <paramref name="allowedCellReferences"/> 列出的儲存格，
    /// 以及 <paramref name="allowedStyleColumns"/> 列出的欄樣式；列出的每一項都必須剛好用到一次，漏掉或重複都丟出例外。
    /// 除了改值與改樣式，工作表其餘的 XML 全部照原樣保留，包含合併儲存格、資料驗證、保護設定與繪圖物件。
    /// </summary>
    internal void EditWorksheetCells(
        string worksheetName,
        IReadOnlyCollection<string> allowedCellReferences,
        Action<DirectTemplateCellEditor, CancellationToken> edit,
        CancellationToken cancellationToken,
        IReadOnlyCollection<uint>? allowedStyleColumns = null)
    {
        ArgumentNullException.ThrowIfNull(allowedCellReferences);
        ArgumentNullException.ThrowIfNull(edit);
        var part = TakeWorksheetPartForRewrite(worksheetName);
        var editor = new DirectTemplateCellEditor(
            part.RequiredWorksheet(),
            allowedCellReferences,
            StylePatcher,
            allowedStyleColumns);
        cancellationToken.ThrowIfCancellationRequested();
        edit(editor, cancellationToken);
        editor.EnsureAllAllowedCellsWereEdited();
        cancellationToken.ThrowIfCancellationRequested();
        ExcelText.SaveWorksheet(part);
    }

    /// <summary>
    /// 把允許改寫的既有工作表交給 <paramref name="rewrite"/> 直接重寫。只給範本裡本來就是空白、
    /// 沒有任何列的工作表使用；工作表裡已經有列時丟出例外。這個方法不新增工作表，也不改 workbook 與 styles part。
    /// </summary>
    internal void RewriteEmptyWorksheet(
        string worksheetName,
        Action<WorksheetPart, CancellationToken> rewrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rewrite);
        var part = TakeWorksheetPartForRewrite(worksheetName);
        var sheetData = part.RequiredWorksheet().GetFirstChild<SheetData>();
        if (sheetData is null || sheetData.Elements<Row>().Any())
        {
            throw new InvalidDataException(
                $"Template worksheet '{worksheetName}' is not an empty worksheet prototype.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        rewrite(part, cancellationToken);
    }

    /// <summary>
    /// 把允許改寫、列數固定的範本工作表交給 <paramref name="rewrite"/> 處理。<paramref name="rewrite"/> 必須直接寫回同一個 part。
    /// 不在允許清單裡的工作表拿不到，同一張工作表只能處理一次。
    /// </summary>
    internal void RewriteWorksheetPart(
        string worksheetName,
        Action<WorksheetPart, CancellationToken> rewrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rewrite);
        var part = TakeWorksheetPartForRewrite(worksheetName);
        cancellationToken.ThrowIfCancellationRequested();
        rewrite(part, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 把允許改寫的既有工作表交給呼叫端，讓它由上往下寫這張工作表，一直寫到非同步的資料列寫出結束。
    /// 同一張工作表一樣只能取得一次。呼叫端不能經由 Worksheet 物件修改內容，
    /// 而且寫完之前，活頁簿不能離開填範本的那個 callback。
    /// </summary>
    internal WorksheetPart OpenWorksheetPartForStreamingRewrite(
        string worksheetName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TakeWorksheetPartForRewrite(worksheetName);
    }

    /// <summary>
    /// 移除一張允許改寫的既有工作表，連同它的 part 與關聯；活頁簿記錄的作用中分頁位置與只屬於單一工作表的定義名稱跟著調整。
    /// 用在某張工作表依條件不需要輸出的時候。
    /// </summary>
    internal void RemoveWorksheet(
        string worksheetName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var part = TakeWorksheetPartForRewrite(worksheetName);
        var existingSheets = Sheets
            .Elements<Sheet>()
            .ToArray();
        var removedIndex = Array.FindIndex(existingSheets, item => string.Equals(
                item.Name?.Value,
                worksheetName,
                StringComparison.Ordinal));
        if (removedIndex < 0)
        {
            throw new InvalidDataException(
                $"Template worksheet '{worksheetName}' disappeared before removal.");
        }
        var sheet = existingSheets[removedIndex];
        var remainingCount = existingSheets.Length - 1;
        foreach (var view in Workbook.BookViews?.Elements<WorkbookView>()
                     ?? Enumerable.Empty<WorkbookView>())
        {
            view.ActiveTab = ShiftTabIndex(view.ActiveTab?.Value, removedIndex, remainingCount);
            if (view.FirstSheet is not null)
            {
                view.FirstSheet = ShiftTabIndex(
                    view.FirstSheet.Value,
                    removedIndex,
                    remainingCount);
            }
        }
        AdjustLocalDefinedNames(checked((uint)removedIndex));
        sheet.Remove();
        _workbookPart.DeletePart(part);
        Workbook.Save();
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 帶 localSheetId 的定義名稱只屬於一張工作表，例如篩選範圍 _xlnm._FilterDatabase；localSheetId 是那張工作表在活頁簿裡的位置，從 0 起算。
    /// 屬於被移除工作表的名稱一起拿掉；排在它後面的工作表往前移一位，名稱的 localSheetId 跟著減 1。不屬於單一工作表的名稱不動。
    /// </summary>
    private void AdjustLocalDefinedNames(uint removedIndex)
    {
        var localNames = Workbook.DefinedNames?
            .Elements<DefinedName>()
            .Where(name => name.LocalSheetId is not null)
            .ToArray()
            ?? [];
        foreach (var name in localNames)
        {
            var owner = name.LocalSheetId!.Value;
            if (owner == removedIndex)
            {
                name.Remove();
            }
            else if (owner > removedIndex)
            {
                name.LocalSheetId = owner - 1U;
            }
        }
    }

    private static uint ShiftTabIndex(uint? current, int removedIndex, int remainingCount)
    {
        var value = current ?? 0U;
        if (remainingCount <= 0)
        {
            return 0U;
        }
        var removed = checked((uint)removedIndex);
        if (value > removed)
        {
            return value - 1U;
        }
        return value == removed
            ? checked((uint)Math.Min(removedIndex, remainingCount - 1))
            : value;
    }

    /// <summary>
    /// 資料超過一張工作表時，把一張允許改寫的範本工作表完整複製一份，加在同一份活頁簿裡當續頁。
    /// 工作表的 XML 與它原有的關聯都照範本；呼叫端之後只能用
    /// <see cref="DirectTemplateStreamingSheetWriter"/> 重寫複本的資料列。
    /// </summary>
    internal WorksheetPart AppendWorksheetClone(
        WorksheetPart prototypePart,
        Worksheet prototypeWorksheet,
        string worksheetName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prototypePart);
        ArgumentNullException.ThrowIfNull(prototypeWorksheet);
        ArgumentException.ThrowIfNullOrWhiteSpace(worksheetName);
        cancellationToken.ThrowIfCancellationRequested();

        if (worksheetName.Length > 31
            || Sheets.Elements<Sheet>().Any(sheet => string.Equals(
                sheet.Name?.Value,
                worksheetName,
                StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Direct-template continuation worksheet name '{worksheetName}' is invalid or duplicated.");
        }

        var clonePart = _workbookPart.AddNewPart<WorksheetPart>();
        clonePart.Worksheet = (Worksheet)prototypeWorksheet.CloneNode(true);
        foreach (var relationship in prototypePart.Parts)
        {
            clonePart.AddPart(relationship.OpenXmlPart, relationship.RelationshipId);
        }
        foreach (var relationship in prototypePart.ExternalRelationships)
        {
            clonePart.AddExternalRelationship(
                relationship.RelationshipType,
                relationship.Uri,
                relationship.Id);
        }

        ExcelText.SaveWorksheet(clonePart);
        var nextSheetId = Sheets.Elements<Sheet>()
            .Select(sheet => sheet.SheetId?.Value ?? 0U)
            .DefaultIfEmpty(0U)
            .Max() + 1U;
        Sheets.Append(new Sheet
        {
            Id = _workbookPart.GetIdOfPart(clonePart),
            SheetId = nextSheetId,
            Name = worksheetName
        });
        Workbook.Save();
        cancellationToken.ThrowIfCancellationRequested();
        return clonePart;
    }

    /// <summary>
    /// 目前開啟的活頁簿。只用來在同一份活頁簿裡新增資料工作表；
    /// 要改範本既有的工作表，仍然要用上面那些受允許清單限制的方法。
    /// </summary>
    internal SpreadsheetDocument Document => _document;

    /// <summary>這份活頁簿的 <see cref="WorkbookStylePatcher"/>：只在樣式表最後面追加新樣式，不改既有的樣式。</summary>
    internal WorkbookStylePatcher StylePatcher => _stylePatcher ??= new WorkbookStylePatcher(
        _workbookPart.WorkbookStylesPart
        ?? throw new InvalidDataException("Template workbook has no styles part."));

    internal Sheets Sheets => Workbook.Sheets
        ?? throw new InvalidDataException("Template workbook has no sheets collection.");

    private Workbook Workbook => _workbookPart.RequiredWorkbook();

    private WorksheetPart TakeWorksheetPartForRewrite(string worksheetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worksheetName);
        if (!_allowedWorksheets.Contains(worksheetName))
        {
            throw new InvalidOperationException(
                $"Worksheet '{worksheetName}' is not authorized for direct-template rewriting.");
        }
        if (!_rewrittenWorksheets.Add(worksheetName))
        {
            throw new InvalidOperationException(
                $"Worksheet '{worksheetName}' was already rewritten in this operation.");
        }

        var sheet = Sheets
            .Elements<Sheet>()
            .Single(item => string.Equals(
                item.Name?.Value,
                worksheetName,
                StringComparison.Ordinal));
        return _workbookPart.GetPartById(sheet.Id!.Value!) as WorksheetPart
            ?? throw new InvalidDataException(
                $"Template worksheet '{worksheetName}' does not resolve to a worksheet part.");
    }

    internal void EnsureAllAllowedWorksheetsWereRewritten()
    {
        if (_rewrittenWorksheets.SetEquals(_allowedWorksheets))
        {
            return;
        }

        var missing = _allowedWorksheets
            .Except(_rewrittenWorksheets, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        throw new InvalidOperationException(
            $"Direct-template operation did not rewrite all authorized worksheets: {string.Join(", ", missing)}.");
    }
}

internal sealed class DirectTemplateCellEditor
{
    private readonly Worksheet _worksheet;
    private readonly Dictionary<string, Cell> _cells;
    private readonly HashSet<string> _allowed;
    private readonly HashSet<uint> _allowedStyleColumns;
    private readonly WorkbookStylePatcher? _stylePatcher;
    private readonly HashSet<string> _edited = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> _patchedStyleColumns = [];

    internal DirectTemplateCellEditor(
        Worksheet worksheet,
        IReadOnlyCollection<string> allowedCellReferences,
        WorkbookStylePatcher? stylePatcher = null,
        IReadOnlyCollection<uint>? allowedStyleColumns = null)
    {
        ArgumentNullException.ThrowIfNull(worksheet);
        ArgumentNullException.ThrowIfNull(allowedCellReferences);
        _worksheet = worksheet;
        _stylePatcher = stylePatcher;
        _allowed = allowedCellReferences.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_allowed.Count == 0
            || _allowed.Any(string.IsNullOrWhiteSpace)
            || _allowed.Count != allowedCellReferences.Count)
        {
            throw new ArgumentException(
                "Direct-template cell allowlist must be non-empty and unique.",
                nameof(allowedCellReferences));
        }

        _allowedStyleColumns = allowedStyleColumns?.ToHashSet() ?? [];
        if (_allowedStyleColumns.Any(column => column == 0)
            || _allowedStyleColumns.Count != (allowedStyleColumns?.Count ?? 0))
        {
            throw new ArgumentException(
                "Direct-template style-column allowlist must contain unique positive indexes.",
                nameof(allowedStyleColumns));
        }
        if (_allowedStyleColumns.Count > 0 && stylePatcher is null)
        {
            throw new ArgumentException(
                "A workbook style patcher is required for style-column editing.",
                nameof(stylePatcher));
        }

        _cells = worksheet.Descendants<Cell>()
            .Where(cell => !string.IsNullOrWhiteSpace(cell.CellReference?.Value))
            .ToDictionary(
                cell => cell.CellReference!.Value!,
                StringComparer.OrdinalIgnoreCase);
        var missing = _allowed.Where(reference => !_cells.ContainsKey(reference)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"Template worksheet is missing allowed cell(s): {string.Join(", ", missing)}.");
        }
    }

    internal void SetInlineString(
        string reference,
        string? value,
        WorkbookStylePatch? stylePatch = null)
    {
        var cell = TakeCell(reference);
        ClearPayload(cell);
        ApplyStylePatch(cell, stylePatch);
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        cell.DataType = CellValues.InlineString;
        cell.InlineString = ExcelText.InlineString(value, WorksheetPart);
    }

    /// <summary>照原樣寫出、不截斷的文字，給篩選條件說明切好的分段。</summary>
    internal void SetVerbatimInlineString(string reference, string? value)
    {
        var cell = TakeCell(reference);
        ClearPayload(cell);
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        cell.DataType = CellValues.InlineString;
        cell.InlineString = ExcelText.VerbatimInlineString(value);
    }

    /// <summary>超過 Excel 每格上限的文字截斷後，截斷格數記在這張工作表上。</summary>
    private WorksheetPart WorksheetPart => _worksheet.WorksheetPart
        ?? throw new InvalidOperationException("Template worksheet is not loaded from a worksheet part.");

    internal void SetNumber(
        string reference,
        decimal value,
        WorkbookStylePatch? stylePatch = null)
    {
        var cell = TakeCell(reference);
        ClearPayload(cell);
        ApplyStylePatch(cell, stylePatch);
        cell.DataType = CellValues.Number;
        cell.CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture));
    }

    internal void Clear(string reference, WorkbookStylePatch? stylePatch = null)
    {
        var cell = TakeCell(reference);
        ClearPayload(cell);
        ApplyStylePatch(cell, stylePatch);
    }

    /// <summary>儲存格的內容不動，只把樣式換成依目前樣式修改而來的新樣式。</summary>
    internal void PatchCell(string reference, WorkbookStylePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ApplyStylePatch(TakeCell(reference), patch);
    }

    /// <summary>
    /// 修改允許清單裡某一欄的樣式：欄的預設樣式與這一欄既有儲存格的樣式，都依 <paramref name="patch"/> 換成新樣式。
    /// 欄寬與儲存格的內容不變。範本必須有只涵蓋這一欄的 col 定義；沒有，或那個定義涵蓋多欄時，丟出例外。
    /// </summary>
    internal void PatchColumn(uint column, WorkbookStylePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (!_allowedStyleColumns.Contains(column))
        {
            throw new InvalidOperationException(
                $"Column {column} is not authorized for direct-template style editing.");
        }
        if (!_patchedStyleColumns.Add(column))
        {
            throw new InvalidOperationException(
                $"Column {column} was already style-patched in this operation.");
        }
        var patcher = _stylePatcher
            ?? throw new InvalidOperationException(
                "Direct-template style editing requires a workbook style patcher.");
        var columns = _worksheet.GetFirstChild<Columns>()
            ?? throw new InvalidDataException("Template worksheet has no columns element.");
        var definitions = columns.Elements<Column>()
            .Where(item => (item.Min?.Value ?? 0U) <= column
                           && (item.Max?.Value ?? 0U) >= column)
            .ToArray();
        if (definitions.Length != 1)
        {
            throw new InvalidDataException(
                $"Template column {column} must have exactly one column definition.");
        }
        var definition = definitions[0];
        if (definition.Min?.Value != column || definition.Max?.Value != column)
        {
            throw new InvalidDataException(
                $"Template column {column} style prototype must be a dedicated single-column span.");
        }
        definition.Style = patcher.Patch(definition.Style?.Value ?? 0U, patch);

        foreach (var cell in _cells.Values.Where(cell =>
                     CellColumn(cell.CellReference?.Value) == column))
        {
            cell.StyleIndex = patcher.Patch(cell.StyleIndex?.Value ?? 0U, patch);
        }
    }

    /// <summary>
    /// 替整張工作表加上不設密碼的保護，屬性和串流寫出的保護頁相同。範本已經有保護時保留原本的設定。
    /// 保護元素照 OpenXML 的順序接在 sheetCalcPr 之後；範本沒有 sheetCalcPr 時接在 sheetData 之後。
    /// </summary>
    internal void ProtectSheet()
    {
        if (_worksheet.GetFirstChild<SheetProtection>() is not null)
        {
            return;
        }

        OpenXmlElement anchor = (OpenXmlElement?)_worksheet.GetFirstChild<SheetCalculationProperties>()
            ?? _worksheet.GetFirstChild<SheetData>()
            ?? throw new InvalidDataException("Template worksheet has no sheetData element.");
        anchor.InsertAfterSelf(new SheetProtection
        {
            Sheet = true,
            Objects = true,
            Scenarios = true,
            SelectLockedCells = false,
            SelectUnlockedCells = false
        });
    }

    internal void EnsureAllAllowedCellsWereEdited()
    {
        if (_edited.SetEquals(_allowed)
            && _patchedStyleColumns.SetEquals(_allowedStyleColumns))
        {
            return;
        }

        var missingCells = _allowed
            .Except(_edited, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var missingColumns = _allowedStyleColumns
            .Except(_patchedStyleColumns)
            .Order()
            .ToArray();
        throw new InvalidOperationException(
            "Direct-template operation did not consume every exact allowlist entry; "
            + $"cells=[{string.Join(", ", missingCells)}], "
            + $"styleColumns=[{string.Join(", ", missingColumns)}].");
    }

    private Cell TakeCell(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (!_allowed.Contains(reference))
        {
            throw new InvalidOperationException(
                $"Cell '{reference}' is not authorized for direct-template editing.");
        }
        if (!_edited.Add(reference))
        {
            throw new InvalidOperationException(
                $"Cell '{reference}' was already edited in this operation.");
        }
        return _cells[reference];
    }

    private void ApplyStylePatch(Cell cell, WorkbookStylePatch? patch)
    {
        if (patch is null)
        {
            return;
        }
        var patcher = _stylePatcher
            ?? throw new InvalidOperationException(
                "Direct-template style editing requires a workbook style patcher.");
        cell.StyleIndex = patcher.Patch(cell.StyleIndex?.Value ?? 0U, patch);
    }

    private static uint CellColumn(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new InvalidDataException("Template cell has no reference.");
        }
        var column = 0U;
        var index = 0;
        while (index < reference.Length && char.IsLetter(reference[index]))
        {
            column = checked(
                column * 26U
                + (uint)(char.ToUpperInvariant(reference[index]) - 'A' + 1));
            index++;
        }
        if (column == 0U || index == reference.Length)
        {
            throw new InvalidDataException($"Template cell reference '{reference}' is invalid.");
        }
        return column;
    }

    private static void ClearPayload(Cell cell)
    {
        cell.CellFormula = null;
        cell.CellValue = null;
        cell.InlineString = null;
        cell.DataType = null;
    }
}

internal sealed record DirectTemplateWorksheetRewritePlan(
    uint FirstDynamicRow,
    string DimensionReference,
    IReadOnlyCollection<uint> PrototypeColumns,
    Func<uint, uint, uint>? MapColumnStyle,
    Func<DirectTemplateWorksheetPrototype, CancellationToken, IEnumerable<XElement>> CreateDynamicRows,
    IReadOnlyDictionary<string, string>? InlineStringCellOverrides = null,
    XElement? SheetProtection = null,
    XElement? DataValidations = null,
    IReadOnlySet<string>? RemoveTopLevelElements = null,
    IReadOnlyDictionary<uint, double>? MinimumColumnWidths = null,
    IReadOnlySet<uint>? AutoFitColumns = null);

/// <param name="Part">正在改寫的工作表；資料列的文字超過 Excel 每格上限而截斷時，截斷格數記在這裡。</param>
internal sealed record DirectTemplateWorksheetPrototype(
    IReadOnlyDictionary<uint, uint> OriginalColumnStyles,
    IReadOnlyDictionary<uint, uint> ColumnStyles,
    XElement DynamicRow,
    WorksheetPart Part);

/// <summary>
/// 由上往下重寫一張工作表。原本的 XML 先複製到關閉時自動刪除的暫存檔，再一邊讀一邊寫回去；暫存檔不是 xlsx 檔。
/// 除了計畫裡指定要換文字的固定儲存格，固定列與計畫沒有要改的頂層元素都逐個照抄，不把整張工作表載入記憶體。
/// </summary>
internal static class DirectTemplateWorksheetRewriter
{
    private const string SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly HashSet<string> AfterSheetProtection = new(StringComparer.Ordinal)
    {
        "protectedRanges", "scenarios", "autoFilter", "sortState", "dataConsolidate",
        "customSheetViews", "mergeCells", "phoneticPr", "conditionalFormatting",
        "dataValidations", "hyperlinks", "printOptions", "pageMargins", "pageSetup",
        "headerFooter", "rowBreaks", "colBreaks", "customProperties", "cellWatches",
        "ignoredErrors", "smartTags", "drawing", "legacyDrawing", "legacyDrawingHF",
        "picture", "oleObjects", "controls", "webPublishItems", "tableParts", "extLst"
    };

    private static readonly HashSet<string> AfterDataValidations = new(StringComparer.Ordinal)
    {
        "hyperlinks", "printOptions", "pageMargins", "pageSetup", "headerFooter",
        "rowBreaks", "colBreaks", "customProperties", "cellWatches", "ignoredErrors",
        "smartTags", "drawing", "legacyDrawing", "legacyDrawingHF", "picture",
        "oleObjects", "controls", "webPublishItems", "tableParts", "extLst"
    };

    internal static void Rewrite(
        WorksheetPart part,
        DirectTemplateWorksheetRewritePlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.FirstDynamicRow == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "First dynamic row must be positive.");
        }
        if (string.IsNullOrWhiteSpace(plan.DimensionReference))
        {
            throw new ArgumentException("Worksheet dimension is required.", nameof(plan));
        }
        if (plan.PrototypeColumns.Count == 0
            || plan.PrototypeColumns.Any(column => column == 0)
            || plan.PrototypeColumns.Distinct().Count() != plan.PrototypeColumns.Count)
        {
            throw new ArgumentException(
                "Prototype columns must be a non-empty set of positive indexes.",
                nameof(plan));
        }
        if (plan.MinimumColumnWidths is not null
            && plan.MinimumColumnWidths.Any(item =>
                !plan.PrototypeColumns.Contains(item.Key)
                || !double.IsFinite(item.Value)
                || item.Value <= 0D
                || item.Value > 255D))
        {
            throw new ArgumentException(
                "Minimum column widths must target prototype columns and stay within Excel's width range.",
                nameof(plan));
        }
        if (plan.AutoFitColumns is not null
            && (plan.AutoFitColumns.Any(column => !plan.PrototypeColumns.Contains(column))
                || plan.AutoFitColumns.Any(column => column == 0)))
        {
            throw new ArgumentException(
                "Auto-fit columns must target positive prototype columns.",
                nameof(plan));
        }

        using var sourceCopy = CreateTemporaryWorksheetStream();
        using (var source = part.GetStream(FileMode.Open, FileAccess.Read))
        {
            source.CopyTo(sourceCopy);
        }
        sourceCopy.Position = 0;

        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = false,
            IgnoreWhitespace = false,
            CloseInput = false
        };
        var writerSettings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            OmitXmlDeclaration = false,
            CloseOutput = false
        };

        using var target = part.GetStream(FileMode.Create, FileAccess.Write);
        using var reader = XmlReader.Create(sourceCopy, readerSettings);
        using var writer = XmlWriter.Create(target, writerSettings);
        try
        {
            RewriteXml(part, reader, writer, plan, cancellationToken);
        }
        catch (Exception error) when (ExcelText.MarkWriteFailure(error, part, Array.Empty<XElement>()))
        {
            throw;
        }
        writer.Flush();
        target.Flush();
    }

    private static void RewriteXml(
        WorksheetPart part,
        XmlReader reader,
        XmlWriter writer,
        DirectTemplateWorksheetRewritePlan plan,
        CancellationToken cancellationToken)
    {
        reader.MoveToContent();
        if (reader.NodeType != XmlNodeType.Element
            || reader.LocalName != "worksheet"
            || reader.NamespaceURI != SpreadsheetNamespace)
        {
            throw new InvalidDataException("Worksheet part has no SpreadsheetML worksheet root.");
        }

        writer.WriteStartDocument(standalone: true);
        WriteStartElementWithAttributes(reader, writer);
        reader.Read();

        var originalStyles = new Dictionary<uint, uint>();
        var mappedStyles = new Dictionary<uint, uint>();
        var wroteSheetData = false;
        var wroteProtection = plan.SheetProtection is null;
        var wroteValidations = plan.DataValidations is null;
        while (!(reader.NodeType == XmlNodeType.EndElement
                 && reader.LocalName == "worksheet"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element)
            {
                writer.WriteNode(reader, defattr: true);
                continue;
            }

            var localName = reader.LocalName;
            if (!wroteProtection && AfterSheetProtection.Contains(localName))
            {
                plan.SheetProtection!.WriteTo(writer);
                wroteProtection = true;
            }
            if (!wroteValidations && AfterDataValidations.Contains(localName))
            {
                plan.DataValidations!.WriteTo(writer);
                wroteValidations = true;
            }

            if (localName == "dimension" && reader.NamespaceURI == SpreadsheetNamespace)
            {
                var dimension = ReadElement(reader);
                dimension.SetAttributeValue("ref", plan.DimensionReference);
                dimension.WriteTo(writer);
                continue;
            }
            if (localName == "cols" && reader.NamespaceURI == SpreadsheetNamespace)
            {
                var columns = ReadElement(reader);
                CaptureAndMapColumns(columns, plan, originalStyles, mappedStyles);
                columns.WriteTo(writer);
                continue;
            }
            if (localName == "sheetData" && reader.NamespaceURI == SpreadsheetNamespace)
            {
                RewriteSheetData(
                    part,
                    reader,
                    writer,
                    plan,
                    originalStyles,
                    mappedStyles,
                    cancellationToken);
                wroteSheetData = true;
                continue;
            }
            if (localName == "sheetProtection" && plan.SheetProtection is not null)
            {
                if (!wroteProtection)
                {
                    plan.SheetProtection.WriteTo(writer);
                    wroteProtection = true;
                }
                reader.Skip();
                continue;
            }
            if (localName == "dataValidations" && plan.DataValidations is not null)
            {
                if (!wroteValidations)
                {
                    plan.DataValidations.WriteTo(writer);
                    wroteValidations = true;
                }
                reader.Skip();
                continue;
            }
            if (plan.RemoveTopLevelElements?.Contains(localName) == true)
            {
                reader.Skip();
                continue;
            }

            writer.WriteNode(reader, defattr: true);
        }

        if (!wroteSheetData)
        {
            throw new InvalidDataException("Template worksheet has no sheetData element.");
        }
        if (!wroteProtection)
        {
            plan.SheetProtection!.WriteTo(writer);
        }
        if (!wroteValidations)
        {
            plan.DataValidations!.WriteTo(writer);
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void CaptureAndMapColumns(
        XElement columns,
        DirectTemplateWorksheetRewritePlan plan,
        IDictionary<uint, uint> originalStyles,
        IDictionary<uint, uint> mappedStyles)
    {
        foreach (var columnElement in columns.Elements(XName.Get("col", SpreadsheetNamespace)))
        {
            var minimum = ParseUInt(columnElement.Attribute("min"));
            var maximum = ParseUInt(columnElement.Attribute("max"));
            var style = ParseUInt(columnElement.Attribute("style"));
            foreach (var requestedColumn in plan.PrototypeColumns
                         .Where(column => column >= minimum && column <= maximum))
            {
                originalStyles.Add(requestedColumn, style);
                var mapped = plan.MapColumnStyle?.Invoke(requestedColumn, style) ?? style;
                mappedStyles.Add(requestedColumn, mapped);
                if (mapped != style)
                {
                    if (minimum != maximum || minimum != requestedColumn)
                    {
                        throw new InvalidDataException(
                            $"Cannot change style for column {requestedColumn} inside shared span {minimum}:{maximum}.");
                    }
                    columnElement.SetAttributeValue(
                        "style",
                        mapped.ToString(CultureInfo.InvariantCulture));
                }

                var minimumWidth = 0D;
                var hasMinimum = plan.MinimumColumnWidths?.TryGetValue(
                    requestedColumn,
                    out minimumWidth) == true;
                var legacyAutoFit = plan.AutoFitColumns is null && hasMinimum;
                var explicitAutoFit = plan.AutoFitColumns?.Contains(requestedColumn) == true;
                var markBestFit = legacyAutoFit || explicitAutoFit;
                if (hasMinimum || markBestFit)
                {
                    var currentWidth = ParseDouble(columnElement.Attribute("width"));
                    var requestedWidth = hasMinimum
                        ? Math.Max(currentWidth, minimumWidth)
                        : currentWidth;
                    if (requestedWidth > currentWidth || markBestFit)
                    {
                        if (minimum != maximum || minimum != requestedColumn)
                        {
                            throw new InvalidDataException(
                                $"Cannot change width for column {requestedColumn} inside shared span {minimum}:{maximum}.");
                        }
                        if (requestedWidth > currentWidth)
                        {
                            columnElement.SetAttributeValue(
                                "width",
                                requestedWidth.ToString(CultureInfo.InvariantCulture));
                            columnElement.SetAttributeValue("customWidth", "1");
                        }
                        if (markBestFit)
                        {
                            columnElement.SetAttributeValue("bestFit", "1");
                            columnElement.SetAttributeValue("customWidth", "1");
                        }
                    }
                }
            }
        }

        var missing = plan.PrototypeColumns.Where(column => !originalStyles.ContainsKey(column)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"Template worksheet has no column style prototype for: {string.Join(", ", missing)}.");
        }
    }

    private static void RewriteSheetData(
        WorksheetPart part,
        XmlReader reader,
        XmlWriter writer,
        DirectTemplateWorksheetRewritePlan plan,
        IReadOnlyDictionary<uint, uint> originalStyles,
        IReadOnlyDictionary<uint, uint> mappedStyles,
        CancellationToken cancellationToken)
    {
        WriteStartElementWithAttributes(reader, writer);
        reader.Read();
        var wroteDynamicRows = false;
        var appliedCellOverrides = new HashSet<string>(StringComparer.Ordinal);
        while (!(reader.NodeType == XmlNodeType.EndElement
                 && reader.LocalName == "sheetData"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element
                || reader.LocalName != "row"
                || reader.NamespaceURI != SpreadsheetNamespace)
            {
                writer.WriteNode(reader, defattr: true);
                continue;
            }

            var rowIndex = ParseUInt(reader.GetAttribute("r"));
            if (rowIndex < plan.FirstDynamicRow)
            {
                if (plan.InlineStringCellOverrides is null)
                {
                    writer.WriteNode(reader, defattr: true);
                }
                else
                {
                    var staticRow = ReadElement(reader);
                    ApplyInlineStringCellOverrides(
                        staticRow,
                        plan.InlineStringCellOverrides,
                        appliedCellOverrides);
                    WriteRow(staticRow, writer);
                }
                continue;
            }

            if (!wroteDynamicRows)
            {
                if (rowIndex != plan.FirstDynamicRow)
                {
                    throw new InvalidDataException(
                        $"Template dynamic row starts at {rowIndex}, expected {plan.FirstDynamicRow}.");
                }
                var prototypeRow = ReadElement(reader);
                var prototype = new DirectTemplateWorksheetPrototype(
                    new Dictionary<uint, uint>(originalStyles),
                    new Dictionary<uint, uint>(mappedStyles),
                    prototypeRow,
                    part);
                foreach (var row in plan.CreateDynamicRows(prototype, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteRow(row, writer);
                }
                wroteDynamicRows = true;
                continue;
            }

            reader.Skip();
        }

        if (!wroteDynamicRows)
        {
            throw new InvalidDataException(
                $"Template worksheet has no row {plan.FirstDynamicRow} prototype.");
        }
        if (plan.InlineStringCellOverrides is not null
            && !appliedCellOverrides.SetEquals(plan.InlineStringCellOverrides.Keys))
        {
            var missing = plan.InlineStringCellOverrides.Keys
                .Except(appliedCellOverrides, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            throw new InvalidDataException(
                $"Template worksheet is missing static cell override target(s): {string.Join(", ", missing)}.");
        }
        writer.WriteEndElement();
        reader.Read();
    }

    /// <summary>寫出一列；失敗時記下第一個含 XML 不允許字元的儲存格，工作表順序由外層 <see cref="Rewrite"/> 補上。</summary>
    private static void WriteRow(XElement row, XmlWriter writer)
    {
        try
        {
            row.WriteTo(writer);
        }
        catch (Exception error) when (ExcelText.MarkWriteFailure(
            error,
            part: null,
            row.Elements(XName.Get("c", SpreadsheetNamespace))))
        {
            throw;
        }
    }

    private static void ApplyInlineStringCellOverrides(
        XElement row,
        IReadOnlyDictionary<string, string> overrides,
        ISet<string> applied)
    {
        XNamespace spreadsheet = SpreadsheetNamespace;
        foreach (var cell in row.Elements(spreadsheet + "c"))
        {
            var reference = (string?)cell.Attribute("r");
            if (reference is null || !overrides.TryGetValue(reference, out var value))
            {
                continue;
            }

            cell.RemoveNodes();
            cell.SetAttributeValue("t", "inlineStr");
            // 固定儲存格換上的是程式寫好的說明文字，照原樣寫出。
            cell.Add(ExcelText.VerbatimInlineStringElement(spreadsheet, value));
            applied.Add(reference);
        }
    }

    private static void WriteStartElementWithAttributes(XmlReader reader, XmlWriter writer)
    {
        writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
        if (!reader.HasAttributes)
        {
            return;
        }

        reader.MoveToFirstAttribute();
        do
        {
            writer.WriteAttributeString(
                reader.Prefix,
                reader.LocalName,
                reader.NamespaceURI,
                reader.Value);
        }
        while (reader.MoveToNextAttribute());
        reader.MoveToElement();
    }

    private static XElement ReadElement(XmlReader reader) =>
        XElement.Parse(reader.ReadOuterXml(), LoadOptions.PreserveWhitespace);

    private static uint ParseUInt(string? value) =>
        uint.Parse(
            value ?? throw new InvalidDataException("Required uint attribute is missing."),
            CultureInfo.InvariantCulture);

    private static uint ParseUInt(XAttribute? value) => ParseUInt(value?.Value);

    private static double ParseDouble(XAttribute? value) =>
        double.Parse(
            value?.Value
            ?? throw new InvalidDataException("Template column width is missing."),
            NumberStyles.Float,
            CultureInfo.InvariantCulture);

    private static FileStream CreateTemporaryWorksheetStream()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $".jet-template-worksheet-{Guid.NewGuid():N}.tmp");
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan | FileOptions.DeleteOnClose);
    }
}
