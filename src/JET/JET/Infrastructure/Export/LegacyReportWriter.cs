using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// Validation／INF／Pre-screening／Criteria 四份報告的 OpenXML SAX writer。
/// 大型明細由既有規則 repository keyset 分頁，原始欄位以每頁 entry ids 回取，不把母體載入記憶體。
/// </summary>
public sealed partial class LegacyReportWriter(
    ICompletenessAccountPageRepository completenessAccounts,
    ICompletenessDiffPageRepository completenessDiffs,
    IUnbalancedGlEntryPageRepository unbalancedGlEntries,
    INullRecordsPageRepository nullRecords,
    IInfSamplePageRepository infSamples,
    IPrescreenPageRepository prescreenPages,
    IRawGlExportRepository rawRows,
    IImportRepository imports,
    IMappingStateStore mappings,
    ICreatorSummaryExportRepository creatorSummaries,
    IAccountUsageExportRepository accountUsage,
    ITagMatrixScenariosRepository tagMatrixCounts,
    ITagMatrixRowPageRepository? tagMatrixRows = null,
    IResultPageRdeValuesPort? resultPageRdeValues = null,
    ISourceQualityPageRepository? sourceQualityPages = null)
    : IValidationReportWriter, ITypedValidationReportWriter, IPlannedValidationReportWriter,
      IFormalPlannedValidationReportWriter,
      IInfReportWriter, IFormalInfReportWriter,
      IPrescreenReportWriter, ITypedPrescreenReportWriter,
      IPlannedPrescreenReportWriter, IFormalPlannedPrescreenReportWriter,
      ICriteriaSelectionReportWriter, ITypedCriteriaSelectionReportWriter,
      IFormalCriteriaSelectionReportWriter
{
    private const int DefaultProgressRowInterval = 10_000;
    private const uint ExcelMaxRow = ExcelWorksheetConstraints.MaxRows;
    // Legacy Step4 BAS 8889 / ISM 10256: only the criteria database's
    // EntireColumn.AutoFit is suppressed above one million exported rows.
    private const long LegacyCriteriaAutoFitMaximumRows = 1_000_000;
    private readonly uint _continuationRowLimit = ExcelMaxRow;
    private readonly int _progressRowInterval = DefaultProgressRowInterval;
    private readonly ReportTemplateCatalog _templates = ReportTemplateCatalog.Default;
    private readonly ILegacyFieldDefinitionFactsPort? _fieldDefinitionFactsPort;

    internal LegacyReportWriter(
        ICompletenessAccountPageRepository completenessAccounts,
        ICompletenessDiffPageRepository completenessDiffs,
        IUnbalancedGlEntryPageRepository unbalancedGlEntries,
        INullRecordsPageRepository nullRecords,
        IInfSamplePageRepository infSamples,
        IPrescreenPageRepository prescreenPages,
        IRawGlExportRepository rawRows,
        IImportRepository imports,
        IMappingStateStore mappings,
        ICreatorSummaryExportRepository creatorSummaries,
        IAccountUsageExportRepository accountUsage,
        ITagMatrixScenariosRepository tagMatrixCounts,
        LegacyReportWriterOptions options,
        int progressRowInterval = DefaultProgressRowInterval)
        : this(
            completenessAccounts,
            completenessDiffs,
            unbalancedGlEntries,
            nullRecords,
            infSamples,
            prescreenPages,
            rawRows,
            imports,
            mappings,
            creatorSummaries,
            accountUsage,
            tagMatrixCounts)
    {
        _continuationRowLimit = options.ValidateAndGetLimit();
        if (progressRowInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progressRowInterval));
        }
        _progressRowInterval = progressRowInterval;
    }

    internal LegacyReportWriter(
        ICompletenessAccountPageRepository completenessAccounts,
        ICompletenessDiffPageRepository completenessDiffs,
        IUnbalancedGlEntryPageRepository unbalancedGlEntries,
        INullRecordsPageRepository nullRecords,
        IInfSamplePageRepository infSamples,
        IPrescreenPageRepository prescreenPages,
        IRawGlExportRepository rawRows,
        IImportRepository imports,
        IMappingStateStore mappings,
        ICreatorSummaryExportRepository creatorSummaries,
        IAccountUsageExportRepository accountUsage,
        ITagMatrixScenariosRepository tagMatrixCounts,
        ITagMatrixRowPageRepository tagMatrixRows,
        LegacyReportWriterOptions options,
        int progressRowInterval = DefaultProgressRowInterval)
        : this(
            completenessAccounts,
            completenessDiffs,
            unbalancedGlEntries,
            nullRecords,
            infSamples,
            prescreenPages,
            rawRows,
            imports,
            mappings,
            creatorSummaries,
            accountUsage,
            tagMatrixCounts,
            tagMatrixRows)
    {
        _continuationRowLimit = options.ValidateAndGetLimit();
        if (progressRowInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progressRowInterval));
        }
        _progressRowInterval = progressRowInterval;
    }

    internal LegacyReportWriter(
        ICompletenessAccountPageRepository completenessAccounts,
        ICompletenessDiffPageRepository completenessDiffs,
        IUnbalancedGlEntryPageRepository unbalancedGlEntries,
        INullRecordsPageRepository nullRecords,
        IInfSamplePageRepository infSamples,
        IPrescreenPageRepository prescreenPages,
        IRawGlExportRepository rawRows,
        IImportRepository imports,
        IMappingStateStore mappings,
        ICreatorSummaryExportRepository creatorSummaries,
        IAccountUsageExportRepository accountUsage,
        ITagMatrixScenariosRepository tagMatrixCounts,
        ITagMatrixRowPageRepository tagMatrixRows,
        ILegacyFieldDefinitionFactsPort fieldDefinitionFactsPort,
        IResultPageRdeValuesPort? resultPageRdeValues = null,
        ISourceQualityPageRepository? sourceQualityPages = null)
        : this(
            completenessAccounts,
            completenessDiffs,
            unbalancedGlEntries,
            nullRecords,
            infSamples,
            prescreenPages,
            rawRows,
            imports,
            mappings,
            creatorSummaries,
            accountUsage,
            tagMatrixCounts,
            tagMatrixRows,
            resultPageRdeValues,
            sourceQualityPages)
    {
        ArgumentNullException.ThrowIfNull(fieldDefinitionFactsPort);
        _fieldDefinitionFactsPort = fieldDefinitionFactsPort;
    }

    internal LegacyReportWriter(
        ICompletenessAccountPageRepository completenessAccounts,
        ICompletenessDiffPageRepository completenessDiffs,
        IUnbalancedGlEntryPageRepository unbalancedGlEntries,
        INullRecordsPageRepository nullRecords,
        IInfSamplePageRepository infSamples,
        IPrescreenPageRepository prescreenPages,
        IRawGlExportRepository rawRows,
        IImportRepository imports,
        IMappingStateStore mappings,
        ICreatorSummaryExportRepository creatorSummaries,
        IAccountUsageExportRepository accountUsage,
        ITagMatrixScenariosRepository tagMatrixCounts,
        ITagMatrixRowPageRepository tagMatrixRows,
        ILegacyFieldDefinitionFactsPort fieldDefinitionFactsPort,
        LegacyReportWriterOptions options,
        int progressRowInterval = DefaultProgressRowInterval,
        IResultPageRdeValuesPort? resultPageRdeValues = null,
        ISourceQualityPageRepository? sourceQualityPages = null)
        : this(
            completenessAccounts,
            completenessDiffs,
            unbalancedGlEntries,
            nullRecords,
            infSamples,
            prescreenPages,
            rawRows,
            imports,
            mappings,
            creatorSummaries,
            accountUsage,
            tagMatrixCounts,
            tagMatrixRows,
            fieldDefinitionFactsPort,
            resultPageRdeValues,
            sourceQualityPages)
    {
        _continuationRowLimit = options.ValidateAndGetLimit();
        if (progressRowInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progressRowInterval));
        }
        _progressRowInterval = progressRowInterval;
    }

    private static readonly ReportPageMargins StandardMargins =
        new(0.7, 0.7, 0.75, 0.75, 0.3, 0.3);




    private static SpreadsheetDocument CreateWorkbook(Stream output, out Sheets sheets)
    {
        var document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = LegacyReportStyles.Build();
        stylesPart.Stylesheet.Save();
        sheets = workbookPart.Workbook.AppendChild(new Sheets());
        return document;
    }

    private static ReportSheetWriter OpenSheet(
        SpreadsheetDocument document,
        Sheets sheets,
        string name,
        IReadOnlyList<ReportColumn>? columns = null,
        ReportSheetOptions? options = null,
        Func<uint, uint>? mapStyle = null)
    {
        var part = document.WorkbookPart!.AddNewPart<WorksheetPart>();
        sheets.Append(new Sheet
        {
            Id = document.WorkbookPart.GetIdOfPart(part),
            SheetId = sheets.Elements<Sheet>()
                .Select(sheet => sheet.SheetId?.Value ?? 0U)
                .DefaultIfEmpty(0U)
                .Max() + 1U,
            Name = name
        });
        return new ReportSheetWriter(name, part, columns, options, mapStyle);
    }

    private static void AddStat(
        List<SheetStat> stats,
        SheetStat stat,
        Action<WorkpaperProgress>? progress)
    {
        stats.Add(stat);
        progress?.Invoke(new WorkpaperProgress(stat.SheetName, stats.Count, stat.RowsWritten));
    }

    private void ReportIntermediateRows(
        List<SheetStat> stats,
        string sheetName,
        long rowsWritten,
        Action<WorkpaperProgress>? progress)
    {
        if (progress is not null && rowsWritten > 0 && rowsWritten % _progressRowInterval == 0)
        {
            progress(new WorkpaperProgress(sheetName, stats.Count, rowsWritten));
        }
    }

    private static void ReportSheetStarted(
        List<SheetStat> stats,
        string sheetName,
        Action<WorkpaperProgress>? progress) =>
        progress?.Invoke(new WorkpaperProgress(sheetName, stats.Count, 0));

    private void ReportPreparationPulse(
        List<SheetStat> stats,
        string sheetName,
        long rowsInspected,
        Action<WorkpaperProgress>? progress)
    {
        if (progress is not null
            && rowsInspected > 0
            && rowsInspected % _progressRowInterval == 0)
        {
            progress(new WorkpaperProgress(sheetName, stats.Count, 0));
        }
    }

    private async Task EmitRawPagedSheetsAsync<T>(
        SpreadsheetDocument document,
        Sheets sheets,
        List<SheetStat> stats,
        string projectId,
        string baseName,
        Func<PageRequest, CancellationToken, Task<PageResult<T>>> fetchPage,
        Func<T, long> entryId,
        WorkbookStyleMap? styles,
        RawSheetAutoFitMode autoFitMode,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress,
        RawSheetAppearanceMode appearanceMode = RawSheetAppearanceMode.ExportDatabase,
        IReadOnlyList<GlRdeFieldMetadata>? customFields = null,
        IReadOnlyList<GlRdeFieldMetadata>? registryFields = null,
        int? customFieldMoneyScale = null)
    {
        var batch = await imports.GetLatestBatchAsync(projectId, DatasetKind.Gl, cancellationToken);
        var rawColumns = batch?.Columns ?? Array.Empty<string>();
        if (rawColumns.Count == 0)
        {
            throw MissingRawGlSource();
        }
        customFields ??= Array.Empty<GlRdeFieldMetadata>();
        registryFields ??= Array.Empty<GlRdeFieldMetadata>();
        var columns = rawColumns.Concat(customFields.Select(field => field.Label)).ToArray();

        var bodyStyles = appearanceMode == RawSheetAppearanceMode.ExportDatabase
            ? await ResolveExportDatabaseStylesAsync(
                    projectId,
                    rawColumns,
                    cancellationToken)
                .ConfigureAwait(false)
            : Enumerable.Repeat(LegacyReportStyles.RawBody, rawColumns.Count).ToArray();
        bodyStyles = bodyStyles
            .Concat(Enumerable.Repeat(LegacyReportStyles.RawBody, customFields.Count))
            .ToArray();
        var headerStyles = appearanceMode == RawSheetAppearanceMode.ExportDatabase
            ? bodyStyles
            : Enumerable.Repeat(LegacyReportStyles.RawHeader, columns.Length).ToArray();

        ReportSheetStarted(stats, baseName, progress);
        using var spool = RawReportSpool.Create(columns);
        long spooledRows = 0;
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await fetchPage(
                new PageRequest(cursor, PageRequest.DefaultPageSize),
                cancellationToken).ConfigureAwait(false);
            var ids = page.Rows.Select(entryId).Where(id => id > 0).ToArray();
            var pageRaw = await rawRows.FetchJsonByEntryIdsAsync(
                projectId,
                ids,
                cancellationToken).ConfigureAwait(false);
            EnsureRawRowsComplete(ids, pageRaw);
            var pageRde = await ReadReportRdeValuesAsync(
                projectId,
                ids,
                registryFields,
                customFields,
                customFieldMoneyScale,
                cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = entryId(item);
                var rowJson = pageRaw[id];
                var values = ParseRawValues(rowJson, rawColumns)
                    .Concat(customFields.Select(field => pageRde[id][field.FieldId]))
                    .ToArray();
                spool.Append(values);
                spooledRows = checked(spooledRows + 1);
                ReportPreparationPulse(
                    stats,
                    baseName,
                    spooledRows,
                    progress);
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        spool.Complete();
        var rawWidths = spool.Widths;
        var resolvedAutoFitMode = ResolveRawSheetAutoFitMode(
            autoFitMode,
            spooledRows);

        var part = 1;
        var rowIndex = 2U;
        var sheetName = SeriesName(baseName, part);
        ReportSheetWriter? sheet = OpenRawSheet(
            document,
            sheets,
            sheetName,
            columns,
            rawWidths,
            styles,
            resolvedAutoFitMode,
            headerStyles,
            appearanceMode == RawSheetAppearanceMode.ExportDatabase ? 12.5D : null);
        try
        {
            foreach (var values in spool.ReadRows(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rowIndex > _continuationRowLimit)
                {
                    AddStat(stats, sheet.CloseAndSummarize(cancellationToken), progress);
                    sheet.Dispose();
                    part++;
                    rowIndex = 2;
                    sheetName = SeriesName(baseName, part);
                    sheet = OpenRawSheet(
                        document,
                        sheets,
                        sheetName,
                        columns,
                        rawWidths,
                        styles,
                        resolvedAutoFitMode,
                        headerStyles,
                        appearanceMode == RawSheetAppearanceMode.ExportDatabase ? 12.5D : null);
                }

                sheet.WriteDataRow(rowIndex, TextCells(sheet, rowIndex, values, bodyStyles));
                rowIndex++;
                ReportIntermediateRows(
                    stats,
                    sheetName,
                    (long)rowIndex - 2,
                    progress);
            }

            AddStat(stats, sheet.CloseAndSummarize(cancellationToken), progress);
            sheet.Dispose();
            sheet = null;
        }
        finally
        {
            sheet?.Dispose();
        }
    }

    private static void EnsureRawRowsComplete(
        IReadOnlyCollection<long> requestedEntryIds,
        IReadOnlyDictionary<long, string> rawRowsByEntryId)
    {
        if (requestedEntryIds.Any(entryId => !rawRowsByEntryId.ContainsKey(entryId)))
        {
            throw MissingRawGlSource();
        }
    }

    private static JetActionException MissingRawGlSource() => new(
        JetErrorCodes.StaleResult,
        "報告引用的原始 GL 列已不完整，請重新匯入、配對並執行對應步驟後再產出報告。");

    private static ReportSheetWriter OpenRawSheet(
        SpreadsheetDocument document,
        Sheets sheets,
        string name,
        IReadOnlyList<string> columns,
        IReadOnlyList<double> widths,
        WorkbookStyleMap? styles,
        RawSheetAutoFitMode autoFitMode,
        IReadOnlyList<uint> headerStyles,
        double? defaultRowHeight)
    {
        if (widths.Count != columns.Count)
        {
            throw new InvalidDataException(
                $"Raw report width count {widths.Count} does not match column count {columns.Count}.");
        }
        if (headerStyles.Count != columns.Count)
        {
            throw new InvalidDataException(
                "Raw report appearance style count does not match column count.");
        }

        var sheet = OpenSheet(
            document,
            sheets,
            name,
            LegacyAutoFitColumns(widths, autoFitMode),
            new ReportSheetOptions(
                PageMargins: StandardMargins,
                DefaultRowHeight: defaultRowHeight),
            styles is null ? null : styles.Map);
        sheet.WriteFixedRow(
            1,
            columns.Select((label, index) =>
                    sheet.TextCell(1, checked((uint)index + 1U), label, headerStyles[index]))
                .ToArray());
        return sheet;
    }

    private static IReadOnlyList<ReportColumn> LegacyAutoFitColumns(
        IReadOnlyList<double> widths,
        RawSheetAutoFitMode mode)
    {
        ArgumentNullException.ThrowIfNull(widths);
        var columnCount = mode == RawSheetAutoFitMode.LegacyFirstTwenty
            ? Math.Max(20, widths.Count)
            : widths.Count;
        var columns = new ReportColumn[columnCount];
        for (var index = 0; index < columnCount; index++)
        {
            var columnIndex = checked((uint)index + 1U);
            var width = index < widths.Count
                ? widths[index]
                : ExcelDisplayWidth.ToColumnWidth(0D);
            var bestFit = mode switch
            {
                RawSheetAutoFitMode.None => false,
                RawSheetAutoFitMode.MeasuredColumns => index < widths.Count,
                RawSheetAutoFitMode.LegacyFirstTwenty => index < 20,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
            };
            columns[index] = new ReportColumn(
                columnIndex,
                columnIndex,
                width,
                BestFit: bestFit);
        }
        return columns;
    }

    private static RawSheetAutoFitMode ResolveRawSheetAutoFitMode(
        RawSheetAutoFitMode requested,
        long spooledRows)
    {
        if (spooledRows < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(spooledRows),
                spooledRows,
                "Spooled report row count cannot be negative.");
        }
        return requested switch
        {
            RawSheetAutoFitMode.LegacyFirstTwentyWhenAtMostOneMillion =>
                spooledRows <= LegacyCriteriaAutoFitMaximumRows
                    ? RawSheetAutoFitMode.LegacyFirstTwenty
                    : RawSheetAutoFitMode.None,
            _ => requested
        };
    }

    private enum RawSheetAutoFitMode
    {
        None,
        MeasuredColumns,
        LegacyFirstTwenty,
        LegacyFirstTwentyWhenAtMostOneMillion
    }

    private enum RawSheetAppearanceMode
    {
        ExportDatabase,
        JetExtension
    }

    internal static string SeriesName(string baseName, int part)
    {
        if (part == 1)
        {
            return baseName.Length <= 31 ? baseName : baseName[..31];
        }
        var suffix = $" ({part})";
        var length = Math.Min(baseName.Length, 31 - suffix.Length);
        return baseName[..length] + suffix;
    }

    private static IReadOnlyList<Cell> HeaderCells(
        ReportSheetWriter sheet,
        uint row,
        IReadOnlyList<string> labels,
        uint style = LegacyReportStyles.Header)
    {
        var cells = new List<Cell>(labels.Count);
        for (var index = 0; index < labels.Count; index++)
        {
            cells.Add(sheet.TextCell(
                row,
                (uint)index + 1,
                labels[index],
                style));
        }
        return cells;
    }

    private static IReadOnlyList<Cell> TextCells(
        ReportSheetWriter sheet,
        uint row,
        IReadOnlyList<string?> values,
        IReadOnlyList<uint> styles)
    {
        if (styles.Count != values.Count)
        {
            throw new InvalidDataException(
                "Raw report body style count does not match value count.");
        }
        var cells = new List<Cell>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            cells.Add(sheet.TextCell(
                row,
                (uint)index + 1,
                values[index],
                styles[index]));
        }
        return cells;
    }

    private async Task<IReadOnlyList<uint>> ResolveExportDatabaseStylesAsync(
        string projectId,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LegacyFieldDefinition> definitions = _fieldDefinitionFactsPort is null
            ? []
            : await _fieldDefinitionFactsPort.ReadAsync(
                    projectId,
                    DatasetKind.Gl,
                    LegacyFieldDefinitionScope.Source,
                    cancellationToken)
                .ConfigureAwait(false);
        var result = new uint[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            var ordinal = index + 1;
            var definition = definitions.FirstOrDefault(item =>
                item.Ordinal == ordinal
                && string.Equals(item.FieldName, columns[index], StringComparison.Ordinal));
            result[index] = ExportDatabaseStyle(definition);
        }
        return result;
    }

    private static uint ExportDatabaseStyle(LegacyFieldDefinition? definition) =>
        definition?.Kind switch
        {
            LegacyFieldKind.Text => LegacyReportStyles.ExportDatabaseText,
            LegacyFieldKind.Number when definition.DecimalPlaces is >= 0 and <= 28 =>
                LegacyReportStyles.ExportDatabaseNumber(definition.DecimalPlaces.Value),
            LegacyFieldKind.Date => LegacyReportStyles.ExportDatabaseDate,
            LegacyFieldKind.Time => LegacyReportStyles.ExportDatabaseTime,
            _ => LegacyReportStyles.ExportDatabaseGeneral
        };

    private static IReadOnlyList<string?> ParseRawValues(string json, IReadOnlyList<string> columns)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var result = new string?[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            if (!root.TryGetProperty(columns[index], out var value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            result[index] = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.GetRawText();
        }
        return result;
    }

    private static decimal Display(long scaled, int moneyScale) => (decimal)scaled / moneyScale;

    private static string DisplayLastPeriodStart(string? lastPeriodStart) =>
        string.IsNullOrWhiteSpace(lastPeriodStart)
            ? "N/A"
            : string.Concat(lastPeriodStart.Where(char.IsDigit));

    private static long RuleCount(JsonElement root, string section, string property = "count") =>
        root.TryGetProperty(section, out var value) ? GetLong(value, property) : 0;

    private static string RuleStatus(JsonElement root, string section) =>
        root.TryGetProperty(section, out var value) ? GetString(value, "status") ?? "N/A" : "N/A";

    private static long ArrayLength(JsonElement root, string section, string property)
    {
        return root.TryGetProperty(section, out var value)
            && value.TryGetProperty(property, out var array)
            && array.ValueKind == JsonValueKind.Array
            ? array.GetArrayLength()
            : 0;
    }

    private static long GetLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return 0;
        }
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    }

    private static decimal GetDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDecimal(out var number))
        {
            return 0;
        }

        return number;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

}
