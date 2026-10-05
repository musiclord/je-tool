using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class WorkpaperWriter
{
    private static readonly IReadOnlyList<string> WorkpaperDirectTemplateSheets =
    [
        WorkpaperSheetCatalog.Cover,
        WorkpaperSheetCatalog.Step1,
        WorkpaperSheetCatalog.Step11,
        WorkpaperSheetCatalog.Step12,
        WorkpaperSheetCatalog.Step13,
        WorkpaperSheetCatalog.Step2,
        WorkpaperSheetCatalog.Step3,
        WorkpaperSheetCatalog.Step4,
        WorkpaperSheetCatalog.Step41,
        WorkpaperSheetCatalog.FieldInfo,
        WorkpaperSheetCatalog.CalendarInfo,
        WorkpaperSheetCatalog.AccountMapping
    ];

    private static readonly IReadOnlySet<string> WorkpaperSourceProtection =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "sheetProtection"
        };

    private static readonly IReadOnlySet<string> WorkpaperSourceProtectionAndValidation =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "sheetProtection",
            "dataValidations"
        };

    private sealed record LocalDefinedNameOwner(int Ordinal, string Owner);

    private sealed record ContinuationPrototype(string SheetName, WorksheetPart Part);

    private sealed class WorkpaperWriteSession
    {
        private readonly DirectTemplateWorkbookEditor _editor;
        private readonly WorkbookPart _workbookPart;
        private readonly WorkbookStyleMap _styles;
        private readonly WorkbookStylePatcher _stylePatcher;
        private readonly bool _useFinalStep41Schema;
        private readonly CancellationToken _cancellationToken;
        private readonly IReadOnlyList<LocalDefinedNameOwner> _localDefinedNameOwners;
        private readonly Dictionary<string, WorksheetPart> _templateParts =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyDictionary<string, uint>>
            _continuationPrototypeStyles = new(StringComparer.Ordinal);
        private readonly List<string> _desiredNames = [];
        private readonly HashSet<string> _openedNames = new(StringComparer.Ordinal);
        private bool _topologyChanged;

        internal WorkpaperWriteSession(
            DirectTemplateWorkbookEditor editor,
            WorkbookStyleMap styles,
            WorkbookStylePatcher stylePatcher,
            bool useFinalStep41Schema,
            CancellationToken cancellationToken)
        {
            _editor = editor;
            _styles = styles;
            _stylePatcher = stylePatcher;
            _useFinalStep41Schema = useFinalStep41Schema;
            _cancellationToken = cancellationToken;
            _workbookPart = editor.Document.WorkbookPart
                ?? throw new InvalidDataException("WorkingPaper 範本缺少 workbook part。");
            _localDefinedNameOwners = CaptureLocalDefinedNameOwners(_workbookPart);
        }

        internal SheetWriter OpenSheet(
            string name,
            WorkpaperAppearanceProfile? appearance = null)
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

            if (name is WorkpaperSheetCatalog.Intro or WorkpaperSheetCatalog.Step5)
            {
                return SheetWriter.PreserveTemplate(name, _styles, _cancellationToken);
            }

            if (WorkpaperDirectTemplateSheets.Contains(name, StringComparer.Ordinal))
            {
                var part = _editor.OpenWorksheetPartForStreamingRewrite(
                    name,
                    _cancellationToken);
                if (string.Equals(name, WorkpaperSheetCatalog.Cover, StringComparison.Ordinal))
                {
                    RemoveCoverEmbeddedDocument(part, _cancellationToken);
                }
                if (string.Equals(name, WorkpaperSheetCatalog.Step4, StringComparison.Ordinal))
                {
                    _continuationPrototypeStyles.Add(
                        name,
                        CaptureStep4ContinuationPrototypeStyles(
                            part,
                            _cancellationToken));
                }
                _templateParts.Add(name, part);
                Func<uint, uint, uint?, uint, uint>? mergeCellStyle = name is
                    WorkpaperSheetCatalog.Step2
                    or WorkpaperSheetCatalog.Step3
                    or WorkpaperSheetCatalog.Step4
                    ? (_, column, templateStyle, sourceStyle) =>
                    {
                        if (templateStyle is null
                            && string.Equals(
                                name,
                                WorkpaperSheetCatalog.Step4,
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
                return SheetWriter.OverlayTemplate(
                    name,
                    part,
                    DirectOverlayPlan(
                        name,
                        _useFinalStep41Schema,
                        mapCellStyle,
                        mergeCellStyle),
                    _styles,
                    mapCellStyle,
                    _cancellationToken);
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
            var continuationStyleMap = CreateContinuationCellStyleMapper(
                prototype.SheetName,
                mapCellStyle);
            return SheetWriter.CreateContinuation(
                name,
                continuationPart,
                _styles,
                continuationStyleMap,
                _cancellationToken,
                prototypePart.Parts
                    .SingleOrDefault(relationship =>
                        relationship.OpenXmlPart is SpreadsheetPrinterSettingsPart)
                    .RelationshipId);
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
                    WorkpaperSheetCatalog.Step4,
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
            CaptureStep4ContinuationPrototypeStyles(
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
            foreach (var sheetName in WorkpaperDirectTemplateSheets)
            {
                if (_openedNames.Contains(sheetName))
                {
                    continue;
                }
                _editor.RemoveWorksheet(sheetName, _cancellationToken);
                _topologyChanged = true;
            }

            if (!_openedNames.Contains(WorkpaperSheetCatalog.Intro)
                || !_openedNames.Contains(WorkpaperSheetCatalog.Step5))
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
                    _localDefinedNameOwners,
                    _cancellationToken);
            }
        }
    }

    private static IReadOnlyList<LocalDefinedNameOwner> CaptureLocalDefinedNameOwners(
        WorkbookPart workbookPart)
    {
        var sheets = workbookPart.Workbook.Sheets!.Elements<Sheet>().ToArray();
        return workbookPart.Workbook.DefinedNames?
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

    private static void AlignDirectTemplateSheets(
        WorkbookPart workbookPart,
        Sheets sheets,
        IReadOnlyList<string> desiredNames,
        IReadOnlyList<LocalDefinedNameOwner> localDefinedNameOwners,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        var definedNames = workbookPart.Workbook.DefinedNames?
            .Elements<DefinedName>()
            .ToArray()
            ?? [];
        foreach (var localName in localDefinedNameOwners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (localName.Ordinal >= definedNames.Length)
            {
                throw new InvalidDataException(
                    "WorkingPaper 範本 defined names 在直接 SAX 寫出期間發生未授權變更。");
            }

            var definedName = definedNames[localName.Ordinal];
            if (indexes.TryGetValue(localName.Owner, out var index))
            {
                definedName.LocalSheetId = index;
            }
            else
            {
                definedName.Remove();
            }
        }

        workbookPart.Workbook.Save();
    }

    private static DirectTemplateWorksheetOverlayPlan DirectOverlayPlan(
        string sheetName,
        bool useFinalStep41Schema,
        Func<uint, uint, uint, uint>? mapCellStyle,
        Func<uint, uint, uint?, uint, uint>? mergeCellStyle) =>
        (sheetName switch
        {
            WorkpaperSheetCatalog.Cover => OverlayOnly(
                mapCellStyle,
                (1U, 1U), (2U, 1U), (3U, 1U), (6U, 1U)),
            WorkpaperSheetCatalog.Step1 => RowsAndCells(
                row => row >= 20,
                (row, column) => row >= 20 && column is >= 1 and <= 6,
                [(1U, 1U), (2U, 1U), (3U, 1U), (7U, 1U), (15U, 2U), (17U, 2U)],
                WorkpaperSourceProtection),
            // legacy 版面：B14 說明、第 16 列五欄表頭、第 17 列起明細（idea-tool.bas:10468-10483）。
            WorkpaperSheetCatalog.Step11 => RowsAndCells(
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
                WorkpaperSourceProtection),
            WorkpaperSheetCatalog.Step12 => RowsAndCells(
                row => row >= 12,
                (row, column) => row >= 12 && column is >= 1 and <= 8,
                [(1U, 1U), (2U, 1U), (3U, 1U), (7U, 1U)],
                WorkpaperSourceProtection),
            WorkpaperSheetCatalog.Step13 => RowsAndCells(
                row => row >= 17,
                (row, column) => row >= 17 && column is >= 1 and <= 7,
                [(1U, 1U), (2U, 1U), (3U, 1U), (7U, 1U), (12U, 2U)],
                WorkpaperSourceProtection),
            WorkpaperSheetCatalog.Step2 => RowsAndCells(
                row => row is 49 or 51 || row is >= 53 and <= 111,
                (row, column) =>
                    row == 49 && column >= 21
                    || row == 51 && column >= 21
                    || row is >= 53 and <= 111 && column >= 1,
                [(1U, 1U), (2U, 1U), (51U, 12U)],
                WorkpaperSourceProtection),
            WorkpaperSheetCatalog.Step3 => RowsAndCells(
                row => row >= 19,
                (row, column) => row >= 19 && column is >= 2 and <= 5,
                [(1U, 1U), (2U, 1U), (7U, 2U), (9U, 2U)],
                sourcePostElements: null),
            WorkpaperSheetCatalog.Step4 => RowsAndCells(
                row => row >= 13,
                (row, column) => row >= 13
                    && (column is >= 1 and <= 15
                        || row > 1000 && column is >= 16 and <= 21),
                [(1U, 1U), (2U, 1U), (12U, 1U)],
                WorkpaperSourceProtectionAndValidation),
            WorkpaperSheetCatalog.Step41 when useFinalStep41Schema =>
                new DirectTemplateWorksheetOverlayPlan(
                    IsDynamicRow: row => row is 1 or 2 || row >= 5,
                    IsDynamicCell: (row, column) =>
                        row == 1 && column == 1
                        || row == 2 && column == 1
                        || row >= 5,
                    UseSourcePostSheetDataElements: WorkpaperSourceProtection,
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
            WorkpaperSheetCatalog.Step41 => RowsAndCells(
                row => row >= 6,
                (row, column) => row >= 6
                    && (column is >= 1 and <= 13
                        || column is >= 17 and <= 26
                        || row > 503 && column is >= 14 and <= 16),
                [(1U, 1U), (2U, 1U)],
                WorkpaperSourceProtection,
                mergeSourceColumnWidths: false),
            WorkpaperSheetCatalog.FieldInfo => ReferenceData(maximumDynamicColumn: 8),
            WorkpaperSheetCatalog.CalendarInfo => ReferenceData(maximumDynamicColumn: 3),
            WorkpaperSheetCatalog.AccountMapping => ReferenceData(
                maximumDynamicColumn: 3,
                WorkpaperSourceProtection),
            _ => throw new InvalidDataException(
                $"WorkingPaper worksheet '{sheetName}' 沒有直接 SAX overlay policy。")
        }) with
        {
            MapTemplateCellStyle = mapCellStyle,
            MergeCellStyle = mergeCellStyle
        };

    private static void RemoveCoverEmbeddedDocument(
        WorksheetPart worksheetPart,
        CancellationToken cancellationToken)
    {
        const string spreadsheetNamespace =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string relationshipNamespace =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        cancellationToken.ThrowIfCancellationRequested();

        var relationshipIds = new HashSet<string>(StringComparer.Ordinal);
        var removedElement = false;
        using var rewritten = CreateTemporaryCoverWorksheetStream();
        using (var reader = OpenXmlReader.Create(worksheetPart))
        using (var writer = OpenXmlWriter.Create(rewritten))
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

        if (!removedElement)
        {
            return;
        }

        rewritten.Position = 0;
        worksheetPart.FeedData(rewritten);

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

    private static FileStream CreateTemporaryCoverWorksheetStream()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $".jet-workpaper-cover-{Guid.NewGuid():N}.tmp");
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan | FileOptions.DeleteOnClose);
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
