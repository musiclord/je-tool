using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using SpreadsheetColor = DocumentFormat.OpenXml.Spreadsheet.Color;
using SpreadsheetFont = DocumentFormat.OpenXml.Spreadsheet.Font;

namespace JET.Infrastructure;

/// <summary>
/// 隨 JET 發布的正式報表範本目錄。檔名是封閉集合，caller 不得傳入路徑。
/// </summary>
internal sealed class ReportTemplateCatalog
{
    internal const string AccountMapping = "AccountMapping.xlsx";
    internal const string CriteriaSelection = "CriteriaSelectionReport.xlsx";
    internal const string Inf = "INFReport.xlsx";
    internal const string Prescreen = "PrescreeningReport.xlsx";
    internal const string Validation = "ValidationReport.xlsx";
    internal const string WorkingPaper = "WorkingPaper.xlsx";
    internal const string Holiday = "Holiday2025TW.xlsx";
    internal const string MakeUpDay = "MakeUpDay2025TW.xlsx";

    internal static readonly IReadOnlyList<string> FileNames =
    [
        AccountMapping,
        CriteriaSelection,
        Inf,
        Prescreen,
        Validation,
        WorkingPaper,
        Holiday,
        MakeUpDay
    ];

    internal static ReportTemplateCatalog Default { get; } =
        new(Path.Combine(AppContext.BaseDirectory, "Templates"));

    private static readonly HashSet<string> AllowedNames =
        FileNames.ToHashSet(StringComparer.Ordinal);

    private readonly string _templateRoot;

    internal ReportTemplateCatalog(string templateRoot)
    {
        if (string.IsNullOrWhiteSpace(templateRoot))
        {
            throw new ArgumentException("Template root is required.", nameof(templateRoot));
        }

        _templateRoot = Path.GetFullPath(templateRoot);
    }

    internal async Task CopyToAsync(
        string fileName,
        Stream output,
        CancellationToken cancellationToken)
    {
        if (!AllowedNames.Contains(fileName))
        {
            throw new InvalidOperationException($"未登錄的正式報表範本 '{fileName}'。");
        }
        if (!output.CanRead || !output.CanWrite || !output.CanSeek)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                "正式報表範本需要可讀、可寫且可定位的 staging stream。");
        }

        var path = Path.Combine(_templateRoot, fileName);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists
                || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw TemplateReadFailure(fileName);
            }

            output.Position = 0;
            output.SetLength(0);
            await using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Position = 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JetActionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            throw TemplateReadFailure(fileName);
        }
    }

    private static JetActionException TemplateReadFailure(string fileName) => new(
        JetErrorCodes.FileReadError,
        $"無法讀取 JET 內嵌範本 '{fileName}'；檔案可能缺失、損壞或無法存取。");
}

/// <summary>
/// 先複製範本，再讓 bounded/SAX writer 直接改寫已授權的 worksheet parts。
/// 大型明細不建立第二個 workbook，也不把完整 worksheet 載入記憶體。
/// </summary>
internal static class ReportTemplatePackage
{
    /// <summary>
    /// 複製固定範本後，只把 allowlist 內既有 worksheet parts 交給 forward-only editor。
    /// 這條路徑不建立 current-result workbook；既有 style index 不移動。新增 dynamic sheet
    /// 如需 writer 樣式，只能經 WorkbookStyleMap 以 bounded append 取得新索引；protection
    /// variant 則由 editor 的既有 prototype 派生。
    /// </summary>
    internal static Task<ExportStats> FillDirectAsync(
        ReportTemplateCatalog catalog,
        string templateName,
        Stream output,
        IReadOnlyCollection<string> allowedWorksheetNames,
        Func<DirectTemplateWorkbookEditor, CancellationToken, IReadOnlyList<SheetStat>> fill,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fill);

        return FillDirectCoreAsync(
            catalog,
            templateName,
            output,
            allowedWorksheetNames,
            (editor, ct) => Task.FromResult(fill(editor, ct)),
            cancellationToken);
    }

    /// <summary>
    /// Direct-template 的 async streaming 入口。與 <see cref="FillDirectAsync"/>
    /// 共用同一 copy/open/allowlist/package 邊界，只讓 callback 在既有 package 內
    /// 逐頁取得資料並直接寫 worksheet parts。
    /// </summary>
    internal static Task<ExportStats> FillDirectStreamingAsync(
        ReportTemplateCatalog catalog,
        string templateName,
        Stream output,
        IReadOnlyCollection<string> allowedWorksheetNames,
        Func<DirectTemplateWorkbookEditor, CancellationToken, Task<IReadOnlyList<SheetStat>>> fill,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fill);

        return FillDirectCoreAsync(
            catalog,
            templateName,
            output,
            allowedWorksheetNames,
            fill,
            cancellationToken);
    }

    private static async Task<ExportStats> FillDirectCoreAsync(
        ReportTemplateCatalog catalog,
        string templateName,
        Stream output,
        IReadOnlyCollection<string> allowedWorksheetNames,
        Func<DirectTemplateWorkbookEditor, CancellationToken, Task<IReadOnlyList<SheetStat>>> fill,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(allowedWorksheetNames);
        ArgumentNullException.ThrowIfNull(fill);

        await catalog.CopyToAsync(templateName, output, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            IReadOnlyList<SheetStat> sheetStats;
            using (var target = SpreadsheetDocument.Open(
                       output,
                       true,
                       new OpenSettings { AutoSave = false }))
            {
                ValidateWorkbook(target, templateName);
                var editor = new DirectTemplateWorkbookEditor(target, allowedWorksheetNames);
                sheetStats = await fill(editor, cancellationToken).ConfigureAwait(false);
                editor.EnsureAllAllowedWorksheetsWereRewritten();
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Streaming writers have already opened rewritten worksheet entries in
            // package Update mode. Reopen once before touching every worksheet DOM;
            // the packaging API does not permit opening the same entry twice in one pass.
            using (var target = SpreadsheetDocument.Open(
                       output,
                       true,
                       new OpenSettings { AutoSave = false }))
            {
                ValidateWorkbook(target, templateName);
                FormalWorkbookTypography.Normalize(target);
                cancellationToken.ThrowIfCancellationRequested();
            }

            output.Flush();
            return new ExportStats(output.Length, sheetStats);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JetActionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or OpenXmlPackageException
                                           or FileFormatException
                                           or InvalidDataException
                                           or XmlException)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"無法直接填入 JET 內嵌範本 '{templateName}'；範本可能損壞或不是有效的 xlsx。");
        }
    }

    private static void ValidateWorkbook(SpreadsheetDocument document, string name)
    {
        var workbookPart = document.WorkbookPart;
        if (workbookPart?.Workbook?.Sheets is null
            || workbookPart.WorkbookStylesPart?.Stylesheet is null)
        {
            throw new InvalidDataException($"Workbook '{name}' 缺少必要的 workbook 或 styles part。");
        }

        var sheets = workbookPart.Workbook.Sheets.Elements<Sheet>().ToArray();
        foreach (var sheet in sheets)
        {
            if (string.IsNullOrWhiteSpace(sheet.Name?.Value)
                || string.IsNullOrWhiteSpace(sheet.Id?.Value))
            {
                throw new InvalidDataException(
                    $"Workbook '{name}' 含有缺少名稱或 relationship id 的工作表。");
            }
        }

        var duplicate = sheets
            .GroupBy(sheet => sheet.Name!.Value!, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Workbook '{name}' 含有重複工作表名稱 '{duplicate.Key}'。");
        }

        foreach (var sheet in sheets)
        {
            OpenXmlPart part;
            try
            {
                part = workbookPart.GetPartById(sheet.Id!.Value!);
            }
            catch (Exception exception) when (
                exception is ArgumentOutOfRangeException
                    or KeyNotFoundException
                    or InvalidOperationException)
            {
                throw new InvalidDataException(
                    $"Workbook '{name}' 的工作表 '{sheet.Name!.Value}' relationship 無效。",
                    exception);
            }

            if (part is not WorksheetPart)
            {
                throw new InvalidDataException(
                    $"Workbook '{name}' 的工作表 '{sheet.Name!.Value}' relationship 不是 worksheet part。");
            }
        }
    }
}

/// <summary>
/// 對既有 template cellXf 做 component-level、append-only 的外觀 patch。
/// null 表示保留 prototype；既有 font/fill/cellXf 索引永不搬動。
/// </summary>
internal sealed record WorkbookStylePatch(
    string? FontName = null,
    double? FontSize = null,
    bool? Bold = null,
    string? FontColorArgb = null,
    string? FillForegroundArgb = null,
    bool? WrapText = null,
    string? NumberFormatCode = null,
    bool Borderless = false)
{
    internal bool HasFontPatch =>
        FontName is not null
        || FontSize is not null
        || Bold is not null
        || FontColorArgb is not null;

    internal bool IsEmpty =>
        !HasFontPatch
        && FillForegroundArgb is null
        && WrapText is null
        && NumberFormatCode is null
        && !Borderless;
}

/// <summary>
/// 由 template style prototype 派生 bounded 外觀 variant。每組 prototype/patch
/// 只追加一次，且只切換被要求的 cellXf component。
/// </summary>
internal sealed class WorkbookStylePatcher
{
    private const uint FirstCustomNumberFormatId = 164;
    private readonly Stylesheet _stylesheet;
    private readonly Dictionary<(uint PrototypeStyle, WorkbookStylePatch Patch), uint> _cache = [];
    private readonly Dictionary<(uint TemplateStyle, uint SourceStyle), uint>
        _templateFillAndBorderCache = [];
    private uint? _borderlessBorderId;

    internal WorkbookStylePatcher(WorkbookStylesPart stylesPart)
    {
        ArgumentNullException.ThrowIfNull(stylesPart);
        _stylesheet = stylesPart.Stylesheet
            ?? throw new InvalidDataException("Template workbook has no stylesheet.");
        _stylesheet.Fonts ??= new Fonts();
        _stylesheet.Fills ??= new Fills();
        _stylesheet.Borders ??= new Borders();
        _stylesheet.CellStyleFormats ??= new CellStyleFormats();
        _stylesheet.CellFormats ??= new CellFormats();
    }

    internal uint Patch(uint prototypeStyleIndex, WorkbookStylePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        Validate(patch);
        var cellFormats = _stylesheet.CellFormats
            ?? throw new InvalidDataException("Template stylesheet has no cell formats.");
        var formats = cellFormats.Elements<CellFormat>().ToArray();
        if (prototypeStyleIndex >= formats.Length)
        {
            throw new InvalidDataException(
                $"Template style prototype {prototypeStyleIndex} does not exist.");
        }
        if (patch.IsEmpty)
        {
            return prototypeStyleIndex;
        }
        if (_cache.TryGetValue((prototypeStyleIndex, patch), out var cached))
        {
            return cached;
        }

        var prototype = formats[prototypeStyleIndex];
        var derived = (CellFormat)prototype.CloneNode(true);
        if (patch.HasFontPatch)
        {
            derived.FontId = AppendFont(prototype.FontId?.Value ?? 0U, patch);
            derived.ApplyFont = true;
        }
        if (patch.FillForegroundArgb is not null)
        {
            derived.FillId = AppendFill(
                prototype.FillId?.Value ?? 0U,
                patch.FillForegroundArgb);
            derived.ApplyFill = true;
        }
        if (patch.WrapText is not null)
        {
            var alignment = prototype.Alignment is null
                ? new Alignment()
                : (Alignment)prototype.Alignment.CloneNode(true);
            alignment.WrapText = patch.WrapText.Value;
            derived.Alignment = alignment;
            derived.ApplyAlignment = true;
        }
        if (patch.NumberFormatCode is not null)
        {
            derived.NumberFormatId = EnsureNumberFormat(patch.NumberFormatCode);
            derived.ApplyNumberFormat = true;
        }
        if (patch.Borderless)
        {
            derived.BorderId = EnsureBorderlessBorder();
            derived.ApplyBorder = true;
        }

        var result = checked((uint)cellFormats.ChildElements.Count);
        cellFormats.Append(derived);
        UpdateCounts();
        _stylesheet.Save();
        _cache[(prototypeStyleIndex, patch)] = result;
        return result;
    }

    /// <summary>
    /// 以 writer source style 保留資料格式、單行對齊與 protection，只把範本 prototype
    /// 的 fill／border component 帶回來。兩組 component 的 ownership 不互相覆蓋。
    /// </summary>
    internal uint PreserveTemplateFillAndBorder(
        uint templateStyleIndex,
        uint sourceStyleIndex)
    {
        if (_templateFillAndBorderCache.TryGetValue(
                (templateStyleIndex, sourceStyleIndex),
                out var cached))
        {
            return cached;
        }

        var cellFormats = _stylesheet.CellFormats
            ?? throw new InvalidDataException("Template stylesheet has no cell formats.");
        var formats = cellFormats.Elements<CellFormat>().ToArray();
        if (templateStyleIndex >= formats.Length || sourceStyleIndex >= formats.Length)
        {
            throw new InvalidDataException(
                $"Cannot merge template style {templateStyleIndex} with source style {sourceStyleIndex}.");
        }
        var template = formats[templateStyleIndex];
        var source = formats[sourceStyleIndex];
        if (template.FillId?.Value == source.FillId?.Value
            && template.ApplyFill?.Value == source.ApplyFill?.Value
            && template.BorderId?.Value == source.BorderId?.Value
            && template.ApplyBorder?.Value == source.ApplyBorder?.Value)
        {
            _templateFillAndBorderCache[(templateStyleIndex, sourceStyleIndex)] = sourceStyleIndex;
            return sourceStyleIndex;
        }

        var derived = (CellFormat)source.CloneNode(true);
        derived.FillId = template.FillId?.Value ?? 0U;
        derived.ApplyFill = template.ApplyFill?.Value;
        derived.BorderId = template.BorderId?.Value ?? 0U;
        derived.ApplyBorder = template.ApplyBorder?.Value;
        var result = checked((uint)cellFormats.ChildElements.Count);
        cellFormats.Append(derived);
        UpdateCounts();
        _stylesheet.Save();
        _templateFillAndBorderCache[(templateStyleIndex, sourceStyleIndex)] = result;
        return result;
    }

    private uint AppendFont(uint prototypeFontIndex, WorkbookStylePatch patch)
    {
        var fontCollection = _stylesheet.Fonts
            ?? throw new InvalidDataException("Template stylesheet has no fonts.");
        var fonts = fontCollection.Elements<SpreadsheetFont>().ToArray();
        if (prototypeFontIndex >= fonts.Length)
        {
            throw new InvalidDataException(
                $"Template font prototype {prototypeFontIndex} does not exist.");
        }

        var font = (SpreadsheetFont)fonts[prototypeFontIndex].CloneNode(true);
        if (patch.FontName is not null)
        {
            font.FontName = new FontName { Val = patch.FontName };
        }
        if (patch.FontSize is not null)
        {
            font.FontSize = new FontSize { Val = patch.FontSize.Value };
        }
        if (patch.Bold is not null)
        {
            font.Bold = patch.Bold.Value ? new Bold() : null;
        }
        if (patch.FontColorArgb is not null)
        {
            font.Color = new SpreadsheetColor { Rgb = patch.FontColorArgb };
        }

        var result = checked((uint)fontCollection.ChildElements.Count);
        fontCollection.Append(font);
        return result;
    }

    private uint AppendFill(uint prototypeFillIndex, string foregroundArgb)
    {
        var fillCollection = _stylesheet.Fills
            ?? throw new InvalidDataException("Template stylesheet has no fills.");
        var fills = fillCollection.Elements<Fill>().ToArray();
        if (prototypeFillIndex >= fills.Length)
        {
            throw new InvalidDataException(
                $"Template fill prototype {prototypeFillIndex} does not exist.");
        }

        var fill = (Fill)fills[prototypeFillIndex].CloneNode(true);
        var pattern = fill.PatternFill is null
            ? new PatternFill()
            : (PatternFill)fill.PatternFill.CloneNode(true);
        pattern.PatternType = PatternValues.Solid;
        pattern.ForegroundColor = new ForegroundColor { Rgb = foregroundArgb };
        fill.PatternFill = pattern;
        var result = checked((uint)fillCollection.ChildElements.Count);
        fillCollection.Append(fill);
        return result;
    }

    private uint EnsureNumberFormat(string code)
    {
        if (string.Equals(code, "General", StringComparison.Ordinal))
        {
            return 0U;
        }
        if (string.Equals(code, "@", StringComparison.Ordinal))
        {
            return 49U;
        }

        var numberingFormats = _stylesheet.NumberingFormats ??= new NumberingFormats();
        var formats = numberingFormats.Elements<NumberingFormat>().ToArray();
        var existing = formats.FirstOrDefault(format => string.Equals(
            format.FormatCode?.Value,
            code,
            StringComparison.Ordinal));
        if (existing?.NumberFormatId?.Value is { } existingId)
        {
            return existingId;
        }

        var used = formats
            .Where(format => format.NumberFormatId?.Value is not null)
            .Select(format => format.NumberFormatId!.Value)
            .ToHashSet();
        var next = used
            .Where(value => value >= FirstCustomNumberFormatId)
            .DefaultIfEmpty(FirstCustomNumberFormatId - 1U)
            .Max() + 1U;
        while (used.Contains(next))
        {
            next++;
        }
        numberingFormats.Append(new NumberingFormat
        {
            NumberFormatId = next,
            FormatCode = code
        });
        return next;
    }

    private uint EnsureBorderlessBorder()
    {
        if (_borderlessBorderId is { } cached)
        {
            return cached;
        }

        var borders = _stylesheet.Borders
            ?? throw new InvalidDataException("Template stylesheet has no borders.");
        var result = checked((uint)borders.ChildElements.Count);
        borders.Append(new Border(
            new LeftBorder(),
            new RightBorder(),
            new TopBorder(),
            new BottomBorder(),
            new DiagonalBorder()));
        _borderlessBorderId = result;
        return result;
    }

    private static void Validate(WorkbookStylePatch patch)
    {
        if (patch.FontName is not null && string.IsNullOrWhiteSpace(patch.FontName))
        {
            throw new ArgumentException("Font name cannot be blank.", nameof(patch));
        }
        if (patch.FontSize is { } size && (!double.IsFinite(size) || size <= 0D))
        {
            throw new ArgumentOutOfRangeException(nameof(patch), "Font size must be finite and positive.");
        }
        ValidateArgb(patch.FontColorArgb, nameof(patch.FontColorArgb));
        ValidateArgb(patch.FillForegroundArgb, nameof(patch.FillForegroundArgb));
        if (patch.NumberFormatCode is not null && string.IsNullOrWhiteSpace(patch.NumberFormatCode))
        {
            throw new ArgumentException("Number format code cannot be blank.", nameof(patch));
        }
    }

    private static void ValidateArgb(string? value, string name)
    {
        if (value is null)
        {
            return;
        }
        if (value.Length != 8 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("ARGB colors must contain exactly eight hexadecimal digits.", name);
        }
    }

    private void UpdateCounts()
    {
        if (_stylesheet.NumberingFormats is not null)
        {
            _stylesheet.NumberingFormats.Count =
                checked((uint)_stylesheet.NumberingFormats.ChildElements.Count);
        }
        _stylesheet.Fonts!.Count = checked((uint)_stylesheet.Fonts.ChildElements.Count);
        _stylesheet.Fills!.Count = checked((uint)_stylesheet.Fills.ChildElements.Count);
        _stylesheet.Borders!.Count = checked((uint)_stylesheet.Borders.ChildElements.Count);
        _stylesheet.CellStyleFormats!.Count =
            checked((uint)_stylesheet.CellStyleFormats.ChildElements.Count);
        _stylesheet.CellFormats!.Count =
            checked((uint)_stylesheet.CellFormats.ChildElements.Count);
    }
}

/// <summary>
/// 將 current writer 的 cellXfs 與相依 font/fill/border/numFmt 追加到範本樣式表，
/// 只讓新增的動態 sheet／cell 使用新索引；範本既有 style index 完全不搬動。
/// </summary>
internal sealed class WorkbookStyleMap
{
    private const uint FirstCustomNumberFormatId = 164;
    private readonly IReadOnlyDictionary<uint, uint> _cellFormats;

    private WorkbookStyleMap(IReadOnlyDictionary<uint, uint> cellFormats)
    {
        _cellFormats = cellFormats;
    }

    internal uint Map(uint sourceStyleIndex) =>
        _cellFormats.TryGetValue(sourceStyleIndex, out var mapped)
            ? mapped
            : throw new InvalidDataException(
                $"Generated workbook references unknown style index {sourceStyleIndex}.");

    internal static WorkbookStyleMap Append(
        WorkbookStylesPart targetPart,
        WorkbookStylesPart sourcePart)
    {
        var source = sourcePart.Stylesheet
            ?? throw new InvalidDataException("Generated workbook has no stylesheet.");
        return Append(targetPart, source);
    }

    internal static WorkbookStyleMap Append(
        WorkbookStylesPart targetPart,
        Stylesheet source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var target = targetPart.Stylesheet
            ?? throw new InvalidDataException("Template workbook has no stylesheet.");

        target.Fonts ??= new Fonts();
        target.Fills ??= new Fills();
        target.Borders ??= new Borders();
        target.CellStyleFormats ??= new CellStyleFormats();
        target.CellFormats ??= new CellFormats();

        var numberFormats = AppendNumberFormats(target, source);
        var fonts = AppendElements(target.Fonts, source.Fonts?.Elements<SpreadsheetFont>());
        var fills = AppendElements(target.Fills, source.Fills?.Elements<Fill>());
        var borders = AppendElements(target.Borders, source.Borders?.Elements<Border>());

        var styleFormats = new Dictionary<uint, uint>();
        var sourceStyleFormats = source.CellStyleFormats?.Elements<CellFormat>().ToArray()
            ?? [];
        for (var index = 0; index < sourceStyleFormats.Length; index++)
        {
            var clone = CloneFormat(
                sourceStyleFormats[index],
                numberFormats,
                fonts,
                fills,
                borders,
                styleFormats: null);
            var targetIndex = checked((uint)target.CellStyleFormats.Elements<CellFormat>().Count());
            target.CellStyleFormats.Append(clone);
            styleFormats[checked((uint)index)] = targetIndex;
        }

        var formats = new Dictionary<uint, uint>();
        var sourceFormats = source.CellFormats?.Elements<CellFormat>().ToArray()
            ?? throw new InvalidDataException("Generated workbook has no cell formats.");
        for (var index = 0; index < sourceFormats.Length; index++)
        {
            var clone = CloneFormat(
                sourceFormats[index],
                numberFormats,
                fonts,
                fills,
                borders,
                styleFormats);
            var targetIndex = checked((uint)target.CellFormats.Elements<CellFormat>().Count());
            target.CellFormats.Append(clone);
            formats[checked((uint)index)] = targetIndex;
        }

        UpdateCounts(target);
        target.Save();
        return new WorkbookStyleMap(formats);
    }

    private static Dictionary<uint, uint> AppendNumberFormats(
        Stylesheet target,
        Stylesheet source)
    {
        target.NumberingFormats ??= new NumberingFormats();
        var targetFormats = target.NumberingFormats
            .Elements<NumberingFormat>()
            .Where(format => format.NumberFormatId?.Value is not null)
            .ToArray();
        var byCode = targetFormats
            .Where(format => format.FormatCode?.Value is not null)
            .GroupBy(format => format.FormatCode!.Value!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().NumberFormatId!.Value,
                StringComparer.Ordinal);
        var used = targetFormats
            .Select(format => format.NumberFormatId!.Value)
            .ToHashSet();
        var next = used
            .Where(value => value >= FirstCustomNumberFormatId)
            .DefaultIfEmpty(FirstCustomNumberFormatId - 1)
            .Max() + 1;
        var result = new Dictionary<uint, uint>();

        foreach (var sourceFormat in source.NumberingFormats?.Elements<NumberingFormat>()
                     ?? Enumerable.Empty<NumberingFormat>())
        {
            var sourceId = sourceFormat.NumberFormatId?.Value
                ?? throw new InvalidDataException("Generated number format has no id.");
            var code = sourceFormat.FormatCode?.Value
                ?? throw new InvalidDataException("Generated number format has no format code.");
            if (byCode.TryGetValue(code, out var existing))
            {
                result[sourceId] = existing;
                continue;
            }

            while (used.Contains(next))
            {
                next++;
            }

            var clone = (NumberingFormat)sourceFormat.CloneNode(true);
            clone.NumberFormatId = next;
            target.NumberingFormats.Append(clone);
            result[sourceId] = next;
            byCode[code] = next;
            used.Add(next);
            next++;
        }

        return result;
    }

    private static Dictionary<uint, uint> AppendElements<T>(
        OpenXmlCompositeElement target,
        IEnumerable<T>? source)
        where T : OpenXmlElement
    {
        var result = new Dictionary<uint, uint>();
        var index = 0U;
        foreach (var item in source ?? Enumerable.Empty<T>())
        {
            var targetIndex = checked((uint)target.ChildElements.Count);
            target.Append(item.CloneNode(true));
            result[index] = targetIndex;
            index++;
        }
        return result;
    }

    private static CellFormat CloneFormat(
        CellFormat source,
        IReadOnlyDictionary<uint, uint> numberFormats,
        IReadOnlyDictionary<uint, uint> fonts,
        IReadOnlyDictionary<uint, uint> fills,
        IReadOnlyDictionary<uint, uint> borders,
        IReadOnlyDictionary<uint, uint>? styleFormats)
    {
        var clone = (CellFormat)source.CloneNode(true);
        clone.FontId = MapRequired(source.FontId?.Value ?? 0, fonts, "font");
        clone.FillId = MapRequired(source.FillId?.Value ?? 0, fills, "fill");
        clone.BorderId = MapRequired(source.BorderId?.Value ?? 0, borders, "border");

        var sourceNumberFormat = source.NumberFormatId?.Value ?? 0;
        clone.NumberFormatId = sourceNumberFormat >= FirstCustomNumberFormatId
            ? MapRequired(sourceNumberFormat, numberFormats, "number format")
            : sourceNumberFormat;

        if (styleFormats is not null)
        {
            clone.FormatId = MapRequired(source.FormatId?.Value ?? 0, styleFormats, "cell style format");
        }
        return clone;
    }

    private static uint MapRequired(
        uint source,
        IReadOnlyDictionary<uint, uint> map,
        string kind) =>
        map.TryGetValue(source, out var target)
            ? target
            : throw new InvalidDataException(
                $"Generated stylesheet references unknown {kind} index {source}.");

    private static void UpdateCounts(Stylesheet stylesheet)
    {
        if (stylesheet.NumberingFormats is not null)
        {
            stylesheet.NumberingFormats.Count =
                checked((uint)stylesheet.NumberingFormats.ChildElements.Count);
        }
        stylesheet.Fonts!.Count = checked((uint)stylesheet.Fonts.ChildElements.Count);
        stylesheet.Fills!.Count = checked((uint)stylesheet.Fills.ChildElements.Count);
        stylesheet.Borders!.Count = checked((uint)stylesheet.Borders.ChildElements.Count);
        stylesheet.CellStyleFormats!.Count =
            checked((uint)stylesheet.CellStyleFormats.ChildElements.Count);
        stylesheet.CellFormats!.Count =
            checked((uint)stylesheet.CellFormats.ChildElements.Count);
    }
}
