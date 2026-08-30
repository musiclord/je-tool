using System.Globalization;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 匯出底稿(WorkingPaper).xlsx 寫出器。<see cref="IWorkpaperWriter"/> 的 deep module 實作:
/// 對外只 <see cref="WriteAsync"/>,內部隱藏全部 OpenXML 細節與資料表 keyset 串流。
///
/// 為什麼 SAX(OpenXmlPartWriter)、不用 ClosedXML:
///   真實母體達百萬列(實務見過 ~140 萬列)。ClosedXML(及 OpenXML DOM)要把整個 worksheet
///   建成記憶體物件樹才落地,會 OutOfMemory;SAX 是 forward-only、逐元素串流寫,記憶體有界。
///   因此 ClosedXML 僅限 dev fixture(DemoWorkbookWriter),底稿寫出鐵律走 SAX。
///
/// 為什麼 inline string、不用 sharedStrings:
///   sharedStrings 需要一張全域字串表(理想上要先看完所有字串才能去重),與 forward-only 串流相斥;
///   inline string 讓每個 cell 自帶文字、一次寫完不回頭,正是串流情境該用的形式。
///
/// 元素順序的硬約束(ISO/IEC 29500 CT_Worksheet)由 <see cref="SheetWriter"/> 原語封裝:
///   worksheet 子序列為 …→ sheetData → … → mergeCells,即 mergeCells 必須在 sheetData 之後。
///   emitter 可任意順序呼叫 WriteRow / AddMerge;原語保證先串完整個 sheetData、關閉後才寫 mergeCells。
///
/// 資料表共用骨架(DRY 閾值=3,本 task step1 / step1-2 / step1-3 達標):
///   <see cref="WriteCommonHeader"/> 出 A1-A4(公司/期間/財報準備日/斜體說明,五表全有);
///   <see cref="EmitTableSheetAsync"/> 出「欄標列 + 逐列資料(列號內部累計)」;
///   <see cref="StreamRowsAsync"/> 把 keyset 分頁 repo 轉成不全載入的列序列。
///   step1-1(條件例外表)與 step1-3(完整性差異條件表)結構不同,各自處理——
///   完整性差異科目數為零時,step1-3 不註冊工作表;條件由 emitter guard 決定,非 god-switch。
///
/// step2/3/4/4-1(Task 4)高風險矩陣家族:
///   step2 可靠性逐頁 infSamplePage，依 legacy 範本以借貸代號＋帶號金額落欄；
///   production 另以 bounded raw-row port 回填原始借貸代號與來源模組。
///   step3/4/4-1 的 C 欄集由本次所選情境決定——step4 用全部所選 position，
///   step4-1 只用所選集合中「行層命中(rowHitCount>0)」的 position。
///   這是業務分支(對齊樣本的動態 schema):先以 scenarios 算出欄集 list(<see cref="TagColumnSet"/>),再逐列依該列
///   matchedPositions 是否含某 position 對映 Y——用 data structure 消特例,不寫「第幾欄特判」。
///   step4/4-1 逐頁串流,且每頁回的 matchedPositions 是「另一個 dict」(voucher 以 documentNumber、row 以 entry_id 對齊),
///   故另備 <see cref="StreamTaggedVoucherRowsAsync"/>／<see cref="StreamTaggedRowDetailRowsAsync"/>
///   把「列 + 該列命中位置」配對成串流(沿用 keyset 不全載入)。
/// </summary>
public sealed partial class WorkpaperWriter(
    ICompletenessAccountPageRepository completenessAccounts,
    ICompletenessDiffPageRepository completenessDiffs,
    IDocBalancePageRepository docBalances,
    ICreatorSummaryExportRepository creatorSummaries,
    IInfSamplePageRepository infSamples,
    IFilterScenarioStore filterScenarios,
    ITagMatrixScenariosRepository tagMatrixCounts,
    ITagMatrixVoucherPageRepository tagMatrixVouchers,
    ITagMatrixRowPageRepository tagMatrixRows,
    IMappingStateStore mappingStates,
    ICalendarExportRepository calendarDays,
    IAccountMappingExportRepository accountMappings,
    IResultPageRdeValuesPort? resultPageRdeValues = null) : IWorkpaperWriter, IWorkpaperPlanWriter, IFormalWorkpaperPlanWriter
{
    private const int DefaultProgressRowInterval = 10_000;
    private static readonly double MaximumAmountDisplayColumnWidth =
        ExcelDisplayWidth.ToColumnWidth(
            ExcelDisplayWidth.Measure("-9,223,372,036,854,775,808.0000"));
    private static readonly double MaximumCountDisplayColumnWidth =
        ExcelDisplayWidth.ToColumnWidth(
            ExcelDisplayWidth.Measure("9,223,372,036,854,775,807"));

    private readonly uint _continuationRowLimit = ExcelWorksheetConstraints.MaxRows;
    private readonly int _progressRowInterval = DefaultProgressRowInterval;
    private readonly ReportTemplateCatalog _templates = ReportTemplateCatalog.Default;
    private readonly IRawGlExportRepository? _rawRows;
    private readonly IWorkpaperStep4StreamRepository? _step4Vouchers =
        tagMatrixVouchers as IWorkpaperStep4StreamRepository;
    private readonly IWorkpaperStep41PageRepository? _step41Rows =
        tagMatrixRows as IWorkpaperStep41PageRepository;
    private readonly IWorkpaperStep41PreparedSessionFactory? _step41PreparedSessions =
        tagMatrixRows as IWorkpaperStep41PreparedSessionFactory;
    private readonly IAccountMappingStore? _accountMappingStateStore;

    internal WorkpaperWriter(
        ICompletenessAccountPageRepository completenessAccounts,
        ICompletenessDiffPageRepository completenessDiffs,
        IDocBalancePageRepository docBalances,
        ICreatorSummaryExportRepository creatorSummaries,
        IInfSamplePageRepository infSamples,
        IFilterScenarioStore filterScenarios,
        ITagMatrixScenariosRepository tagMatrixCounts,
        ITagMatrixVoucherPageRepository tagMatrixVouchers,
        ITagMatrixRowPageRepository tagMatrixRows,
        IMappingStateStore mappingStates,
        ICalendarExportRepository calendarDays,
        IAccountMappingExportRepository accountMappings,
        IRawGlExportRepository rawRows)
        : this(
            completenessAccounts,
            completenessDiffs,
            docBalances,
            creatorSummaries,
            infSamples,
            filterScenarios,
            tagMatrixCounts,
            tagMatrixVouchers,
            tagMatrixRows,
            mappingStates,
            calendarDays,
            accountMappings)
    {
        _rawRows = rawRows ?? throw new ArgumentNullException(nameof(rawRows));
    }

    internal WorkpaperWriter(
        ICompletenessAccountPageRepository completenessAccounts,
        ICompletenessDiffPageRepository completenessDiffs,
        IDocBalancePageRepository docBalances,
        ICreatorSummaryExportRepository creatorSummaries,
        IInfSamplePageRepository infSamples,
        IFilterScenarioStore filterScenarios,
        ITagMatrixScenariosRepository tagMatrixCounts,
        ITagMatrixVoucherPageRepository tagMatrixVouchers,
        ITagMatrixRowPageRepository tagMatrixRows,
        IMappingStateStore mappingStates,
        ICalendarExportRepository calendarDays,
        IAccountMappingExportRepository accountMappings,
        IAccountMappingStore accountMappingStateStore)
        : this(
            completenessAccounts,
            completenessDiffs,
            docBalances,
            creatorSummaries,
            infSamples,
            filterScenarios,
            tagMatrixCounts,
            tagMatrixVouchers,
            tagMatrixRows,
            mappingStates,
            calendarDays,
            accountMappings)
    {
        _accountMappingStateStore = accountMappingStateStore
            ?? throw new ArgumentNullException(nameof(accountMappingStateStore));
    }

    internal WorkpaperWriter(
        ICompletenessAccountPageRepository completenessAccounts,
        ICompletenessDiffPageRepository completenessDiffs,
        IDocBalancePageRepository docBalances,
        ICreatorSummaryExportRepository creatorSummaries,
        IInfSamplePageRepository infSamples,
        IFilterScenarioStore filterScenarios,
        ITagMatrixScenariosRepository tagMatrixCounts,
        ITagMatrixVoucherPageRepository tagMatrixVouchers,
        ITagMatrixRowPageRepository tagMatrixRows,
        IMappingStateStore mappingStates,
        ICalendarExportRepository calendarDays,
        IAccountMappingExportRepository accountMappings,
        IAccountMappingStore accountMappingStateStore,
        IRawGlExportRepository rawRows,
        IResultPageRdeValuesPort? resultPageRdeValues = null)
        : this(
            completenessAccounts,
            completenessDiffs,
            docBalances,
            creatorSummaries,
            infSamples,
            filterScenarios,
            tagMatrixCounts,
            tagMatrixVouchers,
            tagMatrixRows,
            mappingStates,
            calendarDays,
            accountMappings,
            resultPageRdeValues)
    {
        _accountMappingStateStore = accountMappingStateStore
            ?? throw new ArgumentNullException(nameof(accountMappingStateStore));
        _rawRows = rawRows ?? throw new ArgumentNullException(nameof(rawRows));
    }

    internal WorkpaperWriter(
        ICompletenessAccountPageRepository completenessAccounts,
        ICompletenessDiffPageRepository completenessDiffs,
        IDocBalancePageRepository docBalances,
        ICreatorSummaryExportRepository creatorSummaries,
        IInfSamplePageRepository infSamples,
        IFilterScenarioStore filterScenarios,
        ITagMatrixScenariosRepository tagMatrixCounts,
        ITagMatrixVoucherPageRepository tagMatrixVouchers,
        ITagMatrixRowPageRepository tagMatrixRows,
        IMappingStateStore mappingStates,
        ICalendarExportRepository calendarDays,
        IAccountMappingExportRepository accountMappings,
        WorkpaperWriterOptions options,
        int progressRowInterval = DefaultProgressRowInterval,
        IRawGlExportRepository? rawRows = null,
        IAccountMappingStore? accountMappingStateStore = null,
        IResultPageRdeValuesPort? resultPageRdeValues = null)
        : this(
            completenessAccounts,
            completenessDiffs,
            docBalances,
            creatorSummaries,
            infSamples,
            filterScenarios,
            tagMatrixCounts,
            tagMatrixVouchers,
            tagMatrixRows,
            mappingStates,
            calendarDays,
            accountMappings,
            resultPageRdeValues)
    {
        _rawRows = rawRows;
        _accountMappingStateStore = accountMappingStateStore;
        _continuationRowLimit = options.ValidateAndGetLimit();
        if (progressRowInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progressRowInterval));
        }
        _progressRowInterval = progressRowInterval;
    }

    public Task<ExportStats> WriteAsync(
        Stream output,
        WorkpaperContext context,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null) =>
        WriteCoreAsync(output, context, plan: null, cancellationToken, progress);

    Task<ExportStats> IWorkpaperPlanWriter.WriteAsync(
        Stream output,
        WorkpaperContext context,
        WorkpaperPlan plan,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress) =>
        WriteCoreAsync(output, context, plan, cancellationToken, progress);

    private async Task<ExportStats> WriteCoreAsync(
        Stream output,
        WorkpaperContext context,
        WorkpaperPlan? plan,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        return await ReportTemplatePackage.FillDirectStreamingAsync(
            _templates,
            ReportTemplateCatalog.WorkingPaper,
            output,
            WorkpaperDirectTemplateSheets,
            async (editor, ct) =>
            {
                var workbookPart = editor.Document.WorkbookPart
                    ?? throw new InvalidDataException("WorkingPaper 範本缺少 workbook part。");
                var stylesPart = workbookPart.WorkbookStylesPart
                    ?? throw new InvalidDataException("WorkingPaper 範本缺少 styles part。");
                var styles = WorkbookStyleMap.Append(stylesPart, WorkpaperStyles.Build());
                var session = new WorkpaperWriteSession(
                    editor,
                    styles,
                    editor.StylePatcher,
                    useFinalStep41Schema: plan is not null,
                    ct);
                var sheetStats = await EmitDirectAsync(
                    session,
                    context,
                    plan,
                    ct,
                    progress);
                session.Complete();
                if (plan?.Request.WorkbookMetadata is { } workbookMetadata)
                {
                    ReportWorkbookMetadataWorksheet.Append(
                        editor.Document,
                        workbookMetadata,
                        ct);
                }
                return sheetStats;
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<SheetStat>> EmitDirectAsync(
        WorkpaperWriteSession session,
        WorkpaperContext context,
        WorkpaperPlan? plan,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        if (plan is not null && !plan.IsFinalized)
        {
            throw new InvalidOperationException("WorkpaperPlan 尚未 Finalize。");
        }

        var sheetStats = new List<SheetStat>();

        var coverPlan = GetSheetPlan(plan, WorkpaperSheetCatalog.Cover);
        if (ShouldEmit(coverPlan))
        {
            EmitCoverSheet(session, sheetStats, context, coverPlan, cancellationToken, progress);
        }

        var introPlan = GetSheetPlan(plan, WorkpaperSheetCatalog.Intro);
        if (ShouldEmit(introPlan))
        {
            EmitIntroSheet(session, sheetStats, introPlan, cancellationToken, progress);
        }

        // step1 家族(封面後、step2 前)。step1-3 是否 emit 取決於有無 diff≠0 科目(emitter 內條件 guard)。
        var step1Plan = GetSheetPlan(plan, WorkpaperSheetCatalog.Step1);
        var step13Plan = GetSheetPlan(plan, WorkpaperSheetCatalog.Step13);
        if (ShouldEmit(step1Plan))
        {
            await EmitStep1Async(
                session,
                sheetStats,
                context,
                step1Plan,
                hasCompletenessDifferences: plan is null ? null : step13Plan?.Emit == true,
                cancellationToken,
                progress);
        }

        var step11Plan = GetSheetPlan(plan, WorkpaperSheetCatalog.Step11);
        if (ShouldEmit(step11Plan))
        {
            await EmitStep11Async(session, sheetStats, context, step11Plan, cancellationToken, progress);
        }

        var step12Plan = GetSheetPlan(plan, WorkpaperSheetCatalog.Step12);
        if (ShouldEmit(step12Plan))
        {
            await EmitStep12Async(session, sheetStats, context, step12Plan, cancellationToken, progress);
        }

        if (ShouldEmit(step13Plan))
        {
            await EmitStep13Async(session, sheetStats, context, step13Plan, cancellationToken, progress);
        }

        // step2(可靠性)+ step3/4/4-1(高風險矩陣);step3/4/4-1 共用一次 scenarios 欄集計算。
        var step2Plan = GetSheetPlan(plan, WorkpaperSheetCatalog.Step2);
        if (ShouldEmit(step2Plan))
        {
            await EmitStep2Async(
                session,
                sheetStats,
                context,
                step2Plan,
                plan?.Request.WorkbookMetadata,
                plan?.Request.CustomFields ?? [],
                cancellationToken,
                progress);
        }

        var step3Plan = GetSheetPlan(plan, WorkpaperSheetCatalog.Step3);
        var step4Plan = GetSheetPlan(plan, WorkpaperSheetCatalog.Step4);
        var step41Plan = GetSheetPlan(plan, WorkpaperSheetCatalog.Step41);
        var needsTagColumns = ShouldEmit(step3Plan) || ShouldEmit(step4Plan) || ShouldEmit(step41Plan);
        if (needsTagColumns)
        {
            var tagColumns = plan is null
                ? await TagColumnSet.LoadAsync(
                    filterScenarios,
                    tagMatrixCounts,
                    context.ProjectId,
                    context.ScenarioPositions,
                    cancellationToken)
                : TagColumnSet.FromPlan(plan, step3Plan!, step4Plan!, step41Plan!);

            if (ShouldEmit(step3Plan))
            {
                EmitStep3(
                    session, sheetStats, context, tagColumns, step3Plan, cancellationToken, progress);
            }
            if (ShouldEmit(step4Plan))
            {
                await EmitStep4Async(
                    session, sheetStats, context, tagColumns, step4Plan, cancellationToken, progress);
            }
            if (ShouldEmit(step41Plan))
            {
                if (plan is null)
                {
                    await EmitLegacyStep41Async(
                        session,
                        sheetStats,
                        context,
                        tagColumns,
                        cancellationToken,
                        progress);
                }
                else
                {
                    await EmitStep41Async(
                        session,
                        sheetStats,
                        context,
                        plan.Step41Columns,
                        plan.Request.WorkbookMetadata,
                        plan.Request.CustomFields ?? [],
                        tagColumns,
                        cancellationToken,
                        progress);
                }
            }
        }

        var step5Plan = GetSheetPlan(plan, WorkpaperSheetCatalog.Step5);
        if (ShouldEmit(step5Plan))
        {
            EmitStep5Sheet(session, sheetStats, step5Plan, cancellationToken, progress);
        }

        // 三張「自動化工具」參考資料表(對齊樣本順序,接在 step5 後)。三表結構各異 = 各自 emitter,不抽 god 模板。
        if (ShouldEmit(GetSheetPlan(plan, WorkpaperSheetCatalog.FieldInfo)))
        {
            await EmitFieldInfoSheetAsync(
                session,
                sheetStats,
                context,
                plan?.FieldInfo,
                cancellationToken,
                progress);
        }
        if (ShouldEmit(GetSheetPlan(plan, WorkpaperSheetCatalog.CalendarInfo)))
        {
            await EmitCalendarInfoSheetAsync(session, sheetStats, context, cancellationToken, progress);
        }
        if (ShouldEmit(GetSheetPlan(plan, WorkpaperSheetCatalog.AccountMapping)))
        {
            await EmitAccountMappingSheetAsync(session, sheetStats, context, cancellationToken, progress);
        }

        return sheetStats;
    }

    private static WorkpaperSheetPlan? GetSheetPlan(WorkpaperPlan? plan, string sheetName) =>
        plan?.Sheets.SingleOrDefault(sheet =>
            string.Equals(sheet.SheetName, sheetName, StringComparison.Ordinal))
        ?? (plan is null
            ? null
            : throw new InvalidOperationException($"WorkpaperPlan 缺少工作表 '{sheetName}'。"));

    private static bool ShouldEmit(WorkpaperSheetPlan? sheetPlan) =>
        sheetPlan?.Emit ?? true;

    private static string PlannedText(
        WorkpaperSheetPlan? sheetPlan,
        int index,
        string fallback)
    {
        if (sheetPlan is null)
        {
            return fallback;
        }

        if ((uint)index >= (uint)sheetPlan.Methodology.Count)
        {
            throw new InvalidOperationException(
                $"WorkpaperPlan 工作表 '{sheetPlan.SheetName}' 缺少 methodology[{index}]。");
        }

        return sheetPlan.Methodology[index];
    }

    private static IReadOnlyList<(uint Row, uint Column, string Text)> PlannedLines(
        WorkpaperSheetPlan? sheetPlan,
        IReadOnlyList<(uint Row, uint Column, string Text)> fallback)
    {
        if (sheetPlan is null)
        {
            return fallback;
        }

        if (sheetPlan.Methodology.Count < fallback.Count)
        {
            throw new InvalidOperationException(
                $"WorkpaperPlan 工作表 '{sheetPlan.SheetName}' 的 methodology 數量不足。");
        }

        return fallback
            .Select((line, index) => (line.Row, line.Column, sheetPlan.Methodology[index]))
            .ToArray();
    }

    private static void AddStat(
        List<SheetStat> stats,
        SheetStat stat,
        Action<WorkpaperProgress>? progress)
    {
        stats.Add(stat);
        progress?.Invoke(new WorkpaperProgress(stat.SheetName, stats.Count, stat.RowsWritten));
    }

    private static void ReportSheetStarted(
        IReadOnlyCollection<SheetStat> stats,
        Action<WorkpaperProgress>? progress,
        string sheetName) =>
        progress?.Invoke(new WorkpaperProgress(sheetName, stats.Count, 0));

    private void ReportIntermediateRows(
        List<SheetStat> stats,
        Action<WorkpaperProgress>? progress,
        string sheetName,
        long rowsWritten)
    {
        if (progress is not null && rowsWritten > 0 && rowsWritten % _progressRowInterval == 0)
        {
            progress(new WorkpaperProgress(sheetName, stats.Count, rowsWritten));
        }
    }

    private static IReadOnlyList<Cell> HeaderCells(SheetWriter sheet, uint row, IReadOnlyList<string> labels)
    {
        var cells = new List<Cell>(labels.Count);
        for (var i = 0; i < labels.Count; i++)
        {
            cells.Add(sheet.TextCell(row, (uint)(i + 2), labels[i], WorkpaperStyles.BoldWrap));
        }

        return cells;
    }

    /// <summary>結論列:A 欄「結論：」標籤 + B 欄結論文字,一次寫出(同列須單次 WriteRow,避免重複 RowIndex)。</summary>
    private static void WriteConclusionRow(SheetWriter sheet, uint row, string text) =>
        sheet.WriteFixedRow(row,
        [
            sheet.TextCell(row, 1, "結論：", WorkpaperStyles.Bold),
            sheet.TextCell(row, 2, text, WorkpaperStyles.Bold)
        ]);

    private static decimal Display(long scaled, int moneyScale) =>
        moneyScale <= 0 ? scaled : (decimal)scaled / moneyScale;

    // ---- 固定字串(逐字對齊本機參考樣本,xlsx_inspect.py 復解;含原樣換行)----


    private sealed class TagColumnSet
    {
        private const uint Step4FixedColumns = 5;   // A-E
        private const uint Step41FixedColumns = 16; // A-P

        private readonly IReadOnlyList<int> _allPositions;
        private readonly IReadOnlyList<int> _rowHitPositions;
        private readonly IReadOnlyList<int> _hitVoucherPositions;

        private TagColumnSet(
            IReadOnlyList<ScenarioRow> scenarios,
            IReadOnlyList<int> allPositions,
            IReadOnlyList<int> rowHitPositions,
            IReadOnlyList<int> hitVoucherPositions,
            bool isFromPlan = false)
        {
            Scenarios = scenarios;
            _allPositions = allPositions;
            _rowHitPositions = rowHitPositions;
            _hitVoucherPositions = hitVoucherPositions;
            IsFromPlan = isFromPlan;
        }

        /// <summary>step3 一列(代號 C{Position} / 條件描述=Name / 原因=Rationale / 傳票命中數=VoucherHitCount)。</summary>
        public sealed record ScenarioRow(
            int Position,
            string Name,
            string Rationale,
            long VoucherHitCount,
            long RowHitCount);

        /// <summary>step3 列來源:position 升冪的情境摘要(name/rationale/voucherHitCount)。</summary>
        public IReadOnlyList<ScenarioRow> Scenarios { get; }

        public IReadOnlyList<int> AllPositions => _allPositions;

        public IReadOnlyList<int> RowHitPositions => _rowHitPositions;

        public IReadOnlyList<int> HitVoucherPositions => _hitVoucherPositions;

        public bool IsFromPlan { get; }

        public static async Task<TagColumnSet> LoadAsync(
            IFilterScenarioStore scenarioStore, ITagMatrixScenariosRepository countsRepo,
            string projectId, IReadOnlyList<int> selectedPositions, CancellationToken cancellationToken)
        {
            var saved = await scenarioStore.ListAsync(projectId, cancellationToken);
            var counts = await countsRepo.GetCountsAsync(projectId, cancellationToken);
            var selected = new HashSet<int>(selectedPositions);

            var scenarios = saved
                .Where(s => selected.Contains(s.Position))
                .OrderBy(s => s.Position)
                .Select(s =>
                {
                    var (voucherHits, rowHits) = counts.GetValueOrDefault(s.Position);
                    return new ScenarioRow(
                        s.Position,
                        s.Name,
                        s.Rationale,
                        voucherHits,
                        rowHits);
                })
                .ToList();

            var allPositions = scenarios.Select(s => s.Position).ToList();
            var rowHitPositions = scenarios.Where(s => s.RowHitCount > 0).Select(s => s.Position).ToList();
            return new TagColumnSet(scenarios, allPositions, rowHitPositions, []);
        }

        public static TagColumnSet FromPlan(
            WorkpaperPlan plan,
            WorkpaperSheetPlan step3,
            WorkpaperSheetPlan step4,
            WorkpaperSheetPlan step41)
        {
            RequireSamePositions(step3, plan.AllScenarioPositions);
            RequireSamePositions(step4, plan.AllScenarioPositions);
            RequireSamePositions(step41, plan.RowHitScenarioPositions);

            var byPosition = plan.Scenarios.ToDictionary(scenario => scenario.Position);
            var scenarios = plan.AllScenarioPositions
                .Select(position => byPosition.TryGetValue(position, out var scenario)
                    ? new ScenarioRow(
                        scenario.Position,
                        scenario.Name,
                        scenario.Rationale,
                        scenario.VoucherHitCount,
                        scenario.RowHitCount)
                    : throw new InvalidOperationException(
                        $"WorkpaperPlan 缺少情境 position {position}。"))
                .ToArray();
            var hitVoucherPositions = plan.Scenarios
                .Where(scenario =>
                    scenario.RowHitCount > 0
                    && scenario.TagScope == WorkpaperScenarioTagScope.HitVoucherRows)
                .Select(scenario => scenario.Position)
                .ToArray();

            return new TagColumnSet(
                scenarios,
                plan.AllScenarioPositions,
                plan.RowHitScenarioPositions,
                hitVoucherPositions,
                isFromPlan: true);
        }

        private static void RequireSamePositions(
            WorkpaperSheetPlan sheetPlan,
            IReadOnlyList<int> expected)
        {
            if (!sheetPlan.ScenarioPositions.SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"WorkpaperPlan 工作表 '{sheetPlan.SheetName}' 的情境位置集合不一致。");
            }
        }

        /// <summary>step4 欄標:全 position 升冪,標頭 C{position}{suffix}(suffix 空)。接在固定欄之後。</summary>
        public void AppendAllPositionHeaders(SheetWriter sheet, List<Cell> cells, uint row, string suffix) =>
            AppendHeaders(sheet, cells, row, _allPositions, Step4FixedColumns, suffix);

        /// <summary>step4-1 欄標:只 rowHitCount&gt;0 的 position 升冪,標頭 C{position}_TAG。接在固定欄之後。</summary>
        public void AppendRowHitPositionHeaders(SheetWriter sheet, List<Cell> cells, uint row) =>
            AppendHeaders(sheet, cells, row, _rowHitPositions, Step41FixedColumns, "_TAG");

        /// <summary>step4 列:逐 position 依 matched 是否含之標 Y/空。</summary>
        public void AppendAllPositionMarks(SheetWriter sheet, List<Cell> cells, uint row, IReadOnlyList<int> matched) =>
            AppendMarks(sheet, cells, row, _allPositions, Step4FixedColumns, matched);

        /// <summary>step4-1 列:逐 rowHit position 依 matched 是否含之標 Y/空。</summary>
        public void AppendRowHitPositionMarks(SheetWriter sheet, List<Cell> cells, uint row, IReadOnlyList<int> matched) =>
            AppendMarks(sheet, cells, row, _rowHitPositions, Step41FixedColumns, matched);

        private static void AppendHeaders(
            SheetWriter sheet, List<Cell> cells, uint row, IReadOnlyList<int> positions, uint fixedColumns, string suffix)
        {
            for (var i = 0; i < positions.Count; i++)
            {
                cells.Add(sheet.TextCell(
                    row, fixedColumns + (uint)positions[i], $"C{positions[i]}{suffix}", WorkpaperStyles.Bold));
            }
        }

        private static void AppendMarks(
            SheetWriter sheet, List<Cell> cells, uint row, IReadOnlyList<int> positions, uint fixedColumns, IReadOnlyList<int> matched)
        {
            for (var i = 0; i < positions.Count; i++)
            {
                var column = fixedColumns + (uint)positions[i];
                cells.Add(matched.Contains(positions[i])
                    ? sheet.TextCell(row, column, "Y")
                    : sheet.BlankCell(row, column));
            }
        }
    }

    // ================= SAX 原語=================

    private static string DigitsOnly(string value)
    {
        return string.Concat(value.Where(char.IsDigit));
    }

    /// <summary>
    /// 行事曆日期顯示:儲存格式 yyyy-MM-dd → 樣本顯示形式 yyyy/MM/dd。
    /// 非該格式者原樣返回(不臆造/不竄改;CalendarDayProjector 已保證匯入為 yyyy-MM-dd,此為防禦)。
    /// </summary>
    private static string DisplayDate(string isoDate)
    {
        return DateTime.TryParseExact(
                isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)
            : isoDate;
    }

    /// <summary>
    /// 單張 worksheet 的串流寫出器(SAX 低階原語)。封裝 OpenXmlPartWriter + 元素順序約束:
    /// 構造時開 worksheet → sheetData(forward-only);WriteRow/WriteFixedRow 即時串流寫列;
    /// AddMerge 累積合併範圍(數量有界、屬版面);<see cref="CloseAndSummarize"/> 關 sheetData →
    /// 寫 mergeCells（schema 要求在 sheetData 之後）→ 關 worksheet。
    /// 資料列計入 RowsWritten;固定文字/表頭列不計(對齊 SheetStat 語意)。
    /// 供資料表 emitter 以 keyset 迴圈逐列呼叫 WriteRow（串流、不全載入）。
    /// </summary>
    private sealed class SheetWriter : IDisposable
    {
        private readonly string _name;
        private readonly OpenXmlWriter? _writer;
        private readonly DirectTemplateWorksheetOverlayWriter? _overlay;
        private readonly WorkbookStyleMap _styles;
        private readonly Func<uint, uint, uint, uint>? _mapCellStyle;
        private readonly CancellationToken _cancellationToken;
        private readonly bool _preserveTemplate;
        private readonly string? _pageSetupRelationshipId;
        private readonly List<string> _merges = [];
        private readonly List<(string Range, string Formula)> _validations = [];
        private readonly List<(uint Min, uint Max, double Width, bool Hidden, bool BestFit)> _columns = [];
        private readonly Dictionary<uint, double> _rowHeights = [];
        private string? _dimension;
        private bool _viewConfigured;
        private bool _showGridLines = true;
        private uint? _zoomScale;
        private uint? _freezeRows;
        private double? _defaultRowHeight;
        private (double Left, double Right, double Top, double Bottom, double Header, double Footer)? _pageMargins;
        private (OrientationValues Orientation, uint PaperSize, uint FitToWidth, uint FitToHeight)? _pageSetup;
        private long _dataRows;
        private bool _protect;
        private bool _sheetDataStarted;
        private bool _closed;
        private bool _disposed;

        private SheetWriter(
            string name,
            WorksheetPart? part,
            DirectTemplateWorksheetOverlayWriter? overlay,
            WorkbookStyleMap styles,
            Func<uint, uint, uint, uint>? mapCellStyle,
            CancellationToken cancellationToken,
            bool preserveTemplate,
            string? pageSetupRelationshipId = null)
        {
            _name = name;
            _overlay = overlay;
            _styles = styles;
            _mapCellStyle = mapCellStyle;
            _cancellationToken = cancellationToken;
            _preserveTemplate = preserveTemplate;
            _pageSetupRelationshipId = pageSetupRelationshipId;
            if (part is not null)
            {
                _writer = OpenXmlPartWriter.Create(part);
                _writer.WriteStartDocument();
                _writer.WriteStartElement(new Worksheet());
            }
        }

        internal static SheetWriter OverlayTemplate(
            string name,
            WorksheetPart part,
            DirectTemplateWorksheetOverlayPlan plan,
            WorkbookStyleMap styles,
            Func<uint, uint, uint, uint>? mapCellStyle,
            CancellationToken cancellationToken) =>
            new(
                name,
                part: null,
                new DirectTemplateWorksheetOverlayWriter(
                    name,
                    part,
                    plan,
                    cancellationToken),
                styles,
                mapCellStyle,
                cancellationToken,
                preserveTemplate: false);

        internal static SheetWriter CreateContinuation(
            string name,
            WorksheetPart part,
            WorkbookStyleMap styles,
            Func<uint, uint, uint, uint>? mapCellStyle,
            CancellationToken cancellationToken,
            string? pageSetupRelationshipId) =>
            new(
                name,
                part,
                overlay: null,
                styles,
                mapCellStyle,
                cancellationToken,
                preserveTemplate: false,
                pageSetupRelationshipId);

        internal static SheetWriter PreserveTemplate(
            string name,
            WorkbookStyleMap styles,
            CancellationToken cancellationToken) =>
            new(
                name,
                part: null,
                overlay: null,
                styles,
                mapCellStyle: null,
                cancellationToken,
                preserveTemplate: true);

        public string Name => _name;

        public void SetColumnWidth(
            uint min,
            uint max,
            double width,
            bool hidden = false,
            bool bestFit = false)
        {
            EnsurePreambleMutable();
            ExcelWorksheetConstraints.EnsureCell(1, min);
            ExcelWorksheetConstraints.EnsureCell(1, max);
            if (max < min)
            {
                throw new ArgumentOutOfRangeException(nameof(max));
            }
            if (!double.IsFinite(width) || width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            _columns.Add((min, max, width, hidden, bestFit));
        }

        public void SetDefaultRowHeight(double height)
        {
            EnsurePreambleMutable();
            if (!double.IsFinite(height) || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            _defaultRowHeight = height;
        }

        public void SetDimension(uint maxRow, uint maxColumn)
        {
            EnsurePreambleMutable();
            ExcelWorksheetConstraints.EnsureCell(maxRow, maxColumn);
            _dimension = $"A1:{Reference(maxColumn, maxRow)}";
        }

        public void SetRowHeight(uint row, double height)
        {
            EnsurePreambleMutable();
            ExcelWorksheetConstraints.EnsureCell(row, 1);
            if (!double.IsFinite(height) || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            _rowHeights[row] = height;
        }

        public void SetView(bool showGridLines, uint zoomScale)
        {
            EnsurePreambleMutable();
            if (zoomScale is < 10 or > 400)
            {
                throw new ArgumentOutOfRangeException(nameof(zoomScale));
            }

            _viewConfigured = true;
            _showGridLines = showGridLines;
            _zoomScale = zoomScale;
        }

        public void FreezeRows(uint rows)
        {
            EnsurePreambleMutable();
            if (rows >= ExcelWorksheetConstraints.MaxRows)
            {
                throw new ArgumentOutOfRangeException(nameof(rows));
            }

            _freezeRows = rows;
            _viewConfigured = true;
        }

        public void SetPageMargins(
            double left,
            double right,
            double top,
            double bottom,
            double header,
            double footer)
        {
            var values = new[] { left, right, top, bottom, header, footer };
            if (values.Any(value => !double.IsFinite(value) || value < 0))
            {
                throw new ArgumentOutOfRangeException(nameof(left), "Page margins must be finite non-negative values.");
            }

            _pageMargins = (left, right, top, bottom, header, footer);
        }

        public void SetPageSetup(
            OrientationValues orientation,
            uint paperSize,
            uint fitToWidth,
            uint fitToHeight)
        {
            if (paperSize == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(paperSize));
            }

            _pageSetup = (orientation, paperSize, fitToWidth, fitToHeight);
        }

        /// <summary>寫一列「資料列」(計入 RowsWritten)。供資料表 keyset 逐列呼叫。</summary>
        public void WriteRow(uint rowIndex, IReadOnlyList<Cell> cells)
        {
            WriteRowCore(rowIndex, cells);
            _dataRows++;
        }

        /// <summary>寫一列固定文字/表頭(不計入 RowsWritten)。</summary>
        public void WriteFixedRow(uint rowIndex, IReadOnlyList<Cell> cells)
        {
            WriteRowCore(rowIndex, cells);
        }

        /// <summary>累積一個合併範圍(如 "B1:O1");實際寫出延到 sheetData 關閉之後(schema 約束)。</summary>
        public void AddMerge(string range)
        {
            _merges.Add(range);
        }

        public void AddListValidation(string range, string formula) => _validations.Add((range, formula));

        public void Protect() => _protect = true;

        /// <summary>inline-string 文字 cell(空字串會被視為空 cell 不輸出值,但保留樣式)。</summary>
        public Cell TextCell(uint rowIndex, uint column, string? value, uint style = WorkpaperStyles.Default)
        {
            var cell = new Cell
            {
                CellReference = Reference(column, rowIndex),
                StyleIndex = MapCellStyle(rowIndex, column, style)
            };
            if (!string.IsNullOrEmpty(value))
            {
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString(new Text(value));
            }

            return cell;
        }

        /// <summary>數值 cell(以整數/小數字面寫入;顯示格式交給 StyleIndex 的 numFmt)。供資料表金額欄用。</summary>
        public Cell NumberCell(uint rowIndex, uint column, decimal value, uint style = WorkpaperStyles.Default)
        {
            return new Cell
            {
                CellReference = Reference(column, rowIndex),
                StyleIndex = MapCellStyle(rowIndex, column, style),
                DataType = CellValues.Number,
                CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture))
            };
        }

        /// <summary>
        /// 已驗證的 invariant finite numeric literal。只供需保留超出 Decimal 範圍之
        /// XLSX native Number 的串流投影；一般金額仍應使用 decimal overload。
        /// </summary>
        public Cell NumberLiteralCell(
            uint rowIndex,
            uint column,
            string value,
            uint style = WorkpaperStyles.Default)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Numeric literal 不得為空白。", nameof(value));
            }

            return new Cell
            {
                CellReference = Reference(column, rowIndex),
                StyleIndex = MapCellStyle(rowIndex, column, style),
                DataType = CellValues.Number,
                CellValue = new CellValue(value)
            };
        }

        /// <summary>空白 cell(只佔位 + 帶樣式,無值)。手填欄/版面留白用。</summary>
        public Cell BlankCell(uint rowIndex, uint column, uint style = WorkpaperStyles.Default)
        {
            return new Cell
            {
                CellReference = Reference(column, rowIndex),
                StyleIndex = MapCellStyle(rowIndex, column, style)
            };
        }

        /// <summary>關 sheetData → 寫 mergeCells（若有）→ 關 worksheet,回該表的 <see cref="SheetStat"/>。</summary>
        public SheetStat CloseAndSummarize(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stat = new SheetStat(_name, _dataRows);
            Dispose();
            return stat;
        }

        /// <summary>同 <see cref="CloseAndSummarize"/>,但以外部累計的資料列數覆寫(資料列由骨架累計而非逐次 WriteRow)。</summary>
        public SheetStat CloseAndSummarizeWith(long dataRows, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stat = new SheetStat(_name, dataRows);
            Dispose();
            return stat;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // 例外路徑也要關掉開啟的元素並釋放 writer,否則 part 留半截 XML
            CloseElements();
            _writer?.Dispose();
            _overlay?.Dispose();
            _disposed = true;
        }

        private void WriteRowCore(uint rowIndex, IReadOnlyList<Cell> cells)
        {
            ExcelWorksheetConstraints.EnsureCell(rowIndex, 1);
            EnsureSheetDataStarted();

            var row = new Row { RowIndex = rowIndex };
            if (_rowHeights.TryGetValue(rowIndex, out var height))
            {
                row.Height = height;
                row.CustomHeight = true;
            }
            foreach (var cell in cells)
            {
                row.Append(cell);
            }

            if (_preserveTemplate)
            {
                return;
            }
            if (_overlay is not null)
            {
                _overlay.WriteRow(XElement.Parse(
                    row.OuterXml,
                    LoadOptions.PreserveWhitespace));
                return;
            }

            // 整列一次寫出:記憶體只持有當前列(串流不變),且型別安全勝於手拆 start/string/end
            _writer!.WriteElement(row);
        }

        private void CloseElements()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            EnsureSheetDataStarted();

            if (_preserveTemplate)
            {
                return;
            }

            var postElements = BuildPostElements();
            if (_overlay is not null)
            {
                _overlay.Complete(postElements
                    .Select(element => XElement.Parse(
                        element.OuterXml,
                        LoadOptions.PreserveWhitespace))
                    .ToArray());
                return;
            }

            _writer!.WriteEndElement(); // </sheetData>
            foreach (var element in postElements)
            {
                _writer.WriteElement(element);
            }
            _writer.WriteEndElement(); // </worksheet>
        }

        private IReadOnlyList<OpenXmlElement> BuildPostElements()
        {
            var elements = new List<OpenXmlElement>();
            if (_protect)
            {
                elements.Add(new SheetProtection
                {
                    Sheet = true,
                    Objects = true,
                    Scenarios = true,
                    SelectLockedCells = false,
                    SelectUnlockedCells = false
                });
            }

            if (_merges.Count > 0)
            {
                // mergeCells 必須在 sheetData 之後(CT_Worksheet 子序列);count 屬必填
                var mergeCells = new MergeCells { Count = (uint)_merges.Count };
                foreach (var range in _merges)
                {
                    mergeCells.Append(new MergeCell { Reference = range });
                }
                elements.Add(mergeCells);
            }

            if (_validations.Count > 0)
            {
                var validations = new DataValidations { Count = (uint)_validations.Count };
                foreach (var (range, formula) in _validations)
                {
                    var validation = new DataValidation
                    {
                        Type = DataValidationValues.List,
                        AllowBlank = true,
                        ShowErrorMessage = true,
                        SequenceOfReferences = new ListValue<StringValue> { InnerText = range }
                    };
                    validation.Append(new Formula1(formula));
                    validations.Append(validation);
                }
                elements.Add(validations);
            }

            if (_pageMargins is { } margins)
            {
                elements.Add(new PageMargins
                {
                    Left = margins.Left,
                    Right = margins.Right,
                    Top = margins.Top,
                    Bottom = margins.Bottom,
                    Header = margins.Header,
                    Footer = margins.Footer
                });
            }

            if (_pageSetup is { } setup)
            {
                elements.Add(new PageSetup
                {
                    Orientation = setup.Orientation,
                    PaperSize = setup.PaperSize,
                    FitToWidth = setup.FitToWidth,
                    FitToHeight = setup.FitToHeight,
                    Id = _pageSetupRelationshipId
                });
            }

            return elements;
        }

        private void EnsurePreambleMutable()
        {
            if (_sheetDataStarted)
            {
                throw new InvalidOperationException("Worksheet columns, view and row layout must be set before writing rows.");
            }
        }

        private void EnsureSheetDataStarted()
        {
            if (_sheetDataStarted)
            {
                return;
            }

            _sheetDataStarted = true;
            if (_preserveTemplate)
            {
                return;
            }
            if (_overlay is not null)
            {
                var dimension = _dimension is null
                    ? null
                    : new SheetDimension { Reference = _dimension };
                var generatedColumns = BuildColumns();
                _overlay.Start(
                    dimension is null
                        ? null
                        : XElement.Parse(dimension.OuterXml, LoadOptions.PreserveWhitespace),
                    generatedColumns is null
                        ? null
                        : XElement.Parse(
                            generatedColumns.OuterXml,
                            LoadOptions.PreserveWhitespace));
                return;
            }

            var writer = _writer
                ?? throw new InvalidOperationException("Continuation worksheet writer 未初始化。");

            if (_dimension is not null)
            {
                writer.WriteElement(new SheetDimension { Reference = _dimension });
            }

            if (_viewConfigured)
            {
                var view = new SheetView
                {
                    WorkbookViewId = 0U,
                    ShowGridLines = _showGridLines
                };
                if (_zoomScale is { } zoomScale)
                {
                    view.ZoomScale = zoomScale;
                }
                if (_freezeRows is > 0)
                {
                    view.Append(new Pane
                    {
                        VerticalSplit = (double)_freezeRows.Value,
                        TopLeftCell = Reference(1, _freezeRows.Value + 1),
                        ActivePane = PaneValues.BottomLeft,
                        State = PaneStateValues.Frozen
                    });
                }

                writer.WriteElement(new SheetViews(view));
            }

            if (_defaultRowHeight is { } defaultHeight)
            {
                writer.WriteElement(new SheetFormatProperties
                {
                    DefaultRowHeight = defaultHeight,
                    CustomHeight = true
                });
            }

            if (BuildColumns() is { } columns)
            {
                writer.WriteElement(columns);
            }

            writer.WriteStartElement(new SheetData());
        }

        private Columns? BuildColumns()
        {
            if (_columns.Count == 0)
            {
                return null;
            }

            var columns = new Columns();
            foreach (var column in _columns)
            {
                columns.Append(new Column
                {
                    Min = column.Min,
                    Max = column.Max,
                    Width = column.Width,
                    CustomWidth = true,
                    Hidden = column.Hidden,
                    BestFit = column.BestFit
                });
            }
            return columns;
        }

        private uint MapCellStyle(uint row, uint column, uint sourceStyle)
        {
            var mapped = _styles.Map(sourceStyle);
            return _mapCellStyle?.Invoke(row, column, mapped) ?? mapped;
        }

        /// <summary>(column, row) → A1 形式參考。column/row 皆 1-based。</summary>
        private static string Reference(uint column, uint rowIndex)
        {
            ExcelWorksheetConstraints.EnsureCell(rowIndex, column);
            return ColumnLetters(column) + rowIndex.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>1→A、26→Z、27→AA…(Excel 26 進位欄名)。</summary>
        private static string ColumnLetters(uint column)
        {
            var letters = string.Empty;
            while (column > 0)
            {
                var remainder = (int)((column - 1) % 26);
                letters = (char)('A' + remainder) + letters;
                column = (column - 1) / 26;
            }

            return letters;
        }
    }
}
