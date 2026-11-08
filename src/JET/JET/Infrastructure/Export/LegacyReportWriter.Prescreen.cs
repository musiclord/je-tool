using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class LegacyReportWriter
{
    private const string PrescreenSummaryOnlyMessage =
        "明細筆數超過10,000筆，明細資料不匯出";

    // 裁定 2026-11-05-44：摘要頁 D 欄照舊版 idea-tool.bas 第 8105 到 8806 行與第 9176 行的原句，「編制」也照舊版用字。
    private const string PrescreenNoHitMessage = "沒有符合預篩選條件之明細項";

    private const string PrescreenApprovalDateNotMappedMessage = "沒有設定傳票核准日欄位";

    private const string PrescreenPreparerNotMappedMessage =
        "因於資料欄未配對時未配對【編制人員】欄位，故此預篩選程序未執行";

    /// <summary>
    /// 「財報準備日起核准」只因傳票核准日不可用而不能執行時，第四步存下的原因句；
    /// 和這句相同時照舊版寫「沒有設定傳票核准日欄位」，其他原因（例如期末財報準備日留空）照 JET 寫出原因。
    /// </summary>
    private static readonly string ApprovalDateUnavailableReason = JetRuleRequirements.PrescreenSentences(
        [new JetRulePrerequisite(JetRulePrerequisiteKind.GlField, JetFieldCatalog.GlApprovalDate)],
        AccountCategoryLabels.BuiltIn);

    // idea-script.bas 7925：summary 第三欄整欄 WrapText，無條件套用。
    private static readonly WorkbookStylePatch PrescreenDescriptionColumnWrap =
        new(WrapText: true);

    Task<ExportStats> IPrescreenReportWriter.WriteAsync(
        Stream output,
        PrescreenReportContext context,
        PrescreenReportProjection projection,
        PrescreenReportPlan plan,
        ReportWorkbookMetadata workbookMetadata,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        ReportWorkbookMetadataInvariant.Validate(workbookMetadata);
        return WritePlannedCoreAsync(
            output,
            context,
            projection,
            plan,
            workbookMetadata,
            operatorId,
            cancellationToken,
            progress);
    }

    private async Task<ExportStats> WritePlannedCoreAsync(
        Stream output,
        PrescreenReportContext context,
        PrescreenReportProjection projection,
        PrescreenReportPlan plan,
        ReportWorkbookMetadata workbookMetadata,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsFinalized)
        {
            throw new InvalidOperationException(
                "Pre-screening workbook plan 必須先 Finalize。");
        }

        return await ReportTemplatePackage.FillDirectStreamingAsync(
            _templates,
            ReportTemplateCatalog.Prescreen,
            output,
            ["Pre-screening_Report"],
            async (editor, ct) =>
            {
                var stats = await FillPrescreenTemplateAsync(
                    editor,
                    context,
                    projection,
                    plan,
                    operatorId,
                    ct,
                    progress).ConfigureAwait(false);
                ReportWorkbookMetadataWorksheet.Append(editor.Document, workbookMetadata, ct);
                return stats;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SheetStat>> FillPrescreenTemplateAsync(
        DirectTemplateWorkbookEditor editor,
        PrescreenReportContext context,
        PrescreenReportProjection projection,
        PrescreenReportPlan plan,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var stats = new List<SheetStat>();
        var stylesPart = editor.Document.WorkbookPart?.WorkbookStylesPart
            ?? throw new InvalidDataException(
                "Pre-screening template has no styles part.");
        var legacyStyles = WorkbookStyleMap.Append(
            stylesPart,
            LegacyReportStyles.Build());

        EditPrescreenSummary(
            editor,
            context,
            projection,
            plan,
            operatorId,
            cancellationToken);
        AddStat(stats, new SheetStat("Pre-screening_Report", 0), progress);

        await EmitPrescreenRuleFromPlanAsync(
            editor.Document,
            editor.Sheets,
            stats,
            context.Project,
            plan,
            PrescreenReportDetailKind.PrepPeriodApproval,
            "R1",
            PrescreenRuleKeys.PrepPeriodApproval,
            legacyStyles,
            cancellationToken,
            progress).ConfigureAwait(false);
        await EmitPrescreenRuleFromPlanAsync(
            editor.Document,
            editor.Sheets,
            stats,
            context.Project,
            plan,
            PrescreenReportDetailKind.SuspiciousKeywords,
            "R2",
            PrescreenRuleKeys.SuspiciousKeywords,
            legacyStyles,
            cancellationToken,
            progress).ConfigureAwait(false);
        await EmitPrescreenRuleFromPlanAsync(
            editor.Document,
            editor.Sheets,
            stats,
            context.Project,
            plan,
            PrescreenReportDetailKind.UnexpectedAccountPair,
            "R3",
            PrescreenRuleKeys.UnexpectedAccountPair,
            legacyStyles,
            cancellationToken,
            progress).ConfigureAwait(false);
        await EmitPrescreenRuleFromPlanAsync(
            editor.Document,
            editor.Sheets,
            stats,
            context.Project,
            plan,
            PrescreenReportDetailKind.TrailingZeros,
            "R4",
            PrescreenRuleKeys.TrailingZeros,
            legacyStyles,
            cancellationToken,
            progress).ConfigureAwait(false);

        // R5／R6 是 legacy 的彙總表，不套用 10,000 列明細上限。
        // 裁定 2026-11-05-36：「依分錄編製者彙總」不適用時，照舊版不產生 R5 工作表。
        if (projection.PreparerSummary.NaReason is null)
        {
            await EmitPreparerSummaryAsync(
                editor.Document,
                editor.Sheets,
                stats,
                context.Project,
                legacyStyles,
                cancellationToken,
                progress).ConfigureAwait(false);
        }
        await EmitAccountUsageAsync(
            editor.Document,
            editor.Sheets,
            stats,
            context.Project,
            legacyStyles,
            cancellationToken,
            progress).ConfigureAwait(false);

        await EmitPrescreenRuleFromPlanAsync(
            editor.Document,
            editor.Sheets,
            stats,
            context.Project,
            plan,
            PrescreenReportDetailKind.BlankDescription,
            "R7",
            PrescreenRuleKeys.BlankDescription,
            legacyStyles,
            cancellationToken,
            progress).ConfigureAwait(false);

        editor.Document.WorkbookPart!.RequiredWorkbook().Save();
        return stats;
    }

    private static void EditPrescreenSummary(
        DirectTemplateWorkbookEditor editor,
        PrescreenReportContext context,
        PrescreenReportProjection projection,
        PrescreenReportPlan plan,
        string operatorId,
        CancellationToken cancellationToken)
    {
        editor.RewriteWorksheetPart(
            "Pre-screening_Report",
            (part, ct) =>
            {
                var worksheet = part.RequiredWorksheet();
                AppendPrescreenSummaryRows(worksheet);
                var allowed = new List<string>
                {
                    "E1", "E2", "E4", "E5", "C8", "C11",
                    "B14", "C14", "B15", "C15", "B16", "C16"
                };
                for (var row = 8; row <= 17; row++)
                {
                    allowed.Add($"A{row}");
                    allowed.Add($"D{row}");
                    allowed.Add($"E{row}");
                    allowed.Add($"F{row}");
                }

                var closeDateText = ReadTemplateCellText(part, "C8")
                    .Replace(
                        "xxxx/xx/xx",
                        context.Project.PrepStartDate ?? "N/A",
                        StringComparison.Ordinal);
                // 照 IDEA 版寫那次預篩選由 GL 借方平均算出的個數（裁定 2026-11-05-25），範本 C11 是「設定連續0的位數為：」。
                var zeroText = ReadTemplateCellText(part, "C11")
                    + projection.ZerosThreshold
                        .ToString(CultureInfo.InvariantCulture);
                var cells = new DirectTemplateCellEditor(
                    worksheet,
                    allowed,
                    editor.StylePatcher,
                    [3]);
                cells.SetInlineString(
                    "E1",
                    $"Client: {context.Project.CompanyName}");
                cells.SetInlineString(
                    "E2",
                    $"Year End: {context.Project.PeriodEnd}");
                cells.SetInlineString("E4", $"Prepared by: {operatorId}");
                cells.SetInlineString(
                    "E5",
                    string.Create(CultureInfo.InvariantCulture, $"Prepared date: {context.PreparedDate:yyyy/MM/dd}"));
                cells.SetInlineString("C8", closeDateText);
                cells.SetInlineString("C11", zeroText);

                SetPrescreenHitSummaryRow(
                    cells,
                    8,
                    "R1",
                    projection.PrepPeriodApproval,
                    plan.RequireDetail(
                        PrescreenReportDetailKind.PrepPeriodApproval));
                SetPrescreenHitSummaryRow(
                    cells,
                    9,
                    "R2",
                    projection.SuspiciousKeywords,
                    plan.RequireDetail(
                        PrescreenReportDetailKind.SuspiciousKeywords));
                SetPrescreenHitSummaryRow(
                    cells,
                    10,
                    "R3",
                    projection.UnexpectedAccountPair,
                    plan.RequireDetail(
                        PrescreenReportDetailKind.UnexpectedAccountPair));
                SetPrescreenHitSummaryRow(
                    cells,
                    11,
                    "R4",
                    projection.TrailingZeros,
                    plan.RequireDetail(
                        PrescreenReportDetailKind.TrailingZeros));
                SetPrescreenAggregateSummaryRow(
                    cells,
                    12,
                    projection.PreparerSummary,
                    "R5",
                    PrescreenPreparerNotMappedMessage);
                SetPrescreenAggregateSummaryRow(
                    cells,
                    13,
                    projection.RareAccounts,
                    "R6",
                    notApplicableMessage: null);
                SetRetiredPrescreenSummaryRow(
                    cells,
                    14,
                    "A2",
                    "自訂特定摘要（移至 Step 4）");
                SetRetiredPrescreenSummaryRow(
                    cells,
                    15,
                    "A3",
                    "自訂科目借貸組合（移至 Step 4）");
                SetRetiredPrescreenSummaryRow(
                    cells,
                    16,
                    "A4",
                    "自訂特定尾數（移至 Step 4）");
                SetPrescreenHitSummaryRow(
                    cells,
                    17,
                    "R7",
                    projection.BlankDescription,
                    plan.RequireDetail(
                        PrescreenReportDetailKind.BlankDescription));
                cells.PatchColumn(3, PrescreenDescriptionColumnWrap);
                cells.EnsureAllAllowedCellsWereEdited();
                ExcelText.SaveWorksheet(part);
                ct.ThrowIfCancellationRequested();
            },
            cancellationToken);
    }

    private static void AppendPrescreenSummaryRows(Worksheet worksheet)
    {
        var sheetData = worksheet.GetFirstChild<SheetData>()
            ?? throw new InvalidDataException(
                "Pre-screening template has no sheetData.");
        if (sheetData.Elements<Row>().Any(row =>
                row.RowIndex?.Value is >= 15 and <= 17))
        {
            throw new InvalidDataException(
                "Pre-screening template unexpectedly already contains rows 15–17.");
        }

        var source = sheetData.Elements<Row>()
            .Single(row => row.RowIndex?.Value == 14);
        foreach (var targetRowIndex in new uint[] { 15, 16, 17 })
        {
            var clone = (Row)source.CloneNode(true);
            clone.RowIndex = targetRowIndex;
            foreach (var cell in clone.Elements<Cell>())
            {
                var reference = cell.CellReference?.Value
                    ?? throw new InvalidDataException(
                        "Pre-screening row 14 has a cell without reference.");
                var column = new string(reference
                    .TakeWhile(character => !char.IsDigit(character))
                    .ToArray());
                cell.CellReference = $"{column}{targetRowIndex}";
            }
            sheetData.Append(clone);
        }

        var dimension = worksheet.GetFirstChild<SheetDimension>();
        if (dimension is not null)
        {
            dimension.Reference = "A1:F17";
        }
    }

    private static void SetPrescreenHitSummaryRow(
        DirectTemplateCellEditor cells,
        int row,
        string sheetName,
        PrescreenReportRuleProjection projection,
        PrescreenReportDetailPlan detail)
    {
        cells.SetInlineString($"A{row}", projection.Status);
        cells.SetInlineString($"D{row}", detail.Disposition switch
        {
            PrescreenReportDetailDisposition.NotApplicable =>
                NotApplicableResultText(detail),
            PrescreenReportDetailDisposition.Omit => PrescreenNoHitMessage,
            PrescreenReportDetailDisposition.Emit => DetailSheetResultText(detail.Kind, sheetName),
            PrescreenReportDetailDisposition.SummaryOnly =>
                PrescreenSummaryOnlyMessage,
            _ => throw new InvalidOperationException(
                $"Pre-screening detail '{detail.Kind}' 尚未 Finalize。")
        });

        // 裁定 2026-11-05-44：零筆時照舊版沒有明細工作表，E、F 欄寫 N/A。
        if (detail.Counts is null || detail.Counts.RowHitCount == 0)
        {
            cells.SetInlineString($"E{row}", "N/A");
            cells.SetInlineString($"F{row}", "N/A");
            return;
        }

        cells.SetNumber($"E{row}", detail.Counts.VoucherHitCount);
        cells.SetNumber($"F{row}", detail.Counts.RowHitCount);
    }

    private static void SetPrescreenAggregateSummaryRow(
        DirectTemplateCellEditor cells,
        int row,
        PrescreenReportRuleProjection projection,
        string sheetName,
        string? notApplicableMessage)
    {
        cells.SetInlineString($"A{row}", projection.Status);
        cells.SetInlineString(
            $"D{row}",
            projection.NaReason is not { } naReason
                ? DetailSheetResultText(null, sheetName)
                : notApplicableMessage ?? $"N/A（{naReason}）");
        cells.SetInlineString($"E{row}", "N/A");
        cells.SetInlineString($"F{row}", "N/A");
    }

    // 舊版有明細工作表時寫「符合預篩選條件之明細項請詳 [Rn] 工作表」；空白摘要是舊版另外加的規則，句子多「新增」兩個字。
    private static string DetailSheetResultText(PrescreenReportDetailKind? kind, string sheetName) =>
        kind == PrescreenReportDetailKind.BlankDescription
            ? $"符合新增預篩選條件之明細項請詳 [{sheetName}] 工作表"
            : $"符合預篩選條件之明細項請詳 [{sheetName}] 工作表";

    private static string NotApplicableResultText(PrescreenReportDetailPlan detail) =>
        detail.Kind == PrescreenReportDetailKind.PrepPeriodApproval
        && string.Equals(detail.NaReason, ApprovalDateUnavailableReason, StringComparison.Ordinal)
            ? PrescreenApprovalDateNotMappedMessage
            : $"N/A（{detail.NaReason ?? "前置條件不足，未執行"}）";

    private static void SetRetiredPrescreenSummaryRow(
        DirectTemplateCellEditor cells,
        int row,
        string code,
        string description)
    {
        cells.SetInlineString($"A{row}", "N/A");
        cells.SetInlineString($"B{row}", code);
        cells.SetInlineString($"C{row}", description);
        cells.SetInlineString(
            $"D{row}",
            "N/A（現行 JET 已移至 Step 4）");
        cells.SetInlineString($"E{row}", "N/A");
        cells.SetInlineString($"F{row}", "N/A");
    }

    private static string ReadTemplateCellText(
        WorksheetPart worksheetPart,
        string reference)
    {
        var cell = worksheetPart.RequiredWorksheet().Descendants<Cell>()
            .Single(item => string.Equals(
                item.CellReference?.Value,
                reference,
                StringComparison.OrdinalIgnoreCase));
        if (cell.InlineString is not null)
        {
            return cell.InlineString.InnerText;
        }
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(
                cell.CellValue?.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index))
        {
            var workbookPart = worksheetPart.GetParentParts()
                .OfType<WorkbookPart>()
                .Single();
            return workbookPart.SharedStringTablePart?.SharedStringTable?
                .Elements<SharedStringItem>()
                .ElementAt(index)
                .InnerText
                ?? string.Empty;
        }

        return cell.CellValue?.Text ?? string.Empty;
    }

    private async Task EmitPrescreenRuleFromPlanAsync(
        SpreadsheetDocument document,
        Sheets sheets,
        List<SheetStat> stats,
        ReportDocumentContext context,
        PrescreenReportPlan plan,
        PrescreenReportDetailKind kind,
        string sheetName,
        string ruleKey,
        WorkbookStyleMap styles,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var detail = plan.RequireDetail(kind);
        if (!detail.Emit)
        {
            return;
        }

        await EmitRawPagedSheetsAsync(
            document,
            sheets,
            stats,
            context.ProjectId,
            sheetName,
            (page, ct) => prescreenPages.GetPageAsync(
                context.ProjectId,
                ruleKey,
                plan.Request.RuleContext,
                page,
                ct),
            row => row.EntryId,
            styles,
            RawSheetAutoFitMode.LegacyFirstTwenty,
            cancellationToken,
            progress).ConfigureAwait(false);
    }

    private async Task EmitPreparerSummaryAsync(
        SpreadsheetDocument document,
        Sheets sheets,
        List<SheetStat> stats,
        ReportDocumentContext context,
        WorkbookStyleMap styles,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var preparers = await preparerSummaries.FetchAllAsync(
            context.ProjectId,
            context.PeriodStart,
            context.PeriodEnd,
            cancellationToken).ConfigureAwait(false);
        // 裁定 2026-11-05-32：配對了人工傳票欄位時，照舊版在人員後面多一欄人工旗標，同一位人員依人工旗標分列。
        var includeManual = await IsManualFlagMappedAsync(
            context.ProjectId,
            cancellationToken).ConfigureAwait(false);
        var widths = MeasurePreparerSummaryColumns(
            preparers,
            context.MoneyScale,
            includeManual,
            cancellationToken);
        var autoFitMode = preparers.Count is > 0 and < 10_000
            ? RawSheetAutoFitMode.LegacyFirstTwenty
            : RawSheetAutoFitMode.None;
        var part = 1;
        uint row = 2;
        var sheetName = SeriesName("R5", part);
        ReportSheetWriter? sheet = OpenPreparerSummarySheet(
            document,
            sheets,
            part,
            widths,
            includeManual,
            styles,
            autoFitMode);
        try
        {
            foreach (var item in preparers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row > _continuationRowLimit)
                {
                    AddStat(
                        stats,
                        sheet.CloseAndSummarize(cancellationToken),
                        progress);
                    sheet.Dispose();
                    part++;
                    row = 2;
                    sheetName = SeriesName("R5", part);
                    sheet = OpenPreparerSummarySheet(
                        document,
                        sheets,
                        part,
                        widths,
                        includeManual,
                        styles,
                        autoFitMode);
                }
                sheet.WriteDataRow(
                    row,
                    PreparerSummaryCells(sheet, row, item, includeManual, context.MoneyScale));
                row++;
                ReportIntermediateRows(
                    stats,
                    sheetName,
                    (long)row - 2,
                    progress);
            }
            AddStat(
                stats,
                sheet.CloseAndSummarize(cancellationToken),
                progress);
            sheet.Dispose();
            sheet = null;
        }
        finally
        {
            sheet?.Dispose();
        }
    }

    private static ReportSheetWriter OpenPreparerSummarySheet(
        SpreadsheetDocument document,
        Sheets sheets,
        int part,
        IReadOnlyList<double> widths,
        bool includeManual,
        WorkbookStyleMap styles,
        RawSheetAutoFitMode autoFitMode)
    {
        var headers = PreparerSummaryHeaders(includeManual);
        if (widths.Count != headers.Count)
        {
            throw new InvalidDataException(
                $"R5 display width count must be {headers.Count}, actual {widths.Count}.");
        }
        var sheet = OpenSheet(
            document,
            sheets,
            SeriesName("R5", part),
            LegacyAutoFitColumns(
                widths,
                autoFitMode),
            new ReportSheetOptions(
                PageMargins: StandardMargins,
                DefaultRowHeight: LegacyReportStyles.ExportDatabaseRowHeight),
            styles.Map);
        uint[] headerStyles = includeManual
            ?
            [
                LegacyReportStyles.ExportDatabaseText,
                LegacyReportStyles.ExportDatabaseText,
                LegacyReportStyles.ExportDatabaseNumber(0),
                LegacyReportStyles.ExportDatabaseAmount,
                LegacyReportStyles.ExportDatabaseAmount
            ]
            :
            [
                LegacyReportStyles.ExportDatabaseText,
                LegacyReportStyles.ExportDatabaseNumber(0),
                LegacyReportStyles.ExportDatabaseAmount,
                LegacyReportStyles.ExportDatabaseAmount
            ];
        sheet.WriteFixedRow(1, headers.Select((header, index) =>
                sheet.TextCell(1, checked((uint)index + 1U), header, headerStyles[index]))
            .ToArray());
        return sheet;
    }

    // 人工旗標欄的表頭沿用欄位目錄給報告與底稿的名稱「人工傳票否_JE_S」，和舊版 R5 的表頭相同。
    private static IReadOnlyList<string> PreparerSummaryHeaders(bool includeManual) =>
        includeManual
            ? ["CREATED_BY", ManualFieldReportName(), "ENTRY_COUNT", "DEBIT_TOTAL", "CREDIT_TOTAL"]
            : ["CREATED_BY", "ENTRY_COUNT", "DEBIT_TOTAL", "CREDIT_TOTAL"];

    private static string ManualFieldReportName() =>
        JetFieldCatalog.FindGlFieldBySemanticIdentity(JetFieldCatalog.GlManual)?.LegacyFieldName
            ?? throw new InvalidOperationException("欄位目錄沒有人工傳票欄位的報告名稱。");

    private static IReadOnlyList<Cell> PreparerSummaryCells(
        ReportSheetWriter sheet,
        uint row,
        PreparerSummaryExportRow item,
        bool includeManual,
        int moneyScale)
    {
        var cells = new List<Cell>(5)
        {
            sheet.TextCell(
                row,
                1,
                PreparerSummaryDisplay.NameOf(item.CreatedBy),
                LegacyReportStyles.ExportDatabaseText)
        };
        if (includeManual)
        {
            cells.Add(sheet.TextCell(
                row,
                2,
                item.IsManual is { } isManual ? PreparerSummaryDisplay.ManualText(isManual) : null,
                LegacyReportStyles.ExportDatabaseText));
        }
        var countColumn = includeManual ? 3U : 2U;
        cells.Add(sheet.NumberCell(
            row,
            countColumn,
            item.EntryCount,
            LegacyReportStyles.ExportDatabaseNumber(0)));
        cells.Add(sheet.NumberCell(
            row,
            countColumn + 1U,
            Display(item.DebitTotalScaled, moneyScale),
            LegacyReportStyles.ExportDatabaseAmount));
        cells.Add(sheet.NumberCell(
            row,
            countColumn + 2U,
            Display(item.CreditTotalScaled, moneyScale),
            LegacyReportStyles.ExportDatabaseAmount));
        return cells;
    }

    private static IReadOnlyList<double> MeasurePreparerSummaryColumns(
        IReadOnlyList<PreparerSummaryExportRow> preparers,
        int moneyScale,
        bool includeManual,
        CancellationToken cancellationToken)
    {
        var widths = new ExcelDisplayWidthTracker(PreparerSummaryHeaders(includeManual).ToArray());
        foreach (var item in preparers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string?[] values = includeManual
                ?
                [
                    PreparerSummaryDisplay.NameOf(item.CreatedBy),
                    item.IsManual is { } isManual ? PreparerSummaryDisplay.ManualText(isManual) : null,
                    CountText.Format(item.EntryCount),
                    FormatDisplayedAmount(item.DebitTotalScaled, moneyScale),
                    FormatDisplayedAmount(item.CreditTotalScaled, moneyScale)
                ]
                :
                [
                    PreparerSummaryDisplay.NameOf(item.CreatedBy),
                    CountText.Format(item.EntryCount),
                    FormatDisplayedAmount(item.DebitTotalScaled, moneyScale),
                    FormatDisplayedAmount(item.CreditTotalScaled, moneyScale)
                ];
            widths.Observe(values);
        }
        return widths.Widths;
    }

    private async Task<bool> IsManualFlagMappedAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var mapping = await mappings.FindAsync(
            projectId,
            DatasetKind.Gl,
            cancellationToken).ConfigureAwait(false);
        return mapping is not null
            && mapping.Mapping.TryGetValue(GlMappingKeys.IsManual, out var manualColumn)
            && !string.IsNullOrWhiteSpace(manualColumn);
    }

    private async Task EmitAccountUsageAsync(
        SpreadsheetDocument document,
        Sheets sheets,
        List<SheetStat> stats,
        ReportDocumentContext context,
        WorkbookStyleMap styles,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var headers = await ResolveAccountUsageHeadersAsync(
            context.ProjectId,
            cancellationToken).ConfigureAwait(false);
        var accounts = await accountUsage.FetchAllAsync(
            context.ProjectId,
            context.PeriodStart,
            context.PeriodEnd,
            cancellationToken).ConfigureAwait(false);
        var widths = MeasureAccountUsageColumns(
            accounts,
            headers,
            context.MoneyScale,
            cancellationToken);
        var autoFitMode = accounts.Count is > 0 and < 10_000
            ? RawSheetAutoFitMode.LegacyFirstTwenty
            : RawSheetAutoFitMode.None;
        var part = 1;
        uint row = 2;
        var sheetName = SeriesName("R6", part);
        ReportSheetWriter? sheet = OpenAccountUsageSheet(
            document,
            sheets,
            part,
            headers,
            widths,
            styles,
            autoFitMode);
        try
        {
            foreach (var item in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row > _continuationRowLimit)
                {
                    AddStat(
                        stats,
                        sheet.CloseAndSummarize(cancellationToken),
                        progress);
                    sheet.Dispose();
                    part++;
                    row = 2;
                    sheetName = SeriesName("R6", part);
                    sheet = OpenAccountUsageSheet(
                        document,
                        sheets,
                        part,
                        headers,
                        widths,
                        styles,
                        autoFitMode);
                }
                sheet.WriteDataRow(
                    row,
                    [
                        sheet.TextCell(
                            row,
                            1,
                            item.AccountNumber,
                            LegacyReportStyles.ExportDatabaseText),
                        sheet.TextCell(
                            row,
                            2,
                            item.AccountName,
                            LegacyReportStyles.ExportDatabaseText),
                        sheet.NumberCell(
                            row,
                            3,
                            item.EntryCount,
                            LegacyReportStyles.ExportDatabaseNumber(0)),
                        sheet.NumberCell(
                            row,
                            4,
                            Display(item.DebitTotalScaled, context.MoneyScale),
                            LegacyReportStyles.ExportDatabaseAmount),
                        sheet.NumberCell(
                            row,
                            5,
                            Display(item.CreditTotalScaled, context.MoneyScale),
                            LegacyReportStyles.ExportDatabaseAmount)
                    ]);
                row++;
                ReportIntermediateRows(
                    stats,
                    sheetName,
                    (long)row - 2,
                    progress);
            }
            AddStat(
                stats,
                sheet.CloseAndSummarize(cancellationToken),
                progress);
            sheet.Dispose();
            sheet = null;
        }
        finally
        {
            sheet?.Dispose();
        }
    }

    /// <summary>
    /// master spec「正式底稿欄位來源忠實性」：R6 取自 GL 來源欄位的標題必須沿用與 R2
    /// 相同的 schema lineage（實際配對的來源欄名），不得改寫成固定 alias；彙總結果本身
    /// 的衍生欄位維持既有輸出契約名稱。沒有 committed GL mapping 時 fail closed。
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveAccountUsageHeadersAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var mapping = await mappings.FindAsync(
            projectId,
            DatasetKind.Gl,
            cancellationToken).ConfigureAwait(false);
        if (mapping is null
            || !mapping.Mapping.TryGetValue(GlMappingKeys.AccountNumber, out var accountNumberColumn)
            || string.IsNullOrWhiteSpace(accountNumberColumn)
            || !mapping.Mapping.TryGetValue(GlMappingKeys.AccountName, out var accountNameColumn)
            || string.IsNullOrWhiteSpace(accountNameColumn))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "報告引用的 GL 科目欄位配對已不完整，請重新配對並執行預篩選後再產出報告。");
        }

        return
        [
            accountNumberColumn,
            accountNameColumn,
            "ENTRY_COUNT",
            "DEBIT_TOTAL",
            "CREDIT_TOTAL"
        ];
    }

    private static ReportSheetWriter OpenAccountUsageSheet(
        SpreadsheetDocument document,
        Sheets sheets,
        int part,
        IReadOnlyList<string> headers,
        IReadOnlyList<double> widths,
        WorkbookStyleMap styles,
        RawSheetAutoFitMode autoFitMode)
    {
        if (widths.Count != 5)
        {
            throw new InvalidDataException(
                $"R6 display width count must be 5, actual {widths.Count}.");
        }
        if (headers.Count != 5)
        {
            throw new InvalidDataException(
                $"R6 header count must be 5, actual {headers.Count}.");
        }
        var sheet = OpenSheet(
            document,
            sheets,
            SeriesName("R6", part),
            LegacyAutoFitColumns(
                widths,
                autoFitMode),
            new ReportSheetOptions(
                PageMargins: StandardMargins,
                DefaultRowHeight: LegacyReportStyles.ExportDatabaseRowHeight),
            styles.Map);
        uint[] headerStyles =
        [
            LegacyReportStyles.ExportDatabaseText,
            LegacyReportStyles.ExportDatabaseText,
            LegacyReportStyles.ExportDatabaseNumber(0),
            LegacyReportStyles.ExportDatabaseAmount,
            LegacyReportStyles.ExportDatabaseAmount
        ];
        sheet.WriteFixedRow(1, headers.Select((header, index) =>
                sheet.TextCell(1, checked((uint)index + 1U), header, headerStyles[index]))
            .ToArray());
        return sheet;
    }

    private static IReadOnlyList<double> MeasureAccountUsageColumns(
        IReadOnlyList<AccountUsageExportRow> accounts,
        IReadOnlyList<string> headers,
        int moneyScale,
        CancellationToken cancellationToken)
    {
        var widths = new ExcelDisplayWidthTracker(headers);
        foreach (var item in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            widths.Observe(
            [
                item.AccountNumber,
                item.AccountName,
                CountText.Format(item.EntryCount),
                FormatDisplayedAmount(item.DebitTotalScaled, moneyScale),
                FormatDisplayedAmount(item.CreditTotalScaled, moneyScale)
            ]);
        }
        return widths.Widths;
    }

    private static string FormatDisplayedAmount(long scaled, int moneyScale) =>
        Display(scaled, moneyScale)
            .ToString("#,##0.0000;-#,##0.0000", CultureInfo.InvariantCulture);
}
