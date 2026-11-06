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
    IUnbalancedVoucherPageRepository unbalancedGlEntries,
    IBlankRecordsPageRepository blankRecords,
    IInfSamplePageRepository infSamples,
    IPrescreenPageRepository prescreenPages,
    IRawGlExportRepository rawRows,
    IImportRepository imports,
    IMappingStateStore mappings,
    IPreparerSummaryExportRepository preparerSummaries,
    IAccountUsageExportRepository accountUsage,
    IHighRiskSummaryScenariosRepository highRiskSummaryCounts,
    ISourceQualityPageRepository sourceQualityPages,
    IHighRiskSummaryRowPageRepository? highRiskSummaryRows = null,
    IResultPageRdeValuesPort? resultPageRdeValues = null)
    : IValidationReportWriter, IInfReportWriter, IPrescreenReportWriter, ICriteriaSelectionReportWriter
{
    private const int DefaultProgressRowInterval = 10_000;
    private const uint ExcelMaxRow = ExcelWorksheetConstraints.MaxRows;
    // Legacy Step4 BAS 8889 / ISM 10256: only the criteria database's
    // EntireColumn.AutoFit is suppressed above one million exported rows.
    private const long LegacyCriteriaAutoFitMaximumRows = 1_000_000;
    private readonly uint _continuationRowLimit = ExcelMaxRow;
    private readonly int _progressRowInterval = DefaultProgressRowInterval;
    private readonly ReportTemplateCatalog _templates = ReportTemplateCatalog.Default;
    private readonly IFieldInfoFactsPort? _fieldInfoFactsPort;

    internal LegacyReportWriter(
        ICompletenessAccountPageRepository completenessAccounts,
        ICompletenessDiffPageRepository completenessDiffs,
        IUnbalancedVoucherPageRepository unbalancedGlEntries,
        IBlankRecordsPageRepository blankRecords,
        IInfSamplePageRepository infSamples,
        IPrescreenPageRepository prescreenPages,
        IRawGlExportRepository rawRows,
        IImportRepository imports,
        IMappingStateStore mappings,
        IPreparerSummaryExportRepository preparerSummaries,
        IAccountUsageExportRepository accountUsage,
        IHighRiskSummaryScenariosRepository highRiskSummaryCounts,
        ISourceQualityPageRepository sourceQualityPages,
        LegacyReportWriterOptions options,
        int progressRowInterval = DefaultProgressRowInterval)
        : this(
            completenessAccounts,
            completenessDiffs,
            unbalancedGlEntries,
            blankRecords,
            infSamples,
            prescreenPages,
            rawRows,
            imports,
            mappings,
            preparerSummaries,
            accountUsage,
            highRiskSummaryCounts,
            sourceQualityPages)
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
        IUnbalancedVoucherPageRepository unbalancedGlEntries,
        IBlankRecordsPageRepository blankRecords,
        IInfSamplePageRepository infSamples,
        IPrescreenPageRepository prescreenPages,
        IRawGlExportRepository rawRows,
        IImportRepository imports,
        IMappingStateStore mappings,
        IPreparerSummaryExportRepository preparerSummaries,
        IAccountUsageExportRepository accountUsage,
        IHighRiskSummaryScenariosRepository highRiskSummaryCounts,
        ISourceQualityPageRepository sourceQualityPages,
        IHighRiskSummaryRowPageRepository highRiskSummaryRows,
        LegacyReportWriterOptions options,
        int progressRowInterval = DefaultProgressRowInterval)
        : this(
            completenessAccounts,
            completenessDiffs,
            unbalancedGlEntries,
            blankRecords,
            infSamples,
            prescreenPages,
            rawRows,
            imports,
            mappings,
            preparerSummaries,
            accountUsage,
            highRiskSummaryCounts,
            sourceQualityPages,
            highRiskSummaryRows)
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
        IUnbalancedVoucherPageRepository unbalancedGlEntries,
        IBlankRecordsPageRepository blankRecords,
        IInfSamplePageRepository infSamples,
        IPrescreenPageRepository prescreenPages,
        IRawGlExportRepository rawRows,
        IImportRepository imports,
        IMappingStateStore mappings,
        IPreparerSummaryExportRepository preparerSummaries,
        IAccountUsageExportRepository accountUsage,
        IHighRiskSummaryScenariosRepository highRiskSummaryCounts,
        ISourceQualityPageRepository sourceQualityPages,
        IHighRiskSummaryRowPageRepository highRiskSummaryRows,
        IFieldInfoFactsPort fieldInfoFactsPort,
        IResultPageRdeValuesPort? resultPageRdeValues = null)
        : this(
            completenessAccounts,
            completenessDiffs,
            unbalancedGlEntries,
            blankRecords,
            infSamples,
            prescreenPages,
            rawRows,
            imports,
            mappings,
            preparerSummaries,
            accountUsage,
            highRiskSummaryCounts,
            sourceQualityPages,
            highRiskSummaryRows,
            resultPageRdeValues)
    {
        ArgumentNullException.ThrowIfNull(fieldInfoFactsPort);
        _fieldInfoFactsPort = fieldInfoFactsPort;
    }

    internal LegacyReportWriter(
        ICompletenessAccountPageRepository completenessAccounts,
        ICompletenessDiffPageRepository completenessDiffs,
        IUnbalancedVoucherPageRepository unbalancedGlEntries,
        IBlankRecordsPageRepository blankRecords,
        IInfSamplePageRepository infSamples,
        IPrescreenPageRepository prescreenPages,
        IRawGlExportRepository rawRows,
        IImportRepository imports,
        IMappingStateStore mappings,
        IPreparerSummaryExportRepository preparerSummaries,
        IAccountUsageExportRepository accountUsage,
        IHighRiskSummaryScenariosRepository highRiskSummaryCounts,
        ISourceQualityPageRepository sourceQualityPages,
        IHighRiskSummaryRowPageRepository highRiskSummaryRows,
        IFieldInfoFactsPort fieldInfoFactsPort,
        LegacyReportWriterOptions options,
        int progressRowInterval = DefaultProgressRowInterval,
        IResultPageRdeValuesPort? resultPageRdeValues = null)
        : this(
            completenessAccounts,
            completenessDiffs,
            unbalancedGlEntries,
            blankRecords,
            infSamples,
            prescreenPages,
            rawRows,
            imports,
            mappings,
            preparerSummaries,
            accountUsage,
            highRiskSummaryCounts,
            sourceQualityPages,
            highRiskSummaryRows,
            fieldInfoFactsPort,
            resultPageRdeValues)
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
        Action<WorkingPaperProgress>? progress)
    {
        stats.Add(stat);
        progress?.Invoke(new WorkingPaperProgress(stat.SheetName, stats.Count, stat.RowsWritten));
    }

    private void ReportIntermediateRows(
        List<SheetStat> stats,
        string sheetName,
        long rowsWritten,
        Action<WorkingPaperProgress>? progress)
    {
        if (progress is not null && rowsWritten > 0 && rowsWritten % _progressRowInterval == 0)
        {
            progress(new WorkingPaperProgress(sheetName, stats.Count, rowsWritten));
        }
    }

    private static void ReportSheetStarted(
        List<SheetStat> stats,
        string sheetName,
        Action<WorkingPaperProgress>? progress) =>
        progress?.Invoke(new WorkingPaperProgress(sheetName, stats.Count, 0));

    private void ReportPreparationPulse(
        List<SheetStat> stats,
        string sheetName,
        long rowsInspected,
        Action<WorkingPaperProgress>? progress)
    {
        if (progress is not null
            && rowsInspected > 0
            && rowsInspected % _progressRowInterval == 0)
        {
            progress(new WorkingPaperProgress(sheetName, stats.Count, 0));
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
        Action<WorkingPaperProgress>? progress,
        RawSheetAppearanceMode appearanceMode = RawSheetAppearanceMode.ExportDatabase,
        IReadOnlyList<GlRdeFieldMetadata>? customFields = null,
        IReadOnlyList<GlRdeFieldMetadata>? registryFields = null,
        int? customFieldMoneyScale = null,
        RawSheetMarkerColumn<T>? markerColumn = null)
    {
        var batch = await imports.GetLatestBatchAsync(projectId, DatasetKind.Gl, cancellationToken);
        var rawColumns = batch?.Columns ?? Array.Empty<string>();
        if (rawColumns.Count == 0)
        {
            throw MissingRawGlSource();
        }
        customFields ??= Array.Empty<GlRdeFieldMetadata>();
        registryFields ??= Array.Empty<GlRdeFieldMetadata>();
        // 攸關資料元素的表頭照舊版加 _JE（裁定 2026-11-03-14），撞名用整份配對判斷。
        var columns = rawColumns.Concat(GlRdeOutputNames.For(customFields, registryFields))
            .Concat(markerColumn is null ? Array.Empty<string>() : new[] { markerColumn.Header })
            .ToArray();

        var sourceStyles = appearanceMode == RawSheetAppearanceMode.ExportDatabase
            ? await ResolveExportDatabaseStylesAsync(
                    projectId,
                    rawColumns,
                    cancellationToken)
                .ConfigureAwait(false)
            : Enumerable.Repeat(LegacyReportStyles.RawBody, rawColumns.Count).ToArray();
        // 符合標記在舊版是 IDEA 資料庫裡的文字欄（idea-tool.bas 第 11660 行起的 Final_TAG），跟著其他欄一起匯出，
        // 所以在 IDEA 匯出的明細頁用文字欄的樣式，和 R5 的人工旗標欄相同（裁定 2026-11-05-48）。
        var markerStyles = markerColumn is null
            ? Array.Empty<uint>()
            : new[]
            {
                appearanceMode == RawSheetAppearanceMode.ExportDatabase
                    ? LegacyReportStyles.ExportDatabaseText
                    : LegacyReportStyles.RawBody
            };
        // 舊版的攸關資料元素欄是把來源欄改名加 _JE（idea-tool.bas 第 1688 到 1714 行），明細頁再由 IDEA 匯出整個資料庫
        // （Z_ExportDatabaseXLSX 的 IncludeAllFields，第 9321 行起），所以在 IDEA 匯出的明細頁和它的來源欄同一種樣式；
        // 找不到來源欄時，和沒有欄位定義的來源欄一樣用一般樣式（裁定 2026-11-05-70）。
        var bodyStyles = sourceStyles
            .Concat(customFields.Select(field =>
            {
                if (appearanceMode != RawSheetAppearanceMode.ExportDatabase)
                {
                    return LegacyReportStyles.RawBody;
                }
                for (var index = 0; index < rawColumns.Count; index++)
                {
                    if (string.Equals(rawColumns[index], field.SourceColumn, StringComparison.Ordinal))
                    {
                        return sourceStyles[index];
                    }
                }
                return LegacyReportStyles.ExportDatabaseGeneral;
            }))
            .Concat(markerStyles)
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
                    .Concat(markerColumn is null ? Array.Empty<string?>() : new[] { markerColumn.Value(item) })
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
            appearanceMode == RawSheetAppearanceMode.ExportDatabase ? LegacyReportStyles.ExportDatabaseRowHeight : null);
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
                        appearanceMode == RawSheetAppearanceMode.ExportDatabase ? LegacyReportStyles.ExportDatabaseRowHeight : null);
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

    /// <summary>
    /// 原始 GL 工作表最後多寫的一欄：欄名與每一列的值。目前只有條件篩選報告的「符合條件」標記（裁定 2026-11-05-48）。
    /// </summary>
    private sealed record RawSheetMarkerColumn<TRow>(string Header, Func<TRow, string?> Value);

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
        IReadOnlyList<FieldInfo> definitions = _fieldInfoFactsPort is null
            ? []
            : await _fieldInfoFactsPort.ReadAsync(
                    projectId,
                    DatasetKind.Gl,
                    FieldInfoScope.Source,
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

    private static uint ExportDatabaseStyle(FieldInfo? definition) =>
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

    private static string DisplayPrepStartDate(string? prepStartDate) =>
        string.IsNullOrWhiteSpace(prepStartDate)
            ? "N/A"
            : string.Concat(prepStartDate.Where(char.IsDigit));

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
