using System.Globalization;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class LegacyReportWriter
{
    private const int InfReportSampleSize = InfSampling.DefaultSampleSize;
    private const string InfMainSheet = "INF Testing 可靠性測試";
    private const string InfAllFieldsSheet = "可靠性樣本_所有欄位";

    // INF 固定範本的 0 號樣式是新細明體 12 點、垂直置中、沒有框線與底色、不是粗體，鎖定設定用 Excel 的預設值。
    // 「可靠性樣本_所有欄位」的表頭、資料與欄樣式都以它為底；來源欄位需要數字格式時，
    // 只在樣式表最後面追加一個只差數字格式的新樣式。
    private const uint InfAllFieldsBaseStyle = 0;

    // 舊版 JET 只在配對後資料表的欄位定義是文字型態，而且那個欄位確實對應到 INF 主表的某一欄時，
    // 才替那一欄的樣本列套文字格式。
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

    Task<ExportStats> IInfReportWriter.WriteAsync(
        Stream output,
        InfReportContext context,
        ReportWorkbookMetadata workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress) =>
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
        ReportWorkbookMetadata workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
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
        IReadOnlyList<FieldInfo> targetDefinitions =
            _fieldInfoFactsPort is null
                ? []
                : await _fieldInfoFactsPort.ReadAsync(
                    context.Project.ProjectId,
                    DatasetKind.Gl,
                    FieldInfoScope.Target,
                    cancellationToken).ConfigureAwait(false);
        IReadOnlyList<FieldInfo> sourceDefinitions =
            _fieldInfoFactsPort is null
                ? []
                : await _fieldInfoFactsPort.ReadAsync(
                    context.Project.ProjectId,
                    DatasetKind.Gl,
                    FieldInfoScope.Source,
                    cancellationToken).ConfigureAwait(false);
        var textColumns = InfTextColumns(targetDefinitions, glMapping);
        var templateSamples = BuildInfTemplateSamples(samples, raw, glMapping);
        var rdeValues = await ReadReportRdeValuesAsync(
            context.Project.ProjectId,
            sampleEntryIds,
            workbookMetadata.GlMapping.GlOptions?.RdeFields ?? customFields,
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
        // 先依穩定鍵讀完實際樣本，再照報告的順序排。
        var samples = new List<InfSampleRow>(InfReportSampleSize);
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await infSamples.GetPageAsync(
                context.Project.ProjectId,
                context.ResultId,
                context.Project.MoneyScale,
                new PageRequest(cursor, InfReportSampleSize),
                cancellationToken).ConfigureAwait(false);
            samples.AddRange(page.Rows);
            cursor = page.NextCursor;
        } while (cursor is not null);

        var ordered = ResultPageSorting.InfSampleReportOrder(samples);
        return ordered;
    }

    private static IReadOnlyList<SheetStat> FillInfTemplate(
        DirectTemplateWorkbookEditor editor,
        InfReportContext context,
        IReadOnlyList<string> columns,
        IReadOnlyList<InfSampleRow> samples,
        IReadOnlyList<InfTemplateSample> templateSamples,
        IReadOnlySet<uint> textColumns,
        IReadOnlyList<FieldInfo> sourceDefinitions,
        IReadOnlyDictionary<long, string> raw,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, string?>> rdeValues,
        ReportWorkbookMetadata workbookMetadata,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var stats = new List<SheetStat>(2);
        // 舊版只在配對了傳票核准人員時把 L51 改成「傳票核准人員」；
        // 沒配對時保留範本原字。
        var approverMapped = workbookMetadata.GlMapping.Mapping.TryGetValue(
                GlMappingKeys.ApprovedBy,
                out var approverSource)
            && !string.IsNullOrWhiteSpace(approverSource);
        editor.RewriteWorksheetPart(
            InfMainSheet,
            (part, ct) => FillInfMainWorksheet(
                part,
                editor.StylePatcher,
                context,
                templateSamples,
                textColumns,
                approverMapped,
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
                    workbookMetadata.GlMapping.GlOptions?.RdeFields,
                    rdeValues,
                    ct);
                AddStat(stats, stat, progress);
            },
            cancellationToken);
        ReportWorkbookMetadataWorksheet.Append(editor.Document, workbookMetadata, cancellationToken);
        return stats;
    }

    private static IReadOnlyCollection<string> InfMainMutableCells(int rowCount, bool approverMapped)
    {
        var result = new List<string>(3 + 11 * rowCount)
        {
            "A1",
            "A2"
        };
        if (approverMapped)
        {
            result.Add("L51");
        }
        for (var row = 53; row <= 52 + rowCount; row++)
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
        bool approverMapped,
        CancellationToken cancellationToken)
    {
        cells.SetInlineString("A1", $"公司名稱 [{context.Project.CompanyName}]");
        cells.SetInlineString(
            "A2",
            $"財務報表期間 [{context.Project.PeriodStart} ~ {context.Project.PeriodEnd}]");
        if (approverMapped)
        {
            cells.SetInlineString("L51", "傳票核准人員");
        }

        for (var index = 0; index < Math.Max(InfReportSampleSize, samples.Count); index++)
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
                sample.VoucherNumber,
                CharacterFormat(2));
            cells.SetInlineString(
                ReportSheetWriter.Reference(3, row),
                sample.AccountNumber,
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
                sample.GlDate,
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
        bool approverMapped,
        CancellationToken cancellationToken)
    {
        var worksheet = part.RequiredWorksheet();
        var rowCount = Math.Max(InfReportSampleSize, samples.Count);
        var sheetData = worksheet.GetFirstChild<SheetData>()!;
        var prototype = sheetData.Elements<Row>().Single(row => row.RowIndex?.Value == 111);
        for (var rowIndex = 112U; rowIndex <= 52U + rowCount; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = (Row)prototype.CloneNode(true);
            row.RowIndex = rowIndex;
            foreach (var cell in row.Elements<Cell>())
            {
                var column = new string(cell.CellReference!.Value!.TakeWhile(char.IsLetter).ToArray());
                cell.CellReference = column + rowIndex.ToString(CultureInfo.InvariantCulture);
                cell.RemoveAllChildren();
                cell.DataType = CellValues.InlineString;
                if (column == "A") cell.InlineString = ExcelText.InlineString($"{rowIndex - 52}.");
            }
            sheetData.Append(row);
        }
        foreach (var validation in worksheet.Descendants<DataValidation>())
        {
            if (validation.SequenceOfReferences is { InnerText: { } sqref } references)
                references.InnerText = sqref.Replace("111", (52 + rowCount).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
        if (worksheet.GetFirstChild<SheetDimension>() is { } dimension)
            dimension.Reference = $"A1:T{52 + rowCount}";
        // 範本裡 F51 是不換行的表頭樣式。K 欄的表頭改用 F51 的樣式，K 欄每一列的摘要改用同一列 L 欄的不換行樣式，
        // 這樣欄寬照量出來的寬度決定，Excel 不會把文字換行。
        // 這一步要在套用舊版 JET 的文字格式之前做：之後才複製樣式的話，會把已經套上的 "@" 格式蓋掉。
        SetExactCellStyleFromPrototype(worksheet, "K51", "F51");
        for (var row = 53U; row <= 52U + rowCount; row++)
        {
            SetExactCellStyleFromPrototype(
                worksheet,
                ReportSheetWriter.Reference(11, row),
                ReportSheetWriter.Reference(12, row));
        }

        var cells = new DirectTemplateCellEditor(
            worksheet,
            InfMainMutableCells(rowCount, approverMapped),
            stylePatcher);
        FillInfMainCells(cells, context, samples, textColumns, approverMapped, cancellationToken);
        cells.EnsureAllAllowedCellsWereEdited();

        var displayWidths = MeasureInfMainColumns(
            context,
            samples,
            cancellationToken);
        for (var index = 0; index < displayWidths.Count; index++)
        {
            EnsureMinimumColumnWidth(
                worksheet,
                checked((uint)index + 2),
                displayWidths[index]);
        }

        // 固定範本的 T51 顯示時會被切掉，所以 T 欄改用 M 欄的寬度。
        SetExactColumnWidth(worksheet, 20, 22.453125D);
        ExcelText.SaveWorksheet(part);
    }

    private static IReadOnlySet<uint> InfTextColumns(
        IReadOnlyList<FieldInfo> targetDefinitions,
        CommittedMapping? glMapping)
    {
        var result = new HashSet<uint>();
        string? directionField = null;
        glMapping?.Mapping.TryGetValue(GlMappingKeys.DrCr, out directionField);
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
                sample.VoucherNumber,
                sample.AccountNumber,
                sample.AccountName,
                templateSample.Direction,
                displayedAmount,
                sample.GlDate,
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
        var values = new InfSampleDisplayValues(glMapping);
        var result = new List<InfTemplateSample>(samples.Count);
        foreach (var sample in samples)
        {
            using var document = LocalEngineErrors.ParseStoredObject(raw[sample.EntryId]);
            var (direction, sourceModule) = values.For(document.RootElement, sample);
            result.Add(new InfTemplateSample(sample, direction, sourceModule));
        }
        return result;
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
        IReadOnlyList<FieldInfo> sourceDefinitions,
        IReadOnlyDictionary<long, string> raw,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        IReadOnlyList<GlRdeFieldMetadata>? registryFields,
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, string?>> rdeValues,
        CancellationToken cancellationToken)
    {
        // 攸關資料元素的表頭照舊版加 _JE。
        var outputColumns = columns.Concat(GlRdeOutputNames.For(customFields, registryFields)).ToArray();
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
                // INF 範本內文是 12 點，列高照字級的 1.5 倍。
                DefaultRowHeight: FormalWorkbookTypography.RowHeight(12D)));
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
        IReadOnlyList<FieldInfo> sourceDefinitions,
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

    private static string? InfAllFieldsNumberFormat(FieldInfo? definition) =>
        definition?.Kind switch
        {
            LegacyFieldKind.Text => "@",
            LegacyFieldKind.Number when definition.DecimalPlaces is >= 0
                                               and <= WorkingPaperStyles.MaximumDecimalPlaces =>
                "0" + (definition.DecimalPlaces.Value == 0
                    ? string.Empty
                    : "." + new string('0', definition.DecimalPlaces.Value)),
            LegacyFieldKind.Date => "mm-dd-yy",
            LegacyFieldKind.Time => "h:mm:ss",
            _ => null
        };
}
