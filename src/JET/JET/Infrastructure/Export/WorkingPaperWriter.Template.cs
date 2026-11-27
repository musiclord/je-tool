using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

internal sealed partial class WorkingPaperWriter
{
    private static readonly IReadOnlyList<string> WorkingPaperDirectTemplateSheets =
    [
        WorkingPaperSheetCatalog.Cover,
        WorkingPaperSheetCatalog.Completeness,
        WorkingPaperSheetCatalog.VoucherBalance,
        WorkingPaperSheetCatalog.PreparerSummary,
        WorkingPaperSheetCatalog.CompletenessDifference,
        WorkingPaperSheetCatalog.InfSampling,
        WorkingPaperSheetCatalog.HighRiskSummary,
        WorkingPaperSheetCatalog.HighRiskVoucher,
        WorkingPaperSheetCatalog.HighRiskVoucherDetail,
        WorkingPaperSheetCatalog.FieldInfo,
        WorkingPaperSheetCatalog.CalendarInfo,
        WorkingPaperSheetCatalog.AccountMapping
    ];

    private static readonly IReadOnlySet<string> WorkingPaperSourceProtection =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "sheetProtection"
        };

    private static readonly IReadOnlySet<string> WorkingPaperSourceProtectionAndValidation =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "sheetProtection",
            "dataValidations"
        };

    /// <summary>
    /// 受保護工作表裡要鎖定的資料區，欄與起始列照舊版 JET 鎖定的儲存格。
    /// 程式寫的格子從 <see cref="FirstRow"/> 起往下都算；範本原有的格子只算到 <see cref="LastTemplateRow"/>，
    /// 資料沒寫到的範本空白列維持可填。資料列在這幾欄裡沒有值的格子，寫出時補成空白格一起鎖定。
    /// 續頁照同一個範圍鎖定，其他格子和第一頁一樣可以填。
    /// </summary>
    internal sealed record WorkingPaperLockedArea(
        uint FirstRow,
        uint FirstColumn,
        uint LastColumn,
        uint LastTemplateRow)
    {
        internal bool LocksWrittenCell(uint row, uint column) =>
            row >= FirstRow && column >= FirstColumn && column <= LastColumn;

        internal bool LocksTemplateCell(uint row, uint column) =>
            row <= LastTemplateRow && LocksWrittenCell(row, column);
    }

    private sealed record LocalDefinedNameOwner(int Ordinal, string Owner);

    private sealed record ContinuationPrototype(string SheetName, WorksheetPart Part);

    private sealed class WorkingPaperWriteSession
    {
        private readonly DirectTemplateWorkbookEditor _editor;
        private readonly WorkbookPart _workbookPart;
        private readonly WorkbookStyleMap _styles;
        private readonly WorkbookStylePatcher _stylePatcher;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<string, WorksheetPart> _templateParts =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyDictionary<string, uint>>
            _continuationPrototypeStyles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DataValidations?> _templateValidations =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, double?> _templateDefaultColumnWidths =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<(uint Min, uint Max, uint Style)>>
            _templateColumnStyles = new(StringComparer.Ordinal);
        private readonly List<string> _desiredNames = [];
        private readonly HashSet<string> _openedNames = new(StringComparer.Ordinal);
        private SheetWriter? _lastOpenedSheet;
        private bool _topologyChanged;

        internal WorkingPaperWriteSession(
            DirectTemplateWorkbookEditor editor,
            WorkbookStyleMap styles,
            WorkbookStylePatcher stylePatcher,
            CancellationToken cancellationToken)
        {
            _editor = editor;
            _styles = styles;
            _stylePatcher = stylePatcher;
            _cancellationToken = cancellationToken;
            _workbookPart = editor.Document.WorkbookPart
                ?? throw new InvalidDataException("WorkingPaper 範本缺少 workbook part。");
        }

        /// <summary>
        /// 開啟一張工作表來寫，並記下它。工作表一張寫完才開下一張，所以只要記最後開的那一張。
        /// </summary>
        /// <param name="lockedArea">受保護工作表裡程式寫入的資料區；其他格子保持可填。</param>
        /// <param name="keptTemplateCells">這次不寫、照範本原字保留的固定格。</param>
        internal SheetWriter OpenSheet(
            string name,
            WorkingPaperAppearanceProfile? appearance = null,
            WorkingPaperLockedArea? lockedArea = null,
            IReadOnlySet<(uint Row, uint Column)>? keptTemplateCells = null)
        {
            var sheet = CreateSheetWriter(name, appearance, lockedArea, keptTemplateCells);
            _lastOpenedSheet = sheet;
            return sheet;
        }

        /// <summary>
        /// 放掉最後開啟的工作表。正常寫完的工作表已經關閉，這裡不會再做事；
        /// 開啟之後、交到負責釋放它的程式手上之前就失敗的工作表，例如寫表頭的固定列時失敗，只有這裡放得掉。
        /// </summary>
        internal void ReleaseLastOpenedSheet() => _lastOpenedSheet?.Dispose();

        private SheetWriter CreateSheetWriter(
            string name,
            WorkingPaperAppearanceProfile? appearance,
            WorkingPaperLockedArea? lockedArea,
            IReadOnlySet<(uint Row, uint Column)>? keptTemplateCells)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var normalizedName = ExcelWorksheetConstraints.ContinuationSheetName(name, 1);
            if (!string.Equals(normalizedName, name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Worksheet name '{name}' is not a safe Excel sheet name.");
            }
            if (!_openedNames.Add(name))
            {
                throw new InvalidOperationException($"Worksheet name '{name}' is duplicated.");
            }
            _desiredNames.Add(name);
            Func<uint, uint, uint, uint>? mapCellStyle = appearance is null
                ? null
                : (row, column, prototypeStyle) =>
                    appearance.PatchForCell(row, column) is { } patch
                        ? _stylePatcher.Patch(prototypeStyle, patch)
                        : prototypeStyle;
            var writtenCellStyle = WithLockedArea(mapCellStyle, lockedArea, writtenCell: true);
            var templateCellStyle = WithLockedArea(mapCellStyle, lockedArea, writtenCell: false);

            if (name is WorkingPaperSheetCatalog.Intro or WorkingPaperSheetCatalog.PostClosingEntries)
            {
                return SheetWriter.PreserveTemplate(name, _styles, _cancellationToken);
            }

            if (WorkingPaperDirectTemplateSheets.Contains(name, StringComparer.Ordinal))
            {
                var part = _editor.OpenWorksheetPartForStreamingRewrite(
                    name,
                    _cancellationToken);
                if (string.Equals(name, WorkingPaperSheetCatalog.Cover, StringComparison.Ordinal))
                {
                    RemoveCoverEmbeddedDocument(part, _cancellationToken);
                }
                if (string.Equals(name, WorkingPaperSheetCatalog.HighRiskVoucher, StringComparison.Ordinal))
                {
                    _continuationPrototypeStyles.Add(
                        name,
                        CaptureHighRiskVoucherContinuationPrototypeStyles(
                            part,
                            _cancellationToken));
                }
                if (name is WorkingPaperSheetCatalog.InfSampling or WorkingPaperSheetCatalog.HighRiskVoucher)
                {
                    _templateValidations.Add(
                        name,
                        ReadTemplateDataValidations(part, _cancellationToken));
                }
                if (lockedArea is not null)
                {
                    // 第一頁會直接改寫範本工作表，續頁要用的欄樣式與預設欄寬先讀出來。
                    // 這裡不要求範本有預設欄寬，開續頁時才要求。
                    _templateColumnStyles.Add(
                        name,
                        ReadTemplateColumnStyles(part, _cancellationToken));
                    _templateDefaultColumnWidths.Add(
                        name,
                        ReadTemplateDefaultColumnWidth(part, _cancellationToken));
                }
                _templateParts.Add(name, part);
                Func<uint, uint, uint?, uint, uint>? mergeCellStyle = name is
                    WorkingPaperSheetCatalog.InfSampling
                    or WorkingPaperSheetCatalog.HighRiskSummary
                    or WorkingPaperSheetCatalog.HighRiskVoucher
                    ? (_, column, templateStyle, sourceStyle) =>
                    {
                        if (templateStyle is null
                            && string.Equals(
                                name,
                                WorkingPaperSheetCatalog.HighRiskVoucher,
                                StringComparison.Ordinal)
                            && _continuationPrototypeStyles[name].TryGetValue(
                                ReportSheetWriter.Reference(column, 13U),
                                out var prototypeStyle))
                        {
                            templateStyle = prototypeStyle;
                        }
                        return templateStyle is { } style
                            ? _stylePatcher.PreserveTemplateFillAndBorder(
                                style,
                                sourceStyle)
                            : sourceStyle;
                    }
                    : null;
                var plan = DirectOverlayPlan(
                    name,
                    templateCellStyle,
                    mergeCellStyle);
                if (lockedArea is not null)
                {
                    plan = plan with
                    {
                        MapTemplateColumnStyle = style => _editor.EnsureProtectionStyle(style, locked: false)
                    };
                }
                if (keptTemplateCells is { Count: > 0 })
                {
                    var isDynamicCell = plan.IsDynamicCell;
                    var useSourceCellStyle = plan.UseSourceCellStyle;
                    plan = plan with
                    {
                        IsDynamicCell = (row, column) =>
                            !keptTemplateCells.Contains((row, column)) && isDynamicCell(row, column),
                        UseSourceCellStyle = (row, column) =>
                            !keptTemplateCells.Contains((row, column)) && useSourceCellStyle(row, column)
                    };
                }
                return SheetWriter.OverlayTemplate(
                    name,
                    part,
                    plan,
                    _styles,
                    writtenCellStyle,
                    _cancellationToken,
                    _templateValidations.GetValueOrDefault(name),
                    lockedArea);
            }

            var prototype = FindContinuationPrototype(name);
            var prototypePart = prototype.Part;
            var continuationPart = _workbookPart.AddNewPart<WorksheetPart>();
            CopyContinuationRelationships(prototypePart, continuationPart);
            var nextSheetId = _editor.Sheets.Elements<Sheet>()
                .Select(sheet => sheet.SheetId?.Value ?? 0U)
                .DefaultIfEmpty(0U)
                .Max() + 1U;
            _editor.Sheets.Append(new Sheet
            {
                Id = _workbookPart.GetIdOfPart(continuationPart),
                SheetId = nextSheetId,
                Name = name
            });
            _topologyChanged = true;
            var continuationStyleMap = WithLockedArea(
                CreateContinuationCellStyleMapper(
                    prototype.SheetName,
                    mapCellStyle),
                lockedArea,
                writtenCell: true);
            return SheetWriter.CreateContinuation(
                name,
                continuationPart,
                _styles,
                continuationStyleMap,
                _cancellationToken,
                prototypePart.Parts
                    .SingleOrDefault(relationship =>
                        relationship.OpenXmlPart is SpreadsheetPrinterSettingsPart)
                    .RelationshipId,
                _templateValidations.GetValueOrDefault(prototype.SheetName),
                lockedArea,
                lockedArea is null
                    ? null
                    : (ContinuationColumnStyles(prototype.SheetName),
                        _templateDefaultColumnWidths[prototype.SheetName]
                        ?? throw new InvalidDataException("WorkingPaper 範本工作表缺少預設欄寬。")));
        }

        /// <summary>
        /// 有鎖定區的續頁各欄要套的樣式：照範本同一欄的樣式換成不鎖定的版本，和第一頁沒寫值的格子相同。
        /// 範本沒有設定樣式的欄用 JET 寫出時的預設樣式。回傳的分段由左到右蓋滿每一欄。
        /// </summary>
        private IReadOnlyList<(uint Min, uint Max, uint Style)> ContinuationColumnStyles(
            string prototypeSheetName)
        {
            var fallback = _editor.EnsureProtectionStyle(
                _styles.Map(WorkingPaperStyles.Default),
                locked: false);
            var segments = new List<(uint Min, uint Max, uint Style)>();
            var next = 1U;
            foreach (var column in _templateColumnStyles[prototypeSheetName].OrderBy(item => item.Min))
            {
                var min = Math.Max(column.Min, next);
                var max = Math.Min(column.Max, ExcelWorksheetConstraints.MaxColumns);
                if (min > max)
                {
                    continue;
                }
                if (min > next)
                {
                    segments.Add((next, min - 1, fallback));
                }
                segments.Add((min, max, _editor.EnsureProtectionStyle(column.Style, locked: false)));
                next = max + 1;
            }
            if (next <= ExcelWorksheetConstraints.MaxColumns)
            {
                segments.Add((next, ExcelWorksheetConstraints.MaxColumns, fallback));
            }
            return segments;
        }

        /// <summary>
        /// 受保護的工作表照舊版先解鎖整張表，只鎖程式寫入的資料區。
        /// 程式寫的格子在資料區裡就鎖定，留給審計員填寫的 <see cref="WorkingPaperStyles.Editable"/> 例外；
        /// 範本原有的格子只在資料區的表頭範圍內鎖定。
        /// </summary>
        private Func<uint, uint, uint, uint>? WithLockedArea(
            Func<uint, uint, uint, uint>? inner,
            WorkingPaperLockedArea? lockedArea,
            bool writtenCell)
        {
            if (lockedArea is null)
            {
                return inner;
            }
            var editable = _styles.Map(WorkingPaperStyles.Editable);
            return (row, column, style) =>
            {
                var mapped = inner?.Invoke(row, column, style) ?? style;
                var locked = writtenCell
                    ? lockedArea.LocksWrittenCell(row, column) && style != editable
                    : lockedArea.LocksTemplateCell(row, column);
                return _editor.EnsureProtectionStyle(mapped, locked);
            };
        }

        private static DataValidations? ReadTemplateDataValidations(
            WorksheetPart part,
            CancellationToken cancellationToken)
        {
            using var reader = OpenXmlReader.Create(part);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.IsStartElement && reader.ElementType == typeof(DataValidations))
                {
                    return (DataValidations)reader.LoadCurrentElement()!;
                }
            }
            return null;
        }

        /// <summary>
        /// 範本工作表的預設欄寬；續頁補上的欄設定沿用這個寬度，和第一頁沒有另外設定寬度的欄一樣寬。
        /// 範本沒有寫這個值時回傳 null，只有一頁的工作表用不到它。
        /// </summary>
        private static double? ReadTemplateDefaultColumnWidth(
            WorksheetPart part,
            CancellationToken cancellationToken)
        {
            using var reader = OpenXmlReader.Create(part);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.IsStartElement && reader.ElementType == typeof(SheetFormatProperties))
                {
                    var properties = (SheetFormatProperties)reader.LoadCurrentElement()!;
                    return properties.DefaultColumnWidth?.Value is double width && width > 0D
                        ? width
                        : null;
                }
                if (reader.ElementType == typeof(SheetData))
                {
                    break;
                }
            }
            return null;
        }

        /// <summary>範本工作表各欄的預設樣式；續頁照這些樣式補欄設定，沒寫值的格子才會和第一頁同一欄一樣對齊。</summary>
        private static IReadOnlyList<(uint Min, uint Max, uint Style)> ReadTemplateColumnStyles(
            WorksheetPart part,
            CancellationToken cancellationToken)
        {
            using var reader = OpenXmlReader.Create(part);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.IsStartElement && reader.ElementType == typeof(Columns))
                {
                    return ((Columns)reader.LoadCurrentElement()!).Elements<Column>()
                        .Where(column => column.Min?.Value is not null && column.Max?.Value is not null)
                        .Select(column => (column.Min!.Value, column.Max!.Value, column.Style?.Value ?? 0U))
                        .ToArray();
                }
                if (reader.ElementType == typeof(SheetData))
                {
                    break;
                }
            }
            return [];
        }

        private ContinuationPrototype FindContinuationPrototype(string continuationName)
        {
            foreach (var pair in _templateParts)
            {
                for (var pageNumber = 2;
                     pageNumber <= _desiredNames.Count + 2;
                     pageNumber++)
                {
                    if (string.Equals(
                            ExcelWorksheetConstraints.ContinuationSheetName(
                                pair.Key,
                                pageNumber),
                            continuationName,
                            StringComparison.Ordinal))
                    {
                        return new ContinuationPrototype(pair.Key, pair.Value);
                    }
                }
            }

            throw new InvalidDataException(
                $"WorkingPaper continuation worksheet '{continuationName}' 沒有已開啟的範本 prototype。");
        }

        private Func<uint, uint, uint, uint>? CreateContinuationCellStyleMapper(
            string prototypeSheetName,
            Func<uint, uint, uint, uint>? sourceStyleMap)
        {
            if (!string.Equals(
                    prototypeSheetName,
                    WorkingPaperSheetCatalog.HighRiskVoucher,
                    StringComparison.Ordinal))
            {
                return sourceStyleMap;
            }

            var prototypeStyles = _continuationPrototypeStyles[prototypeSheetName];
            return (row, column, sourceStyle) =>
            {
                var mappedSource = sourceStyleMap?.Invoke(row, column, sourceStyle)
                    ?? sourceStyle;
                var prototypeRow = row >= 13U ? 13U : row;
                var reference = ReportSheetWriter.Reference(column, prototypeRow);
                if (!prototypeStyles.TryGetValue(reference, out var templateStyle))
                {
                    return mappedSource;
                }
                return row < 13U
                    ? templateStyle
                    : _stylePatcher.PreserveTemplateFillAndBorder(
                        templateStyle,
                        mappedSource);
            };
        }

        private static IReadOnlyDictionary<string, uint>
            CaptureHighRiskVoucherContinuationPrototypeStyles(
                WorksheetPart prototypePart,
                CancellationToken cancellationToken)
        {
            var styles = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            using var reader = OpenXmlReader.Create(prototypePart);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.ElementType != typeof(Row))
                {
                    continue;
                }

                var row = reader.LoadCurrentElement() as Row
                    ?? throw new InvalidDataException(
                        "WorkingPaper Step4 prototype row cannot be read.");
                var rowIndex = row.RowIndex?.Value
                    ?? throw new InvalidDataException(
                        "WorkingPaper Step4 prototype row has no index.");
                if (rowIndex > 13U)
                {
                    break;
                }
                foreach (var cell in row.Elements<Cell>())
                {
                    var reference = cell.CellReference?.Value
                        ?? throw new InvalidDataException(
                            "WorkingPaper Step4 prototype cell has no reference.");
                    if (!styles.TryAdd(reference, cell.StyleIndex?.Value ?? 0U))
                    {
                        throw new InvalidDataException(
                            "WorkingPaper Step4 prototype contains a duplicate cell reference.");
                    }
                }
            }
            for (uint column = 1; column <= 21; column++)
            {
                var reference = ReportSheetWriter.Reference(column, 13U);
                if (!styles.ContainsKey(reference))
                {
                    throw new InvalidDataException(
                        "WorkingPaper Step4 prototype does not cover the bounded A13:U13 style row.");
                }
            }
            return styles;
        }

        private static void CopyContinuationRelationships(
            WorksheetPart prototype,
            WorksheetPart continuation)
        {
            foreach (var relationship in prototype.Parts)
            {
                continuation.CreateRelationshipToPart(
                    relationship.OpenXmlPart,
                    relationship.RelationshipId);
            }
            foreach (var relationship in prototype.ExternalRelationships)
            {
                continuation.AddExternalRelationship(
                    relationship.RelationshipType,
                    relationship.Uri,
                    relationship.Id);
            }
            foreach (var relationship in prototype.HyperlinkRelationships)
            {
                continuation.AddHyperlinkRelationship(
                    relationship.Uri,
                    relationship.IsExternal,
                    relationship.Id);
            }
        }

        internal void Complete()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            foreach (var sheetName in WorkingPaperDirectTemplateSheets)
            {
                if (_openedNames.Contains(sheetName))
                {
                    continue;
                }
                _editor.RemoveWorksheet(sheetName, _cancellationToken);
                _topologyChanged = true;
            }

            if (!_openedNames.Contains(WorkingPaperSheetCatalog.Intro)
                || !_openedNames.Contains(WorkingPaperSheetCatalog.PostClosingEntries))
            {
                throw new InvalidDataException(
                    "WorkingPaper 正式輸出不可省略 Intro 或 Step5 固定範本 worksheet。");
            }

            var currentNames = _editor.Sheets.Elements<Sheet>()
                .Select(sheet => sheet.Name?.Value
                    ?? throw new InvalidDataException("WorkingPaper 範本 worksheet 缺少名稱。"))
                .ToArray();
            if (_topologyChanged
                || !currentNames.SequenceEqual(_desiredNames, StringComparer.Ordinal))
            {
                AlignDirectTemplateSheets(
                    _workbookPart,
                    _editor.Sheets,
                    _desiredNames,
                    _cancellationToken);
            }
        }
    }

    private static IReadOnlyList<LocalDefinedNameOwner> CaptureLocalDefinedNameOwners(
        WorkbookPart workbookPart)
    {
        var workbook = workbookPart.RequiredWorkbook();
        var sheets = workbook.Sheets!.Elements<Sheet>().ToArray();
        return workbook.DefinedNames?
            .Elements<DefinedName>()
            .Select((name, ordinal) => (name, ordinal))
            .Where(item => item.name.LocalSheetId?.Value is not null)
            .Select(item =>
            {
                var index = item.name.LocalSheetId!.Value;
                if (index >= sheets.Length)
                {
                    throw new InvalidDataException(
                        $"WorkingPaper 範本 defined name '{item.name.Name?.Value}' 的 localSheetId 無效。");
                }
                return new LocalDefinedNameOwner(
                    item.ordinal,
                    sheets[index].Name!.Value!);
            })
            .ToArray()
            ?? [];
    }

    /// <summary>
    /// 把工作表排成 <paramref name="desiredNames"/> 的順序，帶 localSheetId 的定義名稱跟著改成所屬工作表的新位置。
    /// 移除工作表時，屬於它的定義名稱已經一起拿掉，其餘名稱的 localSheetId 也已調整，
    /// 所以排序前依目前的位置記下每個名稱屬於哪一張工作表。
    /// </summary>
    private static void AlignDirectTemplateSheets(
        WorkbookPart workbookPart,
        Sheets sheets,
        IReadOnlyList<string> desiredNames,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localDefinedNameOwners = CaptureLocalDefinedNameOwners(workbookPart);
        var remaining = sheets.Elements<Sheet>()
            .ToDictionary(
                sheet => sheet.Name?.Value
                    ?? throw new InvalidDataException("WorkingPaper 範本 worksheet 缺少名稱。"),
                StringComparer.Ordinal);
        if (remaining.Count != desiredNames.Count
            || desiredNames.Any(name => !remaining.ContainsKey(name)))
        {
            throw new InvalidDataException(
                "WorkingPaper 範本與直接 SAX 產出 worksheet 集合無法對齊。");
        }

        foreach (var sheet in remaining.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sheet.Remove();
        }
        foreach (var name in desiredNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sheets.Append(remaining[name]);
        }

        var indexes = desiredNames
            .Select((name, index) => (name, index: checked((uint)index)))
            .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
        var workbook = workbookPart.RequiredWorkbook();
        var definedNames = workbook.DefinedNames?
            .Elements<DefinedName>()
            .ToArray()
            ?? [];
        foreach (var localName in localDefinedNameOwners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            definedNames[localName.Ordinal].LocalSheetId = indexes[localName.Owner];
        }

        workbook.Save();
    }

    private static DirectTemplateWorksheetOverlayPlan DirectOverlayPlan(
        string sheetName,
        Func<uint, uint, uint, uint>? mapCellStyle,
        Func<uint, uint, uint?, uint, uint>? mergeCellStyle) =>
        (sheetName switch
        {
            WorkingPaperSheetCatalog.Cover => OverlayOnly(
                mapCellStyle,
                (1U, 1U), (2U, 1U), (3U, 1U), (6U, 1U)),
            WorkingPaperSheetCatalog.Completeness => RowsAndCells(
                row => row >= 20,
                (row, column) => row >= 20 && column is >= 1 and <= 6,
                [(1U, 1U), (2U, 1U), (3U, 1U), (7U, 1U), (15U, 2U), (17U, 2U)],
                WorkingPaperSourceProtection),
            // 舊版 JET 的版面：B14 說明、第 16 列五欄表頭、第 17 列起明細。
            WorkingPaperSheetCatalog.VoucherBalance => RowsAndCells(
                row => row >= 17,
                (row, column) => row >= 17 && column is >= 2 and <= 6,
                [
                    (1U, 1U),
                    (2U, 1U),
                    (3U, 1U),
                    (7U, 1U),
                    (12U, 2U),
                    (14U, 2U),
                    (16U, 2U),
                    (16U, 3U),
                    (16U, 4U),
                    (16U, 5U),
                    (16U, 6U)
                ],
                WorkingPaperSourceProtection),
            WorkingPaperSheetCatalog.PreparerSummary => RowsAndCells(
                row => row >= 12,
                (row, column) => row >= 12 && column is >= 1 and <= 8,
                [(1U, 1U), (2U, 1U), (3U, 1U), (7U, 1U)],
                WorkingPaperSourceProtection),
            WorkingPaperSheetCatalog.CompletenessDifference => RowsAndCells(
                row => row >= 17,
                (row, column) => row >= 17 && column is >= 1 and <= 7,
                [(1U, 1U), (2U, 1U), (3U, 1U), (7U, 1U), (12U, 2U)],
                WorkingPaperSourceProtection),
            WorkingPaperSheetCatalog.InfSampling => RowsAndCells(
                row => row is 49 or 51 || row >= 53,
                (row, column) =>
                    row == 49 && column >= 21
                    || row == 51 && column >= 21
                    || row >= 53 && column >= 1,
                [(1U, 1U), (2U, 1U), (4U, 1U), (51U, 12U)],
                WorkingPaperSourceProtectionAndValidation),
            // 條件或原因換行的列由 EmitHighRiskSummary 依行數加高；其他列沿用範本的列高。
            WorkingPaperSheetCatalog.HighRiskSummary => RowsAndCells(
                row => row >= 19,
                (row, column) => row >= 19 && column is >= 2 and <= 5,
                [(1U, 1U), (2U, 1U), (7U, 2U), (9U, 2U)],
                WorkingPaperSourceProtection) with
            {
                UseSourceRowHeight = static row => row >= HighRiskSummaryFirstDataRow
            },
            // A12 的說明句整句寫在範本裡，第一頁照範本原字。
            WorkingPaperSheetCatalog.HighRiskVoucher => RowsAndCells(
                row => row >= 13,
                (row, column) => row >= 13
                    && (column is >= 1 and <= 15
                        || row > 1000 && column is >= 16 and <= 21),
                [(1U, 1U), (2U, 1U)],
                WorkingPaperSourceProtectionAndValidation),
            WorkingPaperSheetCatalog.HighRiskVoucherDetail =>
                new DirectTemplateWorksheetOverlayPlan(
                    IsDynamicRow: row => row is 1 or 2 || row >= 5,
                    IsDynamicCell: (row, column) =>
                        row == 1 && column == 1
                        || row == 2 && column == 1
                        || row >= 5,
                    UseSourcePostSheetDataElements: WorkingPaperSourceProtection,
                    UseSourceDimension: true,
                    PreserveTemplateExtent: false,
                     UseSourceColumnsWhenTemplateMissing: false,
                     UseSourceColumns: true,
                     MergeSourceColumnWidths: false,
                     UseSourceCellStyle: static (_, _) => true,
                     MapTemplateCellStyle: mapCellStyle,
                     MapTemplateRowStyle: mapCellStyle is null
                         ? null
                         : (row, style) => mapCellStyle(row, 0U, style)),
            // 舊版三張自動化工具表都整張保護。
            WorkingPaperSheetCatalog.FieldInfo => ReferenceData(
                maximumDynamicColumn: 8,
                WorkingPaperSourceProtection),
            WorkingPaperSheetCatalog.CalendarInfo => ReferenceData(
                maximumDynamicColumn: 3,
                WorkingPaperSourceProtection),
            WorkingPaperSheetCatalog.AccountMapping => ReferenceData(
                maximumDynamicColumn: 3,
                WorkingPaperSourceProtection),
            _ => throw new InvalidDataException(
                $"WorkingPaper worksheet '{sheetName}' 沒有直接 SAX overlay policy。")
        }) with
        {
            MapTemplateCellStyle = mapCellStyle,
            MergeCellStyle = mergeCellStyle
        };

    /// <param name="createRewritten">
    /// 建立放改寫內容的暫存檔；沒有指定時照一般匯出建立。測試從這裡留住暫存檔，核對取消之後它已經關閉並刪除。
    /// </param>
    internal static void RemoveCoverEmbeddedDocument(
        WorksheetPart worksheetPart,
        CancellationToken cancellationToken,
        Func<ExportSpoolFile>? createRewritten = null)
    {
        const string spreadsheetNamespace =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string relationshipNamespace =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        cancellationToken.ThrowIfCancellationRequested();

        var relationshipIds = new HashSet<string>(StringComparer.Ordinal);
        var removedElement = false;
        // 改寫的內容先放進暫存檔；取消或用不到這份內容時放掉暫存檔，緩衝寫不出去也不丟出新的錯誤。
        using var rewritten = createRewritten?.Invoke()
            ?? ExportSpoolFile.Create(".jet-working-paper-cover-", bufferSize: 128 * 1024);
        using (var reader = OpenXmlReader.Create(worksheetPart))
        using (var writer = OpenXmlWriter.Create(rewritten.Stream))
        {
            try
            {
                if (reader.StandaloneXml is bool standalone)
                {
                    writer.WriteStartDocument(standalone);
                }
                else
                {
                    writer.WriteStartDocument();
                }

                int? omittedDepth = null;
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.IsStartElement)
                    {
                        if (omittedDepth is null
                            && reader.Depth == 1
                            && string.Equals(
                                reader.NamespaceUri,
                                spreadsheetNamespace,
                                StringComparison.Ordinal)
                            && reader.LocalName is "drawing" or "legacyDrawing" or "oleObjects")
                        {
                            omittedDepth = reader.Depth;
                            removedElement = true;
                        }

                        if (omittedDepth is not null)
                        {
                            foreach (var attribute in reader.Attributes)
                            {
                                if (attribute.NamespaceUri == relationshipNamespace
                                    && attribute.LocalName == "id"
                                    && !string.IsNullOrEmpty(attribute.Value))
                                {
                                    relationshipIds.Add(attribute.Value);
                                }
                            }
                            continue;
                        }

                        writer.WriteStartElement(
                            reader,
                            reader.Attributes,
                            reader.NamespaceDeclarations);
                        var text = reader.GetText();
                        if (!string.IsNullOrEmpty(text))
                        {
                            writer.WriteString(text);
                        }
                        continue;
                    }

                    if (!reader.IsEndElement)
                    {
                        continue;
                    }

                    if (omittedDepth is int depth)
                    {
                        if (reader.Depth == depth)
                        {
                            omittedDepth = null;
                        }
                        continue;
                    }

                    writer.WriteEndElement();
                }
            }
            catch
            {
                // XML 寫入器關閉時會補上還沒寫的結尾，並把緩衝寫進暫存檔；這時暫存資料夾的磁碟已滿就會丟出新的錯誤，
                // 蓋掉取消或原本失敗的原因。改寫已經失敗或取消，先在這裡安靜地放掉寫入器，離開時的釋放就不會再丟出。
                ExportResources.ReleaseAll(writer);
                throw;
            }
        }

        if (!removedElement)
        {
            return;
        }

        rewritten.Stream.Position = 0;
        worksheetPart.FeedData(rewritten.Stream);

        foreach (var relationshipId in relationshipIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!worksheetPart.DeletePart(relationshipId))
            {
                throw new InvalidDataException(
                    "WorkingPaper cover embedded-document relationship closure is invalid.");
            }
        }
    }

    private static DirectTemplateWorksheetOverlayPlan OverlayOnly(
        Func<uint, uint, uint, uint>? mapCellStyle,
        params (uint Row, uint Column)[] cells)
    {
        var owned = cells.ToHashSet();
        return new DirectTemplateWorksheetOverlayPlan(
            IsDynamicRow: row => owned.Any(cell => cell.Row == row),
            IsDynamicCell: (row, column) => owned.Contains((row, column)),
            UseSourcePostSheetDataElements: null,
            UseSourceDimension: false,
            PreserveTemplateExtent: false,
            UseSourceColumnsWhenTemplateMissing: false,
            UseSourceColumns: false,
            MergeSourceColumnWidths: false,
            UseSourceCellStyle: static (_, _) => false,
            MapTemplateCellStyle: mapCellStyle);
    }

    private static DirectTemplateWorksheetOverlayPlan RowsAndCells(
        Func<uint, bool> isDynamicRow,
        Func<uint, uint, bool> isDynamicCell,
        IReadOnlyCollection<(uint Row, uint Column)> fixedCells,
        IReadOnlySet<string>? sourcePostElements,
        bool mergeSourceColumnWidths = true)
    {
        var owned = fixedCells.ToHashSet();
        return new DirectTemplateWorksheetOverlayPlan(
            IsDynamicRow: row => isDynamicRow(row) || owned.Any(cell => cell.Row == row),
            IsDynamicCell: (row, column) =>
                isDynamicCell(row, column) || owned.Contains((row, column)),
            UseSourcePostSheetDataElements: sourcePostElements,
            UseSourceDimension: true,
            PreserveTemplateExtent: true,
            UseSourceColumnsWhenTemplateMissing: false,
            UseSourceColumns: false,
            MergeSourceColumnWidths: mergeSourceColumnWidths,
            UseSourceCellStyle: isDynamicCell);
    }

    private static DirectTemplateWorksheetOverlayPlan ReferenceData(
        uint maximumDynamicColumn,
        IReadOnlySet<string>? sourcePostElements = null) =>
        new(
            IsDynamicRow: static _ => true,
            IsDynamicCell: (_, column) => column <= maximumDynamicColumn,
            UseSourcePostSheetDataElements: sourcePostElements,
            UseSourceDimension: true,
            PreserveTemplateExtent: true,
            UseSourceColumnsWhenTemplateMissing: true,
            UseSourceColumns: false,
            MergeSourceColumnWidths: true,
            UseSourceCellStyle: static (_, _) => false);
}
