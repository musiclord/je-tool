using System.Globalization;
using System.Text.Json;
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

    // idea-script.bas 7925：summary 第三欄整欄 WrapText，無條件套用。
    private static readonly WorkbookStylePatch PrescreenDescriptionColumnWrap =
        new(WrapText: true);

    public async Task<ExportStats> WriteAsync(
        Stream output,
        PrescreenReportContext context,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null)
    {
        // 保留 assembly 外 caller 的 public contract。Production export 會由
        // Application 提供 provider counts 與 finalized plan。
        using var summary = JsonDocument.Parse(context.SummaryJson);
        return await WriteTypedCoreAsync(
            output,
            context,
            ParseLegacyPrescreenProjection(summary.RootElement),
            "JET",
            cancellationToken,
            progress).ConfigureAwait(false);
    }

    Task<ExportStats> ITypedPrescreenReportWriter.WriteTypedAsync(
        Stream output,
        PrescreenReportContext context,
        PrescreenReportProjection projection,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress) =>
        WriteTypedCoreAsync(
            output,
            context,
            projection,
            operatorId,
            cancellationToken,
            progress);

    Task<ExportStats> IPlannedPrescreenReportWriter.WritePlannedAsync(
        Stream output,
        PrescreenReportContext context,
        PrescreenReportProjection projection,
        PrescreenReportPlan plan,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress) =>
        WritePlannedCoreAsync(
            output,
            context,
            projection,
            plan,
            workbookMetadata: null,
            operatorId,
            cancellationToken,
            progress);

    Task<ExportStats> IFormalPlannedPrescreenReportWriter.WriteFormalPlannedAsync(
        Stream output,
        PrescreenReportContext context,
        PrescreenReportProjection projection,
        PrescreenReportPlan plan,
        ReportWorkbookMetadata workbookMetadata,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress) =>
        WritePlannedCoreAsync(
            output,
            context,
            projection,
            plan,
            workbookMetadata,
            operatorId,
            cancellationToken,
            progress);

    private Task<ExportStats> WriteTypedCoreAsync(
        Stream output,
        PrescreenReportContext context,
        PrescreenReportProjection projection,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress) =>
        WritePlannedCoreAsync(
            output,
            context,
            projection,
            CompatibilityPrescreenPlan(context, projection),
            workbookMetadata: null,
            operatorId,
            cancellationToken,
            progress);

    private static PrescreenReportPlan CompatibilityPrescreenPlan(
        PrescreenReportContext context,
        PrescreenReportProjection projection)
    {
        var ruleContext = new FilterRuleContext(
            context.Project.MoneyScale,
            context.Project.LastPeriodStart,
            context.Project.PeriodStart,
            context.Project.PeriodEnd);
        var unfinalized = JetAuditProgram.Plan(new PrescreenReportRequest(
            context.Project.ProjectId,
            ruleContext,
            projection.PostPeriodApproval.NaReason,
            projection.SuspiciousKeywords.NaReason,
            projection.UnexpectedAccountPair.NaReason,
            projection.TrailingZeros.NaReason,
            projection.BlankDescription.NaReason));
        var facts = unfinalized.Details
            .Where(detail => detail.IsApplicable)
            .ToDictionary(
                detail => detail.Kind,
                detail =>
                {
                    var count = ProjectionFor(projection, detail.Kind).ItemCount;
                    return new PrescreenHitCounts(count, count);
                });
        return JetAuditProgram.Finalize(
            unfinalized,
            new PrescreenReportPlanningFacts(facts));
    }

    private async Task<ExportStats> WritePlannedCoreAsync(
        Stream output,
        PrescreenReportContext context,
        PrescreenReportProjection projection,
        PrescreenReportPlan plan,
        ReportWorkbookMetadata? workbookMetadata,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
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
                if (workbookMetadata is not null)
                {
                    ReportWorkbookMetadataWorksheet.Append(editor.Document, workbookMetadata, ct);
                }
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
        Action<WorkpaperProgress>? progress)
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
            PrescreenReportDetailKind.PostPeriodApproval,
            "R1",
            PrescreenRuleKeys.PostPeriodApproval,
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
        await EmitCreatorSummaryAsync(
            editor.Document,
            editor.Sheets,
            stats,
            context.Project,
            legacyStyles,
            cancellationToken,
            progress).ConfigureAwait(false);
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

        editor.Document.WorkbookPart!.Workbook.Save();
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
                AppendPrescreenSummaryRows(part.Worksheet);
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
                        context.Project.LastPeriodStart ?? "N/A",
                        StringComparison.Ordinal);
                var zeroText = ReadTemplateCellText(part, "C11")
                    + TrailingZeroThreshold.DefaultZerosThreshold
                        .ToString(CultureInfo.InvariantCulture);
                var cells = new DirectTemplateCellEditor(
                    part.Worksheet,
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
                    $"Prepared date: {context.GeneratedUtc:yyyy/MM/dd}");
                cells.SetInlineString("C8", closeDateText);
                cells.SetInlineString("C11", zeroText);

                SetPrescreenHitSummaryRow(
                    cells,
                    8,
                    projection.PostPeriodApproval,
                    plan.RequireDetail(
                        PrescreenReportDetailKind.PostPeriodApproval));
                SetPrescreenHitSummaryRow(
                    cells,
                    9,
                    projection.SuspiciousKeywords,
                    plan.RequireDetail(
                        PrescreenReportDetailKind.SuspiciousKeywords));
                SetPrescreenHitSummaryRow(
                    cells,
                    10,
                    projection.UnexpectedAccountPair,
                    plan.RequireDetail(
                        PrescreenReportDetailKind.UnexpectedAccountPair));
                SetPrescreenHitSummaryRow(
                    cells,
                    11,
                    projection.TrailingZeros,
                    plan.RequireDetail(
                        PrescreenReportDetailKind.TrailingZeros));
                SetPrescreenAggregateSummaryRow(
                    cells,
                    12,
                    projection.CreatorSummary,
                    "R5");
                SetPrescreenAggregateSummaryRow(
                    cells,
                    13,
                    projection.RareAccounts,
                    "R6");
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
                    projection.BlankDescription,
                    plan.RequireDetail(
                        PrescreenReportDetailKind.BlankDescription));
                cells.PatchColumn(3, PrescreenDescriptionColumnWrap);
                cells.EnsureAllAllowedCellsWereEdited();
                part.Worksheet.Save();
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
        PrescreenReportRuleProjection projection,
        PrescreenReportDetailPlan detail)
    {
        cells.SetInlineString($"A{row}", projection.Status);
        cells.SetInlineString($"D{row}", detail.Disposition switch
        {
            PrescreenReportDetailDisposition.NotApplicable =>
                $"N/A（{detail.NaReason ?? "前置條件不足，未執行"}）",
            PrescreenReportDetailDisposition.Omit => "沒有符合項目",
            PrescreenReportDetailDisposition.Emit =>
                $"符合 {detail.Counts!.RowHitCount:N0} 筆",
            PrescreenReportDetailDisposition.SummaryOnly =>
                PrescreenSummaryOnlyMessage,
            _ => throw new InvalidOperationException(
                $"Pre-screening detail '{detail.Kind}' 尚未 Finalize。")
        });

        if (detail.Counts is null)
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
        string sheetName)
    {
        cells.SetInlineString($"A{row}", projection.Status);
        cells.SetInlineString(
            $"D{row}",
            SummaryResultText(
                projection.ItemCount,
                sheetName,
                projection.NaReason));
        cells.SetInlineString($"E{row}", "N/A");
        cells.SetInlineString($"F{row}", "N/A");
    }

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
        var cell = worksheetPart.Worksheet.Descendants<Cell>()
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
        Action<WorkpaperProgress>? progress)
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

    private static PrescreenReportProjection ParseLegacyPrescreenProjection(
        JsonElement root) =>
        new(
            PostPeriodApproval: LegacyRule(
                root,
                "postPeriodApproval",
                RuleCount(root, "postPeriodApproval")),
            SuspiciousKeywords: LegacyRule(
                root,
                "suspiciousKeywords",
                RuleCount(root, "suspiciousKeywords")),
            UnexpectedAccountPair: LegacyRule(
                root,
                "unexpectedAccountPair",
                RuleCount(root, "unexpectedAccountPair")),
            TrailingZeros: LegacyRule(
                root,
                "trailingZeros",
                RuleCount(root, "trailingZeros")),
            // V9：摘要列寫完整人數，和第四步、[R5] 工作表一致；舊的預篩選結果沒有這個欄位時，沿用清單列數。
            CreatorSummary: LegacyRule(
                root,
                "creatorSummary",
                OptionalLong(root, "creatorSummary", "totalPreparerCount")
                    ?? ArrayLength(root, "creatorSummary", "creators")),
            RareAccounts: LegacyRule(
                root,
                "rareAccounts",
                GetLong(root.GetProperty("rareAccounts"), "distinctAccountCount")),
            BlankDescription: LegacyRule(
                root,
                "blankDescription",
                RuleCount(root, "blankDescription")));

    private static PrescreenReportRuleProjection LegacyRule(
        JsonElement root,
        string section,
        long itemCount = 0) =>
        new(
            Status: RuleStatus(root, section),
            NaReason: RuleNaReason(root, section),
            ItemCount: itemCount);

    private static PrescreenReportRuleProjection ProjectionFor(
        PrescreenReportProjection projection,
        PrescreenReportDetailKind kind) => kind switch
    {
        PrescreenReportDetailKind.PostPeriodApproval =>
            projection.PostPeriodApproval,
        PrescreenReportDetailKind.SuspiciousKeywords =>
            projection.SuspiciousKeywords,
        PrescreenReportDetailKind.UnexpectedAccountPair =>
            projection.UnexpectedAccountPair,
        PrescreenReportDetailKind.TrailingZeros =>
            projection.TrailingZeros,
        PrescreenReportDetailKind.BlankDescription =>
            projection.BlankDescription,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "未知的 Pre-screening detail kind。")
    };

    private static string SummaryResultText(
        long count,
        string sheetName,
        string? naReason) =>
        !string.IsNullOrWhiteSpace(naReason)
            ? $"N/A（{naReason}）"
            : $"彙總 {count:N0} 項；詳 [{sheetName}] 工作表";

    private static string? RuleNaReason(JsonElement root, string section)
    {
        return root.TryGetProperty(section, out var value)
            && value.TryGetProperty("naReason", out var reason)
            && reason.ValueKind == JsonValueKind.String
            ? reason.GetString()
            : null;
    }

    private async Task EmitCreatorSummaryAsync(
        SpreadsheetDocument document,
        Sheets sheets,
        List<SheetStat> stats,
        ReportDocumentContext context,
        WorkbookStyleMap styles,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var creators = await creatorSummaries.FetchAllAsync(
            context.ProjectId,
            context.PeriodStart,
            context.PeriodEnd,
            cancellationToken).ConfigureAwait(false);
        var widths = MeasureCreatorSummaryColumns(
            creators,
            context.MoneyScale,
            cancellationToken);
        var autoFitMode = creators.Count is > 0 and < 10_000
            ? RawSheetAutoFitMode.LegacyFirstTwenty
            : RawSheetAutoFitMode.None;
        var part = 1;
        uint row = 2;
        var sheetName = SeriesName("R5", part);
        ReportSheetWriter? sheet = OpenCreatorSummarySheet(
            document,
            sheets,
            part,
            widths,
            styles,
            autoFitMode);
        try
        {
            foreach (var item in creators)
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
                    sheet = OpenCreatorSummarySheet(
                        document,
                        sheets,
                        part,
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
                            CreatorSummaryDisplayName(item.CreatedBy),
                            LegacyReportStyles.ExportDatabaseText),
                        sheet.NumberCell(
                            row,
                            2,
                            item.EntryCount,
                            LegacyReportStyles.ExportDatabaseNumber(0)),
                        sheet.NumberCell(
                            row,
                            3,
                            Display(item.DebitTotalScaled, context.MoneyScale),
                            LegacyReportStyles.ExportDatabaseAmount),
                        sheet.NumberCell(
                            row,
                            4,
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

    private static ReportSheetWriter OpenCreatorSummarySheet(
        SpreadsheetDocument document,
        Sheets sheets,
        int part,
        IReadOnlyList<double> widths,
        WorkbookStyleMap styles,
        RawSheetAutoFitMode autoFitMode)
    {
        if (widths.Count != 4)
        {
            throw new InvalidDataException(
                $"R5 display width count must be 4, actual {widths.Count}.");
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
                DefaultRowHeight: 12.5D),
            styles.Map);
        string[] headers = ["CREATED_BY", "ENTRY_COUNT", "DEBIT_TOTAL", "CREDIT_TOTAL"];
        uint[] headerStyles =
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

    private static IReadOnlyList<double> MeasureCreatorSummaryColumns(
        IReadOnlyList<CreatorSummaryExportRow> creators,
        int moneyScale,
        CancellationToken cancellationToken)
    {
        var widths = new ExcelDisplayWidthTracker(
            ["CREATED_BY", "ENTRY_COUNT", "DEBIT_TOTAL", "CREDIT_TOTAL"]);
        foreach (var item in creators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            widths.Observe(
            [
                CreatorSummaryDisplayName(item.CreatedBy),
                item.EntryCount.ToString("N0", CultureInfo.InvariantCulture),
                FormatDisplayedAmount(item.DebitTotalScaled, moneyScale),
                FormatDisplayedAmount(item.CreditTotalScaled, moneyScale)
            ]);
        }
        return widths.Widths;
    }

    private static string CreatorSummaryDisplayName(string? createdBy) =>
        string.IsNullOrWhiteSpace(createdBy) ? "（空白）" : createdBy;

    private async Task EmitAccountUsageAsync(
        SpreadsheetDocument document,
        Sheets sheets,
        List<SheetStat> stats,
        ReportDocumentContext context,
        WorkbookStyleMap styles,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
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
                            item.AccountCode,
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
            || !mapping.Mapping.TryGetValue(GlMappingKeys.AccNum, out var accountCodeColumn)
            || string.IsNullOrWhiteSpace(accountCodeColumn)
            || !mapping.Mapping.TryGetValue(GlMappingKeys.AccName, out var accountNameColumn)
            || string.IsNullOrWhiteSpace(accountNameColumn))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "報告引用的 GL 科目欄位配對已不完整，請重新配對並執行預篩選後再產出報告。");
        }

        return
        [
            accountCodeColumn,
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
                DefaultRowHeight: 12.5D),
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
                item.AccountCode,
                item.AccountName,
                item.EntryCount.ToString("N0", CultureInfo.InvariantCulture),
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
