using System.Globalization;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

internal sealed partial class WorkingPaperWriter
{
    // ================= step2、step3、step4 與 step4-1：可靠性測試與高風險條件彙總的工作表 =================

    /// <summary>
    /// 「step2 可靠性測試」:樣本以上的說明、測試程序與欄標都是範本的原字。JET 只寫 A1、A2、
    /// A4 範本原句後的隨機種子序號、配對了傳票核准人員時的 L51，以及 U 欄起攸關資料元素欄的標題。
    /// 樣本從第 53 列起寫；樣本用一個查詢讀完，再每批讀原始 GL 列與攸關資料元素的值。
    /// A 欄的樣本序號是列的順序 1 到 N；J 欄分錄來源與 E 欄借貸代號由 InfSampleDisplayValues 決定；T 欄說明留給審計員填寫。
    /// </summary>
    private async Task EmitInfSamplingAsync(
        WorkingPaperWriteSession session, List<SheetStat> stats,
        WorkingPaperContext context,
        ReportWorkbookMetadata? workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var glMapping = await mappingStates.FindAsync(
            context.ProjectId,
            DatasetKind.Gl,
            cancellationToken);
        // 攸關資料元素的表頭照舊版加 _JE，撞名用整份配對判斷。
        var rdeHeaders = GlRdeOutputNames.For(customFields, workbookMetadata?.GlMapping.GlOptions?.RdeFields);
        // 這份清單只用來估欄寬，欄標的字在範本裡。
        var headers = new string?[]
            {
                "樣本編號",
                "傳票號碼\n(分錄編號)",
                "會計科目編號",
                "會計科目名稱",
                "借貸方\n代號\n(若有)",
                "金額 - 借(貸)",
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
            .Concat(rdeHeaders)
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
        using var spool = WorkingPaperDisplaySpool.Create(
            headers,
            minimumWidths);
        await FillDisplaySpoolAsync(
            spool,
            StreamInfSamplingRowsAsync(
                context,
                glMapping,
                workbookMetadata,
                customFields,
                cancellationToken),
            cancellationToken);

        // 舊版只在配對了傳票核准人員時把 L51 改成「傳票核准人員」；
        // 沒配對時保留範本原字，L 欄也沒有資料。
        var approverMapped = glMapping is not null
            && glMapping.Mapping.TryGetValue(GlMappingKeys.ApprovedBy, out var approverSource)
            && !string.IsNullOrWhiteSpace(approverSource);
        using var sheet = session.OpenSheet(
            WorkingPaperSheetCatalog.InfSampling,
            keptTemplateCells: approverMapped ? null : new HashSet<(uint Row, uint Column)> { (51U, 12U) });
        ConfigureInfSamplingLayout(sheet, spool.Widths);
        WriteCompanyAndPeriod(sheet, context);
        // 範本原句後接上這批樣本實際使用的隨機種子序號；這一格照範本 A4 的格式，傳入的樣式不會用到。
        sheet.WriteFixedRow(4, [sheet.TextCell(4, 1, InfSampling.SampleSeedSentence(context.SampleSeed), WorkingPaperStyles.Default)]);

        // 第 49 與 51 列只寫範本沒有的欄標;資料第 53 列起。
        WriteInfSamplingColumnHeaders(sheet, rdeHeaders, approverMapped);
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
                WorkingPaperSheetCatalog.InfSampling,
                rows);
        }

        for (var row = 53U + (uint)rows; row <= 111; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blanks = new List<Cell>(20 + customFields.Count);
            for (uint column = 1; column <= 20; column++)
            {
                blanks.Add(sheet.BlankCell(
                    row, column, column >= 13 ? WorkingPaperStyles.Editable : WorkingPaperStyles.Default));
            }
            for (var index = 0; index < customFields.Count; index++)
            {
                var column = checked((uint)(21 + index));
                blanks.Add(sheet.BlankCell(
                    row,
                    column,
                    InfSamplingRdeCellStyle(customFields[index], context.MoneyScale)));
            }
            sheet.WriteFixedRow(row, blanks);
        }
        // 下拉選單照範本 M53:S111 與 B 欄的選項，樣本超過第 111 列時延伸到最後一個樣本。
        // 舊版不保護這張表。
        sheet.ExtendTemplateValidations(templateLastRow: 111, lastRow: checked(52U + (uint)rows));

        AddStat(stats, sheet.CloseAndSummarizeWith(rows, cancellationToken), progress);
    }

    private async IAsyncEnumerable<IReadOnlyList<WorkingPaperDisplayCell>> StreamInfSamplingRowsAsync(
        WorkingPaperContext context,
        CommittedMapping? glMapping,
        ReportWorkbookMetadata? workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var values = new InfSampleDisplayValues(glMapping);
        // 依審計員選的筆數抽樣，先依穩定鍵讀完，再照 INF 報告的順序排，
        // 之後每 DefaultPageSize 筆一批讀原始 GL 列與攸關資料元素的值。
        var samples = new List<InfSampleRow>();
        await foreach (var sample in infSamples.StreamAsync(
                           context.ProjectId,
                           context.ValidationResultId,
                           cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(sample);
        }

        if (_rawRows is null)
        {
            throw MissingInfSamplingRawSource();
        }

        var ordinal = 0L;
        foreach (var chunk in ResultPageSorting.InfSampleReportOrder(samples).Chunk(PageRequest.DefaultPageSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryIds = chunk.Select(sample => sample.EntryId).ToArray();

            if (entryIds.Any(entryId => entryId <= 0))
            {
                throw MissingInfSamplingRawSource();
            }

            var rawPage = await _rawRows.FetchJsonByEntryIdsAsync(
                context.ProjectId,
                entryIds,
                cancellationToken);
            if (entryIds.Any(entryId => !rawPage.ContainsKey(entryId)))
            {
                throw MissingInfSamplingRawSource();
            }
            var rdePage = await ReadWorkingPaperRdeValuesAsync(
                context.ProjectId,
                entryIds,
                workbookMetadata,
                customFields,
                context.MoneyScale,
                cancellationToken).ConfigureAwait(false);

            foreach (var sample in chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var raw = LocalEngineErrors.ParseStoredObject(rawPage[sample.EntryId]);
                var (direction, sourceModule) = values.For(raw.RootElement, sample);

                yield return ProjectInfSample(
                    checked(++ordinal),
                    sample,
                    direction,
                    sourceModule,
                    context.MoneyScale,
                    customFields,
                    rdePage[sample.EntryId]);
            }
        }
    }

    private static JetActionException MissingInfSamplingRawSource() => new(
        JetErrorCodes.StaleResult,
        "底稿引用的原始 GL 列已不完整，請重新匯入、配對並執行對應步驟後再產出底稿。");

    /// <summary>
    /// 「step3 高風險條件彙總」：上面是固定的測試說明，下面是情境清單。欄標在第 18 列，資料從第 19 列起，依情境的 position 由小到大排。
    /// B 欄是代號 C{position}；C 欄是條件描述，內容是情境名稱，有條件內容時下面再接「篩選條件：」與條件內容；
    /// D 欄是選擇此條件的原因；E 欄是符合條件的傳票數。看得到的表格只到 E 欄。
    /// 情境的名稱、原因與傳票數都取自底稿計畫，由 <see cref="TagColumnSet"/> 整理好。
    /// 條件描述超過一個儲存格放得下的長度時分成幾列，後面幾列的代號加「（續）」。
    /// </summary>
    private void EmitHighRiskSummary(
        WorkingPaperWriteSession session, List<SheetStat> stats,
        WorkingPaperContext context, TagColumnSet tagColumns, WorkingPaperSheetPlan? sheetPlan,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var dataWidths = MeasureDisplayWidths(
                [null, "高風險範圍條件", "選擇此篩選條件的原因", "符合條件之傳票數目"],
                [9D, 30D, 42D, MaximumCountDisplayColumnWidth],
                tagColumns.Scenarios.Select(scenario =>
                    (IReadOnlyList<string?>)new string?[]
                    {
                        $"C{scenario.Position}",
                        scenario.Description,
                        scenario.Rationale,
                        CountText.Format(scenario.VoucherHitCount)
                    }))
            .ToArray();
        dataWidths[1] = Math.Min(dataWidths[1], HighRiskSummaryConditionMaxWidth);
        dataWidths[2] = Math.Min(dataWidths[2], HighRiskSummaryRationaleMaxWidth);
        var dataRows = tagColumns.Scenarios
            .SelectMany(scenario => FilterConditionExcelText.Parts(scenario.Description)
                .Select((part, index) => (Scenario: scenario, Part: part, Index: index)))
            .ToArray();
        using var sheet = session.OpenSheet(
            WorkingPaperSheetCatalog.HighRiskSummary,
            lockedArea: HighRiskSummaryLockedArea);
        ConfigureHighRiskSummaryLayout(sheet, dataWidths);
        for (var position = 0; position < dataRows.Length; position++)
        {
            var (scenario, part, index) = dataRows[position];
            var lines = Math.Max(
                ExcelDisplayWidth.WrappedLineCount(part, dataWidths[1]),
                ExcelDisplayWidth.WrappedLineCount(index == 0 ? scenario.Rationale : null, dataWidths[2]));
            if (lines > 1)
            {
                sheet.SetRowHeight(
                    checked(HighRiskSummaryFirstDataRow + (uint)position),
                    FormalWorkbookTypography.RowHeight(WorkingPaperStyles.RegularFontSize, lines));
            }
        }
        WriteSubstantiveHeader(sheet, context);

        var headerLines = PlannedLines(sheetPlan, HighRiskSummaryHeaderLines(context));
        if (sheetPlan?.AuditCondition is { } auditCondition)
        {
            headerLines = headerLines
                .Select(line => line.Row == 7 && line.Column == 2
                    ? (line.Row, line.Column, auditCondition)
                    : line)
                .ToArray();
        }
        WriteFixedLines(sheet, headerLines);
        foreach (var range in HighRiskSummaryHeaderMerges)
        {
            sheet.AddMerge(range);
        }

        // 第 18 列欄標(B 欄無欄標,直接是代號;C/D/E 有欄標)。
        sheet.WriteFixedRow(18,
        [
            sheet.TextCell(18, 3, "高風險範圍條件", WorkingPaperStyles.Bold),
            sheet.TextCell(18, 4, "選擇此篩選條件的原因", WorkingPaperStyles.Bold),
            sheet.TextCell(18, 5, "符合條件之傳票數目", WorkingPaperStyles.Bold)
        ]);

        var rowIndex = HighRiskSummaryFirstDataRow;
        foreach (var (scenario, part, index) in dataRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sheet.WriteRow(rowIndex,
            [
                sheet.TextCell(rowIndex, 2, $"C{scenario.Position}" + (index == 0 ? "" : "（續）")),
                sheet.VerbatimTextCell(rowIndex, 3, part, WorkingPaperStyles.Wrap),
                sheet.TextCell(rowIndex, 4, index == 0 ? scenario.Rationale : "", WorkingPaperStyles.Wrap),
                index == 0 ? sheet.NumberCell(rowIndex, 5, scenario.VoucherHitCount) : sheet.BlankCell(rowIndex, 5)
            ]);
            rowIndex++;
        }

        // 舊版保護這張表。
        sheet.Protect();
        AddStat(stats, sheet.CloseAndSummarizeWith(dataRows.Length, cancellationToken), progress);
    }

    private const uint HighRiskSummaryFirstDataRow = 19;

    // 受保護時鎖定的資料區照舊版 JET 的 B7:E28；條件說明超過 Excel 單格容量、接續寫過第 28 列時，多出的列也鎖 B 到 E 欄，舊版只鎖到第 28 列。
    private static readonly WorkingPaperLockedArea HighRiskSummaryLockedArea = new(7, 2, 5, 28);

    // 受保護時鎖定的資料區照舊版 JET 的 A13:O，只鎖到最後一筆資料；舊版會多鎖最後一筆的下一列，JET 不鎖。
    // 覆核欄 P 到 U 與第 12 列以上維持可填。
    private static readonly WorkingPaperLockedArea HighRiskVoucherLockedArea = new(13, 1, 15, 12);

    // 條件與原因欄的寬度上限，參考舊版 JET 底稿約 50 與 34 個字元的欄寬；超過時換行並加高該列，
    // 不依最長的一段條件把欄寬撐到 255，工作表外觀和舊版 JET 底稿一致。
    private const double HighRiskSummaryConditionMaxWidth = 60D;
    private const double HighRiskSummaryRationaleMaxWidth = 50D;

    /// <summary>
    /// 「step4 符合高風險條件傳票」：第 11 列是欄標，A 到 E 欄依序是編號、傳票號碼、總帳日期、編製者、傳票總金額，
    /// 後面每個所選情境一欄，欄標是 C 加 position，依 position 由小到大排。第 12 列是固定說明，資料從第 13 列起。
    /// 符合的傳票用一次只往前讀的查詢全部讀完。
    /// 每一列看那張傳票符合哪些情境，符合的情境欄寫 Y，其他留白；P 到 U 欄留給審計員填寫。
    /// </summary>
    private async Task EmitHighRiskVoucherAsync(
        WorkingPaperWriteSession session, List<SheetStat> stats,
        WorkingPaperContext context, TagColumnSet tagColumns, WorkingPaperSheetPlan? sheetPlan,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        const uint firstDataRow = 13;
        var pageCapacity = (long)_continuationRowLimit - firstDataRow + 1;
        var ordinal = 0L;
        using var spool = WorkingPaperDisplaySpool.Create(
            HighRiskVoucherDisplayHeaders(tagColumns),
            HighRiskVoucherMinimumWidths());
        ReportSheetStarted(stats, progress, WorkingPaperSheetCatalog.HighRiskVoucher);
        var sourceRows = StreamHighRiskVoucherRowsAsync(
            context,
            tagColumns.AllPositions,
            () => ++ordinal,
            cancellationToken);
        await FillDisplaySpoolAsync(
            spool,
            ProjectRowsAsync(
                sourceRows,
                tagged => ProjectHighRiskVoucher(
                    tagged,
                    tagColumns,
                    context.MoneyScale),
                cancellationToken),
            cancellationToken);

        var pageNumber = 1;
        var pageRows = 0L;
        var sheet = CreateHighRiskVoucherPage(
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
                    AddStat(stats, CloseHighRiskVoucherPage(sheet, pageRows, cancellationToken), progress);
                    sheet.Dispose();
                    pageNumber = checked(pageNumber + 1);
                    sheet = CreateHighRiskVoucherPage(
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

            AddStat(stats, CloseHighRiskVoucherPage(sheet, pageRows, cancellationToken), progress);
        }
        finally
        {
            sheet.Dispose();
        }
    }

    private static SheetWriter CreateHighRiskVoucherPage(
        WorkingPaperWriteSession session,
        WorkingPaperContext context,
        TagColumnSet tagColumns,
        WorkingPaperSheetPlan? sheetPlan,
        IReadOnlyList<double> dataWidths,
        int pageNumber)
    {
        var sheet = session.OpenSheet(
            ExcelWorksheetConstraints.ContinuationSheetName(WorkingPaperSheetCatalog.HighRiskVoucher, pageNumber),
            lockedArea: HighRiskVoucherLockedArea);
        ConfigureHighRiskVoucherLayout(sheet, dataWidths);
        WriteSubstantiveHeader(sheet, context);

        WriteFixedLines(sheet, PlannedLines(sheetPlan, HighRiskVoucherHeaderLines));
        foreach (var range in HighRiskVoucherHeaderMerges)
        {
            sheet.AddMerge(range);
        }

        var headerCells = new List<Cell>
        {
            sheet.TextCell(11, 1, "編號", WorkingPaperStyles.Bold),
            sheet.TextCell(11, 2, "傳票號碼", WorkingPaperStyles.Bold),
            sheet.TextCell(11, 3, "總帳日期", WorkingPaperStyles.Bold),
            sheet.TextCell(11, 4, "編製者", WorkingPaperStyles.Bold),
            sheet.TextCell(11, 5, "傳票總金額", WorkingPaperStyles.Bold)
        };
        tagColumns.AppendAllPositionHeaders(sheet, headerCells, 11, suffix: string.Empty);
        var reviewHeaders = new[] { "覆核結果", "附件索引", "差異說明", "處理結論", "覆核人", "備註" };
        for (var index = 0; index < reviewHeaders.Length; index++)
        {
            headerCells.Add(sheet.TextCell(11, (uint)(16 + index), reviewHeaders[index], WorkingPaperStyles.Bold));
        }
        sheet.WriteFixedRow(11, headerCells);
        sheet.WriteFixedRow(12,
        [
            sheet.TextCell(
                12,
                1,
                PlannedText(sheetPlan, HighRiskVoucherHeaderLines.Count, HighRiskVoucherSelectionNote),
                WorkingPaperStyles.Bold)
        ]);
        return sheet;
    }

    private static SheetStat CloseHighRiskVoucherPage(
        SheetWriter sheet,
        long rows,
        CancellationToken cancellationToken)
    {
        // 下拉選單照範本 P、Q、R 與 S 欄的選項，傳票超過第 27 列時延伸到最後一張傳票。
        if (rows > 0)
        {
            sheet.ExtendTemplateValidations(
                templateLastRow: 27,
                lastRow: CheckedLastDataRow(firstDataRow: 13, rows));
        }

        sheet.Protect();
        return sheet.CloseAndSummarizeWith(rows, cancellationToken);
    }

    /// <summary>
    /// step4-1 的明細。資料庫只開一個依序往前讀的查詢，整個母體只掃一次，記憶體用量有上限。
    /// 沒有攸關資料元素欄時，一邊讀一邊把每一列轉成要寫的儲存格、記下各欄最寬的內容，寫進關閉時自動刪除的暫存檔。
    /// 有攸關資料元素欄時，先把讀到的來源列寫進另一個暫存檔，等查詢與它的交易結束後，再分批讀攸關資料元素的值；
    /// 這樣不會在查詢還開著、占著資料庫的時候另開一條連線。
    /// 欄寬確定後，再從暫存檔逐列讀回來，直接寫進範本的工作表。
    /// </summary>
    private async Task EmitHighRiskVoucherDetailAsync(
        WorkingPaperWriteSession session,
        List<SheetStat> stats,
        WorkingPaperContext context,
        IReadOnlyList<WorkingPaperHighRiskVoucherDetailColumn> columns,
        ReportWorkbookMetadata? workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        TagColumnSet tagColumns,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        ValidateHighRiskVoucherDetailNumberScales(columns);

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
        using var spool = HighRiskVoucherDetailProjectedSpool.Create(
            columns,
            tagColumns.RowHitPositions);
        using var sourceSpool = customFields.Count == 0
            ? null
            : HighRiskVoucherDetailSourceSpool.Create();
        ReportSheetStarted(stats, progress, WorkingPaperSheetCatalog.HighRiskVoucherDetail);
        await using (var prepared = await highRiskVoucherDetailPreparedSessions.PrepareAsync(
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
                        ProjectHighRiskVoucherDetailValues(
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
            var batch = new List<WorkingPaperHighRiskVoucherDetailSourceRow>(PageRequest.MaxPageSize);
            foreach (var source in sourceSpool.ReadRows(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch.Add(source);
                if (batch.Count == PageRequest.MaxPageSize)
                {
                    await AppendHighRiskVoucherDetailRdeBatchAsync(batch).ConfigureAwait(false);
                    batch.Clear();
                }
            }
            await AppendHighRiskVoucherDetailRdeBatchAsync(batch).ConfigureAwait(false);

            async Task AppendHighRiskVoucherDetailRdeBatchAsync(
                IReadOnlyList<WorkingPaperHighRiskVoucherDetailSourceRow> rows)
            {
                if (rows.Count == 0)
                {
                    return;
                }
                var rde = await ReadWorkingPaperRdeValuesAsync(
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
                        ProjectHighRiskVoucherDetailValues(
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
        var sheet = CreateHighRiskVoucherDetailPage(
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
                    AddStat(stats, CloseHighRiskVoucherDetailPage(sheet, pageRows, cancellationToken), progress);
                    emittedRows = checked(emittedRows + pageRows);
                    sheet.Dispose();
                    pageNumber = checked(pageNumber + 1);
                    sheet = CreateHighRiskVoucherDetailPage(
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
                    HighRiskVoucherDetailSpoolCells(
                        sheet,
                        row,
                        projected,
                        tagColumns.RowHitPositions,
                        columns.Count));
                pageRows++;
                ReportIntermediateRows(stats, progress, sheet.Name, pageRows);
            }

            AddStat(stats, CloseHighRiskVoucherDetailPage(sheet, pageRows, cancellationToken), progress);
        }
        finally
        {
            sheet.Dispose();
        }
    }

    private static SheetWriter CreateHighRiskVoucherDetailPage(
        WorkingPaperWriteSession session,
        WorkingPaperContext context,
        IReadOnlyList<WorkingPaperHighRiskVoucherDetailColumn> columns,
        IReadOnlyList<int> rowHitPositions,
        IReadOnlyList<double> widths,
        long dataRows,
        int pageNumber)
    {
        var sheet = session.OpenSheet(
            ExcelWorksheetConstraints.ContinuationSheetName(
                WorkingPaperSheetCatalog.HighRiskVoucherDetail,
                pageNumber),
            HighRiskVoucherDetailAppearance());
        ConfigureHighRiskVoucherDetailLayout(sheet, widths);
        sheet.SetDimension(
            checked((uint)(5 + dataRows)),
            checked((uint)(columns.Count + rowHitPositions.Count)));

        sheet.WriteFixedRow(
            1,
            [sheet.TextCell(
                1,
                1,
                $"公司名稱 : {context.CompanyName}",
                WorkingPaperStyles.Bold)]);
        sheet.WriteFixedRow(
            2,
            [sheet.TextCell(
                2,
                1,
                $"財務報表期間 :  {DigitsOnly(context.PeriodStart)} ~ {DigitsOnly(context.PeriodEnd)}",
                WorkingPaperStyles.Bold)]);
        sheet.WriteFixedRow(
            3,
            [sheet.TextCell(
                3,
                1,
                "符合高風險範圍條件之傳票明細",
                WorkingPaperStyles.Bold)]);

        var headerCells = columns
            .Select((column, index) =>
                sheet.TextCell(
                    5,
                    checked((uint)index + 1),
                    column.Header,
                    HighRiskVoucherDetailColumnStyle(column)))
            .ToList();
        for (var index = 0; index < rowHitPositions.Count; index++)
        {
            headerCells.Add(sheet.TextCell(
                5,
                checked((uint)(columns.Count + index + 1)),
                $"C{rowHitPositions[index]}_TAG",
                WorkingPaperStyles.HighRiskVoucherDetailText));
        }
        sheet.WriteFixedRow(5, headerCells);
        return sheet;
    }

    private static void ConfigureHighRiskVoucherDetailLayout(
        SheetWriter sheet,
        IReadOnlyList<double> widths)
    {
        sheet.SetDefaultRowHeight(FormalWorkbookTypography.RowHeight(WorkingPaperStyles.HighRiskVoucherDetailFontSize));
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

    private static IReadOnlyList<Cell> HighRiskVoucherDetailSpoolCells(
        SheetWriter sheet,
        uint row,
        HighRiskVoucherDetailSpoolRow projected,
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
                ? sheet.TextCell(row, column, "Y", WorkingPaperStyles.HighRiskVoucherDetailText)
                : sheet.BlankCell(row, column, WorkingPaperStyles.HighRiskVoucherDetailText));
        }
        return cells;
    }

    private static IReadOnlyList<HighRiskVoucherDetailProjectedValue> ProjectHighRiskVoucherDetailValues(
        WorkingPaperHighRiskVoucherDetailSourceRow source,
        IReadOnlyList<WorkingPaperHighRiskVoucherDetailColumn> columns,
        int moneyScale,
        IReadOnlyDictionary<string, string?>? rdeValues = null)
    {
        using var raw = LocalEngineErrors.ParseStoredObject(source.RawJson);
        var values = new HighRiskVoucherDetailProjectedValue[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            values[index] = column.ValueSource switch
            {
                WorkingPaperHighRiskVoucherDetailValueSource.VoucherNumber => Typed(source.VoucherNumber, column),
                WorkingPaperHighRiskVoucherDetailValueSource.LineItem => Typed(source.LineItem, column),
                WorkingPaperHighRiskVoucherDetailValueSource.ApprovalDate => Typed(source.ApprovalDate, column),
                WorkingPaperHighRiskVoucherDetailValueSource.GlDate => Typed(source.GlDate, column),
                WorkingPaperHighRiskVoucherDetailValueSource.CreatedBy => Typed(source.CreatedBy, column),
                WorkingPaperHighRiskVoucherDetailValueSource.ApprovedBy => Typed(source.ApprovedBy, column),
                WorkingPaperHighRiskVoucherDetailValueSource.AccountNumber => Typed(source.AccountNumber, column),
                WorkingPaperHighRiskVoucherDetailValueSource.AccountName => Typed(source.AccountName, column),
                WorkingPaperHighRiskVoucherDetailValueSource.Description => Typed(source.Description, column),
                WorkingPaperHighRiskVoucherDetailValueSource.SourceModule => Typed(source.SourceModule, column),
                WorkingPaperHighRiskVoucherDetailValueSource.IsManual => Typed(
                    source.IsManual is null ? null : source.IsManual.Value ? "人工" : "自動",
                    column),
                WorkingPaperHighRiskVoucherDetailValueSource.PostingStatus => Typed(source.PostingStatus, column),
                WorkingPaperHighRiskVoucherDetailValueSource.SignedAmount => Amount(
                    Display(source.AmountScaled, moneyScale),
                    column),
                WorkingPaperHighRiskVoucherDetailValueSource.RawField => Raw(
                    raw.RootElement,
                    column),
                WorkingPaperHighRiskVoucherDetailValueSource.RdeField => Typed(
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

    private static HighRiskVoucherDetailProjectedValue Raw(
        JsonElement raw,
        WorkingPaperHighRiskVoucherDetailColumn column)
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

    // 欄位型態是匯入時照儲存格型別記下的，來源的內容本來就可能不合這個型態，例如數值儲存格寫了 NaN，
    // 或其他程式在 t="d" 的儲存格寫了不是日期的文字。值合乎型態才寫成數值、日期或時間儲存格，
    // 其他照存下來的文字寫成文字儲存格，匯出照常完成；這不是案件資料庫損壞。
    private static HighRiskVoucherDetailProjectedValue Typed(
        string? value,
        WorkingPaperHighRiskVoucherDetailColumn column)
    {
        return column.Kind switch
        {
            LegacyFieldKind.Text => Text(value),
            LegacyFieldKind.Number => string.IsNullOrEmpty(value)
                ? Blank(WorkingPaperStyles.HighRiskVoucherDetailNumber(DecimalPlaces(column)))
                : Number(value, column),
            LegacyFieldKind.Date => string.IsNullOrEmpty(value)
                ? Blank(WorkingPaperStyles.HighRiskVoucherDetailDate)
                : Date(value, column),
            LegacyFieldKind.Time => string.IsNullOrEmpty(value)
                ? Blank(WorkingPaperStyles.HighRiskVoucherDetailTime)
                : Time(value, column),
            _ => throw new ArgumentOutOfRangeException(
                nameof(column.Kind),
                column.Kind,
                null)
        };
    }

    private static HighRiskVoucherDetailProjectedValue Number(
        string value,
        WorkingPaperHighRiskVoucherDetailColumn column)
    {
        var decimals = DecimalPlaces(column);
        var format = NumberFormatCode(decimals, grouped: false);
        string numberLiteral;
        string renderedText;
        if (bool.TryParse(value, out var boolean))
        {
            // xlsx 的布林儲存格，匯入時欄位定義記成數值、小數 0 位，值 1 與 0 在 row_json 裡存成 true 與 false。
            // 舊版 JET 的明細在這種格子裡是 Excel 的數值 1 或 0，所以這裡轉成 1 或 0 寫出。
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
            // 超出 System.Decimal 範圍、但仍是有限數值的 xlsx 數值，匯入時存成 double 能原樣讀回的文字。
            // step4-1 寫出時仍然要是數值儲存格。
            numberLiteral = doubleNumber.ToString("R", CultureInfo.InvariantCulture);
            renderedText = doubleNumber.ToString(format, CultureInfo.InvariantCulture);
        }
        else
        {
            // 不是數字的內容，或 NaN 與無限大，照存下來的文字寫出。
            return Text(value);
        }

        return new HighRiskVoucherDetailProjectedValue(
            Text: null,
            NumberLiteral: numberLiteral,
            Style: WorkingPaperStyles.HighRiskVoucherDetailNumber(decimals),
            RenderedText: renderedText);
    }

    private static HighRiskVoucherDetailProjectedValue Date(
        string value,
        WorkingPaperHighRiskVoucherDetailColumn column)
    {
        // 日期型欄位存的日期一律是 yyyy-MM-dd，只有這種寫法寫成日期儲存格。寬鬆的日期解析會把「1.25」這類序列值的文字
        // 當成沒寫年份的 1 月 25 日，補上匯出當年，改掉來源的值，所以不用它。
        if (!DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            // 換算不出日期的 Excel 日期儲存格，例如負的序列值，或其他程式寫進檔案的 NaN 與無限大，讀取器存下的是序列值的文字；
            // Excel 顯示成日曆上沒有的 1900-02-29 或 1900 年 1 月 0 日時，存下的是 Excel 顯示的年月日。
            // 這些和其他讀不成日期的內容一樣，照存下來的文字寫出。
            return Text(value);
        }

        if (!ExcelSerialDate.TryExcelDateSerial(date, out var serial))
        {
            return Text(value);
        }

        return new HighRiskVoucherDetailProjectedValue(
            Text: null,
            NumberLiteral: serial,
            Style: WorkingPaperStyles.HighRiskVoucherDetailDate,
            RenderedText: value);
    }

    private static HighRiskVoucherDetailProjectedValue Time(
        string value,
        WorkingPaperHighRiskVoucherDetailColumn column)
    {
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time))
        {
            // 讀不成時間的內容照存下來的文字寫出。
            return Text(value);
        }

        return new HighRiskVoucherDetailProjectedValue(
            Text: null,
            NumberLiteral: time.TotalDays.ToString(
                "R",
                CultureInfo.InvariantCulture),
            Style: WorkingPaperStyles.HighRiskVoucherDetailTime,
            // Excel 的固定 hh:mm:ss 格式只顯示日內時、分、秒；日數與小數秒
            // 不會出現在可見文字。AutoFit 必須量同一份可見字串，否則含小數秒
            // 或超過 24 小時的來源值會把欄寬算得太寬。
            RenderedText: time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
    }

    private static HighRiskVoucherDetailProjectedValue Text(string? value) =>
        new(
            Text: value,
            NumberLiteral: null,
            Style: WorkingPaperStyles.HighRiskVoucherDetailText,
            RenderedText: value ?? string.Empty);

    private static HighRiskVoucherDetailProjectedValue Blank(uint style) =>
        new(
            Text: null,
            NumberLiteral: null,
            Style: style,
            RenderedText: string.Empty);

    private static HighRiskVoucherDetailProjectedValue Amount(
        decimal value,
        WorkingPaperHighRiskVoucherDetailColumn column)
    {
        var decimals = DecimalPlaces(column);
        var format = NumberFormatCode(decimals, grouped: true);
        return new(
            Text: null,
            NumberLiteral: value.ToString(CultureInfo.InvariantCulture),
            Style: WorkingPaperStyles.HighRiskVoucherDetailGroupedNumber(decimals),
            RenderedText: value.ToString(format, CultureInfo.InvariantCulture));
    }

    private static uint HighRiskVoucherDetailColumnStyle(WorkingPaperHighRiskVoucherDetailColumn column)
    {
        if (column.ValueSource == WorkingPaperHighRiskVoucherDetailValueSource.SignedAmount)
        {
            return WorkingPaperStyles.HighRiskVoucherDetailGroupedNumber(DecimalPlaces(column));
        }

        return column.Kind switch
        {
            LegacyFieldKind.Text => WorkingPaperStyles.HighRiskVoucherDetailText,
            LegacyFieldKind.Number => WorkingPaperStyles.HighRiskVoucherDetailNumber(DecimalPlaces(column)),
            LegacyFieldKind.Date => WorkingPaperStyles.HighRiskVoucherDetailDate,
            LegacyFieldKind.Time => WorkingPaperStyles.HighRiskVoucherDetailTime,
            _ => throw new ArgumentOutOfRangeException(
                nameof(column.Kind),
                column.Kind,
                null)
        };
    }

    private static int DecimalPlaces(WorkingPaperHighRiskVoucherDetailColumn column)
    {
        if (column.DecimalPlaces is not { } decimalPlaces
            || decimalPlaces is < 0 or > WorkingPaperStyles.MaximumDecimalPlaces)
        {
            throw new InvalidDataException(
                $"step4-1 Number 欄位 '{column.Header}' 的 decimal scale "
                + $"'{column.DecimalPlaces?.ToString(CultureInfo.InvariantCulture) ?? "null"}' "
                + $"不在 0..{WorkingPaperStyles.MaximumDecimalPlaces}。");
        }

        return decimalPlaces;
    }

    private static void ValidateHighRiskVoucherDetailNumberScales(
        IReadOnlyList<WorkingPaperHighRiskVoucherDetailColumn> columns)
    {
        foreach (var column in columns.Where(column =>
                     column.Kind == LegacyFieldKind.Number
                     || column.ValueSource == WorkingPaperHighRiskVoucherDetailValueSource.SignedAmount))
        {
            _ = DecimalPlaces(column);
        }
    }

    private static string NumberFormatCode(int decimalPlaces, bool grouped) =>
        (grouped ? "#,##0" : "0")
        + (decimalPlaces == 0 ? string.Empty : "." + new string('0', decimalPlaces));

    private readonly record struct HighRiskVoucherDetailProjectedValue(
        string? Text,
        string? NumberLiteral,
        uint Style,
        string RenderedText);

    private readonly record struct HighRiskVoucherDetailSpoolRow(
        IReadOnlyList<HighRiskVoucherDetailProjectedValue> Values,
        int TagMask);

    /// <summary>
    /// 有攸關資料元素欄時暫放 step4-1 來源列的暫存檔，關閉時自動刪除。查詢進行中只往檔案寫，
    /// 查詢的交易結束後才從頭逐列讀回。這樣不必把 step4-1 的母體放進記憶體，
    /// 也不會在資料庫查詢還開著的時候，為了讀攸關資料元素的值另開第二條連線。
    /// </summary>
    private sealed class HighRiskVoucherDetailSourceSpool : IDisposable
    {
        private readonly ExportSpoolFile _file;
        private readonly BinaryWriter _writer;
        private BinaryReader? _reader;
        private bool _completed;
        private bool _readStarted;
        private bool _disposed;

        private HighRiskVoucherDetailSourceSpool(ExportSpoolFile file)
        {
            _file = file;
            _writer = file.Writer;
        }

        private long RowCount { get; set; }

        internal static HighRiskVoucherDetailSourceSpool Create() =>
            new(ExportSpoolFile.Create(".jet-working-paper-high-risk-voucher-detail-source-", bufferSize: 1024 * 1024));

        internal void Append(WorkingPaperHighRiskVoucherDetailSourceRow source)
        {
            ThrowIfDisposed();
            if (_completed)
            {
                throw new InvalidOperationException(
                    "Step4-1 source spool 已完成，不能再追加列。");
            }

            _writer.Write(source.EntryId);
            WriteNullableString(source.VoucherNumber);
            WriteNullableString(source.LineItem);
            WriteNullableString(source.GlDate);
            WriteNullableString(source.ApprovalDate);
            WriteNullableString(source.CreatedBy);
            WriteNullableString(source.ApprovedBy);
            WriteNullableString(source.AccountNumber);
            WriteNullableString(source.AccountName);
            _writer.Write(source.AmountScaled);
            WriteNullableString(source.Description);
            WriteNullableString(source.SourceModule);
            WriteNullableString(source.PostingStatus);
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

            _reader = _file.StartReading(throwOnInvalidBytes: false);
            _completed = true;
        }

        internal IEnumerable<WorkingPaperHighRiskVoucherDetailSourceRow> ReadRows(
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
            _file.Dispose();
        }

        private static WorkingPaperHighRiskVoucherDetailSourceRow ReadRow(BinaryReader reader)
        {
            var entryId = reader.ReadInt64();
            var voucherNumber = ReadNullableString(reader);
            var lineItem = ReadNullableString(reader);
            var glDate = ReadNullableString(reader);
            var approvalDate = ReadNullableString(reader);
            var createdBy = ReadNullableString(reader);
            var approvedBy = ReadNullableString(reader);
            var accountNumber = ReadNullableString(reader);
            var accountName = ReadNullableString(reader);
            var amountScaled = reader.ReadInt64();
            var description = ReadNullableString(reader);
            var sourceModule = ReadNullableString(reader);
            var postingStatus = ReadNullableString(reader);
            var isManual = reader.ReadBoolean()
                ? reader.ReadBoolean()
                : (bool?)null;
            var rawJson = reader.ReadString();
            var positionCount = reader.ReadInt32();
            if (positionCount < 0 || positionCount > FilterScenarioLimits.MaxSavedScenarios)
            {
                throw new InvalidDataException(
                    $"Step4-1 source spool tag count '{positionCount}' 無效。");
            }
            var matchedPositions = new int[positionCount];
            for (var index = 0; index < positionCount; index++)
            {
                matchedPositions[index] = reader.ReadInt32();
            }

            return new WorkingPaperHighRiskVoucherDetailSourceRow(
                entryId,
                voucherNumber,
                lineItem,
                glDate,
                approvalDate,
                createdBy,
                approvedBy,
                accountNumber,
                accountName,
                amountScaled,
                description,
                sourceModule,
                isManual,
                rawJson,
                matchedPositions,
                postingStatus);
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
    /// 資料只讀一次，而工作表的 cols 必須寫在 sheetData 前面，所以先把每一列要寫的內容放進這個暫存檔，量完欄寬再寫工作表。
    /// 檔案裡只存已經轉好型別的儲存格內容，以及這一列符合哪些情境的位元旗標；不存原始 JSON，也不另外建立第二份活頁簿。
    /// </summary>
    private sealed class HighRiskVoucherDetailProjectedSpool : IDisposable
    {
        private readonly ExportSpoolFile _file;
        private readonly BinaryWriter _writer;
        private readonly IReadOnlyList<int> _rowHitPositions;
        private readonly double[] _maximums;
        private BinaryReader? _reader;
        private bool _completed;
        private bool _readStarted;
        private bool _disposed;

        private HighRiskVoucherDetailProjectedSpool(
            ExportSpoolFile file,
            IReadOnlyList<WorkingPaperHighRiskVoucherDetailColumn> columns,
            IReadOnlyList<int> rowHitPositions)
        {
            _file = file;
            _writer = file.Writer;
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

        internal static HighRiskVoucherDetailProjectedSpool Create(
            IReadOnlyList<WorkingPaperHighRiskVoucherDetailColumn> columns,
            IReadOnlyList<int> rowHitPositions)
        {
            var file = ExportSpoolFile.Create(".jet-working-paper-high-risk-voucher-detail-", bufferSize: 1024 * 1024);
            try
            {
                return new HighRiskVoucherDetailProjectedSpool(file, columns, rowHitPositions);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        internal void Append(
            IReadOnlyList<HighRiskVoucherDetailProjectedValue> values,
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

            _reader = _file.StartReading(throwOnInvalidBytes: false);
            _completed = true;
        }

        internal IEnumerable<HighRiskVoucherDetailSpoolRow> ReadRows(
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
                var values = new HighRiskVoucherDetailProjectedValue[ValueColumnCount];
                for (var index = 0; index < ValueColumnCount; index++)
                {
                    var kind = _reader.ReadByte();
                    var style = _reader.ReadUInt32();
                    values[index] = kind switch
                    {
                        0 => new HighRiskVoucherDetailProjectedValue(
                            null,
                            null,
                            style,
                            string.Empty),
                        1 => new HighRiskVoucherDetailProjectedValue(
                            _reader.ReadString(),
                            null,
                            style,
                            string.Empty),
                        2 => new HighRiskVoucherDetailProjectedValue(
                            null,
                            _reader.ReadString(),
                            style,
                            string.Empty),
                        _ => throw new InvalidDataException(
                            $"Step4-1 projected spool cell kind '{kind}' 無效。")
                    };
                }
                yield return new HighRiskVoucherDetailSpoolRow(values, _reader.ReadInt32());
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _file.Dispose();
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    // 舊版不保護 step4-1。
    private static SheetStat CloseHighRiskVoucherDetailPage(
        SheetWriter sheet,
        long rows,
        CancellationToken cancellationToken) =>
        sheet.CloseAndSummarizeWith(rows, cancellationToken);

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

    private static void ConfigureInfSamplingLayout(
        SheetWriter sheet,
        IReadOnlyList<double> dataWidths)
    {
        sheet.SetDefaultRowHeight(FormalWorkbookTypography.RowHeight(WorkingPaperStyles.RegularFontSize));
        ApplyDisplayWidths(sheet, firstColumn: 1, widths: dataWidths);
        sheet.SetRowHeight(49, 38);
        sheet.SetRowHeight(50, 28);
        sheet.SetRowHeight(51, 48);
        sheet.SetRowHeight(52, 24);
    }

    private static void ConfigureHighRiskSummaryLayout(
        SheetWriter sheet,
        IReadOnlyList<double> dataWidths)
    {
        sheet.SetDefaultRowHeight(FormalWorkbookTypography.RowHeight(WorkingPaperStyles.RegularFontSize));
        sheet.SetColumnWidth(1, 1, 12);
        ApplyDisplayWidths(sheet, firstColumn: 2, widths: dataWidths);
        sheet.SetRowHeight(18, 34);
    }

    private static void ConfigureHighRiskVoucherLayout(
        SheetWriter sheet,
        IReadOnlyList<double> dataWidths)
    {
        sheet.SetDefaultRowHeight(FormalWorkbookTypography.RowHeight(WorkingPaperStyles.RegularFontSize));
        ApplyDisplayWidths(sheet, firstColumn: 1, widths: dataWidths);
        sheet.SetRowHeight(10, 32);
        sheet.SetRowHeight(11, 42);
        sheet.SetRowHeight(12, 32);
    }

    /// <summary>
    /// step3 與 step4 共用的前三列表頭：A1 公司名稱、A2 財務報表期間、A3 副標題「證實測試 - 會計分錄及其他調整」。
    /// step1 系列用的是 <see cref="WriteCommonHeader"/>，內容是測試資料期間與財報準備開始日；
    /// 兩組表頭的內容不同，所以分開寫。
    /// </summary>
    private static void WriteSubstantiveHeader(SheetWriter sheet, WorkingPaperContext context)
    {
        WriteCompanyAndPeriod(sheet, context);
        sheet.WriteFixedRow(3, [sheet.TextCell(3, 1, "證實測試 - 會計分錄及其他調整", WorkingPaperStyles.Bold)]);
    }

    /// <summary>A1 公司名與 A2 財務報表期間。step2 的第 3 列是範本的原字。</summary>
    private static void WriteCompanyAndPeriod(SheetWriter sheet, WorkingPaperContext context)
    {
        sheet.WriteFixedRow(1, [sheet.TextCell(1, 1, $"公司名稱 : {context.CompanyName}", WorkingPaperStyles.Bold)]);
        sheet.WriteFixedRow(2, [sheet.TextCell(2, 1,
            $"財務報表期間 :  {DigitsOnly(context.PeriodStart)} ~ {DigitsOnly(context.PeriodEnd)}", WorkingPaperStyles.Bold)]);
    }

    /// <summary>
    /// 寫出固定文字，每一項是列號、欄號與文字。step3 與 step4 表頭的大段說明文字共用這個方法。
    /// 同一列的幾段文字合成一列、照欄的順序一次寫出，工作表裡同一個列號才不會出現兩次。
    /// </summary>
    private static void WriteFixedLines(SheetWriter sheet, IReadOnlyList<(uint Row, uint Column, string Text)> lines)
    {
        foreach (var row in lines.GroupBy(line => line.Row))
        {
            sheet.WriteFixedRow(
                row.Key,
                row.OrderBy(line => line.Column)
                    .Select(line => sheet.TextCell(line.Row, line.Column, line.Text, WorkingPaperStyles.Bold))
                    .ToArray());
        }
    }

    /// <summary>
    /// step2 的欄標在範本裡。JET 只寫範本沒有的兩種：配對了傳票核准人員時的 L51，
    /// 以及 U 欄起攸關資料元素欄在第 49 列的標題與第 51 列的欄名。沒有要寫的格子時，那一列整列照範本。
    /// </summary>
    private static void WriteInfSamplingColumnHeaders(
        SheetWriter sheet,
        IReadOnlyList<string> customFields,
        bool approverMapped)
    {
        if (customFields.Count > 0)
        {
            sheet.WriteFixedRow(49,
            [
                sheet.TextCell(49, 21, "所選高風險條件引用RDE", WorkingPaperStyles.BoldWrap)
            ]);
        }

        var row51 = new List<Cell>();
        if (approverMapped)
        {
            row51.Add(sheet.TextCell(51, 12, "傳票核准人員", WorkingPaperStyles.BoldWrap));
        }
        for (var index = 0; index < customFields.Count; index++)
        {
            row51.Add(sheet.TextCell(
                51,
                checked((uint)(21 + index)),
                customFields[index],
                WorkingPaperStyles.BoldWrap));
        }
        if (row51.Count > 0)
        {
            sheet.WriteFixedRow(51, row51);
        }
    }

    /// <summary>
    /// step2 的一列資料。A 欄是呼叫端給的樣本序號，B 傳票號碼，C 科目編號，D 科目名稱，
    /// E 借貸代號，沒有時留白，規則見 InfSampleDisplayValues；F 是借方減貸方的帶號金額，G 總帳日期，H 核准日，
    /// I 編製人員，J 分錄來源，取 GL 整理後的值；K 摘要，L 核准人員。M 到 S 欄的結果 A 到 G 與 T 欄的說明留給審計員填寫。
    /// U 欄起是所選情境用到的攸關資料元素欄，同一個欄位只出現一次，順序照已確認配對裡的欄位順序。
    /// </summary>
    private static IReadOnlyList<WorkingPaperDisplayCell> ProjectInfSample(
        long ordinal,
        InfSampleRow sample,
        string? direction,
        string? sourceModule,
        int moneyScale,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        IReadOnlyDictionary<string, string?> rdeValues)
    {
        var cells = new List<WorkingPaperDisplayCell>(20 + customFields.Count)
        {
            ProjectInteger(ordinal),
            ProjectText(sample.VoucherNumber),
            ProjectText(sample.AccountNumber),
            ProjectText(sample.AccountName),
            ProjectText(direction),
            ProjectAmount(sample.DebitScaled - sample.CreditScaled, moneyScale),
            ProjectText(sample.GlDate),
            ProjectText(sample.ApprovalDate),
            ProjectText(sample.CreatedBy),
            ProjectText(sourceModule),
            ProjectText(sample.Description),
            ProjectText(sample.ApprovedBy),
            ProjectBlank(WorkingPaperStyles.Editable),
            ProjectBlank(WorkingPaperStyles.Editable),
            ProjectBlank(WorkingPaperStyles.Editable),
            ProjectBlank(WorkingPaperStyles.Editable),
            ProjectBlank(WorkingPaperStyles.Editable),
            ProjectBlank(WorkingPaperStyles.Editable),
            ProjectBlank(WorkingPaperStyles.Editable),
            ProjectBlank(WorkingPaperStyles.Editable)
        };
        foreach (var field in customFields)
        {
            cells.Add(ProjectInfSamplingRdeCell(
                field,
                rdeValues[field.FieldId],
                moneyScale));
        }
        return cells;
    }

    private static WorkingPaperDisplayCell ProjectInfSamplingRdeCell(
        GlRdeFieldMetadata field,
        string? value,
        int moneyScale)
    {
        if (string.IsNullOrEmpty(value))
        {
            return ProjectBlank(InfSamplingRdeCellStyle(field, moneyScale));
        }

        return field.ValueType switch
        {
            RdeFieldValueTypeNames.Text => ProjectText(value),
            RdeFieldValueTypeNames.Date => ProjectInfSamplingRdeDate(field, value),
            RdeFieldValueTypeNames.Amount => ProjectInfSamplingRdeMoney(field, value, moneyScale),
            _ => throw new InvalidDataException(
                $"step2 RDE field '{field.FieldId}' has unsupported value type '{field.ValueType}'.")
        };
    }

    private static WorkingPaperDisplayCell ProjectInfSamplingRdeDate(
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

        if (!ExcelSerialDate.TryExcelDateSerial(date, out var serial))
        {
            return ProjectText(value);
        }

        return new WorkingPaperDisplayCell(
            Text: null,
            NumberLiteral: serial,
            Style: WorkingPaperStyles.Date,
            RenderedText: value);
    }

    private static WorkingPaperDisplayCell ProjectInfSamplingRdeMoney(
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

        var decimals = InfSamplingRdeMoneyDecimalPlaces(moneyScale);
        var format = NumberFormatCode(decimals, grouped: true);
        return new WorkingPaperDisplayCell(
            Text: null,
            NumberLiteral: amount.ToString(CultureInfo.InvariantCulture),
            Style: WorkingPaperStyles.GroupedNumber(decimals),
            RenderedText: amount.ToString(format, CultureInfo.InvariantCulture));
    }

    private static uint InfSamplingRdeCellStyle(GlRdeFieldMetadata field, int moneyScale) =>
        field.ValueType switch
        {
            RdeFieldValueTypeNames.Text => WorkingPaperStyles.Default,
            RdeFieldValueTypeNames.Date => WorkingPaperStyles.Date,
            RdeFieldValueTypeNames.Amount => WorkingPaperStyles.GroupedNumber(
                InfSamplingRdeMoneyDecimalPlaces(moneyScale)),
            _ => throw new InvalidDataException(
                $"step2 RDE field '{field.FieldId}' has unsupported value type '{field.ValueType}'.")
        };

    private static int InfSamplingRdeMoneyDecimalPlaces(int moneyScale)
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

    // ---- step4 的資料來源：每張傳票配上它符合的情境位置，逐列讀取，不整批載入 ----

    /// <summary>
    /// step4 的資料來源：用一次依序、只往前讀的查詢取回全部符合的傳票，每張傳票配上它符合的情境位置。
    /// 編號由呼叫端的 <paramref name="nextOrdinal"/> 累計。每頁 200 筆的分頁查詢只給畫面用。
    /// </summary>
    private async IAsyncEnumerable<TaggedVoucherRow> StreamHighRiskVoucherRowsAsync(
        WorkingPaperContext context,
        IReadOnlyList<int> scenarioPositions,
        Func<long> nextOrdinal,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var populationContext = new GlPopulationContext(
            context.PopulationScope,
            context.PeriodStart,
            context.PeriodEnd);
        await foreach (var row in highRiskVouchers.StreamAsync(
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

    private readonly record struct TaggedVoucherRow(
        VoucherTagRow Voucher,
        IReadOnlyList<int> MatchedPositions,
        long Ordinal);

    private static IReadOnlyList<string?> HighRiskVoucherDisplayHeaders(TagColumnSet tagColumns)
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

    private static IReadOnlyList<double> HighRiskVoucherMinimumWidths() =>
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

    /// <summary>step4 的一列傳票：A 到 E 是固定欄，接著每個所選情境一欄，符合那個情境就寫 Y；P 到 U 欄留給審計員填寫。</summary>
    private static IReadOnlyList<WorkingPaperDisplayCell> ProjectHighRiskVoucher(
        TaggedVoucherRow tagged,
        TagColumnSet tagColumns,
        int moneyScale)
    {
        var values = Enumerable.Range(0, 21)
            .Select(_ => ProjectBlank())
            .ToArray();
        values[0] = ProjectInteger(tagged.Ordinal);
        values[1] = ProjectText(tagged.Voucher.VoucherNumber);
        values[2] = ProjectText(tagged.Voucher.GlDate);
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
            values[index] = ProjectBlank(WorkingPaperStyles.Editable);
        }

        return values;
    }

    /// <summary>step3 的測試目的、測試範圍、選擇範圍的理由與風險評估文字。測試範圍與理由兩列依這次的篩選範圍寫出。</summary>
    private static IReadOnlyList<(uint Row, uint Column, string Text)> HighRiskSummaryHeaderLines(
        WorkingPaperContext context)
    {
        if (context.PopulationScope != GlPopulationScope.AuditPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context), context.PopulationScope, "未知的 GL 母體範圍。");
        }

        var range = WorkingPaperResultTexts.HighRiskSummaryTestScope(context.PeriodStart, context.PeriodEnd);
        const string rationale = WorkingPaperResultTexts.HighRiskSummaryScopeRationale;

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

    /// <summary>step3 固定文字合併範圍,範本裡也有這幾個。</summary>
    private static readonly IReadOnlyList<string> HighRiskSummaryHeaderMerges =
    [
        "B5:E5", "B7:E7", "A9:A12", "B9:E12", "B15:E15", "B16:E16"
    ];

    /// <summary>step4 的測試目的、測試程序與彙總區段的標題。第 11 列的欄標由 CreateHighRiskVoucherPage 寫。</summary>
    private static readonly IReadOnlyList<(uint Row, uint Column, string Text)> HighRiskVoucherHeaderLines =
    [
        (5, 1, "測試目的"),
        (5, 2, "取得查核證據，以測試會計分錄及其他調整：\ni)  不存在因舞弊所導致的重大不實表達；\nii) 有適當的支持性文件；\niii) 反映了相關的事項、情況和交易，並且\niv) 按照財務報導架構，記錄在正確的會計期間。"),
        (7, 1, "測試程序"),
        (7, 2, "A.核至相關傳票之複核紀錄，以確認該分錄之過帳係經適當核准。\nB.核至相關傳票附件，以確認該分錄所載內容與附件一致，分錄係記錄於正確的\n    會計期間，並確認依附件所登錄的科目係屬適當及相關。\nC.詢問負責人員編製分錄之細節，以確認該分錄編製無存在不合理之情形。"),
        (10, 6, "決定進行測試之高風險範圍條件"),
        (10, 16, "查核程序"),
        (10, 19, "有無舞弊\n或不實表達\n(Yes/No)"),
    ];

    /// <summary>step4 固定文字合併範圍,範本裡也有這幾個。</summary>
    private static readonly IReadOnlyList<string> HighRiskVoucherHeaderMerges =
    [
        "B5:E5", "B7:E7", "P10:R10", "S10:S11"
    ];

    // 範本 A12 直接寫著整句，續頁才由程式照寫。
    private const string HighRiskVoucherSelectionNote =
        "因為設定高風險範圍條件，從母體#2挑選之分錄傳票(執行重大性或其他固定金額不應作為挑選的門檻)" +
        "；空白傳票號碼的命中分錄逐筆列出，不計傳票張數，金額為該筆借方金額。";
}
