using System.Globalization;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class LegacyReportWriter
{
    private const int InfReportSampleSize = 59;
    private const string InfMainSheet = "INF Testing 可靠性測試";
    private const string InfAllFieldsSheet = "可靠性樣本_所有欄位";

    // INF 固定範本 style 0 是新細明體 12、垂直置中、無框線與填色、
    // 非粗體且使用 Excel 預設 locked。「所有欄位」只由它 append-only
    // 衍生來源欄位 number-format variant，表頭、資料與欄樣式保持一致。
    private const uint InfAllFieldsBaseStyle = 0;

    // idea-script.bas 4828–4854：只有 legacy target TableDef 中的 character
    // 欄位，且確實對映到 INF 主表欄位時，實際 sample rows 才套文字格式。
    private static readonly WorkbookStylePatch InfCharacterNumberFormat =
        new(NumberFormatCode: "@");

    private static readonly IReadOnlyDictionary<string, uint> InfMainColumnsByTargetField =
        new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["傳票號碼_JE"] = 2,
            ["會計科目編號_JE"] = 3,
            ["會計科目名稱_JE"] = 4,
            ["傳票金額_JE"] = 6,
            ["總帳日期_JE"] = 7,
            ["傳票核准日_JE"] = 8,
            ["傳票建立人員_JE"] = 9,
            ["分錄來源模組_JE"] = 10,
            ["傳票摘要_JE"] = 11,
            ["傳票核准人員_JE"] = 12
        };

    public async Task<ExportStats> WriteAsync(
        Stream output,
        InfReportContext context,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null)
        => await WriteInfCoreAsync(
            output,
            context,
            workbookMetadata: null,
            customFields: [],
            cancellationToken,
            progress).ConfigureAwait(false);

    Task<ExportStats> IFormalInfReportWriter.WriteFormalAsync(
        Stream output,
        InfReportContext context,
        ReportWorkbookMetadata workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress) =>
        WriteInfCoreAsync(
            output,
            context,
            workbookMetadata,
            ReportWorkbookMetadataInvariant.ValidateCustomFields(workbookMetadata, customFields),
            cancellationToken,
            progress);

    private async Task<ExportStats> WriteInfCoreAsync(
        Stream output,
        InfReportContext context,
        ReportWorkbookMetadata? workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var samples = await ReadInfSamplesAsync(context, cancellationToken).ConfigureAwait(false);
        var batch = await imports.GetLatestBatchAsync(
            context.Project.ProjectId,
            DatasetKind.Gl,
            cancellationToken).ConfigureAwait(false);
        var columns = batch?.Columns ?? Array.Empty<string>();
        if (columns.Count == 0 || samples.Any(row => row.EntryId <= 0))
        {
            throw MissingRawGlSource();
        }

        var sampleEntryIds = samples.Select(row => row.EntryId).ToArray();
        var raw = await rawRows.FetchJsonByEntryIdsAsync(
            context.Project.ProjectId,
            sampleEntryIds,
            cancellationToken).ConfigureAwait(false);
        EnsureRawRowsComplete(sampleEntryIds, raw);
        var glMapping = await mappings.FindAsync(
            context.Project.ProjectId,
            DatasetKind.Gl,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LegacyFieldDefinition> targetDefinitions =
            _fieldDefinitionFactsPort is null
                ? []
                : await _fieldDefinitionFactsPort.ReadAsync(
                    context.Project.ProjectId,
                    DatasetKind.Gl,
                    LegacyFieldDefinitionScope.Target,
                    cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LegacyFieldDefinition> sourceDefinitions =
            _fieldDefinitionFactsPort is null
                ? []
                : await _fieldDefinitionFactsPort.ReadAsync(
                    context.Project.ProjectId,
                    DatasetKind.Gl,
                    LegacyFieldDefinitionScope.Source,
                    cancellationToken).ConfigureAwait(false);
        var textColumns = InfTextColumns(targetDefinitions, glMapping);
        var templateSamples = BuildInfTemplateSamples(samples, raw, glMapping);
        var rdeValues = await ReadReportRdeValuesAsync(
            context.Project.ProjectId,
            sampleEntryIds,
            workbookMetadata?.GlMapping.GlOptions?.RdeFields ?? customFields,
            customFields,
            context.Project.MoneyScale,
            cancellationToken).ConfigureAwait(false);

        return await ReportTemplatePackage.FillDirectAsync(
            _templates,
            ReportTemplateCatalog.Inf,
            output,
            [InfMainSheet, InfAllFieldsSheet],
            (editor, ct) => FillInfTemplate(
                editor,
                context,
                columns,
                samples,
                templateSamples,
                textColumns,
                sourceDefinitions,
                raw,
                customFields,
                rdeValues,
                workbookMetadata,
                ct,
                progress),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<InfSampleRow>> ReadInfSamplesAsync(
        InfReportContext context,
        CancellationToken cancellationToken)
    {
        var samples = new List<InfSampleRow>(InfReportSampleSize);
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await infSamples.GetPageAsync(
                context.Project.ProjectId,
                context.RunId,
                context.Project.MoneyScale,
                new PageRequest(cursor, InfReportSampleSize),
                cancellationToken).ConfigureAwait(false);
            samples.AddRange(page.Rows);
            cursor = page.NextCursor;
        } while (cursor is not null && samples.Count < InfReportSampleSize);

        if (samples.Count > InfReportSampleSize)
        {
            samples.RemoveRange(InfReportSampleSize, samples.Count - InfReportSampleSize);
        }
        return samples;
    }

    private static IReadOnlyList<SheetStat> FillInfTemplate(
        DirectTemplateWorkbookEditor editor,
        InfReportContext context,
        IReadOnlyList<string> columns,
        IReadOnlyList<InfSampleRow> samples,
        IReadOnlyList<InfTemplateSample> templateSamples,
        IReadOnlySet<uint> textColumns,
        IReadOnlyList<LegacyFieldDefinition> sourceDefinitions,
        IReadOnlyDictionary<long, string> raw,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, string?>> rdeValues,
        ReportWorkbookMetadata? workbookMetadata,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var stats = new List<SheetStat>(2);
        editor.RewriteWorksheetPart(
            InfMainSheet,
            (part, ct) => FillInfMainWorksheet(
                part,
                editor.StylePatcher,
                context,
                templateSamples,
                textColumns,
                ct),
            cancellationToken);
        AddStat(stats, new SheetStat(InfMainSheet, samples.Count), progress);

        editor.RewriteEmptyWorksheet(
            InfAllFieldsSheet,
            (part, ct) =>
            {
                var stat = EmitInfAllFields(
                    part,
                    editor.StylePatcher,
                    columns,
                    samples,
                    sourceDefinitions,
                    raw,
                    customFields,
                    rdeValues,
                    ct);
                AddStat(stats, stat, progress);
            },
            cancellationToken);
        if (workbookMetadata is not null)
        {
            ReportWorkbookMetadataWorksheet.Append(editor.Document, workbookMetadata, cancellationToken);
        }
        return stats;
    }

    private static IReadOnlyCollection<string> InfMainMutableCells()
    {
        var result = new List<string>(3 + 11 * InfReportSampleSize)
        {
            "A1",
            "A2",
            "L51"
        };
        for (var row = 53; row <= 52 + InfReportSampleSize; row++)
        {
            for (var column = 2U; column <= 12; column++)
            {
                result.Add(ReportSheetWriter.Reference(column, (uint)row));
            }
        }
        return result;
    }

    private static void FillInfMainCells(
        DirectTemplateCellEditor cells,
        InfReportContext context,
        IReadOnlyList<InfTemplateSample> samples,
        IReadOnlySet<uint> textColumns,
        CancellationToken cancellationToken)
    {
        cells.SetInlineString("A1", $"公司名稱 [{context.Project.CompanyName}]");
        cells.SetInlineString(
            "A2",
            $"財務報表期間 [{context.Project.PeriodStart} ~ {context.Project.PeriodEnd}]");
        cells.SetInlineString("L51", "傳票核准人員");

        for (var index = 0; index < InfReportSampleSize; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = (uint)index + 53;
            if (index >= samples.Count)
            {
                for (var column = 2U; column <= 12; column++)
                {
                    cells.Clear(ReportSheetWriter.Reference(column, row));
                }
                continue;
            }

            var templateSample = samples[index];
            var sample = templateSample.Row;
            WorkbookStylePatch? CharacterFormat(uint column) =>
                textColumns.Contains(column) ? InfCharacterNumberFormat : null;
            cells.SetInlineString(
                ReportSheetWriter.Reference(2, row),
                sample.DocumentNumber,
                CharacterFormat(2));
            cells.SetInlineString(
                ReportSheetWriter.Reference(3, row),
                sample.AccountCode,
                CharacterFormat(3));
            cells.SetInlineString(
                ReportSheetWriter.Reference(4, row),
                sample.AccountName,
                CharacterFormat(4));
            cells.SetInlineString(
                ReportSheetWriter.Reference(5, row),
                templateSample.Direction,
                CharacterFormat(5));
            cells.SetNumber(
                ReportSheetWriter.Reference(6, row),
                Display(sample.DebitScaled - sample.CreditScaled, context.Project.MoneyScale),
                CharacterFormat(6));
            cells.SetInlineString(
                ReportSheetWriter.Reference(7, row),
                sample.PostDate,
                CharacterFormat(7));
            cells.SetInlineString(
                ReportSheetWriter.Reference(8, row),
                sample.ApprovalDate,
                CharacterFormat(8));
            cells.SetInlineString(
                ReportSheetWriter.Reference(9, row),
                sample.CreatedBy,
                CharacterFormat(9));
            cells.SetInlineString(
                ReportSheetWriter.Reference(10, row),
                templateSample.SourceModule,
                CharacterFormat(10));
            cells.SetInlineString(
                ReportSheetWriter.Reference(11, row),
                sample.Description,
                CharacterFormat(11));
            cells.SetInlineString(
                ReportSheetWriter.Reference(12, row),
                sample.ApprovedBy,
                CharacterFormat(12));
        }
    }

    private static void FillInfMainWorksheet(
        WorksheetPart part,
        WorkbookStylePatcher stylePatcher,
        InfReportContext context,
        IReadOnlyList<InfTemplateSample> samples,
        IReadOnlySet<uint> textColumns,
        CancellationToken cancellationToken)
    {
        // F51 is the template's no-wrap header prototype. K's header and all 59
        // description cells reuse corresponding no-wrap template styles so the
        // measured width is authoritative instead of Excel wrapping the text.
        // Do this before applying the legacy character number format: copying the
        // prototype afterwards would silently replace the mapped "@" component.
        SetExactCellStyleFromPrototype(part.Worksheet, "K51", "F51");
        for (var row = 53U; row <= 52U + InfReportSampleSize; row++)
        {
            SetExactCellStyleFromPrototype(
                part.Worksheet,
                ReportSheetWriter.Reference(11, row),
                ReportSheetWriter.Reference(12, row));
        }

        var cells = new DirectTemplateCellEditor(
            part.Worksheet,
            InfMainMutableCells(),
            stylePatcher);
        FillInfMainCells(cells, context, samples, textColumns, cancellationToken);
        cells.EnsureAllAllowedCellsWereEdited();

        var displayWidths = MeasureInfMainColumns(
            context,
            samples,
            cancellationToken);
        for (var index = 0; index < displayWidths.Count; index++)
        {
            EnsureMinimumColumnWidth(
                part.Worksheet,
                checked((uint)index + 2),
                displayWidths[index]);
        }

        // The fixed template clips T51 when rendered; reuse the proven M width.
        SetExactColumnWidth(part.Worksheet, 20, 22.453125D);
        part.Worksheet.Save();
    }

    private static IReadOnlySet<uint> InfTextColumns(
        IReadOnlyList<LegacyFieldDefinition> targetDefinitions,
        CommittedMapping? glMapping)
    {
        var result = new HashSet<uint>();
        string? directionField = null;
        glMapping?.Mapping.TryGetValue(GlMappingKeys.DcField, out directionField);
        foreach (var definition in targetDefinitions)
        {
            if (definition.Kind != LegacyFieldKind.Text)
            {
                continue;
            }
            if (InfMainColumnsByTargetField.TryGetValue(
                    definition.FieldName,
                    out var column))
            {
                result.Add(column);
            }
            else if (!string.IsNullOrWhiteSpace(directionField)
                     && string.Equals(
                         definition.FieldName,
                         directionField,
                         StringComparison.Ordinal))
            {
                result.Add(5);
            }
        }
        return result;
    }

    private static IReadOnlyList<double> MeasureInfMainColumns(
        InfReportContext context,
        IReadOnlyList<InfTemplateSample> samples,
        CancellationToken cancellationToken)
    {
        var widths = new ExcelDisplayWidthTracker(
        [
            "傳票號碼",
            "會計科目編號",
            "會計科目名稱",
            "借貸方向",
            "傳票金額",
            "總帳日期",
            "傳票核准日",
            "分錄編製人員",
            "分錄來源模組",
            "傳票摘要",
            "傳票核准人員"
        ]);
        foreach (var templateSample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = templateSample.Row;
            var displayedAmount = Display(
                    sample.DebitScaled - sample.CreditScaled,
                    context.Project.MoneyScale)
                .ToString("0.00", CultureInfo.InvariantCulture);
            widths.Observe(
            [
                sample.DocumentNumber,
                sample.AccountCode,
                sample.AccountName,
                templateSample.Direction,
                displayedAmount,
                sample.PostDate,
                sample.ApprovalDate,
                sample.CreatedBy,
                templateSample.SourceModule,
                sample.Description,
                sample.ApprovedBy
            ]);
        }
        return widths.Widths;
    }

    private static void SetExactCellStyleFromPrototype(
        Worksheet worksheet,
        string targetReference,
        string prototypeReference)
    {
        var cells = worksheet.Descendants<Cell>()
            .Where(cell => cell.CellReference?.Value is not null)
            .ToDictionary(
                cell => cell.CellReference!.Value!,
                StringComparer.OrdinalIgnoreCase);
        if (!cells.TryGetValue(targetReference, out var target)
            || !cells.TryGetValue(prototypeReference, out var prototype)
            || prototype.StyleIndex is null)
        {
            throw new InvalidDataException(
                $"INF template requires cells {targetReference} and {prototypeReference} with a style prototype.");
        }
        target.StyleIndex = prototype.StyleIndex.Value;
    }

    private static void SetExactColumnWidth(Worksheet worksheet, uint columnIndex, double width)
    {
        var columns = worksheet.GetFirstChild<Columns>()
            ?? throw new InvalidDataException("INF template worksheet has no column definitions.");
        var matching = columns.Elements<Column>()
            .Where(column => column.Min?.Value <= columnIndex && column.Max?.Value >= columnIndex)
            .ToArray();
        if (matching.Length != 1)
        {
            throw new InvalidDataException(
                $"INF template column {columnIndex} must have exactly one width prototype.");
        }

        var target = matching[0];
        var originalMin = target.Min!.Value;
        var originalMax = target.Max!.Value;
        if (originalMin < columnIndex)
        {
            var before = (Column)target.CloneNode(true);
            before.Min = originalMin;
            before.Max = columnIndex - 1;
            target.InsertBeforeSelf(before);
        }
        if (originalMax > columnIndex)
        {
            var after = (Column)target.CloneNode(true);
            after.Min = columnIndex + 1;
            after.Max = originalMax;
            target.InsertAfterSelf(after);
        }

        target.Min = columnIndex;
        target.Max = columnIndex;
        target.Width = width;
        target.CustomWidth = true;
    }

    private static void EnsureMinimumColumnWidth(
        Worksheet worksheet,
        uint columnIndex,
        double minimumWidth)
    {
        var columns = worksheet.GetFirstChild<Columns>()
            ?? throw new InvalidDataException("INF template worksheet has no column definitions.");
        var matching = columns.Elements<Column>()
            .Where(column => column.Min?.Value <= columnIndex && column.Max?.Value >= columnIndex)
            .ToArray();
        if (matching.Length != 1)
        {
            throw new InvalidDataException(
                $"INF template column {columnIndex} must have exactly one width prototype.");
        }

        var currentWidth = matching[0].Width?.Value ?? 0D;
        if (currentWidth < minimumWidth)
        {
            SetExactColumnWidth(worksheet, columnIndex, minimumWidth);
        }
    }

    private static IReadOnlyList<InfTemplateSample> BuildInfTemplateSamples(
        IReadOnlyList<InfSampleRow> samples,
        IReadOnlyDictionary<long, string> raw,
        CommittedMapping? glMapping)
    {
        string? directionColumn = null;
        string? sourceModuleColumn = null;
        if (glMapping is not null)
        {
            glMapping.Mapping.TryGetValue(GlMappingKeys.DcField, out directionColumn);
            glMapping.Mapping.TryGetValue(GlMappingKeys.JeSource, out sourceModuleColumn);
        }

        var result = new List<InfTemplateSample>(samples.Count);
        foreach (var sample in samples)
        {
            using var document = JsonDocument.Parse(raw[sample.EntryId]);
            result.Add(new InfTemplateSample(
                sample,
                ReadRawValue(document.RootElement, directionColumn),
                ReadRawValue(document.RootElement, sourceModuleColumn)));
        }
        return result;
    }

    private static string? ReadRawValue(JsonElement row, string? sourceColumn)
    {
        if (string.IsNullOrWhiteSpace(sourceColumn)
            || !row.TryGetProperty(sourceColumn, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }

    private sealed record InfTemplateSample(
        InfSampleRow Row,
        string? Direction,
        string? SourceModule);

    private static SheetStat EmitInfAllFields(
        WorksheetPart part,
        WorkbookStylePatcher stylePatcher,
        IReadOnlyList<string> columns,
        IReadOnlyList<InfSampleRow> samples,
        IReadOnlyList<LegacyFieldDefinition> sourceDefinitions,
        IReadOnlyDictionary<long, string> raw,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, string?>> rdeValues,
        CancellationToken cancellationToken)
    {
        var outputColumns = columns.Concat(customFields.Select(field => field.Label)).ToArray();
        var styles = InfAllFieldsStyles(columns, sourceDefinitions, stylePatcher)
            .Concat(Enumerable.Repeat(InfAllFieldsBaseStyle, customFields.Count))
            .ToArray();
        var widths = new ExcelDisplayWidthTracker(outputColumns);
        var projectedRows = new List<IReadOnlyList<string?>>(samples.Count);
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawJson = raw[sample.EntryId];
            var values = ParseRawValues(rawJson, columns)
                .Concat(customFields.Select(field => rdeValues[sample.EntryId][field.FieldId]))
                .ToArray();
            widths.Observe(values);
            projectedRows.Add(values);
        }

        using var sheet = new ReportSheetWriter(
            InfAllFieldsSheet,
            part,
            widths.Widths.Select((width, index) => new ReportColumn(
                    checked((uint)index + 1),
                    checked((uint)index + 1),
                    width,
                    BestFit: true))
                .ToArray(),
            new ReportSheetOptions(
                PageMargins: StandardMargins,
                DefaultRowHeight: 17D));
        sheet.WriteFixedRow(
            1,
            outputColumns.Select((value, index) =>
                    sheet.TextCell(1, (uint)index + 1, value, styles[index]))
                .ToArray());

        uint row = 2;
        foreach (var values in projectedRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sheet.WriteDataRow(
                row,
                values.Select((value, index) =>
                        sheet.TextCell(row, (uint)index + 1, value, styles[index]))
                    .ToArray());
            row++;
        }
        return sheet.CloseAndSummarize(cancellationToken);
    }

    private static IReadOnlyList<uint> InfAllFieldsStyles(
        IReadOnlyList<string> columns,
        IReadOnlyList<LegacyFieldDefinition> sourceDefinitions,
        WorkbookStylePatcher stylePatcher)
    {
        var result = new uint[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            var ordinal = index + 1;
            var definition = sourceDefinitions.FirstOrDefault(item =>
                item.Ordinal == ordinal
                && string.Equals(item.FieldName, columns[index], StringComparison.Ordinal));
            var numberFormat = InfAllFieldsNumberFormat(definition);
            result[index] = numberFormat is null
                ? InfAllFieldsBaseStyle
                : stylePatcher.Patch(
                    InfAllFieldsBaseStyle,
                    new WorkbookStylePatch(NumberFormatCode: numberFormat));
        }
        return result;
    }

    private static string? InfAllFieldsNumberFormat(LegacyFieldDefinition? definition) =>
        definition?.Kind switch
        {
            LegacyFieldKind.Text => "@",
            LegacyFieldKind.Number when definition.DecimalPlaces is >= 0
                                               and <= WorkpaperStyles.MaximumDecimalPlaces =>
                "0" + (definition.DecimalPlaces.Value == 0
                    ? string.Empty
                    : "." + new string('0', definition.DecimalPlaces.Value)),
            LegacyFieldKind.Date => "mm-dd-yy",
            LegacyFieldKind.Time => "h:mm:ss",
            _ => null
        };
}
