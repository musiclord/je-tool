using System.Globalization;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class WorkpaperWriter
{
    private async Task EmitStep2Async(
        WorkpaperWriteSession session, List<SheetStat> stats,
        WorkpaperContext context, WorkpaperSheetPlan? sheetPlan,
        ReportWorkbookMetadata? workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var glMapping = await mappingStates.FindAsync(
            context.ProjectId,
            DatasetKind.Gl,
            cancellationToken);
        var headers = new string?[]
            {
                "樣本編號",
                "傳票號碼\n(分錄編號)",
                "會計科目編號",
                "會計科目名稱",
                "借方金額",
                "貸方金額",
                "總帳日期",
                "傳票核准日期\n(若有)",
                "分錄編製人員\n(或過帳人員)",
                "分錄來源或\n人工/自動分錄判斷(若有)",
                "分錄備註/說明",
                "傳票核准人員",
                "A",
                "B",
                "C",
                "D",
                "E",
                "F",
                "G",
                null
            }
            .Concat(customFields.Select(static field => field.Label))
            .ToArray();
        var minimumWidths = new[]
            {
                9D,
                18D,
                18D,
                18D,
                15D,
                MaximumAmountDisplayColumnWidth,
                16D,
                16D,
                16D,
                16D,
                30D,
                30D,
                11D,
                11D,
                11D,
                11D,
                11D,
                11D,
                11D,
                38D
            }
            .Concat(Enumerable.Repeat(18D, customFields.Count))
            .ToArray();
        using var spool = WorkpaperDisplaySpool.Create(
            headers,
            minimumWidths);
        await FillDisplaySpoolAsync(
            spool,
            StreamStep2RowsAsync(
                context,
                glMapping,
                workbookMetadata,
                customFields,
                cancellationToken),
            cancellationToken);

        using var sheet = session.OpenSheet(WorkpaperSheetCatalog.Step2);
        ConfigureStep2Layout(sheet, spool.Widths);
        WriteSubstantiveHeader(sheet, context, "此Report將提供測試JE母體攸關資料元素可靠性之樣本");

        // 測試說明 + 測試程序固定文字(逐字對齊樣本;列號/欄/合併皆照樣本)。
        WriteFixedLines(sheet, PlannedLines(sheetPlan, Step2HeaderLines));
        foreach (var range in Step2HeaderMerges(customFields.Count))
        {
            sheet.AddMerge(range);
        }

        // 第 49-52 列欄標(多層合併);資料第 53 列起。
        WriteStep2ColumnHeaders(sheet, customFields);
        var rows = 0L;
        foreach (var projected in spool.ReadRows(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = checked(53U + (uint)rows);
            sheet.WriteRow(
                row,
                ProjectedCells(sheet, row, projected, firstColumn: 1));
            rows++;
            ReportIntermediateRows(
                stats,
                progress,
                WorkpaperSheetCatalog.Step2,
                rows);
        }

        for (var row = 53U + (uint)rows; row <= 111; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blanks = new List<Cell>(20 + customFields.Count);
            for (uint column = 1; column <= 20; column++)
            {
                blanks.Add(sheet.BlankCell(
                    row, column, column >= 13 ? WorkpaperStyles.Editable : WorkpaperStyles.Default));
            }
            for (var index = 0; index < customFields.Count; index++)
            {
                var column = checked((uint)(21 + index));
                blanks.Add(sheet.BlankCell(
                    row,
                    column,
                    Step2RdeCellStyle(customFields[index], context.MoneyScale)));
            }
            sheet.WriteFixedRow(row, blanks);
        }
        sheet.AddListValidation("M53:S111", "\"Y,N,N/A\"");
        sheet.Protect();

        AddStat(stats, sheet.CloseAndSummarizeWith(rows, cancellationToken), progress);
    }

    private async IAsyncEnumerable<IReadOnlyList<WorkpaperDisplayCell>> StreamStep2RowsAsync(
        WorkpaperContext context,
        CommittedMapping? glMapping,
        ReportWorkbookMetadata? workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? directionColumn = null;
        string? sourceModuleColumn = null;
        if (glMapping is not null)
        {
            glMapping.Mapping.TryGetValue(GlMappingKeys.DcField, out directionColumn);
            glMapping.Mapping.TryGetValue(GlMappingKeys.JeSource, out sourceModuleColumn);
        }

        string? cursor = null;
        var ordinal = 0L;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await infSamples.GetPageAsync(
                context.ProjectId,
                context.ValidationRunId,
                context.MoneyScale,
                new PageRequest(cursor, PageRequest.DefaultPageSize),
                cancellationToken);
            var entryIds = page.Rows.Select(sample => sample.EntryId).ToArray();

            IReadOnlyDictionary<long, string>? rawPage = null;
            if (_rawRows is not null)
            {
                if (entryIds.Any(entryId => entryId <= 0))
                {
                    throw MissingStep2RawSource();
                }

                rawPage = await _rawRows.FetchJsonByEntryIdsAsync(
                    context.ProjectId,
                    entryIds,
                    cancellationToken);
                if (entryIds.Any(entryId => !rawPage.ContainsKey(entryId)))
                {
                    throw MissingStep2RawSource();
                }
            }
            var rdePage = await ReadWorkpaperRdeValuesAsync(
                context.ProjectId,
                entryIds,
                workbookMetadata,
                customFields,
                context.MoneyScale,
                cancellationToken).ConfigureAwait(false);

            foreach (var sample in page.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? direction;
                string? sourceModule;
                if (rawPage is null)
                {
                    // public plan-less compatibility path 沒有 raw-row port；以既有 normalized side
                    // 補上可覆核代號，不臆造來源模組。
                    direction = sample.CreditScaled > 0 ? "CREDIT" : "DEBIT";
                    sourceModule = null;
                }
                else
                {
                    using var raw = JsonDocument.Parse(rawPage[sample.EntryId]);
                    direction = ReadStep2RawValue(raw.RootElement, directionColumn);
                    sourceModule = ReadStep2RawValue(raw.RootElement, sourceModuleColumn);
                }

                yield return ProjectStep2Sample(
                    checked(++ordinal),
                    sample,
                    direction,
                    sourceModule,
                    context.MoneyScale,
                    customFields,
                    rdePage[sample.EntryId]);
            }

            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    private static string? ReadStep2RawValue(JsonElement row, string? sourceColumn)
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

    private static JetActionException MissingStep2RawSource() => new(
        JetErrorCodes.StaleResult,
        "底稿引用的原始 GL 列已不完整，請重新匯入、配對並執行對應步驟後再產出底稿。");

    /// <summary>
    /// 「step3 高風險條件彙總」:固定測試說明區塊 + C 表(欄標第 18 列;資料第 19 列起,依 position 升冪):
    /// B 代號 C{position} / C 條件描述=情境 name / D 選擇此條件原因=情境 rationale /
    /// E 符合條件之傳票數=voucherHitCount。可見表固定止於 E 欄。
    /// 情境清單(name/rationale/position)取自 scenario store、傳票命中數取自 tagMatrix counts,於 TagColumnSet 合併;
    /// </summary>
    private void EmitStep3(
        WorkpaperWriteSession session, List<SheetStat> stats,
        WorkpaperContext context, TagColumnSet tagColumns, WorkpaperSheetPlan? sheetPlan,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var dataWidths = MeasureDisplayWidths(
            [null, "高風險範圍條件", "選擇此篩選條件的原因", "符合條件之傳票數目"],
            [9D, 30D, 42D, MaximumCountDisplayColumnWidth],
            tagColumns.Scenarios.Select(scenario =>
                (IReadOnlyList<string?>)new string?[]
                {
                    $"C{scenario.Position}",
                    scenario.Name,
                    scenario.Rationale,
                    scenario.VoucherHitCount.ToString("#,##0", CultureInfo.InvariantCulture)
                }));
        using var sheet = session.OpenSheet(WorkpaperSheetCatalog.Step3);
        ConfigureStep3Layout(sheet, dataWidths);
        WriteSubstantiveHeader(sheet, context, "證實測試 - 會計分錄及其他調整", omitSubtitle: true);

        var headerLines = PlannedLines(sheetPlan, Step3HeaderLines(context));
        if (sheetPlan?.AuditCondition is { } auditCondition)
        {
            headerLines = headerLines
                .Select(line => line.Row == 7 && line.Column == 2
                    ? (line.Row, line.Column, auditCondition)
                    : line)
                .ToArray();
        }
        WriteFixedLines(sheet, headerLines);
        foreach (var range in Step3HeaderMerges)
        {
            sheet.AddMerge(range);
        }

        // 第 18 列欄標(B 欄無欄標,直接是代號;C/D/E 有欄標)。
        sheet.WriteFixedRow(18,
        [
            sheet.TextCell(18, 3, "高風險範圍條件", WorkpaperStyles.Bold),
            sheet.TextCell(18, 4, "選擇此篩選條件的原因", WorkpaperStyles.Bold),
            sheet.TextCell(18, 5, "符合條件之傳票數目", WorkpaperStyles.Bold)
        ]);

        var rowIndex = 19u;
        long dataRows = 0;
        foreach (var scenario in tagColumns.Scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sheet.WriteRow(rowIndex,
            [
                sheet.TextCell(rowIndex, 2, $"C{scenario.Position}"),
                sheet.TextCell(rowIndex, 3, scenario.Name),
                sheet.TextCell(rowIndex, 4, scenario.Rationale),
                sheet.NumberCell(rowIndex, 5, scenario.VoucherHitCount)
            ]);
            rowIndex++;
            dataRows++;
        }

        AddStat(stats, sheet.CloseAndSummarizeWith(dataRows, cancellationToken), progress);
    }

    /// <summary>
    /// 「step4 符合高風險條件傳票」:第 11 列欄標(A 編號/B 傳票號碼/C 總帳日期/D 編製者/E 傳票總金額)
    /// + 動態 C1..CN 欄(欄集 = 全 position 升冪);第 12 列固定說明;資料第 13 列起,逐頁 tagMatrixVoucherPage。
    /// 每列以該傳票 matchedPositions(PositionsByDoc 對齊)含某 position 標 'Y' 否則空;P-U 手填留空。
    /// </summary>
    private async Task EmitStep4Async(
        WorkpaperWriteSession session, List<SheetStat> stats,
        WorkpaperContext context, TagColumnSet tagColumns, WorkpaperSheetPlan? sheetPlan,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        const uint firstDataRow = 13;
        var pageCapacity = (long)_continuationRowLimit - firstDataRow + 1;
        var ordinal = 0L;
        using var spool = WorkpaperDisplaySpool.Create(
            Step4DisplayHeaders(tagColumns),
            Step4MinimumWidths());
        ReportSheetStarted(stats, progress, WorkpaperSheetCatalog.Step4);
        var sourceRows = sheetPlan is null
            ? StreamTaggedVoucherRowsAsync(
                context,
                context.ScenarioPositions,
                () => ++ordinal,
                cancellationToken)
            : StreamFinalizedStep4VoucherRowsAsync(
                context,
                tagColumns.AllPositions,
                () => ++ordinal,
                cancellationToken);
        await FillDisplaySpoolAsync(
            spool,
            ProjectRowsAsync(
                sourceRows,
                tagged => ProjectStep4Voucher(
                    tagged,
                    tagColumns,
                    context.MoneyScale),
                cancellationToken),
            cancellationToken);

        var pageNumber = 1;
        var pageRows = 0L;
        var sheet = CreateStep4Page(
            session,
            context,
            tagColumns,
            sheetPlan,
            spool.Widths,
            pageNumber);
        try
        {
            foreach (var projected in spool.ReadRows(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pageRows == pageCapacity)
                {
                    AddStat(stats, CloseStep4Page(sheet, pageRows, cancellationToken), progress);
                    sheet.Dispose();
                    pageNumber = checked(pageNumber + 1);
                    sheet = CreateStep4Page(
                        session,
                        context,
                        tagColumns,
                        sheetPlan,
                        spool.Widths,
                        pageNumber);
                    pageRows = 0;
                    ReportSheetStarted(stats, progress, sheet.Name);
                }

                var row = checked((uint)(firstDataRow + pageRows));
                sheet.WriteRow(
                    row,
                    ProjectedCells(sheet, row, projected, firstColumn: 1));
                pageRows++;
                ReportIntermediateRows(stats, progress, sheet.Name, pageRows);
            }

            AddStat(stats, CloseStep4Page(sheet, pageRows, cancellationToken), progress);
        }
        finally
        {
            sheet.Dispose();
        }
    }

    private static SheetWriter CreateStep4Page(
        WorkpaperWriteSession session,
        WorkpaperContext context,
        TagColumnSet tagColumns,
        WorkpaperSheetPlan? sheetPlan,
        IReadOnlyList<double> dataWidths,
        int pageNumber)
    {
        var sheet = session.OpenSheet(
            ExcelWorksheetConstraints.ContinuationSheetName(WorkpaperSheetCatalog.Step4, pageNumber));
        ConfigureStep4Layout(sheet, dataWidths);
        WriteSubstantiveHeader(sheet, context, "證實測試 - 會計分錄及其他調整", omitSubtitle: true);

        WriteFixedLines(sheet, PlannedLines(sheetPlan, Step4HeaderLines));
        foreach (var range in Step4HeaderMerges)
        {
            sheet.AddMerge(range);
        }

        var headerCells = new List<Cell>
        {
            sheet.TextCell(11, 1, "編號", WorkpaperStyles.Bold),
            sheet.TextCell(11, 2, "傳票號碼", WorkpaperStyles.Bold),
            sheet.TextCell(11, 3, "總帳日期", WorkpaperStyles.Bold),
            sheet.TextCell(11, 4, "編製者", WorkpaperStyles.Bold),
            sheet.TextCell(11, 5, "傳票總金額", WorkpaperStyles.Bold)
        };
        tagColumns.AppendAllPositionHeaders(sheet, headerCells, 11, suffix: string.Empty);
        var reviewHeaders = new[] { "覆核結果", "附件索引", "差異說明", "處理結論", "覆核人", "備註" };
        for (var index = 0; index < reviewHeaders.Length; index++)
        {
            headerCells.Add(sheet.TextCell(11, (uint)(16 + index), reviewHeaders[index], WorkpaperStyles.Bold));
        }
        sheet.WriteFixedRow(11, headerCells);
        sheet.WriteFixedRow(12,
        [
            sheet.TextCell(
                12,
                1,
                PlannedText(sheetPlan, Step4HeaderLines.Count, Step4SelectionNote),
                WorkpaperStyles.Bold)
        ]);
        return sheet;
    }

    private static SheetStat CloseStep4Page(
        SheetWriter sheet,
        long rows,
        CancellationToken cancellationToken)
    {
        if (rows > 0)
        {
            var lastRow = CheckedLastDataRow(firstDataRow: 13, rows);
            sheet.AddListValidation($"P13:T{lastRow}", "\"Y,N,N/A\"");
        }

        sheet.Protect();
        return sheet.CloseAndSummarizeWith(rows, cancellationToken);
    }

    /// <summary>
    /// 「step4-1 符合高風險條件傳票明細」:第 5 列欄標(A-P legacy JE 欄)+ 固定 C*_TAG 槽位
    /// (欄集 = 只 rowHitCount>0 的 position 升冪;標頭 C{position}_TAG);資料第 6 列起,逐頁 tagMatrixRowPage。
    /// 每行以該行 matchedPositions(PositionsByEntry 以 index 對齊)含某 position 標 'Y' 否則空。
    /// </summary>
    private async Task EmitLegacyStep41Async(
        WorkpaperWriteSession session, List<SheetStat> stats,
        WorkpaperContext context, TagColumnSet tagColumns, CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        const uint firstDataRow = 6;
        var pageCapacity = (long)_continuationRowLimit - firstDataRow + 1;
        var pageNumber = 1;
        var pageRows = 0L;
        var sheet = CreateLegacyStep41Page(session, context, tagColumns, pageNumber);

        try
        {
            await foreach (var tagged in StreamTaggedRowDetailRowsAsync(
                               context,
                               tagColumns.IsFromPlan ? tagColumns.AllPositions : context.ScenarioPositions,
                               cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pageRows == pageCapacity)
                {
                    AddStat(stats, CloseStep41Page(sheet, pageRows, cancellationToken), progress);
                    sheet.Dispose();
                    pageNumber = checked(pageNumber + 1);
                    sheet = CreateLegacyStep41Page(session, context, tagColumns, pageNumber);
                    pageRows = 0;
                }

                var row = checked((uint)(firstDataRow + pageRows));
                sheet.WriteRow(row, Step41RowCells(
                    sheet, row, tagged.Detail, tagColumns, tagged.MatchedPositions, context.MoneyScale));
                pageRows++;
                ReportIntermediateRows(stats, progress, sheet.Name, pageRows);
            }

            AddStat(stats, CloseStep41Page(sheet, pageRows, cancellationToken), progress);
        }
        finally
        {
            sheet.Dispose();
        }
    }

    /// <summary>
    /// Finalized step4-1：prepared session 只開一條 ordered DB reader；無 RDE 時同一遍
    /// projection 精確聚合全資料欄寬並把 typed cells 寫入 DeleteOnClose spool。有 RDE 時
    /// 先把 source rows 有界落盤，釋放 prepared transaction 後才分批讀取 RDE，避免在
    /// provider reader 持鎖期間另開不相容連線。最後依已知欄寬單向 replay projected spool
    /// 到直接 SAX 範本，整體仍只有一次 population scan 且記憶體有界。
    /// </summary>
    private async Task EmitStep41Async(
        WorkpaperWriteSession session,
        List<SheetStat> stats,
        WorkpaperContext context,
        IReadOnlyList<WorkpaperStep41Column> columns,
        ReportWorkbookMetadata? workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        TagColumnSet tagColumns,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        if (_step41PreparedSessions is null)
        {
            throw new InvalidOperationException(
                "WorkingPaper step4-1 prepared session factory 未設定。");
        }
        ValidateStep41NumberScales(columns);

        const uint firstDataRow = 6;
        var pageCapacity = (long)_continuationRowLimit - firstDataRow + 1;
        var lineItemKind = columns
            .SingleOrDefault(column => string.Equals(
                column.Header,
                "傳票文件項次_JE_S",
                StringComparison.Ordinal))
            ?.Kind
            ?? LegacyFieldKind.Text;
        var populationContext = new GlPopulationContext(
            context.PopulationScope,
            context.PeriodStart,
            context.PeriodEnd);
        using var spool = Step41ProjectedSpool.Create(
            columns,
            tagColumns.RowHitPositions);
        using var sourceSpool = customFields.Count == 0
            ? null
            : Step41SourceSpool.Create();
        ReportSheetStarted(stats, progress, WorkpaperSheetCatalog.Step41);
        await using (var prepared = await _step41PreparedSessions.PrepareAsync(
                         context.ProjectId,
                         populationContext,
                         tagColumns.AllPositions,
                         lineItemKind,
                         cancellationToken,
                         hitVoucherScenarioPositions: tagColumns.HitVoucherPositions))
        {
            prepared.Metrics.WidthAggregations++;
            await foreach (var source in prepared.ReadRowsAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sourceSpool is null)
                {
                    spool.Append(
                        ProjectStep41Values(
                            source,
                            columns,
                            context.MoneyScale),
                        source.MatchedPositions);
                }
                else
                {
                    sourceSpool.Append(source);
                }
            }
        }

        if (sourceSpool is not null)
        {
            sourceSpool.Complete();
            var batch = new List<WorkpaperStep41SourceRow>(PageRequest.MaxPageSize);
            foreach (var source in sourceSpool.ReadRows(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch.Add(source);
                if (batch.Count == PageRequest.MaxPageSize)
                {
                    await AppendStep41RdeBatchAsync(batch).ConfigureAwait(false);
                    batch.Clear();
                }
            }
            await AppendStep41RdeBatchAsync(batch).ConfigureAwait(false);

            async Task AppendStep41RdeBatchAsync(
                IReadOnlyList<WorkpaperStep41SourceRow> rows)
            {
                if (rows.Count == 0)
                {
                    return;
                }
                var rde = await ReadWorkpaperRdeValuesAsync(
                    context.ProjectId,
                    rows.Select(static row => row.EntryId).ToArray(),
                    workbookMetadata,
                    customFields,
                    context.MoneyScale,
                    cancellationToken).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    spool.Append(
                        ProjectStep41Values(
                            row,
                            columns,
                            context.MoneyScale,
                            rde[row.EntryId]),
                        row.MatchedPositions);
                }
            }
        }
        spool.Complete();

        var widths = spool.Widths;
        var pageNumber = 1;
        var pageRows = 0L;
        var emittedRows = 0L;
        var sheet = CreateStep41Page(
            session,
            context,
            columns,
            tagColumns.RowHitPositions,
            widths,
            Math.Min(pageCapacity, spool.RowCount),
            pageNumber);

        try
        {
            foreach (var projected in spool.ReadRows(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pageRows == pageCapacity)
                {
                    AddStat(stats, CloseStep41Page(sheet, pageRows, cancellationToken), progress);
                    emittedRows = checked(emittedRows + pageRows);
                    sheet.Dispose();
                    pageNumber = checked(pageNumber + 1);
                    sheet = CreateStep41Page(
                        session,
                        context,
                        columns,
                        tagColumns.RowHitPositions,
                        widths,
                        Math.Min(
                            pageCapacity,
                            spool.RowCount - emittedRows),
                        pageNumber);
                    pageRows = 0;
                    ReportSheetStarted(stats, progress, sheet.Name);
                }

                var row = checked((uint)(firstDataRow + pageRows));
                sheet.WriteRow(
                    row,
                    Step41SpoolCells(
                        sheet,
                        row,
                        projected,
                        tagColumns.RowHitPositions,
                        columns.Count));
                pageRows++;
                ReportIntermediateRows(stats, progress, sheet.Name, pageRows);
            }

            AddStat(stats, CloseStep41Page(sheet, pageRows, cancellationToken), progress);
        }
        finally
        {
            sheet.Dispose();
        }
    }

    private static SheetWriter CreateStep41Page(
        WorkpaperWriteSession session,
        WorkpaperContext context,
        IReadOnlyList<WorkpaperStep41Column> columns,
        IReadOnlyList<int> rowHitPositions,
        IReadOnlyList<double> widths,
        long dataRows,
        int pageNumber)
    {
        var sheet = session.OpenSheet(
            ExcelWorksheetConstraints.ContinuationSheetName(
                WorkpaperSheetCatalog.Step41,
                pageNumber),
            Step41Appearance());
        ConfigureStep41Layout(sheet, widths);
        sheet.SetDimension(
            checked((uint)(5 + dataRows)),
            checked((uint)(columns.Count + rowHitPositions.Count)));

        sheet.WriteFixedRow(
            1,
            [sheet.TextCell(
                1,
                1,
                $"公司名稱 : {context.CompanyName}",
                WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(
            2,
            [sheet.TextCell(
                2,
                1,
                $"財務報表期間 :  {DigitsOnly(context.PeriodStart)} ~ {DigitsOnly(context.PeriodEnd)}",
                WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(
            3,
            [sheet.TextCell(
                3,
                1,
                "符合高風險範圍條件之傳票明細",
                WorkpaperStyles.Bold)]);

        var headerCells = columns
            .Select((column, index) =>
                sheet.TextCell(
                    5,
                    checked((uint)index + 1),
                    column.Header,
                    Step41ColumnStyle(column)))
            .ToList();
        for (var index = 0; index < rowHitPositions.Count; index++)
        {
            headerCells.Add(sheet.TextCell(
                5,
                checked((uint)(columns.Count + index + 1)),
                $"C{rowHitPositions[index]}_TAG",
                WorkpaperStyles.Step41Text));
        }
        sheet.WriteFixedRow(5, headerCells);
        return sheet;
    }

    private static void ConfigureStep41Layout(
        SheetWriter sheet,
        IReadOnlyList<double> widths)
    {
        sheet.SetDefaultRowHeight(18);
        for (var index = 0; index < widths.Count; index++)
        {
            var column = checked((uint)index + 1);
            sheet.SetColumnWidth(
                column,
                column,
                widths[index],
                bestFit: true);
        }
        sheet.SetRowHeight(5, 42);
    }

    private async Task<Step41Measurement> MeasureStep41WidthsAsync(
        WorkpaperContext context,
        IReadOnlyList<WorkpaperStep41Column> columns,
        IReadOnlyList<int> rowHitPositions,
        LegacyFieldKind lineItemKind,
        CancellationToken cancellationToken)
    {
        var maximums = columns
            .Select(column => ExcelDisplayWidth.Measure(column.Header))
            .Concat(rowHitPositions.Select(position =>
                ExcelDisplayWidth.Measure($"C{position}_TAG")))
            .ToArray();
        var rowCount = 0L;

        await foreach (var source in StreamWorkpaperStep41RowsAsync(
                           context,
                           context.ScenarioPositions,
                           lineItemKind,
                           cancellationToken))
        {
            rowCount = checked(rowCount + 1);
            var projected = ProjectStep41Values(source, columns, context.MoneyScale);
            for (var index = 0; index < projected.Count; index++)
            {
                maximums[index] = Math.Max(
                    maximums[index],
                    ExcelDisplayWidth.Measure(projected[index].RenderedText));
            }
            for (var index = 0; index < rowHitPositions.Count; index++)
            {
                if (source.MatchedPositions.Contains(rowHitPositions[index]))
                {
                    maximums[columns.Count + index] = Math.Max(
                        maximums[columns.Count + index],
                        1);
                }
            }
        }

        var widths = maximums
            .Select(ExcelDisplayWidth.ToColumnWidth)
            .ToArray();
        return new Step41Measurement(widths, rowCount);
    }

    private static IReadOnlyList<Cell> Step41ProjectedCells(
        SheetWriter sheet,
        uint row,
        WorkpaperStep41SourceRow source,
        IReadOnlyList<WorkpaperStep41Column> columns,
        IReadOnlyList<int> rowHitPositions,
        int moneyScale)
    {
        var values = ProjectStep41Values(source, columns, moneyScale);
        var cells = new List<Cell>(columns.Count + rowHitPositions.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var column = checked((uint)index + 1);
            var value = values[index];
            cells.Add(value.NumberLiteral is { } numberLiteral
                ? sheet.NumberLiteralCell(
                    row,
                    column,
                    numberLiteral,
                    value.Style)
                : sheet.TextCell(row, column, value.Text, value.Style));
        }
        for (var index = 0; index < rowHitPositions.Count; index++)
        {
            var column = checked((uint)(columns.Count + index + 1));
            cells.Add(source.MatchedPositions.Contains(rowHitPositions[index])
                ? sheet.TextCell(row, column, "Y", WorkpaperStyles.Step41Text)
                : sheet.BlankCell(row, column, WorkpaperStyles.Step41Text));
        }
        return cells;
    }

    private static IReadOnlyList<Cell> Step41SpoolCells(
        SheetWriter sheet,
        uint row,
        Step41SpoolRow projected,
        IReadOnlyList<int> rowHitPositions,
        int valueColumnCount)
    {
        var cells = new List<Cell>(valueColumnCount + rowHitPositions.Count);
        for (var index = 0; index < projected.Values.Count; index++)
        {
            var column = checked((uint)index + 1);
            var value = projected.Values[index];
            cells.Add(value.NumberLiteral is { } numberLiteral
                ? sheet.NumberLiteralCell(row, column, numberLiteral, value.Style)
                : sheet.TextCell(row, column, value.Text, value.Style));
        }
        for (var index = 0; index < rowHitPositions.Count; index++)
        {
            var column = checked((uint)(valueColumnCount + index + 1));
            cells.Add((projected.TagMask & (1 << index)) != 0
                ? sheet.TextCell(row, column, "Y", WorkpaperStyles.Step41Text)
                : sheet.BlankCell(row, column, WorkpaperStyles.Step41Text));
        }
        return cells;
    }

    private static IReadOnlyList<Step41ProjectedValue> ProjectStep41Values(
        WorkpaperStep41SourceRow source,
        IReadOnlyList<WorkpaperStep41Column> columns,
        int moneyScale,
        IReadOnlyDictionary<string, string?>? rdeValues = null)
    {
        using var raw = JsonDocument.Parse(source.RawJson);
        var values = new Step41ProjectedValue[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            values[index] = column.ValueSource switch
            {
                WorkpaperStep41ValueSource.DocumentNumber => Typed(source.DocumentNumber, column),
                WorkpaperStep41ValueSource.LineItem => Typed(source.LineItem, column),
                WorkpaperStep41ValueSource.ApprovalDate => Typed(source.ApprovalDate, column),
                WorkpaperStep41ValueSource.PostDate => Typed(source.PostDate, column),
                WorkpaperStep41ValueSource.CreatedBy => Typed(source.CreatedBy, column),
                WorkpaperStep41ValueSource.ApprovedBy => Typed(source.ApprovedBy, column),
                WorkpaperStep41ValueSource.AccountCode => Typed(source.AccountCode, column),
                WorkpaperStep41ValueSource.AccountName => Typed(source.AccountName, column),
                WorkpaperStep41ValueSource.Description => Typed(source.Description, column),
                WorkpaperStep41ValueSource.SourceModule => Typed(source.SourceModule, column),
                WorkpaperStep41ValueSource.IsManual => Typed(
                    source.IsManual is null ? null : source.IsManual.Value ? "1" : "0",
                    column),
                WorkpaperStep41ValueSource.SignedAmount => Amount(
                    Display(source.AmountScaled, moneyScale),
                    column),
                WorkpaperStep41ValueSource.RawField => Raw(
                    raw.RootElement,
                    column),
                WorkpaperStep41ValueSource.RdeField => Typed(
                    column.RdeFieldId is not null
                    && rdeValues is not null
                    && rdeValues.TryGetValue(column.RdeFieldId, out var rdeValue)
                        ? rdeValue
                        : null,
                    column),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(column.ValueSource),
                    column.ValueSource,
                    null)
            };
        }
        return values;
    }

    private static Step41ProjectedValue Raw(
        JsonElement raw,
        WorkpaperStep41Column column)
    {
        if (column.RawFieldName is null
            || !raw.TryGetProperty(column.RawFieldName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Typed(null, column);
        }

        var text = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
        if (string.IsNullOrEmpty(text))
        {
            return Typed(null, column);
        }
        return Typed(text, column);
    }

    private static Step41ProjectedValue Typed(
        string? value,
        WorkpaperStep41Column column)
    {
        return column.Kind switch
        {
            LegacyFieldKind.Text => Text(value),
            LegacyFieldKind.Number => string.IsNullOrEmpty(value)
                ? Blank(WorkpaperStyles.Step41Number(DecimalPlaces(column)))
                : Number(value, column),
            LegacyFieldKind.Date => string.IsNullOrEmpty(value)
                ? Blank(WorkpaperStyles.Step41Date)
                : Date(value, column),
            LegacyFieldKind.Time => string.IsNullOrEmpty(value)
                ? Blank(WorkpaperStyles.Step41Time)
                : Time(value, column),
            _ => throw new ArgumentOutOfRangeException(
                nameof(column.Kind),
                column.Kind,
                null)
        };
    }

    private static Step41ProjectedValue Number(
        string value,
        WorkpaperStep41Column column)
    {
        var decimals = DecimalPlaces(column);
        var format = NumberFormatCode(decimals, grouped: false);
        string numberLiteral;
        string renderedText;
        if (bool.TryParse(value, out var boolean))
        {
            // XLSX native Boolean 由 import reader 保存為 Number/0 metadata，row_json
            // 則保留 invariant true/false；Legacy actual cell 仍須是 Excel numeric 1/0。
            var number = boolean ? 1m : 0m;
            numberLiteral = number.ToString(CultureInfo.InvariantCulture);
            renderedText = number.ToString(format, CultureInfo.InvariantCulture);
        }
        else if (decimal.TryParse(
                     value,
                     NumberStyles.Number | NumberStyles.AllowExponent,
                     CultureInfo.InvariantCulture,
                     out var decimalNumber))
        {
            numberLiteral = decimalNumber.ToString(CultureInfo.InvariantCulture);
            renderedText = decimalNumber.ToString(format, CultureInfo.InvariantCulture);
        }
        else if (double.TryParse(
                     value,
                     NumberStyles.Number | NumberStyles.AllowExponent,
                     CultureInfo.InvariantCulture,
                     out var doubleNumber)
                 && double.IsFinite(doubleNumber))
        {
            // Import 對超出 System.Decimal 範圍但仍為合法 finite XLSX Number 的值，
            // 以 double round-trip literal 保存；Step4-1 必須維持 numeric cell。
            numberLiteral = doubleNumber.ToString("R", CultureInfo.InvariantCulture);
            renderedText = doubleNumber.ToString(format, CultureInfo.InvariantCulture);
        }
        else
        {
            throw new InvalidDataException(
                $"step4-1 欄位 '{column.Header}' 的 Number 值 '{value}' 無法解析。");
        }

        return new Step41ProjectedValue(
            Text: null,
            NumberLiteral: numberLiteral,
            Style: WorkpaperStyles.Step41Number(decimals),
            RenderedText: renderedText);
    }

    private static Step41ProjectedValue Date(
        string value,
        WorkpaperStep41Column column)
    {
        if (!DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out var date))
        {
            throw new InvalidDataException(
                $"step4-1 欄位 '{column.Header}' 的 Date 值 '{value}' 無法解析。");
        }

        return new Step41ProjectedValue(
            Text: null,
            NumberLiteral: date.Date.ToOADate().ToString(
                "R",
                CultureInfo.InvariantCulture),
            Style: WorkpaperStyles.Step41Date,
            RenderedText: date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static Step41ProjectedValue Time(
        string value,
        WorkpaperStep41Column column)
    {
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time))
        {
            throw new InvalidDataException(
                $"step4-1 欄位 '{column.Header}' 的 Time 值 '{value}' 無法解析。");
        }

        return new Step41ProjectedValue(
            Text: null,
            NumberLiteral: time.TotalDays.ToString(
                "R",
                CultureInfo.InvariantCulture),
            Style: WorkpaperStyles.Step41Time,
            // Excel 的固定 hh:mm:ss 格式只顯示日內時、分、秒；日數與小數秒
            // 不會出現在可見文字。AutoFit 必須量同一份可見字串，否則含小數秒
            // 或超過 24 小時的來源值會把 Legacy 欄寬錯誤放大。
            RenderedText: time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
    }

    private static Step41ProjectedValue Text(string? value) =>
        new(
            Text: value,
            NumberLiteral: null,
            Style: WorkpaperStyles.Step41Text,
            RenderedText: value ?? string.Empty);

    private static Step41ProjectedValue Blank(uint style) =>
        new(
            Text: null,
            NumberLiteral: null,
            Style: style,
            RenderedText: string.Empty);

    private static Step41ProjectedValue Amount(
        decimal value,
        WorkpaperStep41Column column)
    {
        var decimals = DecimalPlaces(column);
        var format = NumberFormatCode(decimals, grouped: true);
        return new(
            Text: null,
            NumberLiteral: value.ToString(CultureInfo.InvariantCulture),
            Style: WorkpaperStyles.Step41GroupedNumber(decimals),
            RenderedText: value.ToString(format, CultureInfo.InvariantCulture));
    }

    private static uint Step41ColumnStyle(WorkpaperStep41Column column)
    {
        if (column.ValueSource == WorkpaperStep41ValueSource.SignedAmount)
        {
            return WorkpaperStyles.Step41GroupedNumber(DecimalPlaces(column));
        }

        return column.Kind switch
        {
            LegacyFieldKind.Text => WorkpaperStyles.Step41Text,
            LegacyFieldKind.Number => WorkpaperStyles.Step41Number(DecimalPlaces(column)),
            LegacyFieldKind.Date => WorkpaperStyles.Step41Date,
            LegacyFieldKind.Time => WorkpaperStyles.Step41Time,
            _ => throw new ArgumentOutOfRangeException(
                nameof(column.Kind),
                column.Kind,
                null)
        };
    }

    private static int DecimalPlaces(WorkpaperStep41Column column)
    {
        if (column.DecimalPlaces is not { } decimalPlaces
            || decimalPlaces is < 0 or > WorkpaperStyles.MaximumDecimalPlaces)
        {
            throw new InvalidDataException(
                $"step4-1 Number 欄位 '{column.Header}' 的 decimal scale "
                + $"'{column.DecimalPlaces?.ToString(CultureInfo.InvariantCulture) ?? "null"}' "
                + $"不在 0..{WorkpaperStyles.MaximumDecimalPlaces}。");
        }

        return decimalPlaces;
    }

    private static void ValidateStep41NumberScales(
        IReadOnlyList<WorkpaperStep41Column> columns)
    {
        foreach (var column in columns.Where(column =>
                     column.Kind == LegacyFieldKind.Number
                     || column.ValueSource == WorkpaperStep41ValueSource.SignedAmount))
        {
            _ = DecimalPlaces(column);
        }
    }

    private static string NumberFormatCode(int decimalPlaces, bool grouped) =>
        (grouped ? "#,##0" : "0")
        + (decimalPlaces == 0 ? string.Empty : "." + new string('0', decimalPlaces));

    private readonly record struct Step41ProjectedValue(
        string? Text,
        string? NumberLiteral,
        uint Style,
        string RenderedText);

    private readonly record struct Step41Measurement(
        IReadOnlyList<double> Widths,
        long RowCount);

    private readonly record struct Step41SpoolRow(
        IReadOnlyList<Step41ProjectedValue> Values,
        int TagMask);

    /// <summary>
    /// RDE projection 專用的 DeleteOnClose source spool。prepared session 內只做單向寫入，
    /// transaction 釋放後才單向 replay；因此不需把 step4-1 population 放進記憶體，
    /// 也不會在 active provider reader 期間開第二條 RDE connection。
    /// </summary>
    private sealed class Step41SourceSpool : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private BinaryReader? _reader;
        private bool _completed;
        private bool _readStarted;
        private bool _disposed;

        private Step41SourceSpool(FileStream stream)
        {
            _stream = stream;
            _writer = new BinaryWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true);
        }

        private long RowCount { get; set; }

        internal static Step41SourceSpool Create()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $".jet-workpaper-step41-source-{Guid.NewGuid():N}.tmp");
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            try
            {
                return new Step41SourceSpool(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal void Append(WorkpaperStep41SourceRow source)
        {
            ThrowIfDisposed();
            if (_completed)
            {
                throw new InvalidOperationException(
                    "Step4-1 source spool 已完成，不能再追加列。");
            }

            _writer.Write(source.EntryId);
            WriteNullableString(source.DocumentNumber);
            WriteNullableString(source.LineItem);
            WriteNullableString(source.PostDate);
            WriteNullableString(source.ApprovalDate);
            WriteNullableString(source.CreatedBy);
            WriteNullableString(source.ApprovedBy);
            WriteNullableString(source.AccountCode);
            WriteNullableString(source.AccountName);
            _writer.Write(source.AmountScaled);
            WriteNullableString(source.Description);
            WriteNullableString(source.SourceModule);
            _writer.Write(source.IsManual.HasValue);
            if (source.IsManual is { } isManual)
            {
                _writer.Write(isManual);
            }
            _writer.Write(source.RawJson);
            _writer.Write(source.MatchedPositions.Count);
            foreach (var position in source.MatchedPositions)
            {
                _writer.Write(position);
            }
            RowCount = checked(RowCount + 1);
        }

        internal void Complete()
        {
            ThrowIfDisposed();
            if (_completed)
            {
                throw new InvalidOperationException(
                    "Step4-1 source spool 已完成。");
            }

            _writer.Flush();
            _stream.Position = 0;
            _reader = new BinaryReader(
                _stream,
                Encoding.UTF8,
                leaveOpen: true);
            _completed = true;
        }

        internal IEnumerable<WorkpaperStep41SourceRow> ReadRows(
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_completed || _reader is null)
            {
                throw new InvalidOperationException(
                    "Step4-1 source spool 尚未完成。");
            }
            if (_readStarted)
            {
                throw new InvalidOperationException(
                    "Step4-1 source spool 只能單向 replay 一次。");
            }
            _readStarted = true;

            for (var row = 0L; row < RowCount; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ReadRow(_reader);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                _reader?.Dispose();
            }
            finally
            {
                try
                {
                    _writer.Dispose();
                }
                finally
                {
                    _stream.Dispose();
                }
            }
        }

        private static WorkpaperStep41SourceRow ReadRow(BinaryReader reader)
        {
            var entryId = reader.ReadInt64();
            var documentNumber = ReadNullableString(reader);
            var lineItem = ReadNullableString(reader);
            var postDate = ReadNullableString(reader);
            var approvalDate = ReadNullableString(reader);
            var createdBy = ReadNullableString(reader);
            var approvedBy = ReadNullableString(reader);
            var accountCode = ReadNullableString(reader);
            var accountName = ReadNullableString(reader);
            var amountScaled = reader.ReadInt64();
            var description = ReadNullableString(reader);
            var sourceModule = ReadNullableString(reader);
            var isManual = reader.ReadBoolean()
                ? reader.ReadBoolean()
                : (bool?)null;
            var rawJson = reader.ReadString();
            var positionCount = reader.ReadInt32();
            if (positionCount is < 0 or > 10)
            {
                throw new InvalidDataException(
                    $"Step4-1 source spool tag count '{positionCount}' 無效。");
            }
            var matchedPositions = new int[positionCount];
            for (var index = 0; index < positionCount; index++)
            {
                matchedPositions[index] = reader.ReadInt32();
            }

            return new WorkpaperStep41SourceRow(
                entryId,
                documentNumber,
                lineItem,
                postDate,
                approvalDate,
                createdBy,
                approvedBy,
                accountCode,
                accountName,
                amountScaled,
                description,
                sourceModule,
                isManual,
                rawJson,
                matchedPositions);
        }

        private void WriteNullableString(string? value)
        {
            _writer.Write(value is not null);
            if (value is not null)
            {
                _writer.Write(value);
            }
        }

        private static string? ReadNullableString(BinaryReader reader) =>
            reader.ReadBoolean() ? reader.ReadString() : null;

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    /// <summary>
    /// 單 reader 與 worksheet cols-before-sheetData 約束之間的 bounded bridge。
    /// 只保存已投影 typed cells 與 tag bits，不保存 raw JSON，也不建立第二個 workbook。
    /// </summary>
    private sealed class Step41ProjectedSpool : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private readonly IReadOnlyList<int> _rowHitPositions;
        private readonly double[] _maximums;
        private BinaryReader? _reader;
        private bool _completed;
        private bool _readStarted;
        private bool _disposed;

        private Step41ProjectedSpool(
            FileStream stream,
            IReadOnlyList<WorkpaperStep41Column> columns,
            IReadOnlyList<int> rowHitPositions)
        {
            _stream = stream;
            _writer = new BinaryWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true);
            _rowHitPositions = rowHitPositions;
            _maximums = columns
                .Select(column => ExcelDisplayWidth.Measure(column.Header))
                .Concat(rowHitPositions.Select(position =>
                    ExcelDisplayWidth.Measure($"C{position}_TAG")))
                .ToArray();
            ValueColumnCount = columns.Count;
        }

        internal long RowCount { get; private set; }

        internal int ValueColumnCount { get; }

        internal IReadOnlyList<double> Widths
        {
            get
            {
                if (!_completed)
                {
                    throw new InvalidOperationException(
                        "Step4-1 projected spool 尚未完成，不能讀取欄寬。");
                }

                return _maximums
                    .Select(ExcelDisplayWidth.ToColumnWidth)
                    .ToArray();
            }
        }

        internal static Step41ProjectedSpool Create(
            IReadOnlyList<WorkpaperStep41Column> columns,
            IReadOnlyList<int> rowHitPositions)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $".jet-workpaper-step41-{Guid.NewGuid():N}.tmp");
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            try
            {
                return new Step41ProjectedSpool(stream, columns, rowHitPositions);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal void Append(
            IReadOnlyList<Step41ProjectedValue> values,
            IReadOnlyList<int> matchedPositions)
        {
            ThrowIfDisposed();
            if (_completed)
            {
                throw new InvalidOperationException(
                    "Step4-1 projected spool 已完成，不能再追加列。");
            }
            if (values.Count != ValueColumnCount)
            {
                throw new InvalidDataException(
                    $"Step4-1 projected value count {values.Count} "
                    + $"不等於 schema count {ValueColumnCount}。");
            }

            for (var index = 0; index < values.Count; index++)
            {
                var value = values[index];
                _maximums[index] = Math.Max(
                    _maximums[index],
                    ExcelDisplayWidth.Measure(value.RenderedText));
                if (value.NumberLiteral is { } numberLiteral)
                {
                    _writer.Write((byte)2);
                    _writer.Write(value.Style);
                    _writer.Write(numberLiteral);
                }
                else if (value.Text is { } text)
                {
                    _writer.Write((byte)1);
                    _writer.Write(value.Style);
                    _writer.Write(text);
                }
                else
                {
                    _writer.Write((byte)0);
                    _writer.Write(value.Style);
                }
            }

            var tagMask = 0;
            for (var index = 0; index < _rowHitPositions.Count; index++)
            {
                if (!matchedPositions.Contains(_rowHitPositions[index]))
                {
                    continue;
                }

                tagMask |= 1 << index;
                _maximums[ValueColumnCount + index] = Math.Max(
                    _maximums[ValueColumnCount + index],
                    1);
            }
            _writer.Write(tagMask);
            RowCount = checked(RowCount + 1);
        }

        internal void Complete()
        {
            ThrowIfDisposed();
            if (_completed)
            {
                throw new InvalidOperationException(
                    "Step4-1 projected spool 已完成。");
            }

            _writer.Flush();
            _stream.Position = 0;
            _reader = new BinaryReader(
                _stream,
                Encoding.UTF8,
                leaveOpen: true);
            _completed = true;
        }

        internal IEnumerable<Step41SpoolRow> ReadRows(
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_completed || _reader is null)
            {
                throw new InvalidOperationException(
                    "Step4-1 projected spool 尚未完成。");
            }
            if (_readStarted)
            {
                throw new InvalidOperationException(
                    "Step4-1 projected spool 只能單向 replay 一次。");
            }
            _readStarted = true;

            for (var row = 0L; row < RowCount; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new Step41ProjectedValue[ValueColumnCount];
                for (var index = 0; index < ValueColumnCount; index++)
                {
                    var kind = _reader.ReadByte();
                    var style = _reader.ReadUInt32();
                    values[index] = kind switch
                    {
                        0 => new Step41ProjectedValue(
                            null,
                            null,
                            style,
                            string.Empty),
                        1 => new Step41ProjectedValue(
                            _reader.ReadString(),
                            null,
                            style,
                            string.Empty),
                        2 => new Step41ProjectedValue(
                            null,
                            _reader.ReadString(),
                            style,
                            string.Empty),
                        _ => throw new InvalidDataException(
                            $"Step4-1 projected spool cell kind '{kind}' 無效。")
                    };
                }
                yield return new Step41SpoolRow(values, _reader.ReadInt32());
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                _reader?.Dispose();
            }
            finally
            {
                try
                {
                    _writer.Dispose();
                }
                finally
                {
                    _stream.Dispose();
                }
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private static SheetWriter CreateLegacyStep41Page(
        WorkpaperWriteSession session,
        WorkpaperContext context,
        TagColumnSet tagColumns,
        int pageNumber)
    {
        var sheet = session.OpenSheet(
            ExcelWorksheetConstraints.ContinuationSheetName(WorkpaperSheetCatalog.Step41, pageNumber));
        ConfigureLegacyStep41Layout(sheet);

        sheet.WriteFixedRow(1, [sheet.TextCell(1, 1, $"公司名稱 : {context.CompanyName}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(2, [sheet.TextCell(2, 1,
            $"財務報表期間 :  {DigitsOnly(context.PeriodStart)} ~ {DigitsOnly(context.PeriodEnd)}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(3, [sheet.TextCell(3, 1, "符合高風險範圍條件之傳票明細", WorkpaperStyles.Bold)]);

        var headerCells = new List<Cell>();
        var fixedLabels = new[]
        {
            "DOCUMENT_NUM_JE", "DOCUMENT_LINE_ID_JE", "POSTING_DATE_JE", "DOCUMENT_DATE_JE",
            "ACCOUNT_NUM_JE", "ACCOUNT_DESC_JE", "DESC_JE", "CREATE_BY_JE",
            "APPROVE_BY_JE", "DEBIT_AMOUNT_JE", "CREDIT_AMOUNT_JE", "MANUAL_JE",
            "SOURCE_JE", "自建欄位1", "自建欄位2", "自建欄位3"
        };
        for (var index = 0; index < fixedLabels.Length; index++)
        {
            headerCells.Add(sheet.TextCell(5, (uint)(index + 1), fixedLabels[index], WorkpaperStyles.Bold));
        }

        tagColumns.AppendRowHitPositionHeaders(sheet, headerCells, 5);
        sheet.WriteFixedRow(5, headerCells);
        return sheet;
    }

    private static SheetStat CloseStep41Page(
        SheetWriter sheet,
        long rows,
        CancellationToken cancellationToken)
    {
        sheet.Protect();
        return sheet.CloseAndSummarizeWith(rows, cancellationToken);
    }

    private static uint CheckedLastDataRow(uint firstDataRow, long rows)
    {
        if (rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }

        var last = checked((long)firstDataRow + rows - 1);
        if (last > ExcelWorksheetConstraints.MaxRows)
        {
            throw new InvalidOperationException(
                $"WorkingPaper data row {last} exceeds Excel's {ExcelWorksheetConstraints.MaxRows}-row limit.");
        }

        return (uint)last;
    }

    private static void ConfigureStep2Layout(
        SheetWriter sheet,
        IReadOnlyList<double> dataWidths)
    {
        sheet.SetDefaultRowHeight(18);
        ApplyDisplayWidths(sheet, firstColumn: 1, widths: dataWidths);
        sheet.SetRowHeight(49, 38);
        sheet.SetRowHeight(50, 28);
        sheet.SetRowHeight(51, 48);
        sheet.SetRowHeight(52, 24);
    }

    private static void ConfigureStep3Layout(
        SheetWriter sheet,
        IReadOnlyList<double> dataWidths)
    {
        sheet.SetDefaultRowHeight(18);
        sheet.SetColumnWidth(1, 1, 12);
        ApplyDisplayWidths(sheet, firstColumn: 2, widths: dataWidths);
        sheet.SetRowHeight(18, 34);
    }

    private static void ConfigureStep4Layout(
        SheetWriter sheet,
        IReadOnlyList<double> dataWidths)
    {
        sheet.SetDefaultRowHeight(18);
        ApplyDisplayWidths(sheet, firstColumn: 1, widths: dataWidths);
        sheet.SetRowHeight(10, 32);
        sheet.SetRowHeight(11, 42);
        sheet.SetRowHeight(12, 32);
    }

    private static void ConfigureLegacyStep41Layout(SheetWriter sheet)
    {
        sheet.SetDefaultRowHeight(18);
        sheet.SetColumnWidth(1, 1, 18);
        sheet.SetColumnWidth(2, 2, 11);
        sheet.SetColumnWidth(3, 3, 14);
        sheet.SetColumnWidth(4, 5, 18);
        sheet.SetColumnWidth(6, 6, 15);
        sheet.SetColumnWidth(7, 7, 24);
        sheet.SetColumnWidth(8, 8, 15);
        sheet.SetColumnWidth(9, 9, 38);
        sheet.SetColumnWidth(10, 19, 9);
        sheet.SetColumnWidth(20, 21, 14);
        sheet.SetColumnWidth(22, 23, 22);
        sheet.SetColumnWidth(24, 26, 14);
        sheet.SetRowHeight(5, 42);
    }

    // ---- step2/3/4 共用表頭 + 固定文字寫入 ----

    /// <summary>
    /// 證實測試表頭(step2/3/4 共用前三列):A1 公司名 / A2 財務報表期間 / A3 副標題。
    /// 與 step1 家族的 <see cref="WriteCommonHeader"/> 不同(那是「測試資料期間/財報準備開始日」),
    /// 故另立——這是兩組不同的固定表頭,非可統一的重複(對齊樣本各表逐字)。
    /// </summary>

    private static void WriteSubstantiveHeader(
        SheetWriter sheet, WorkpaperContext context, string subtitle, bool omitSubtitle = false)
    {
        sheet.WriteFixedRow(1, [sheet.TextCell(1, 1, $"公司名稱 : {context.CompanyName}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(2, [sheet.TextCell(2, 1,
            $"財務報表期間 :  {DigitsOnly(context.PeriodStart)} ~ {DigitsOnly(context.PeriodEnd)}", WorkpaperStyles.Bold)]);
        if (!omitSubtitle)
        {
            sheet.WriteFixedRow(3, [sheet.TextCell(3, 1, subtitle, WorkpaperStyles.Bold)]);
        }
        else
        {
            sheet.WriteFixedRow(3, [sheet.TextCell(3, 1, "證實測試 - 會計分錄及其他調整", WorkpaperStyles.Bold)]);
        }
    }

    /// <summary>逐列寫固定文字(列號/欄/文字三元組);供 step2/3/4 表頭大段 boilerplate 共用。</summary>
    private static void WriteFixedLines(SheetWriter sheet, IReadOnlyList<(uint Row, uint Column, string Text)> lines)
    {
        foreach (var (row, column, text) in lines)
        {
            sheet.WriteFixedRow(row, [sheet.TextCell(row, column, text, WorkpaperStyles.Bold)]);
        }
    }

    /// <summary>step2 第 49-52 列多層欄標(逐字對齊樣本;合併範圍見 Step2HeaderMerges)。</summary>
    private static void WriteStep2ColumnHeaders(
        SheetWriter sheet,
        IReadOnlyList<GlRdeFieldMetadata> customFields)
    {
        var row49 = new List<Cell>
        {
            sheet.TextCell(49, 1, "樣本編號", WorkpaperStyles.BoldWrap),
            sheet.TextCell(49, 3, "擬測試其正確性的攸關資料元素(即RDE，請詳上述說明1~3)", WorkpaperStyles.BoldWrap),
            sheet.TextCell(49, 13, "測試結果(若左列預設的欄位屬於高風險條件的RDE，則應執行A~G測試程序，不得勾選N/A)", WorkpaperStyles.BoldWrap),
            sheet.TextCell(49, 20, "說明詳細測試過程", WorkpaperStyles.BoldWrap)
        };
        if (customFields.Count > 0)
        {
            row49.Add(sheet.TextCell(
                49,
                21,
                "所選高風險條件引用RDE",
                WorkpaperStyles.BoldWrap));
        }
        sheet.WriteFixedRow(49, row49);
        sheet.WriteFixedRow(50,
        [
            sheet.TextCell(50, 3, "財務類型RDE(對應測試結果A)", WorkpaperStyles.BoldWrap),
            sheet.TextCell(50, 7, "非財務類型RDE(對應測試結果B~G)", WorkpaperStyles.BoldWrap)
        ]);
        var row51 = new List<Cell>
        {
            sheet.TextCell(51, 2, "傳票號碼\n(分錄編號)", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 3, "會計科目編號", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 4, "會計科目名稱", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 5, "借方金額", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 6, "貸方金額", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 7, "總帳日期", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 8, "傳票核准日期\n(若有)", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 9, "分錄編製人員\n(或過帳人員)", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 10, "分錄來源或\n人工/自動分錄判斷(若有)", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 11, "分錄備註/說明", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 12, "傳票核准人員", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 14, "非財務類型", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 20, "(例如：向誰詢問、觀察或檢視哪些內容或註記TickMark說明核至哪些相關文件。)", WorkpaperStyles.BoldWrap)
        };
        for (var index = 0; index < customFields.Count; index++)
        {
            row51.Add(sheet.TextCell(
                51,
                checked((uint)(21 + index)),
                customFields[index].Label,
                WorkpaperStyles.BoldWrap));
        }
        sheet.WriteFixedRow(51, row51);
        sheet.WriteFixedRow(52,
        [
            sheet.TextCell(52, 13, "A", WorkpaperStyles.BoldWrap),
            sheet.TextCell(52, 14, "B", WorkpaperStyles.BoldWrap),
            sheet.TextCell(52, 15, "C", WorkpaperStyles.BoldWrap),
            sheet.TextCell(52, 16, "D", WorkpaperStyles.BoldWrap),
            sheet.TextCell(52, 17, "E", WorkpaperStyles.BoldWrap),
            sheet.TextCell(52, 18, "F", WorkpaperStyles.BoldWrap),
            sheet.TextCell(52, 19, "G", WorkpaperStyles.BoldWrap)
        ]);
    }

    /// <summary>
    /// step2 資料列:A 樣本序號(列序,emitter 給) / B 傳票號 / C 科目編號 / D 名稱 /
    /// E 原始借貸代號（若有）/ F 帶號金額(DebitScaled-CreditScaled→顯示) / G 總帳日 / H 核准日 /
    /// I 編製人員 / J 原始來源模組（若有）/ K 摘要 / L 核准人員；M-S 結果A-G、T 說明(手填留空)；
    /// U 起依 committed ordinal 附加所選情境引用的 RDE union。
    /// </summary>
    private static IReadOnlyList<WorkpaperDisplayCell> ProjectStep2Sample(
        long ordinal,
        InfSampleRow sample,
        string? direction,
        string? sourceModule,
        int moneyScale,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        IReadOnlyDictionary<string, string?> rdeValues)
    {
        var cells = new List<WorkpaperDisplayCell>(20 + customFields.Count)
        {
            ProjectInteger(ordinal),
            ProjectText(sample.DocumentNumber),
            ProjectText(sample.AccountCode),
            ProjectText(sample.AccountName),
            ProjectText(direction),
            ProjectAmount(sample.DebitScaled - sample.CreditScaled, moneyScale),
            ProjectText(sample.PostDate),
            ProjectText(sample.ApprovalDate),
            ProjectText(sample.CreatedBy),
            ProjectText(sourceModule),
            ProjectText(sample.Description),
            ProjectText(sample.ApprovedBy),
            ProjectBlank(WorkpaperStyles.Editable),
            ProjectBlank(WorkpaperStyles.Editable),
            ProjectBlank(WorkpaperStyles.Editable),
            ProjectBlank(WorkpaperStyles.Editable),
            ProjectBlank(WorkpaperStyles.Editable),
            ProjectBlank(WorkpaperStyles.Editable),
            ProjectBlank(WorkpaperStyles.Editable),
            ProjectBlank(WorkpaperStyles.Editable)
        };
        foreach (var field in customFields)
        {
            cells.Add(ProjectStep2RdeCell(
                field,
                rdeValues[field.FieldId],
                moneyScale));
        }
        return cells;
    }

    private static WorkpaperDisplayCell ProjectStep2RdeCell(
        GlRdeFieldMetadata field,
        string? value,
        int moneyScale)
    {
        if (string.IsNullOrEmpty(value))
        {
            return ProjectBlank(Step2RdeCellStyle(field, moneyScale));
        }

        return field.ValueType switch
        {
            RdeFieldValueTypeNames.Text => ProjectText(value),
            RdeFieldValueTypeNames.Date => ProjectStep2RdeDate(field, value),
            RdeFieldValueTypeNames.Money => ProjectStep2RdeMoney(field, value, moneyScale),
            _ => throw new InvalidDataException(
                $"step2 RDE field '{field.FieldId}' has unsupported value type '{field.ValueType}'.")
        };
    }

    private static WorkpaperDisplayCell ProjectStep2RdeDate(
        GlRdeFieldMetadata field,
        string value)
    {
        if (!DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            throw new InvalidDataException(
                $"step2 RDE field '{field.FieldId}' has invalid date '{value}'.");
        }

        return new WorkpaperDisplayCell(
            Text: null,
            NumberLiteral: date.ToDateTime(TimeOnly.MinValue).ToOADate().ToString(
                "R",
                CultureInfo.InvariantCulture),
            Style: WorkpaperStyles.Date,
            RenderedText: value);
    }

    private static WorkpaperDisplayCell ProjectStep2RdeMoney(
        GlRdeFieldMetadata field,
        string value,
        int moneyScale)
    {
        if (!decimal.TryParse(
                value,
                NumberStyles.Number | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            throw new InvalidDataException(
                $"step2 RDE field '{field.FieldId}' has invalid money '{value}'.");
        }

        var decimals = Step2RdeMoneyDecimalPlaces(moneyScale);
        var format = NumberFormatCode(decimals, grouped: true);
        return new WorkpaperDisplayCell(
            Text: null,
            NumberLiteral: amount.ToString(CultureInfo.InvariantCulture),
            Style: WorkpaperStyles.GroupedNumber(decimals),
            RenderedText: amount.ToString(format, CultureInfo.InvariantCulture));
    }

    private static uint Step2RdeCellStyle(GlRdeFieldMetadata field, int moneyScale) =>
        field.ValueType switch
        {
            RdeFieldValueTypeNames.Text => WorkpaperStyles.Default,
            RdeFieldValueTypeNames.Date => WorkpaperStyles.Date,
            RdeFieldValueTypeNames.Money => WorkpaperStyles.GroupedNumber(
                Step2RdeMoneyDecimalPlaces(moneyScale)),
            _ => throw new InvalidDataException(
                $"step2 RDE field '{field.FieldId}' has unsupported value type '{field.ValueType}'.")
        };

    private static int Step2RdeMoneyDecimalPlaces(int moneyScale)
    {
        if (moneyScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(moneyScale));
        }

        var remainder = moneyScale;
        var decimals = 0;
        while (remainder > 1 && remainder % 10 == 0)
        {
            remainder /= 10;
            decimals++;
        }
        if (remainder != 1 || decimals > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(moneyScale),
                "WorkingPaper step2 RDE money requires a power-of-ten MoneyScale with at most four decimals.");
        }
        return decimals;
    }

    // ---- step4/4-1 動態欄列串流(列 + 該列命中位置配對;沿用 keyset 不全載入)----

    /// <summary>
    /// step4 傳票層:逐頁 tagMatrixVoucherPage，查詢母體與 matchedPositions 都限定在
    /// context.ScenarioPositions；每動態欄依該傳票是否含該 position 標 Y。
    /// 傳票總額由 repository 以「任一所選情境命中的傳票之完整 GL 借方」聚合。
    /// 編號由呼叫端 nextOrdinal 累計。
    /// </summary>
    private async IAsyncEnumerable<TaggedVoucherRow> StreamTaggedVoucherRowsAsync(
        WorkpaperContext context,
        IReadOnlyList<int> scenarioPositions,
        Func<long> nextOrdinal,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var populationContext = new GlPopulationContext(
            context.PopulationScope,
            context.PeriodStart,
            context.PeriodEnd);
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (page, positionsByDoc) = await tagMatrixVouchers.GetPageAsync(
                context.ProjectId, populationContext, new PageRequest(cursor, PageRequest.DefaultPageSize),
                scenarioPositions, cancellationToken);

            foreach (var voucher in page.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matched = voucher.DocumentNumber is not null
                    && positionsByDoc.TryGetValue(voucher.DocumentNumber, out var p)
                    ? p
                    : (IReadOnlyList<int>)[];
                var ordinal = nextOrdinal();
                yield return new TaggedVoucherRow(voucher, matched, ordinal);
            }

            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    /// <summary>
    /// step4-1 行層:逐頁 tagMatrixRowPage，命中傳票母體與 matchedPositions 都限定在
    /// context.ScenarioPositions；但入選傳票的完整 GL 行仍全數列出。每動態 C*_TAG 欄依該行
    /// 是否含該 position 標 Y。
    /// </summary>
    private async IAsyncEnumerable<TaggedRowDetail> StreamTaggedRowDetailRowsAsync(
        WorkpaperContext context,
        IReadOnlyList<int> scenarioPositions,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var populationContext = new GlPopulationContext(
            context.PopulationScope,
            context.PeriodStart,
            context.PeriodEnd);
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (page, entryIds, positionsByEntry) = await tagMatrixRows.GetPageAsync(
                context.ProjectId, populationContext, new PageRequest(cursor, PageRequest.DefaultPageSize),
                scenarioPositions, cancellationToken);

            for (var i = 0; i < page.Rows.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var detail = page.Rows[i];
                var matched = positionsByEntry.TryGetValue(entryIds[i], out var p) ? p : (IReadOnlyList<int>)[];
                yield return new TaggedRowDetail(detail, matched);
            }

            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    /// <summary>
    /// Finalized step4 production path：一次 ordered forward-only stream 取回全部命中傳票；
    /// 公開 query 的 200-row paging 僅保留給 plan-less compatibility 與 UI。
    /// </summary>
    private async IAsyncEnumerable<TaggedVoucherRow> StreamFinalizedStep4VoucherRowsAsync(
        WorkpaperContext context,
        IReadOnlyList<int> scenarioPositions,
        Func<long> nextOrdinal,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        if (_step4Vouchers is null)
        {
            throw new InvalidOperationException(
                "WorkingPaper step4 stream repository 未設定。");
        }

        var populationContext = new GlPopulationContext(
            context.PopulationScope,
            context.PeriodStart,
            context.PeriodEnd);
        await foreach (var row in _step4Vouchers.StreamAsync(
                           context.ProjectId,
                           populationContext,
                           scenarioPositions,
                           cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TaggedVoucherRow(
                row.Voucher,
                row.MatchedPositions,
                nextOrdinal());
        }
    }

    private async IAsyncEnumerable<WorkpaperStep41SourceRow> StreamWorkpaperStep41RowsAsync(
        WorkpaperContext context,
        IReadOnlyList<int> scenarioPositions,
        LegacyFieldKind lineItemKind,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_step41Rows is null)
        {
            throw new InvalidOperationException(
                "WorkingPaper step4-1 typed page repository 未設定。");
        }

        var populationContext = new GlPopulationContext(
            context.PopulationScope,
            context.PeriodStart,
            context.PeriodEnd);
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _step41Rows.GetPageAsync(
                context.ProjectId,
                populationContext,
                new PageRequest(cursor, PageRequest.DefaultPageSize),
                scenarioPositions,
                lineItemKind,
                cancellationToken);
            foreach (var row in page.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    private readonly record struct TaggedVoucherRow(
        VoucherTagRow Voucher,
        IReadOnlyList<int> MatchedPositions,
        long Ordinal);

    private readonly record struct TaggedRowDetail(
        RowTagRow Detail,
        IReadOnlyList<int> MatchedPositions);

    private static IReadOnlyList<string?> Step4DisplayHeaders(TagColumnSet tagColumns)
    {
        var headers = new string?[21];
        headers[0] = "編號";
        headers[1] = "傳票號碼";
        headers[2] = "總帳日期";
        headers[3] = "編製者";
        headers[4] = "傳票總金額";
        foreach (var position in tagColumns.AllPositions)
        {
            var column = checked(5 + position);
            if (column is < 6 or > 15)
            {
                throw new InvalidDataException(
                    $"WorkingPaper step4 scenario position {position} is outside 1..10.");
            }
            headers[column - 1] = $"C{position}";
        }

        var reviewHeaders = new[]
        {
            "覆核結果",
            "附件索引",
            "差異說明",
            "處理結論",
            "覆核人",
            "備註"
        };
        for (var index = 0; index < reviewHeaders.Length; index++)
        {
            headers[15 + index] = reviewHeaders[index];
        }

        return headers;
    }

    private static IReadOnlyList<double> Step4MinimumWidths() =>
    [
        8D,
        18D,
        14D,
        18D,
        Math.Max(30.453125, MaximumAmountDisplayColumnWidth),
        9D,
        9D,
        9D,
        9D,
        9D,
        9D,
        9D,
        9D,
        9D,
        9D,
        14D,
        14D,
        22D,
        22D,
        14D,
        14D
    ];

    /// <summary>step4 傳票列:固定 A-E + 動態 C{pos} 欄(matchedPositions 含該 position→Y);P-U 手填留空。</summary>
    private static IReadOnlyList<WorkpaperDisplayCell> ProjectStep4Voucher(
        TaggedVoucherRow tagged,
        TagColumnSet tagColumns,
        int moneyScale)
    {
        var values = Enumerable.Range(0, 21)
            .Select(_ => ProjectBlank())
            .ToArray();
        values[0] = ProjectInteger(tagged.Ordinal);
        values[1] = ProjectText(tagged.Voucher.DocumentNumber);
        values[2] = ProjectText(tagged.Voucher.PostDate);
        values[3] = ProjectText(tagged.Voucher.CreatedBy);
        values[4] = ProjectAmount(tagged.Voucher.VoucherTotalScaled, moneyScale);
        foreach (var position in tagColumns.AllPositions)
        {
            var column = checked(5 + position);
            if (column is < 6 or > 15)
            {
                throw new InvalidDataException(
                    $"WorkingPaper step4 scenario position {position} is outside 1..10.");
            }
            values[column - 1] = tagged.MatchedPositions.Contains(position)
                ? ProjectText("Y")
                : ProjectBlank();
        }
        for (var index = 15; index < values.Length; index++)
        {
            values[index] = ProjectBlank(WorkpaperStyles.Editable);
        }

        return values;
    }

    /// <summary>
    /// step4-1 行列依 legacy 範本固定 A-P：借貸金額由 signed amount 拆至 J/K，
    /// L-P 無來源欄位時保持空白；Q-Z 依 scenario position 固定槽位標記。
    /// </summary>
    private static IReadOnlyList<Cell> Step41RowCells(
        SheetWriter sheet, uint row, RowTagRow detail,
        TagColumnSet tagColumns, IReadOnlyList<int> matched, int moneyScale)
    {
        var amount = Display(detail.AmountScaled, moneyScale);
        var cells = new List<Cell>
        {
            sheet.TextCell(row, 1, detail.DocumentNumber),
            sheet.TextCell(row, 2, detail.LineItem),
            sheet.TextCell(row, 3, detail.PostDate),
            sheet.TextCell(row, 4, detail.ApprovalDate),
            sheet.TextCell(row, 5, detail.AccountCode),
            sheet.TextCell(row, 6, detail.AccountName),
            sheet.TextCell(row, 7, detail.Description),
            sheet.TextCell(row, 8, detail.CreatedBy),
            sheet.TextCell(row, 9, detail.ApprovedBy),
            detail.AmountScaled >= 0
                ? sheet.NumberCell(row, 10, amount, WorkpaperStyles.Amount)
                : sheet.BlankCell(row, 10),
            detail.AmountScaled < 0
                ? sheet.NumberCell(row, 11, -amount, WorkpaperStyles.Amount)
                : sheet.BlankCell(row, 11),
            sheet.BlankCell(row, 12),
            sheet.BlankCell(row, 13),
            sheet.BlankCell(row, 14, WorkpaperStyles.Editable),
            sheet.BlankCell(row, 15, WorkpaperStyles.Editable),
            sheet.BlankCell(row, 16, WorkpaperStyles.Editable)
        };
        tagColumns.AppendRowHitPositionMarks(sheet, cells, row, matched);
        return cells;
    }

    // ================= 資料表共用骨架(DRY=3)=================

    /// <summary>
    /// 可能超過 Excel 列上限的資料表共用續頁骨架。每頁重建固定表頭與欄標；普通規模只建立正準 sheet 名，
    /// 第 2 頁起才加「(續N)」。列來源保持 forward-only，換頁不重查、不全載入。
    /// </summary>

    private static readonly IReadOnlyList<(uint Row, uint Column, string Text)> Step2HeaderLines =
    [
        (6, 1, "測試說明："),
        (6, 3, "1. 依照KAEG-I [ISA | 815.13507]，JE攸關母體有納入高風險條件(HRC)的欄位即攸關資料元素(RDE)，需先確認RDE的可靠性後，方執行高風險篩選條件。\n    若為初步篩選(Screening)之預篩選程序，因屬於風險評估，則於該階段可不用確認RDE可靠性。"),
        (7, 3, "2. 上述提到之高風險條件的RDE大多屬於非財務性質，例如篩選條件考量過帳日期、分錄摘要的關鍵字、分錄標註為人工分錄者、特定人員。"),
        (8, 3, "3. 若高風險條件除上述非財務性質欄位外，亦有使用會計科目編號、科目名稱或金額等欄位來進行篩選，則這些納入篩選條件的欄位亦屬於RDE。"),
        (9, 3, "4. JE的RDE可靠性確認包括確認完整性及正確性，由於完整性已於JE攸關母體完整性測試程序中執行，故此處可靠性測試係針對JE RDE的正確性進行測試。"),
        (10, 3, "5. 依照KAEG-I [ISA | 2701.1500]，需依照屬性抽樣表格[ISA | 4164.1300]選取樣本(選樣方法可採隨機、隨意或系統抽樣)核對會計傳票附件以確認RDE的正確性。"),
        (11, 3, "   (由於JE具有管理階層逾越控制之顯著風險，因此其固有風險為Significant，對照上述表格後的最低測試樣本量為59筆。)"),
        (13, 1, "測試程序：(若JE高風險條件(HRC)不包含下列A~G程序提到的欄位，則該測試程序可設為N/A)"),
        (14, 1, "- 財務類型RDE (如會計科目編號、科目名稱、借貸方代號、分錄金額)"),
        (15, 1, "A."),
        (15, 3, "確認是否已於JE母體完整性測試時，完成此類RDE的測試。若無，則確認分錄金額是否與傳票附件符合，其餘則核至已核准的會計科目表。"),
        (16, 3, "(此類型RDE通常已於JE母體完整性測試過程與TB(試算表)比對時完成可靠性測試)"),
        (18, 1, "- 非財務類型RDE"),
        (19, 1, "B."),
        (19, 2, "過帳日期/過帳時間：屬內部交易過帳者，核至經核准的內部交易日期。若屬於外部交易者，則核對相關交易憑證的日期"),
        (20, 1, "C."),
        (20, 2, "傳票建立日期/分錄時間：根據案件情況及所選樣本來判斷選擇以下一項或多項程序來執行，以驗證其可靠性。"),
        (27, 1, "D."),
        (27, 2, "分錄編製人員(或過帳人員)：根據案件情況及所選樣本來判斷選擇以下一項或多項程序來執行，以驗證其可靠性。"),
        (34, 1, "E."),
        (34, 2, "分錄來源：核對分錄至傳票附件內容(若為人工分錄)與受查者系統畫面資訊(若為自動分錄)，以確認是否符合其所標註的分錄來源或是對於人工/自動分錄之標記。"),
        (36, 1, "F."),
        (36, 2, "分錄備註/說明：選取以下一項或多項程序來執行，以驗證其可靠性。"),
        (40, 1, "G."),
        (40, 2, "除上述以外之RDE(勾選以下適合程序來執行測試)"),
        (45, 2, "註1:"),
        (45, 3, "建議透過詢問JE資料流與流程作業，來決定JE高風險範圍條件所要篩選的日期要使用過帳日(Posting date)還是編製日/立帳日(Create date/Document date)。"),
        (46, 2, "註2:"),
        (46, 3, "若下列樣本未涵蓋自動分錄且自動分錄未於其他程序執行測試者，當高風險條件有篩選到自動分錄，應補執行上述對自動分錄提到的程序。"),
        (47, 2, "註3:"),
        (47, 3, "若有其他用在高風險條件的RDE欄位，請自行於工作表：可靠性樣本_所有欄位中複製貼上至此底稿中。"),
    ];

    /// <summary>step2 表頭合併範圍(逐字對齊樣本;欄標多層合併)。</summary>
    private static IReadOnlyList<string> Step2HeaderMerges(int customFieldCount)
    {
        var ranges = new List<string>
        {
            "C6:K6",
            "A49:A52", "C49:L49", "M49:S50", "T49:T50",
            "C50:F50", "G50:L50",
            "B51:B52", "C51:C52", "D51:D52", "E51:E52", "F51:F52", "G51:G52", "H51:H52",
            "I51:I52", "J51:J52", "K51:K52", "L51:L52", "N51:S51", "T51:T52"
        };
        if (customFieldCount <= 0)
        {
            return ranges;
        }

        var firstCustomColumn = 21U;
        var lastCustomColumn = checked(firstCustomColumn + (uint)customFieldCount - 1);
        ranges.Add(
            $"{ReportSheetWriter.Reference(firstCustomColumn, 49)}:"
            + ReportSheetWriter.Reference(lastCustomColumn, 50));
        for (var column = firstCustomColumn; column <= lastCustomColumn; column++)
        {
            ranges.Add(
                $"{ReportSheetWriter.Reference(column, 51)}:"
                + ReportSheetWriter.Reference(column, 52));
        }
        return ranges;
    }

    /// <summary>step3 測試目的 / 範圍 / 理由 / 風險評估文字；母體相關兩列依本 revision scope 明示。</summary>
    private static IReadOnlyList<(uint Row, uint Column, string Text)> Step3HeaderLines(
        WorkpaperContext context)
    {
        if (context.PopulationScope != GlPopulationScope.AuditPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context), context.PopulationScope, "未知的 GL 母體範圍。");
        }

        var range = $"查核期間內（{context.PeriodStart} ~ {context.PeriodEnd}）之會計分錄";
        const string rationale =
            "本次高風險條件以專案查核期間內的會計分錄為母體；查核期間外與無有效過帳日之列不納入本版情境命中與矩陣。";

        return
        [
            (5, 1, "測試目的"),
            (5, 2, "取得查核證據，以測試會計分錄及其他調整：\ni)  不存在因舞弊所導致的重大不實表達；\nii) 有適當的支持性文件；\niii) 反映了相關的事項、情況和交易，並且\niv) 按照財務報導架構，記錄在正確的會計期間。"),
            (7, 1, "測試範圍"),
            (7, 2, range),
            (9, 1, "選擇該測試範圍的理由"),
            (9, 2, rationale),
            (14, 1, "Step 3"),
            (15, 1, "辨認高風險條件"),
            (15, 2, "風險評估及查核程序："),
            (16, 2, "查核團隊基於下列程序來辨識JE測試之攸關母體及高風險範圍條件：\n•在風險評估與查核團隊討論及計畫討論管理階層踰越控制風險，包含分錄及其他調整\n•了解分錄及其他調整步驟\n•特別詢問處理分錄的會計人員，以及詢問管理階層或其他人員\n•了解交易模式\n•辨認舞弊風險及舞弊因子\n•蒐集查核與特別矛盾之發現，以及辨認舞弊可能已經發生情況之證據\n\n根據上述程序，查核團隊已將所辨認出高風險範圍條件記錄於下方表格。"),
            (17, 2, "辨認出高風險範圍條件，並篩選符合條件之分錄進行測試(相關之傳票分錄明細將列在step4)"),
        ];
    }

    /// <summary>step3 固定文字合併範圍(逐字對齊樣本)。</summary>
    private static readonly IReadOnlyList<string> Step3HeaderMerges =
    [
        "B5:E5", "B7:E7", "A9:A12", "B9:E12", "B15:E15", "B16:E16"
    ];

    /// <summary>step4 測試目的 / 測試程序 + 矩陣區段標題(欄標另見 EmitStep4Async)。</summary>
    private static readonly IReadOnlyList<(uint Row, uint Column, string Text)> Step4HeaderLines =
    [
        (5, 1, "測試目的"),
        (5, 2, "取得查核證據，以測試會計分錄及其他調整：\ni)  不存在因舞弊所導致的重大不實表達；\nii) 有適當的支持性文件；\niii) 反映了相關的事項、情況和交易，並且\niv) 按照財務報導架構，記錄在正確的會計期間。"),
        (7, 1, "測試程序"),
        (7, 2, "A.核至相關傳票之複核紀錄，以確認該分錄之過帳係經適當核准。\nB.核至相關傳票附件，以確認該分錄所載內容與附件一致，分錄係記錄於正確的\n    會計期間，並確認依附件所登錄的科目係屬適當及相關。\nC.詢問負責人員編製分錄之細節，以確認該分錄編製無存在不合理之情形。"),
        (10, 6, "決定進行測試之高風險範圍條件"),
        (10, 16, "查核程序"),
        (10, 19, "有無舞弊\n或不實表達\n(Yes/No)"),
    ];

    /// <summary>step4 固定文字合併範圍(逐字對齊樣本)。</summary>
    private static readonly IReadOnlyList<string> Step4HeaderMerges =
    [
        "B5:E5", "B7:E7", "P10:R10", "S10:S11"
    ];

    private const string Step4SelectionNote =
        "因為設定高風險範圍條件，從母體#2挑選之分錄傳票(執行重大性或其他固定金額不應作為挑選的門檻)";

    /// <summary>
    /// 欄位資訊 GL 段尾的衍生旗標欄(對齊本機參考樣本 A49-A52)。原工具於匯入 precompute 這些 K_ 旗標欄並登錄於
    /// Field Mapping Info;JET 以 set-based SQL 即時算規則,無實體欄,故此處以固定字面登錄其存在(對齊樣本版面)。
    /// </summary>
}
