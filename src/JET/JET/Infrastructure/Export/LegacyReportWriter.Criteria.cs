using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class LegacyReportWriter
{
    /// <summary>條件篩選報告的情境列從第 5 列起，一個已存情境一列，列數就是已存情境上限。</summary>
    private const int CriteriaFirstScenarioRow = 5;

    /// <summary>
    /// 「#Criteria Select」工作表最後一欄的欄名與值：照舊版 idea-tool.bas 第 11660 行的 Final_TAG，
    /// 符合條件的分錄寫「Y」，同一張傳票的其他分錄留白（裁定 2026-11-05-48；欄名照用，裁定 2026-11-05-67）。
    /// </summary>
    internal const string CriteriaMatchedColumnName = "Final_TAG";

    internal const string CriteriaMatchedValue = "Y";

    /// <summary>
    /// 命中傳票的一列：<see cref="Matched"/> 是這一列是否符合條件。單獨使用「未預期借貸組合」的情境照舊版以整張傳票呈現，
    /// 整張傳票都算符合，和底稿 Step 4-1 的標記相同（<c>JetAuditProgram.UsesLegacyWholeVoucherRows</c>）。
    /// </summary>
    private readonly record struct CriteriaVoucherRow(long EntryId, bool Matched);

    Task<ExportStats> ICriteriaSelectionReportWriter.WriteAsync(
        Stream output,
        CriteriaSelectionReportContext context,
        ReportWorkbookMetadata workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress) =>
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
        ReportWorkbookMetadata workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress) =>
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
        ReportWorkbookMetadata workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var stats = new List<SheetStat>();
        var counts = await highRiskSummaryCounts.GetCountsAsync(
            context.Project.ProjectId,
            cancellationToken).ConfigureAwait(false);
        var blankVoucherNumberHitRows = await highRiskSummaryCounts.GetBlankVoucherNumberHitRowCountsAsync(
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
                row => row.EntryId,
                legacyStyles,
                // Resolve the legacy threshold from the full-voucher rows
                // already counted by the disk spool, never from hit-row counts.
                RawSheetAutoFitMode.LegacyFirstTwentyWhenAtMostOneMillion,
                cancellationToken,
                progress,
                customFields: customFields,
                registryFields: workbookMetadata.GlMapping.GlOptions?.RdeFields,
                customFieldMoneyScale: context.Project.MoneyScale,
                markerColumn: new RawSheetMarkerColumn<CriteriaVoucherRow>(
                    CriteriaMatchedColumnName,
                    static row => row.Matched ? CriteriaMatchedValue : null)).ConfigureAwait(false);
            exportedRowCounts[scenario.Position] = stats
                .Skip(firstScenarioStatIndex)
                .Sum(item => item.RowsWritten);
        }

        EditCriteriaSummary(
            editor,
            context,
            counts,
            blankVoucherNumberHitRows,
            exportedRowCounts,
            operatorId,
            cancellationToken);
        ReportWorkbookMetadataWorksheet.Append(editor.Document, workbookMetadata, cancellationToken);
        editor.Document.WorkbookPart!.Workbook.Save();
        return stats;
    }

    private async Task<PageResult<CriteriaVoucherRow>> ReadCriteriaVoucherRowsAsync(
        CriteriaSelectionReportContext context,
        int scenarioPosition,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        if (highRiskSummaryRows is null)
        {
            throw new InvalidOperationException(
                "Criteria Selection whole-voucher export requires the high-risk summary row port.");
        }
        var population = new GlPopulationContext(
            context.PopulationScope,
            context.Project.PeriodStart,
            context.Project.PeriodEnd);
        var result = await highRiskSummaryRows.GetPageAsync(
            context.Project.ProjectId,
            population,
            page,
            [scenarioPosition],
            cancellationToken).ConfigureAwait(false);
        var wholeVoucher = context.WholeVoucherSummaryPositions?.Contains(scenarioPosition) == true;
        return new PageResult<CriteriaVoucherRow>(
            result.EntryIds
                .Select(entryId => new CriteriaVoucherRow(
                    entryId,
                    wholeVoucher || result.PositionsByEntry.ContainsKey(entryId)))
                .ToArray(),
            result.Page.NextCursor);
    }

    private static void EditCriteriaSummary(
        DirectTemplateWorkbookEditor editor,
        CriteriaSelectionReportContext context,
        IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)> counts,
        IReadOnlyDictionary<int, long> blankVoucherNumberHitRows,
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
                for (var row = CriteriaFirstScenarioRow;
                     row < CriteriaFirstScenarioRow + FilterScenarioLimits.MaxSavedScenarios;
                     row++)
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
                    string.Create(CultureInfo.InvariantCulture, $"Prepared date: {context.PreparedDate:yyyy/MM/dd}"));
                cells.Clear("A4");
                cells.Clear("C4");
                cells.Clear("D4");

                var scenarios = context.Scenarios
                    .ToDictionary(item => item.Position);
                var sheetData = part.Worksheet.GetFirstChild<SheetData>()!;
                var nextConditionRow = Math.Max(
                    (uint)(CriteriaFirstScenarioRow + FilterScenarioLimits.MaxSavedScenarios + 1),
                    sheetData.Elements<Row>().Max(row => row.RowIndex?.Value ?? 0) + 2);
                // 情境列下方的接續列：過長條件的分段與空白傳票號碼的說明，都沿用該情境 B 欄的格式。
                void AppendContinuationRow(string label, string text, uint? style)
                {
                    sheetData.Append(new Row(
                        new Cell { CellReference = $"A{nextConditionRow}", DataType = CellValues.InlineString,
                            StyleIndex = style, InlineString = ExcelText.InlineString(label, preserveSpace: false) },
                        new Cell { CellReference = $"B{nextConditionRow}", DataType = CellValues.InlineString,
                            StyleIndex = style, InlineString = ExcelText.InlineString(text) })
                        { RowIndex = nextConditionRow });
                    nextConditionRow++;
                    var dimension = part.Worksheet.GetFirstChild<SheetDimension>();
                    if (dimension is not null) dimension.Reference = $"A1:D{nextConditionRow - 1}";
                }

                for (var position = 1; position <= FilterScenarioLimits.MaxSavedScenarios; position++)
                {
                    var row = CriteriaFirstScenarioRow + position - 1;
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
                    var blankVoucherNumberRows = blankVoucherNumberHitRows.GetValueOrDefault(position);
                    if (conditionParts.Count > 1 || blankVoucherNumberRows > 0)
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

                        if (blankVoucherNumberRows > 0)
                        {
                            // 空白傳票號碼不是一張傳票，明細表不會列出這些分錄。一般情境的 D 欄是行層命中數，含這些分錄；
                            // 整張傳票情境的 D 欄是明細表的分錄數，不含。摘要頁加一列說明（裁定 2026-10-19-12），數字本身不動。
                            AppendContinuationRow(
                                $"Criteria Selection {position}（說明）",
                                $"另有 {CountText.Format(blankVoucherNumberRows)} 筆分錄的傳票號碼空白，未列入明細表。",
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
                ExcelText.SaveWorksheet(part);
                ct.ThrowIfCancellationRequested();
            },
            cancellationToken);
    }
}
