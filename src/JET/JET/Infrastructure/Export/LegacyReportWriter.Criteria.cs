using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class LegacyReportWriter
{
    public async Task<ExportStats> WriteAsync(
        Stream output,
        CriteriaSelectionReportContext context,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null)
    {
        return await WriteCriteriaDirectAsync(
            output,
            context,
            workbookMetadata: null,
            customFields: [],
            "JET",
            cancellationToken,
            progress).ConfigureAwait(false);
    }

    Task<ExportStats> ITypedCriteriaSelectionReportWriter.WriteTypedAsync(
        Stream output,
        CriteriaSelectionReportContext context,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress) =>
        WriteCriteriaDirectAsync(
            output,
            context,
            workbookMetadata: null,
            customFields: [],
            operatorId,
            cancellationToken,
            progress);

    Task<ExportStats> IFormalCriteriaSelectionReportWriter.WriteFormalAsync(
        Stream output,
        CriteriaSelectionReportContext context,
        ReportWorkbookMetadata workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress) =>
        WriteCriteriaDirectAsync(
            output,
            context,
            workbookMetadata,
            ReportWorkbookMetadataInvariant.ValidateCustomFields(workbookMetadata, customFields),
            operatorId,
            cancellationToken,
            progress);

    private Task<ExportStats> WriteCriteriaDirectAsync(
        Stream output,
        CriteriaSelectionReportContext context,
        ReportWorkbookMetadata? workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress) =>
        ReportTemplatePackage.FillDirectStreamingAsync(
            _templates,
            ReportTemplateCatalog.CriteriaSelection,
            output,
            ["Summary Inforamtion"],
            (editor, ct) => FillCriteriaTemplateAsync(
                editor,
                context,
                workbookMetadata,
                customFields,
                operatorId,
                ct,
                progress),
            cancellationToken);

    private async Task<IReadOnlyList<SheetStat>> FillCriteriaTemplateAsync(
        DirectTemplateWorkbookEditor editor,
        CriteriaSelectionReportContext context,
        ReportWorkbookMetadata? workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var stats = new List<SheetStat>();
        var counts = await tagMatrixCounts.GetCountsAsync(
            context.Project.ProjectId,
            cancellationToken).ConfigureAwait(false);
        var blankDocumentHitRows = await tagMatrixCounts.GetBlankDocumentHitRowCountsAsync(
            context.Project.ProjectId,
            cancellationToken).ConfigureAwait(false);
        var stylesPart = editor.Document.WorkbookPart?.WorkbookStylesPart
            ?? throw new InvalidDataException(
                "Criteria Selection template has no styles part.");
        var legacyStyles = WorkbookStyleMap.Append(
            stylesPart,
            LegacyReportStyles.Build());

        AddStat(stats, new SheetStat("Summary Inforamtion", 0), progress);
        var exportedRowCounts = new Dictionary<int, long>();
        foreach (var scenario in context.Scenarios
                     .OrderBy(item => item.Position))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var firstScenarioStatIndex = stats.Count;
            await EmitRawPagedSheetsAsync(
                editor.Document,
                editor.Sheets,
                stats,
                context.Project.ProjectId,
                $"#Criteria Select {scenario.Position}",
                (page, ct) => ReadCriteriaVoucherRowsAsync(
                    context,
                    scenario.Position,
                    page,
                    ct),
                entryId => entryId,
                legacyStyles,
                // Resolve the legacy threshold from the full-voucher rows
                // already counted by the disk spool, never from hit-row counts.
                RawSheetAutoFitMode.LegacyFirstTwentyWhenAtMostOneMillion,
                cancellationToken,
                progress,
                customFields: customFields,
                registryFields: workbookMetadata?.GlMapping.GlOptions?.RdeFields,
                customFieldMoneyScale: context.Project.MoneyScale).ConfigureAwait(false);
            exportedRowCounts[scenario.Position] = stats
                .Skip(firstScenarioStatIndex)
                .Sum(item => item.RowsWritten);
        }

        EditCriteriaSummary(
            editor,
            context,
            counts,
            blankDocumentHitRows,
            exportedRowCounts,
            operatorId,
            cancellationToken);
        if (workbookMetadata is not null)
        {
            ReportWorkbookMetadataWorksheet.Append(editor.Document, workbookMetadata, cancellationToken);
        }
        editor.Document.WorkbookPart!.Workbook.Save();
        return stats;
    }

    private async Task<PageResult<long>> ReadCriteriaVoucherRowsAsync(
        CriteriaSelectionReportContext context,
        int scenarioPosition,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        if (tagMatrixRows is null)
        {
            throw new InvalidOperationException(
                "Criteria Selection whole-voucher export requires the tag-matrix row port.");
        }
        var population = new GlPopulationContext(
            context.PopulationScope,
            context.Project.PeriodStart,
            context.Project.PeriodEnd);
        var result = await tagMatrixRows.GetPageAsync(
            context.Project.ProjectId,
            population,
            page,
            [scenarioPosition],
            cancellationToken).ConfigureAwait(false);
        return new PageResult<long>(
            result.EntryIds,
            result.Page.NextCursor);
    }

    private static void EditCriteriaSummary(
        DirectTemplateWorkbookEditor editor,
        CriteriaSelectionReportContext context,
        IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)> counts,
        IReadOnlyDictionary<int, long> blankDocumentHitRows,
        IReadOnlyDictionary<int, long> exportedRowCounts,
        string operatorId,
        CancellationToken cancellationToken)
    {
        editor.RewriteWorksheetPart(
            "Summary Inforamtion",
            (part, ct) =>
            {
                if (!string.Equals(
                        ReadTemplateCellText(part, "B4"),
                        "條件的內容",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Criteria Selection template B4 contract changed.");
                }

                var allowed = new List<string>
                {
                    "C1", "C2", "D1", "D2",
                    "A4", "C4", "D4"
                };
                for (var row = 5; row <= 14; row++)
                {
                    allowed.Add($"A{row}");
                    allowed.Add($"B{row}");
                    allowed.Add($"C{row}");
                    allowed.Add($"D{row}");
                }

                var cells = new DirectTemplateCellEditor(
                    part.Worksheet,
                    allowed);
                cells.SetInlineString(
                    "C1",
                    $"Client: {context.Project.CompanyName}");
                cells.SetInlineString(
                    "C2",
                    $"Year End: {context.Project.PeriodEnd}");
                cells.SetInlineString("D1", $"Prepared by: {operatorId}");
                cells.SetInlineString(
                    "D2",
                    string.Create(CultureInfo.InvariantCulture, $"Prepared date: {context.GeneratedUtc:yyyy/MM/dd}"));
                cells.Clear("A4");
                cells.Clear("C4");
                cells.Clear("D4");

                var scenarios = context.Scenarios
                    .ToDictionary(item => item.Position);
                var sheetData = part.Worksheet.GetFirstChild<SheetData>()!;
                var nextConditionRow = Math.Max(16u, sheetData.Elements<Row>().Max(row => row.RowIndex?.Value ?? 0) + 2);
                // 情境列下方的接續列：過長條件的分段與空白傳票號碼的說明，都沿用該情境 B 欄的格式。
                void AppendContinuationRow(string label, string text, uint? style)
                {
                    sheetData.Append(new Row(
                        new Cell { CellReference = $"A{nextConditionRow}", DataType = CellValues.InlineString,
                            StyleIndex = style, InlineString = new InlineString(new Text(label)) },
                        new Cell { CellReference = $"B{nextConditionRow}", DataType = CellValues.InlineString,
                            StyleIndex = style, InlineString = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve }) })
                        { RowIndex = nextConditionRow });
                    nextConditionRow++;
                    var dimension = part.Worksheet.GetFirstChild<SheetDimension>();
                    if (dimension is not null) dimension.Reference = $"A1:D{nextConditionRow - 1}";
                }

                for (var position = 1; position <= 10; position++)
                {
                    var row = position + 4;
                    if (!scenarios.TryGetValue(position, out var scenario))
                    {
                        cells.Clear($"A{row}");
                        cells.Clear($"B{row}");
                        cells.Clear($"C{row}");
                        cells.Clear($"D{row}");
                        continue;
                    }

                    var count = counts.GetValueOrDefault(position);
                    string? conditionLogic = null;
                    context.ScenarioConditionLogic?.TryGetValue(
                        position,
                        out conditionLogic);
                    cells.SetInlineString(
                        $"A{row}",
                        $"Criteria Selection {position}");
                    var conditionParts = FilterConditionExcelText.Parts(conditionLogic ?? scenario.Name);
                    cells.SetInlineString($"B{row}", conditionParts.Count == 1 ? conditionParts[0]
                        : $"完整條件見本頁第 {nextConditionRow} 列起，分段接續列出。");
                    var blankDocumentRows = blankDocumentHitRows.GetValueOrDefault(position);
                    if (conditionParts.Count > 1 || blankDocumentRows > 0)
                    {
                        var style = part.Worksheet.Descendants<Cell>().Single(cell => cell.CellReference?.Value == $"B{row}").StyleIndex?.Value;
                        if (conditionParts.Count > 1)
                        {
                            foreach (var conditionPart in conditionParts)
                            {
                                ct.ThrowIfCancellationRequested();
                                AppendContinuationRow($"Criteria Selection {position}（續）", conditionPart, style);
                            }
                        }

                        if (blankDocumentRows > 0)
                        {
                            // 空白傳票號碼不是一張傳票，明細表不會列出這些分錄。一般情境的 D 欄是行層命中數，含這些分錄；
                            // 整張傳票情境的 D 欄是明細表的分錄數，不含。摘要頁加一列說明（使用者 2026-10-19 裁定），數字本身不動。
                            AppendContinuationRow(
                                $"Criteria Selection {position}（說明）",
                                string.Create(CultureInfo.InvariantCulture, $"另有 {blankDocumentRows:N0} 筆分錄的傳票號碼空白，未列入明細表。"),
                                style);
                        }
                    }
                    cells.SetNumber(
                        $"C{row}",
                        count.VoucherHitCount);
                    cells.SetNumber(
                        $"D{row}",
                        context.WholeVoucherSummaryPositions?.Contains(position) == true
                            ? exportedRowCounts.GetValueOrDefault(position)
                            : count.RowHitCount);
                }

                if (context.Scenarios.Count > 0)
                {
                    // idea-script.bas 8839：至少一筆 criteria log 被寫入時，
                    // Cells.EntireRow.AutoFit；OpenXML 以移除 explicit row height 表示。
                    foreach (var row in part.Worksheet.GetFirstChild<SheetData>()!
                                 .Elements<Row>())
                    {
                        row.Height = null;
                        row.CustomHeight = null;
                    }
                }
                cells.EnsureAllAllowedCellsWereEdited();
                part.Worksheet.Save();
                ct.ThrowIfCancellationRequested();
            },
            cancellationToken);
    }
}
