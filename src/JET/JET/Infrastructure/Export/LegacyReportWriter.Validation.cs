using System.Text.Json;
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class LegacyReportWriter
{
    // idea-script.bas 3593–3799：Validation summary 的六個條件式黃底。
    private static readonly WorkbookStylePatch ValidationSummaryDifferenceFill =
        new(FillForegroundArgb: "FFFFFF00");

    public async Task<ExportStats> WriteAsync(
        Stream output,
        ValidationReportContext context,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null)
    {
        // 保留 assembly 外 caller 的既有 public writer contract。Production export
        // 走 IPlannedValidationReportWriter；相容入口仍不改 public context/wire。
        using var summary = JsonDocument.Parse(context.SummaryJson);
        var projection = ParseLegacyProjection(summary.RootElement);
        var plan = CompatibilityPlan(context, projection);
        return await WritePlannedCoreAsync(
            output,
            context,
            projection,
            plan,
            new FieldInfoProjection([], []),
            null,
            "JET",
            cancellationToken,
            progress);
    }

    Task<ExportStats> ITypedValidationReportWriter.WriteTypedAsync(
        Stream output,
        ValidationReportContext context,
        ValidationReportProjection projection,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var plan = CompatibilityPlan(context, projection);
        return WritePlannedCoreAsync(
            output,
            context,
            projection,
            plan,
            new FieldInfoProjection([], []),
            null,
            operatorId,
            cancellationToken,
            progress);
    }

    Task<ExportStats> IPlannedValidationReportWriter.WritePlannedAsync(
        Stream output,
        ValidationReportContext context,
        ValidationReportProjection projection,
        ValidationReportPlan plan,
        FieldInfoProjection fieldInfo,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress) =>
        WritePlannedCoreAsync(
            output,
            context,
            projection,
            plan,
            fieldInfo,
            null,
            operatorId,
            cancellationToken,
            progress);

    Task<ExportStats> IFormalPlannedValidationReportWriter.WriteFormalPlannedAsync(
        Stream output,
        ValidationReportContext context,
        ValidationReportProjection projection,
        ValidationReportPlan plan,
        FieldInfoProjection fieldInfo,
        ReportWorkbookMetadata workbookMetadata,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        ReportWorkbookMetadataInvariant.Validate(workbookMetadata);
        return WritePlannedCoreAsync(
            output,
            context,
            projection,
            plan,
            fieldInfo,
            workbookMetadata,
            operatorId,
            cancellationToken,
            progress);
    }

    private static ValidationReportPlan CompatibilityPlan(
        ValidationReportContext context,
        ValidationReportProjection projection) =>
        JetAuditProgram.Finalize(
            JetAuditProgram.Plan(new ValidationReportRequest(
                context.Project.ProjectId,
                context.Project.PeriodStart,
                context.Project.PeriodEnd,
                projection.CompletenessDiffAccountCount,
                projection.NullAccountCount,
                projection.NullDocumentCount,
                projection.NullDescriptionCount,
                projection.OutOfRangeDateCount)),
            // 此值只服務 assembly 內舊 typed/public 相容入口；production 一律由
            // provider-neutral facts port 提供 raw joined detail count。
            new ValidationReportPlanningFacts(projection.UnbalancedDocumentCount));

    private async Task<ExportStats> WritePlannedCoreAsync(
        Stream output,
        ValidationReportContext context,
        ValidationReportProjection projection,
        ValidationReportPlan plan,
        FieldInfoProjection fieldInfo,
        ReportWorkbookMetadata? workbookMetadata,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(fieldInfo);
        if (!plan.IsFinalized)
        {
            throw new InvalidOperationException("Validation workbook plan 必須先 Finalize。");
        }

        var glBatch = await imports.GetLatestBatchAsync(
            context.Project.ProjectId, DatasetKind.Gl, cancellationToken).ConfigureAwait(false);
        var tbBatch = await imports.GetLatestBatchAsync(
            context.Project.ProjectId, DatasetKind.Tb, cancellationToken).ConfigureAwait(false);
        var glMapping = await mappings.FindAsync(
            context.Project.ProjectId, DatasetKind.Gl, cancellationToken).ConfigureAwait(false);
        var tbMapping = await mappings.FindAsync(
            context.Project.ProjectId, DatasetKind.Tb, cancellationToken).ConfigureAwait(false);
        var hasApprovalDate = glMapping is not null
                              && (glMapping.GlOptions?.ApprovalDateMode
                                  ?? GlMappingOptions.NormalizeLegacy(glMapping.Mapping).ApprovalDateMode)
                              != ApprovalDateModeNames.Unmapped;
        var metadata = glMapping is not null && tbMapping is not null
            ? MappingMetadataCodec.Encode(glMapping, tbMapping)
            : null;

        return await ReportTemplatePackage.FillDirectStreamingAsync(
            _templates,
            ReportTemplateCatalog.Validation,
            output,
            ["ValidationReport", MappingMetadataFormat.WorksheetName, WorkpaperSheetCatalog.Step13],
            (editor, ct) => FillValidationTemplateAsync(
                editor,
                context,
                projection,
                plan,
                fieldInfo,
                glBatch,
                tbBatch,
                hasApprovalDate,
                metadata,
                workbookMetadata,
                operatorId,
                ct,
                progress),
            cancellationToken).ConfigureAwait(false);
    }

    private static ValidationReportProjection ParseLegacyProjection(JsonElement root)
    {
        var stats = root.GetProperty("stats");
        var nullRecords = root.GetProperty("nullRecordsTest");
        var docBalance = root.GetProperty("docBalanceTest");
        var sourceQuality = root.TryGetProperty("sourceQuality", out var sourceQualityValue)
            ? sourceQualityValue
            : default;

        return new ValidationReportProjection(
            Net: GetDecimal(stats, "net"),
            TotalDebit: GetDecimal(stats, "totalDebit"),
            TotalCredit: GetDecimal(stats, "totalCredit"),
            GlRowCount: GetLong(stats, "glRowCount"),
            CompletenessDiffAccountCount: RuleCount(
                root,
                "completenessTest",
                "diffAccountCount"),
            UnbalancedDocumentCount: GetLong(
                docBalance,
                "unbalancedDocumentCount"),
            NullAccountCount: GetLong(nullRecords, "nullAccountCount"),
            NullDocumentCount: GetLong(nullRecords, "nullDocumentCount"),
            NullDescriptionCount: GetLong(nullRecords, "nullDescriptionCount"),
            OutOfRangeDateCount: GetLong(nullRecords, "outOfRangeDateCount"),
            SourceQualityFindingCount: sourceQuality.ValueKind == JsonValueKind.Object
                ? GetLong(sourceQuality, "findingCount")
                : 0,
            SourceQualitySampleRows: ParseLegacySourceQualityRows(sourceQuality));
    }

    private static IReadOnlyList<SourceQualityFindingRow> ParseLegacySourceQualityRows(
        JsonElement sourceQuality)
    {
        if (sourceQuality.ValueKind != JsonValueKind.Object
            || !sourceQuality.TryGetProperty("sampleRows", out var sampleRows)
            || sampleRows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return sampleRows.EnumerateArray()
            .Where(row => row.ValueKind == JsonValueKind.Object)
            .Select(row => new SourceQualityFindingRow(
                GetString(row, "category") ?? string.Empty,
                checked((int)GetLong(row, "sourceRowNumber")),
                GetString(row, "sourceLabel") ?? string.Empty,
                GetString(row, "documentNumber"),
                GetString(row, "accountCode"),
                GetString(row, "postDate"),
                GetString(row, "description"),
                EntryId: 0))
            .ToArray();
    }

    private async Task<IReadOnlyList<SheetStat>> FillValidationTemplateAsync(
        DirectTemplateWorkbookEditor editor,
        ValidationReportContext context,
        ValidationReportProjection projection,
        ValidationReportPlan plan,
        FieldInfoProjection fieldInfo,
        ImportBatchInfo? glBatch,
        ImportBatchInfo? tbBatch,
        bool hasApprovalDate,
        string? mappingMetadata,
        ReportWorkbookMetadata? workbookMetadata,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var stats = new List<SheetStat>();
        var stylesPart = editor.Document.WorkbookPart?.WorkbookStylesPart
            ?? throw new InvalidDataException("Validation template has no styles part.");
        var legacyStyles = WorkbookStyleMap.Append(stylesPart, LegacyReportStyles.Build());
        var fieldInfoStyles = WorkbookStyleMap.Append(stylesPart, FormalFieldInfoStyles.Build());
        EditValidationSummary(
            editor,
            context,
            projection,
            plan.RequireDetail(ValidationReportDetailKind.UnbalancedGlEntry).Emit,
            glBatch,
            tbBatch,
            hasApprovalDate,
            operatorId,
            cancellationToken);
        AddStat(stats, new SheetStat("ValidationReport", 0), progress);

        EmitFieldInfoDirect(
            editor,
            fieldInfo,
            mappingMetadata,
            fieldInfoStyles,
            cancellationToken,
            out var fieldInfoStat);
        AddStat(stats, fieldInfoStat, progress);

        await EmitCompletenessDiffExplanationDirectAsync(
            editor,
            stats,
            context.Project,
            plan.EmitCompletenessExplanation,
            cancellationToken,
            progress).ConfigureAwait(false);
        AddStat(stats, new SheetStat("完整性測試出現差異時之指引", 0), progress);

        var document = editor.Document;
        var sheets = editor.Sheets;
        await EmitNullDetailFromPlanAsync(
            document, sheets, stats, context.Project, plan,
            ValidationReportDetailKind.NullAccount, NullRecordCategory.NullAccount,
            "V_Report 1", legacyStyles, RawSheetAutoFitMode.LegacyFirstTwenty,
            cancellationToken, progress).ConfigureAwait(false);
        await EmitNullDetailFromPlanAsync(
            document, sheets, stats, context.Project, plan,
            ValidationReportDetailKind.NullDocument, NullRecordCategory.NullDocument,
            "V_Report 2", legacyStyles, RawSheetAutoFitMode.LegacyFirstTwenty,
            cancellationToken, progress).ConfigureAwait(false);
        await EmitNullDetailFromPlanAsync(
            document, sheets, stats, context.Project, plan,
            ValidationReportDetailKind.NullDescription, NullRecordCategory.NullDescription,
            "V_Report 3", legacyStyles, RawSheetAutoFitMode.LegacyFirstTwenty,
            cancellationToken, progress).ConfigureAwait(false);
        if (hasApprovalDate)
        {
            await EmitNullDetailFromPlanAsync(
                document, sheets, stats, context.Project, plan,
                ValidationReportDetailKind.OutOfRangeApprovalDate,
                NullRecordCategory.OutOfRangeDate,
                "V_Report 4", legacyStyles, RawSheetAutoFitMode.LegacyFirstTwenty,
                cancellationToken, progress).ConfigureAwait(false);
        }

        // V5 是 Legacy 固定全科目調節表，不受異常明細門檻影響。
        await EmitCompletenessReportAsync(
            document,
            sheets,
            stats,
            context.Project,
            legacyStyles,
            cancellationToken,
            progress).ConfigureAwait(false);

        if (plan.RequireDetail(ValidationReportDetailKind.UnbalancedGlEntry).Emit)
        {
            await EmitUnbalancedGlReportAsync(
                document,
                sheets,
                stats,
                context.Project,
                legacyStyles,
                cancellationToken,
                progress).ConfigureAwait(false);
        }

        await EmitSourceQualitySheetsAsync(
            document,
            sheets,
            stats,
            context.Project.ProjectId,
            projection.SourceQualitySampleRows,
            requireCurrentPopulation: workbookMetadata is not null,
            legacyStyles,
            cancellationToken,
            progress).ConfigureAwait(false);

        if (workbookMetadata is not null)
        {
            ReportWorkbookMetadataWorksheet.Append(document, workbookMetadata, cancellationToken);
        }

        document.WorkbookPart!.Workbook.Save();
        return stats;
    }

    private async Task EmitNullDetailFromPlanAsync(
        SpreadsheetDocument document,
        Sheets sheets,
        List<SheetStat> stats,
        ReportDocumentContext context,
        ValidationReportPlan plan,
        ValidationReportDetailKind detailKind,
        NullRecordCategory category,
        string sheetName,
        WorkbookStyleMap legacyStyles,
        RawSheetAutoFitMode autoFitMode,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var detail = plan.RequireDetail(detailKind);
        if (!detail.Emit)
        {
            return;
        }

        await EmitNullReportIfNeededAsync(
            document,
            sheets,
            stats,
            context,
            category,
            sheetName,
            detail.Count,
            legacyStyles,
            autoFitMode,
            cancellationToken,
            progress).ConfigureAwait(false);
    }

    private static void EditValidationSummary(
        DirectTemplateWorkbookEditor editor,
        ValidationReportContext context,
        ValidationReportProjection projection,
        bool emitUnbalancedDetail,
        ImportBatchInfo? glBatch,
        ImportBatchInfo? tbBatch,
        bool hasApprovalDate,
        string operatorId,
        CancellationToken cancellationToken)
    {
        editor.RewriteWorksheetPart(
            "ValidationReport",
            (part, ct) =>
            {
                var allowed = new List<string>
                {
                    "E1", "E2", "E3", "E4", "E5",
                    "E9", "E10", "E11", "E12", "E13", "E14", "E15",
                    "D20", "D21", "D22", "D23", "D24", "D25"
                };
                if (!hasApprovalDate)
                {
                    allowed.Add("E23");
                }

                var cells = new DirectTemplateCellEditor(
                    part.Worksheet,
                    allowed,
                    editor.StylePatcher);
                cells.SetInlineString("E1", $"Client: {context.Project.CompanyName}");
                cells.SetInlineString("E2", $"Year End: {context.Project.PeriodEnd}");
                cells.Clear("E3");
                cells.SetInlineString("E4", $"Prepared by: {operatorId}");
                cells.SetInlineString("E5", $"Prepared date: {context.GeneratedUtc:yyyy/MM/dd}");
                cells.SetInlineString("E9", glBatch is null
                    ? "N/A"
                    : Path.GetFileName(glBatch.SourceFileName));
                cells.SetNumber("E10", Math.Abs(projection.Net) > 0.01m ? projection.Net : 0m);
                cells.SetNumber("E11", projection.TotalDebit);
                cells.SetNumber("E12", projection.TotalCredit);
                cells.SetNumber("E13", projection.GlRowCount);
                cells.SetInlineString("E14", tbBatch is null
                    ? "N/A"
                    : Path.GetFileName(tbBatch.SourceFileName));
                if (tbBatch is null)
                {
                    cells.SetInlineString("E15", "N/A");
                }
                else
                {
                    cells.SetNumber("E15", tbBatch.RowCount);
                }

                cells.SetNumber(
                    "D20",
                    projection.NullAccountCount,
                    projection.NullAccountCount > 0
                        ? ValidationSummaryDifferenceFill
                        : null);
                cells.SetNumber(
                    "D21",
                    projection.NullDocumentCount,
                    projection.NullDocumentCount > 0
                        ? ValidationSummaryDifferenceFill
                        : null);
                cells.SetNumber(
                    "D22",
                    projection.NullDescriptionCount,
                    projection.NullDescriptionCount > 0
                        ? ValidationSummaryDifferenceFill
                        : null);
                if (hasApprovalDate)
                {
                    cells.SetNumber(
                        "D23",
                        projection.OutOfRangeDateCount,
                        projection.OutOfRangeDateCount > 0
                            ? ValidationSummaryDifferenceFill
                            : null);
                }
                else
                {
                    cells.SetInlineString("D23", "N/A");
                    cells.SetInlineString("E23", "未設定傳票核准日欄位");
                }
                cells.SetNumber(
                    "D24",
                    projection.CompletenessDiffAccountCount,
                    projection.CompletenessDiffAccountCount > 0
                        ? ValidationSummaryDifferenceFill
                        : null);
                // public summary 的 distinct voucher count 維持不變；raw joined count
                // 只存在 finalized plan，專供 V6 workbook gate。
                cells.SetNumber(
                    "D25",
                    projection.UnbalancedDocumentCount,
                    emitUnbalancedDetail
                        ? ValidationSummaryDifferenceFill
                        : null);
                cells.EnsureAllAllowedCellsWereEdited();
                part.Worksheet.Save();
                ct.ThrowIfCancellationRequested();
            },
            cancellationToken);
    }

    private static void EmitFieldInfoDirect(
        DirectTemplateWorkbookEditor editor,
        FieldInfoProjection projection,
        string? mappingMetadata,
        WorkbookStyleMap styles,
        CancellationToken cancellationToken,
        out SheetStat stat)
    {
        var defaultStyle = styles.Map(FormalFieldInfoStyles.Default);
        var boldStyle = styles.Map(FormalFieldInfoStyles.Bold);
        var versionStyle = editor.StylePatcher.Patch(
            boldStyle,
            FormalFieldInfoAppearance.Version);
        var tbHeaderStyle = editor.StylePatcher.Patch(
            boldStyle,
            FormalFieldInfoAppearance.TbHeader);
        var glHeaderStyle = editor.StylePatcher.Patch(
            boldStyle,
            FormalFieldInfoAppearance.GlHeader);
        SheetStat? result = null;
        editor.RewriteEmptyWorksheet(
            MappingMetadataFormat.WorksheetName,
            (part, ct) =>
            {
                var headers = new[]
                {
                    "配對前欄位名稱", "欄位型態", "文字長度", "小數位數", "配對後欄位名稱"
                };
                var allRows = projection.TbRows.Concat(projection.GlRows).ToArray();
                var widths = new[]
                {
                    AutoFitWidth(new[] { "V.2019", "TB檔案配對前後欄位對照表", "GL檔案配對前後欄位對照表", headers[0] }
                        .Concat(allRows.Select(row => row.DisplayName))),
                    AutoFitWidth(new[] { headers[1] }.Concat(allRows.Select(row => row.FieldType))),
                    AutoFitWidth(new[] { headers[2] }.Concat(allRows.Select(row => row.TextLength?.ToString(CultureInfo.InvariantCulture)))),
                    AutoFitWidth(new[] { headers[3] }.Concat(allRows.Select(row => row.DecimalPlaces?.ToString(CultureInfo.InvariantCulture)))),
                    AutoFitWidth(new[] { headers[4] }.Concat(allRows.Select(row => row.ActualFieldName)))
                };
                using var sheet = new ReportSheetWriter(
                    MappingMetadataFormat.WorksheetName,
                    part,
                    [
                        new ReportColumn(1, 1, widths[0], BestFit: true),
                        new ReportColumn(2, 2, widths[1], BestFit: true),
                        new ReportColumn(3, 3, widths[2], BestFit: true),
                        new ReportColumn(4, 4, widths[3], BestFit: true),
                        new ReportColumn(5, 5, widths[4], BestFit: true),
                        new ReportColumn(6, 8, 2, Hidden: true)
                    ],
                    new ReportSheetOptions(
                        PageMargins: StandardMargins,
                        DefaultRowHeight: 18D));
                sheet.AddMerge("A1:E1");
                var firstRow = new List<Cell>
                {
                    sheet.TextCell(1, 1, "V.2019", versionStyle)
                };
                if (mappingMetadata is not null)
                {
                    // Hidden round-trip metadata must never inherit a wrapped visible
                    // header style; a long JSON payload would otherwise auto-expand row 1.
                    firstRow.Add(sheet.TextCell(1, 6, MappingMetadataFormat.Marker, 0));
                    firstRow.Add(sheet.NumberCell(1, 7, MappingMetadataFormat.CurrentVersion, 0));
                    firstRow.Add(sheet.TextCell(1, 8, mappingMetadata, 0));
                }
                sheet.WriteFixedRow(1, firstRow);
                sheet.AddMerge("A2:E2");
                sheet.WriteFixedRow(2,
                    [sheet.TextCell(2, 1, "TB檔案配對前後欄位對照表", boldStyle)]);
                WriteFieldInfoHeader(sheet, 3, headers, tbHeaderStyle);
                uint rowIndex = 4;
                foreach (var row in projection.TbRows)
                {
                    ct.ThrowIfCancellationRequested();
                    WriteFieldInfoRow(sheet, rowIndex++, row, defaultStyle);
                }

                // master spec 明示兩個 physical blank rows；必須真的寫出 Row，
                // 不能只跳過 row index。
                sheet.WriteFixedRow(rowIndex++, []);
                sheet.WriteFixedRow(rowIndex++, []);
                var glTitleRow = rowIndex++;
                sheet.AddMerge($"A{glTitleRow}:E{glTitleRow}");
                sheet.WriteFixedRow(glTitleRow,
                    [sheet.TextCell(glTitleRow, 1, "GL檔案配對前後欄位對照表", boldStyle)]);
                WriteFieldInfoHeader(sheet, rowIndex++, headers, glHeaderStyle);
                foreach (var row in projection.GlRows)
                {
                    ct.ThrowIfCancellationRequested();
                    WriteFieldInfoRow(sheet, rowIndex++, row, defaultStyle);
                }
                sheet.Protect();
                result = sheet.CloseAndSummarize(ct) with
                {
                    RowsWritten = projection.TbRows.Count + projection.GlRows.Count
                };
            },
            cancellationToken);
        stat = result ?? throw new InvalidOperationException("Field Info writer did not produce stats.");
    }

    private static void WriteFieldInfoHeader(
        ReportSheetWriter sheet,
        uint rowIndex,
        IReadOnlyList<string> headers,
        uint style) =>
        sheet.WriteFixedRow(
            rowIndex,
            headers.Select((header, index) =>
                sheet.TextCell(rowIndex, (uint)index + 1, header, style)).ToArray());

    private static void WriteFieldInfoRow(
        ReportSheetWriter sheet,
        uint rowIndex,
        FieldInfoRow row,
        uint style)
    {
        var cells = new List<Cell>
        {
            sheet.TextCell(rowIndex, 1, row.DisplayName, style),
            sheet.TextCell(rowIndex, 2, row.FieldType, style)
        };
        cells.Add(row.TextLength is { } textLength
            ? sheet.NumberCell(rowIndex, 3, textLength, style)
            : sheet.BlankCell(rowIndex, 3, style));
        cells.Add(row.DecimalPlaces is { } decimals
            ? sheet.NumberCell(rowIndex, 4, decimals, style)
            : sheet.BlankCell(rowIndex, 4, style));
        cells.Add(sheet.TextCell(rowIndex, 5, row.ActualFieldName, style));
        sheet.WriteDataRow(rowIndex, cells);
    }

    private static double AutoFitWidth(IEnumerable<string?> values)
    {
        var maximum = values
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(ExcelDisplayWidth.Measure)
            .DefaultIfEmpty(0D)
            .Max();
        return ExcelDisplayWidth.ToColumnWidth(maximum);
    }

    private async Task EmitCompletenessDiffExplanationDirectAsync(
        DirectTemplateWorkbookEditor editor,
        List<SheetStat> stats,
        ReportDocumentContext context,
        bool emit,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        if (!emit)
        {
            editor.RemoveWorksheet(WorkpaperSheetCatalog.Step13, cancellationToken);
            return;
        }

        var page = await completenessDiffs.GetPageAsync(
            context.ProjectId,
            context.MoneyScale,
            context.PeriodStart,
            context.PeriodEnd,
            new PageRequest(null, PageRequest.DefaultPageSize),
            cancellationToken).ConfigureAwait(false);
        if (page.Rows.Count == 0)
        {
            // 保存的 run 說有差異科目，但目前資料查不到任何一列：這是來源資料已變動，
            // 不是範本損壞。錯誤碼比照 MissingRawGlSource，避免 FillDirect 的
            // package catch 把資料不一致誤報成「範本可能損壞」。
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "完整性差異科目已與目前資料不一致，請重新執行資料驗證後再產出報告。");
        }

        WorksheetPart? templatePart = null;
        editor.RewriteWorksheetPart(
            WorkpaperSheetCatalog.Step13,
            (part, _) => templatePart = part,
            cancellationToken);
        var staticCells = new DirectTemplateCellEditor(
            templatePart!.Worksheet,
            ["A1", "A2", "A3"]);
        staticCells.SetInlineString("A1", $"公司名稱 : {context.CompanyName}");
        staticCells.SetInlineString(
            "A2",
            $"測試資料期間 :  {context.PeriodStart} ~ {context.PeriodEnd}");
        staticCells.SetInlineString(
            "A3",
            $"財務報表準備期間 - 開始日 : {DisplayLastPeriodStart(context.LastPeriodStart)}");
        staticCells.EnsureAllAllowedCellsWereEdited();
        var cellStyles = ResolveCompletenessDiffCellStyles(editor, templatePart.Worksheet);
        var continuationPrototype = (Worksheet)templatePart.Worksheet.CloneNode(true);

        var partNumber = 1;
        uint rowIndex = 17;
        var sheetName = WorkpaperSheetCatalog.Step13;
        DirectTemplateStreamingSheetWriter? sheet =
            new(sheetName, templatePart, firstDynamicRow: 17, ensureProtection: true);
        try
        {
            while (true)
            {
                foreach (var item in page.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (rowIndex > _continuationRowLimit)
                    {
                        AddStat(stats, sheet.CloseAndSummarize(cancellationToken), progress);
                        sheet.Dispose();

                        partNumber++;
                        rowIndex = 17;
                        sheetName = SeriesName(WorkpaperSheetCatalog.Step13, partNumber);
                        var continuationPart = editor.AppendWorksheetClone(
                            templatePart,
                            continuationPrototype,
                            sheetName,
                            cancellationToken);
                        sheet = new DirectTemplateStreamingSheetWriter(
                            sheetName,
                            continuationPart,
                            firstDynamicRow: 17,
                            ensureProtection: true);
                    }

                    var cells = CompletenessDiffCells(
                        rowIndex,
                        item,
                        context.MoneyScale,
                        cellStyles);
                    sheet.WriteDataRow(rowIndex, cells);
                    rowIndex++;
                    ReportIntermediateRows(stats, sheetName, (long)rowIndex - 17, progress);
                }

                if (page.NextCursor is null)
                {
                    break;
                }
                page = await completenessDiffs.GetPageAsync(
                    context.ProjectId,
                    context.MoneyScale,
                    context.PeriodStart,
                    context.PeriodEnd,
                    new PageRequest(page.NextCursor, PageRequest.DefaultPageSize),
                    cancellationToken).ConfigureAwait(false);
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

    private static IReadOnlyList<Cell> CompletenessDiffCells(
        uint rowIndex,
        CompletenessDiffAccount item,
        int moneyScale,
        CompletenessDiffCellStyles styles) =>
        [
            DirectBlankCell(rowIndex, 1, styles.LeadingBlank),
            DirectTextCell(rowIndex, 2, item.AccountCode, styles.AccountCode),
            DirectTextCell(rowIndex, 3, item.AccountName, styles.AccountName),
            DirectNumberCell(rowIndex, 4, Display(item.DiffScaled, moneyScale), styles.Difference),
            DirectBlankCell(rowIndex, 5, styles.Explanation),
            DirectBlankCell(rowIndex, 6, styles.Reconciliation),
            DirectBlankCell(rowIndex, 7, styles.RemainingDifference),
            DirectBlankCell(rowIndex, 8, styles.TrailingBlank)
        ];

    private static CompletenessDiffCellStyles ResolveCompletenessDiffCellStyles(
        DirectTemplateWorkbookEditor editor,
        Worksheet worksheet)
    {
        var columns = worksheet.GetFirstChild<Columns>()?.Elements<Column>().ToArray()
            ?? throw new InvalidDataException("Completeness explanation template has no columns.");

        uint Prototype(uint columnIndex)
        {
            var column = columns.SingleOrDefault(item =>
                item.Min?.Value <= columnIndex && item.Max?.Value >= columnIndex)
                ?? throw new InvalidDataException(
                    $"Completeness explanation template has no style for column {columnIndex}.");
            return column.Style?.Value ?? 0U;
        }

        return new CompletenessDiffCellStyles(
            editor.EnsureProtectionStyle(Prototype(1), locked: false),
            editor.EnsureProtectionStyle(Prototype(2), locked: true),
            editor.EnsureProtectionStyle(Prototype(3), locked: true),
            editor.EnsureProtectionStyle(Prototype(4), locked: true),
            editor.EnsureProtectionStyle(Prototype(5), locked: false),
            editor.EnsureProtectionStyle(Prototype(6), locked: false),
            editor.EnsureProtectionStyle(Prototype(7), locked: false),
            editor.EnsureProtectionStyle(Prototype(8), locked: false));
    }

    private sealed record CompletenessDiffCellStyles(
        uint LeadingBlank,
        uint AccountCode,
        uint AccountName,
        uint Difference,
        uint Explanation,
        uint Reconciliation,
        uint RemainingDifference,
        uint TrailingBlank);

    private static Cell DirectTextCell(
        uint row,
        uint column,
        string? value,
        uint style)
    {
        var cell = DirectBlankCell(row, column, style);
        if (!string.IsNullOrEmpty(value))
        {
            cell.DataType = CellValues.InlineString;
            cell.InlineString = new InlineString(
                new Text(value) { Space = SpaceProcessingModeValues.Preserve });
        }
        return cell;
    }

    private static Cell DirectNumberCell(
        uint row,
        uint column,
        decimal value,
        uint style) =>
        new()
        {
            CellReference = ReportSheetWriter.Reference(column, row),
            StyleIndex = style,
            DataType = CellValues.Number,
            CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture))
        };

    private static Cell DirectBlankCell(
        uint row,
        uint column,
        uint style) =>
        new()
        {
            CellReference = ReportSheetWriter.Reference(column, row),
            StyleIndex = style
        };

    private async Task EmitNullReportIfNeededAsync(
        SpreadsheetDocument document,
        Sheets sheets,
        List<SheetStat> stats,
        ReportDocumentContext context,
        NullRecordCategory category,
        string sheetName,
        long count,
        WorkbookStyleMap legacyStyles,
        RawSheetAutoFitMode autoFitMode,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        if (count <= 0)
        {
            return;
        }

        await EmitRawPagedSheetsAsync(
            document,
            sheets,
            stats,
            context.ProjectId,
            sheetName,
            (page, ct) => nullRecords.GetPageAsync(
                context.ProjectId, category, context.PeriodStart, context.PeriodEnd, page, ct),
            row => row.EntryId,
            legacyStyles,
            autoFitMode,
            cancellationToken,
            progress,
            RawSheetAppearanceMode.ExportDatabase);
    }

    private async Task EmitSourceQualitySheetsAsync(
        SpreadsheetDocument document,
        Sheets sheets,
        List<SheetStat> stats,
        string projectId,
        IReadOnlyList<SourceQualityFindingRow> compatibilityRows,
        bool requireCurrentPopulation,
        WorkbookStyleMap legacyStyles,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        const string baseName = "Source_Quality";
        string[] headers =
        [
            "CATEGORY",
            "SOURCE_ROW_NUMBER",
            "SOURCE_LABEL",
            "DOCUMENT_NUMBER",
            "ACCOUNT_CODE",
            "POST_DATE",
            "DESCRIPTION"
        ];
        if (requireCurrentPopulation && sourceQualityPages is null)
        {
            throw new InvalidOperationException(
                "Formal Validation Source_Quality export requires the source-quality page repository.");
        }

        ReportSheetStarted(stats, baseName, progress);
        using var spool = RawReportSpool.Create(headers);
        if (sourceQualityPages is null)
        {
            foreach (var row in compatibilityRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                spool.Append(SourceQualityValues(row));
            }
        }
        else
        {
            string? cursor = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await sourceQualityPages.GetPageAsync(
                    projectId,
                    new PageRequest(cursor, PageRequest.DefaultPageSize),
                    cancellationToken).ConfigureAwait(false);
                foreach (var row in page.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    spool.Append(SourceQualityValues(row));
                }
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        spool.Complete();

        var columns = headers.Select((header, index) => new ReportColumn(
            checked((uint)index + 1U),
            checked((uint)index + 1U),
            spool.Widths[index],
            BestFit: true)).ToArray();

        var part = 1;
        var rowIndex = 2U;
        var sheetName = SeriesName(baseName, part);
        ReportSheetWriter? sheet = OpenSourceQualitySheet(sheetName);
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
                    ReportSheetStarted(stats, sheetName, progress);
                    sheet = OpenSourceQualitySheet(sheetName);
                }

                sheet.WriteDataRow(
                    rowIndex,
                    values.Select((value, index) =>
                        sheet.TextCell(
                            rowIndex,
                            checked((uint)index + 1U),
                            value,
                            LegacyReportStyles.RawBody)).ToArray());
                rowIndex++;
                ReportIntermediateRows(stats, sheetName, rowIndex - 2, progress);
            }
            AddStat(stats, sheet.CloseAndSummarize(cancellationToken), progress);
            sheet.Dispose();
            sheet = null;
        }
        finally
        {
            sheet?.Dispose();
        }

        ReportSheetWriter OpenSourceQualitySheet(string name)
        {
            var writer = OpenSheet(
                document,
                sheets,
                name,
                columns,
                new ReportSheetOptions(PageMargins: StandardMargins, DefaultRowHeight: 12.5D),
                legacyStyles.Map);
            writer.WriteFixedRow(
                1,
                headers.Select((header, index) =>
                    writer.TextCell(
                        1,
                        checked((uint)index + 1U),
                        header,
                        LegacyReportStyles.RawHeader)).ToArray());
            return writer;
        }

        static IReadOnlyList<string?> SourceQualityValues(SourceQualityFindingRow row) =>
        [
            row.Category,
            row.SourceRowNumber.ToString(CultureInfo.InvariantCulture),
            row.SourceLabel,
            row.DocumentNumber,
            row.AccountCode,
            row.PostDate,
            row.Description
        ];
    }

    private async Task EmitCompletenessReportAsync(
        SpreadsheetDocument document,
        Sheets sheets,
        List<SheetStat> stats,
        ReportDocumentContext context,
        WorkbookStyleMap legacyStyles,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        string[] headers =
        [
            "ACCOUNT_NUM_ALL",
            "會計科目名稱_TB",
            "試算表變動金額_TB",
            "傳票金額_JE_SUM",
            "DIFF"
        ];
        using var spool = RawReportSpool.Create(headers);
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await completenessAccounts.GetPageAsync(
                context.ProjectId,
                context.MoneyScale,
                context.PeriodStart,
                context.PeriodEnd,
                new PageRequest(cursor, PageRequest.DefaultPageSize),
                cancellationToken);
            foreach (var item in page.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                spool.Append(
                [
                    item.AccountCode,
                    item.AccountName,
                    FormatAmountForAutoFit(item.TbAmountScaled, context.MoneyScale),
                    FormatAmountForAutoFit(item.GlAmountScaled, context.MoneyScale),
                    FormatAmountForAutoFit(item.DiffScaled, context.MoneyScale)
                ]);
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        spool.Complete();

        var part = 1;
        uint row = 2;
        var sheetName = SeriesName("V_Report 5", part);
        ReportSheetWriter? sheet = OpenCompletenessSheet(
            document,
            sheets,
            part,
            headers,
            spool.Widths,
            legacyStyles);
        try
        {
            uint[] columnStyles =
            [
                LegacyReportStyles.ExportDatabaseText,
                LegacyReportStyles.ExportDatabaseText,
                LegacyReportStyles.ExportDatabaseAmount,
                LegacyReportStyles.ExportDatabaseAmount,
                LegacyReportStyles.ExportDatabaseAmount
            ];
            foreach (var values in spool.ReadRows(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row > ExcelMaxRow)
                {
                    AddStat(stats, sheet.CloseAndSummarize(cancellationToken), progress);
                    sheet.Dispose();
                    part++;
                    row = 2;
                    sheetName = SeriesName("V_Report 5", part);
                    sheet = OpenCompletenessSheet(
                        document,
                        sheets,
                        part,
                        headers,
                        spool.Widths,
                        legacyStyles);
                }

                sheet.WriteDataRow(row,
                [
                    sheet.TextCell(row, 1, values[0], columnStyles[0]),
                    sheet.TextCell(row, 2, values[1], columnStyles[1]),
                    sheet.NumberCell(row, 3, ParseAutoFitAmount(values[2]), columnStyles[2]),
                    sheet.NumberCell(row, 4, ParseAutoFitAmount(values[3]), columnStyles[3]),
                    sheet.NumberCell(row, 5, ParseAutoFitAmount(values[4]), columnStyles[4])
                ], height: 18D);
                row++;
                ReportIntermediateRows(stats, sheetName, (long)row - 2, progress);
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

    private static string FormatAmountForAutoFit(long scaled, int moneyScale) =>
        Display(scaled, moneyScale).ToString(
            "#,##0.0000;-#,##0.0000",
            CultureInfo.InvariantCulture);

    private static decimal ParseAutoFitAmount(string? value) =>
        decimal.Parse(
            value ?? throw new InvalidDataException(
                "Completeness report spool is missing an amount."),
            NumberStyles.Number,
            CultureInfo.InvariantCulture);

    private static ReportSheetWriter OpenCompletenessSheet(
        SpreadsheetDocument document,
        Sheets sheets,
        int part,
        IReadOnlyList<string> headers,
        IReadOnlyList<double> widths,
        WorkbookStyleMap legacyStyles)
    {
        if (headers.Count != widths.Count)
        {
            throw new InvalidDataException(
                $"Completeness report width count {widths.Count} "
                + $"does not match header count {headers.Count}.");
        }

        var sheet = OpenSheet(document, sheets, SeriesName("V_Report 5", part),
            LegacyAutoFitColumns(
                widths,
                RawSheetAutoFitMode.LegacyFirstTwenty),
            new ReportSheetOptions(
                PageMargins: StandardMargins,
                DefaultRowHeight: 18D),
            legacyStyles.Map);
        uint[] headerStyles =
        [
            LegacyReportStyles.ExportDatabaseText,
            LegacyReportStyles.ExportDatabaseText,
            LegacyReportStyles.ExportDatabaseAmount,
            LegacyReportStyles.ExportDatabaseAmount,
            LegacyReportStyles.ExportDatabaseAmount
        ];
        sheet.WriteFixedRow(1, headers.Select((header, index) =>
                sheet.TextCell(1, checked((uint)index + 1U), header, headerStyles[index]))
            .ToArray(), height: 18D);
        return sheet;
    }

    private async Task EmitUnbalancedGlReportAsync(
        SpreadsheetDocument document,
        Sheets sheets,
        List<SheetStat> stats,
        ReportDocumentContext context,
        WorkbookStyleMap legacyStyles,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        // ideascript 4623-4631 將不平傳票清單回接 #GL# 並 IncludeAllPFields；
        // 因此 V_Report 6 必須是所有命中傳票的完整原始 GL 分錄，而非每傳票一列 aggregate。
        await EmitRawPagedSheetsAsync(
            document,
            sheets,
            stats,
            context.ProjectId,
            "V_Report 6",
            (page, ct) => unbalancedGlEntries.GetEntryIdsPageAsync(
                context.ProjectId, context.PeriodStart, context.PeriodEnd, page, ct),
            entryId => entryId,
            legacyStyles,
            RawSheetAutoFitMode.LegacyFirstTwenty,
            cancellationToken,
            progress);
    }
}
