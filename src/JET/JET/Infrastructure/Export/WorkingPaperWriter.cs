using System.Globalization;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 匯出底稿(WorkingPaper).xlsx 寫出器。<see cref="IWorkingPaperPlanWriter"/> 的實作:
/// 對外只 <see cref="IWorkingPaperPlanWriter.WriteAsync"/>,內部隱藏全部 OpenXML 細節。
/// 每一份明細都透過一個查詢讀完的讀取介面取得，不呼叫畫面的分頁查詢。
///
/// 為什麼用 SAX（OpenXmlPartWriter），不用 ClosedXML：
///   母體可能達百萬列以上。ClosedXML 與 OpenXML DOM 要先把整張工作表建成記憶體裡的物件樹才寫出，會記憶體不足；
///   SAX 逐個元素往前寫，記憶體用量有上限。所以 ClosedXML 只用在開發用的示範檔 DemoWorkbookWriter，底稿一律用 SAX 寫出。
///
/// 為什麼用 inline string：
///   底稿的文字寫成 inline string，每格自帶文字，一次寫完不回頭，不必另外收集共用字串表。
///
/// 元素順序的硬性規定（ISO/IEC 29500 CT_Worksheet）由 <see cref="SheetWriter"/> 處理：
///   worksheet 的子元素裡，mergeCells 必須在 sheetData 之後。
///   寫出程式可以用任意順序呼叫 WriteRow 與 AddMerge，SheetWriter 會先寫完整個 sheetData，關閉後才寫 mergeCells。
///
/// step1 系列資料表的共用部分：
///   <see cref="WriteCommonHeader"/> 寫 A1-A4，依序是公司、期間、財報準備日與斜體說明，四張表都有。
///   step1-1（條件例外表）與 step1-3（完整性差異條件表）結構不同，各自寫出。
///   完整性差異科目數為零時，底稿計畫不寫 step1-3，這張工作表也不註冊；每張表寫不寫都看底稿計畫。
///
/// step2、step3、step4 與 step4-1：可靠性測試與高風險條件彙總的工作表。
///   step2 可靠性測試的樣本用一個查詢讀完，照舊版範本以借貸代號與帶號金額寫入各欄；
///   原始 GL 列每批讀回，用來取原始的借貸代號；E 欄借貸代號與 J 欄分錄來源的寫法見 InfSampleDisplayValues。
///   step3、step4 與 step4-1 的情境欄由本次所選情境決定：step4 用全部所選 position，
///   step4-1 只用所選情境中有分錄直接命中（rowHitCount>0）的 position。
///   先用所選情境算出欄的清單（<see cref="TagColumnSet"/>），再逐列看那一列的 matchedPositions 有沒有某個 position，
///   有就寫 Y；不依欄號另外寫判斷。
///   step4 用一次只往前讀的查詢取回全部符合的傳票與各傳票符合的情境位置；
///   step4-1 走 prepared session，只往前讀一次。
/// </summary>
internal sealed partial class WorkingPaperWriter(
    ICompletenessAccountStreamRepository completenessAccounts,
    ICompletenessDiffStreamRepository completenessDiffs,
    IUnbalancedVoucherDateRowStreamRepository voucherBalances,
    IPreparerSummaryExportRepository preparerSummaries,
    IInfSampleStreamRepository infSamples,
    IWorkingPaperHighRiskVoucherStreamRepository highRiskVouchers,
    IWorkingPaperHighRiskVoucherDetailPreparedSessionFactory highRiskVoucherDetailPreparedSessions,
    IMappingStateStore mappingStates,
    ICalendarExportRepository calendarDays,
    IAccountMappingExportRepository accountMapping,
    IResultPageRdeValuesPort? resultPageRdeValues = null) : IWorkingPaperPlanWriter, IFormalWorkingPaperPlanWriter
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
    private readonly IAccountMappingStore? _accountMappingStateStore;

    internal WorkingPaperWriter(
        ICompletenessAccountStreamRepository completenessAccounts,
        ICompletenessDiffStreamRepository completenessDiffs,
        IUnbalancedVoucherDateRowStreamRepository voucherBalances,
        IPreparerSummaryExportRepository preparerSummaries,
        IInfSampleStreamRepository infSamples,
        IWorkingPaperHighRiskVoucherStreamRepository highRiskVouchers,
        IWorkingPaperHighRiskVoucherDetailPreparedSessionFactory highRiskVoucherDetailPreparedSessions,
        IMappingStateStore mappingStates,
        ICalendarExportRepository calendarDays,
        IAccountMappingExportRepository accountMapping,
        IRawGlExportRepository rawRows)
        : this(
            completenessAccounts,
            completenessDiffs,
            voucherBalances,
            preparerSummaries,
            infSamples,
            highRiskVouchers,
            highRiskVoucherDetailPreparedSessions,
            mappingStates,
            calendarDays,
            accountMapping)
    {
        _rawRows = rawRows ?? throw new ArgumentNullException(nameof(rawRows));
    }

    internal WorkingPaperWriter(
        ICompletenessAccountStreamRepository completenessAccounts,
        ICompletenessDiffStreamRepository completenessDiffs,
        IUnbalancedVoucherDateRowStreamRepository voucherBalances,
        IPreparerSummaryExportRepository preparerSummaries,
        IInfSampleStreamRepository infSamples,
        IWorkingPaperHighRiskVoucherStreamRepository highRiskVouchers,
        IWorkingPaperHighRiskVoucherDetailPreparedSessionFactory highRiskVoucherDetailPreparedSessions,
        IMappingStateStore mappingStates,
        ICalendarExportRepository calendarDays,
        IAccountMappingExportRepository accountMapping,
        IAccountMappingStore accountMappingStateStore)
        : this(
            completenessAccounts,
            completenessDiffs,
            voucherBalances,
            preparerSummaries,
            infSamples,
            highRiskVouchers,
            highRiskVoucherDetailPreparedSessions,
            mappingStates,
            calendarDays,
            accountMapping)
    {
        _accountMappingStateStore = accountMappingStateStore
            ?? throw new ArgumentNullException(nameof(accountMappingStateStore));
    }

    internal WorkingPaperWriter(
        ICompletenessAccountStreamRepository completenessAccounts,
        ICompletenessDiffStreamRepository completenessDiffs,
        IUnbalancedVoucherDateRowStreamRepository voucherBalances,
        IPreparerSummaryExportRepository preparerSummaries,
        IInfSampleStreamRepository infSamples,
        IWorkingPaperHighRiskVoucherStreamRepository highRiskVouchers,
        IWorkingPaperHighRiskVoucherDetailPreparedSessionFactory highRiskVoucherDetailPreparedSessions,
        IMappingStateStore mappingStates,
        ICalendarExportRepository calendarDays,
        IAccountMappingExportRepository accountMapping,
        IAccountMappingStore accountMappingStateStore,
        IRawGlExportRepository rawRows,
        IResultPageRdeValuesPort? resultPageRdeValues = null)
        : this(
            completenessAccounts,
            completenessDiffs,
            voucherBalances,
            preparerSummaries,
            infSamples,
            highRiskVouchers,
            highRiskVoucherDetailPreparedSessions,
            mappingStates,
            calendarDays,
            accountMapping,
            resultPageRdeValues)
    {
        _accountMappingStateStore = accountMappingStateStore
            ?? throw new ArgumentNullException(nameof(accountMappingStateStore));
        _rawRows = rawRows ?? throw new ArgumentNullException(nameof(rawRows));
    }

    internal WorkingPaperWriter(
        ICompletenessAccountStreamRepository completenessAccounts,
        ICompletenessDiffStreamRepository completenessDiffs,
        IUnbalancedVoucherDateRowStreamRepository voucherBalances,
        IPreparerSummaryExportRepository preparerSummaries,
        IInfSampleStreamRepository infSamples,
        IWorkingPaperHighRiskVoucherStreamRepository highRiskVouchers,
        IWorkingPaperHighRiskVoucherDetailPreparedSessionFactory highRiskVoucherDetailPreparedSessions,
        IMappingStateStore mappingStates,
        ICalendarExportRepository calendarDays,
        IAccountMappingExportRepository accountMapping,
        WorkingPaperWriterOptions options,
        int progressRowInterval = DefaultProgressRowInterval,
        IRawGlExportRepository? rawRows = null,
        IAccountMappingStore? accountMappingStateStore = null,
        IResultPageRdeValuesPort? resultPageRdeValues = null,
        ReportTemplateCatalog? templates = null)
        : this(
            completenessAccounts,
            completenessDiffs,
            voucherBalances,
            preparerSummaries,
            infSamples,
            highRiskVouchers,
            highRiskVoucherDetailPreparedSessions,
            mappingStates,
            calendarDays,
            accountMapping,
            resultPageRdeValues)
    {
        _rawRows = rawRows;
        _accountMappingStateStore = accountMappingStateStore;
        _continuationRowLimit = options.ValidateAndGetLimit();
        _templates = templates ?? ReportTemplateCatalog.Default;
        if (progressRowInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progressRowInterval));
        }
        _progressRowInterval = progressRowInterval;
    }

    Task<ExportStats> IWorkingPaperPlanWriter.WriteAsync(
        Stream output,
        WorkingPaperContext context,
        WorkingPaperPlan plan,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress) =>
        WriteCoreAsync(output, context, plan, cancellationToken, progress);

    private async Task<ExportStats> WriteCoreAsync(
        Stream output,
        WorkingPaperContext context,
        WorkingPaperPlan plan,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        return await ReportTemplatePackage.FillDirectStreamingAsync(
            _templates,
            ReportTemplateCatalog.WorkingPaper,
            output,
            WorkingPaperDirectTemplateSheets,
            (editor, ct) => FillTemplateAsync(editor, context, plan, ct, progress),
            cancellationToken,
            FormalWorkbookLayout.AsWritten);
    }

    /// <summary>
    /// 把整份底稿寫進已經開啟的範本活頁簿。不論寫完、失敗或取消，結束前都放掉最後開啟的工作表。
    /// </summary>
    // 測試直接呼叫它，核對失敗或取消之後工作表已經放掉；正式程式只由匯出呼叫。
    internal async Task<IReadOnlyList<SheetStat>> FillTemplateAsync(
        DirectTemplateWorkbookEditor editor,
        WorkingPaperContext context,
        WorkingPaperPlan plan,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var workbookPart = editor.Document.WorkbookPart
            ?? throw new InvalidDataException("WorkingPaper 範本缺少 workbook part。");
        var stylesPart = workbookPart.WorkbookStylesPart
            ?? throw new InvalidDataException("WorkingPaper 範本缺少 styles part。");
        var styles = WorkbookStyleMap.Append(stylesPart, WorkingPaperStyles.Build());
        var session = new WorkingPaperWriteSession(
            editor,
            styles,
            editor.StylePatcher,
            cancellationToken);
        try
        {
            var sheetStats = await EmitDirectAsync(
                session,
                context,
                plan,
                cancellationToken,
                progress);
            session.Complete();
            if (plan.Request.WorkbookMetadata is { } workbookMetadata)
            {
                ReportWorkbookMetadataWorksheet.Append(
                    editor.Document,
                    workbookMetadata,
                    cancellationToken);
            }
            return sheetStats;
        }
        finally
        {
            session.ReleaseLastOpenedSheet();
        }
    }

    private async Task<IReadOnlyList<SheetStat>> EmitDirectAsync(
        WorkingPaperWriteSession session,
        WorkingPaperContext context,
        WorkingPaperPlan plan,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        if (!plan.IsFinalized)
        {
            throw new InvalidOperationException("WorkingPaperPlan 尚未 Finalize。");
        }

        var sheetStats = new List<SheetStat>();

        var coverPlan = GetSheetPlan(plan, WorkingPaperSheetCatalog.Cover);
        if (ShouldEmit(coverPlan))
        {
            EmitCoverSheet(session, sheetStats, context, coverPlan, cancellationToken, progress);
        }

        var introPlan = GetSheetPlan(plan, WorkingPaperSheetCatalog.Intro);
        if (ShouldEmit(introPlan))
        {
            EmitIntroSheet(session, sheetStats, introPlan, cancellationToken, progress);
        }

        // step1 系列（封面後、step2 前）。step1-3 只在底稿計畫要寫時寫出，也就是有差異金額不為 0 的科目時。
        var completenessPlan = GetSheetPlan(plan, WorkingPaperSheetCatalog.Completeness);
        var completenessDifferencePlan = GetSheetPlan(plan, WorkingPaperSheetCatalog.CompletenessDifference);
        if (ShouldEmit(completenessPlan))
        {
            await EmitCompletenessAsync(
                session,
                sheetStats,
                context,
                completenessPlan,
                hasCompletenessDifferences: completenessDifferencePlan.Emit,
                cancellationToken,
                progress);
        }

        var voucherBalancePlan = GetSheetPlan(plan, WorkingPaperSheetCatalog.VoucherBalance);
        if (ShouldEmit(voucherBalancePlan))
        {
            await EmitVoucherBalanceAsync(session, sheetStats, context, voucherBalancePlan, cancellationToken, progress);
        }

        var preparerSummaryPlan = GetSheetPlan(plan, WorkingPaperSheetCatalog.PreparerSummary);
        if (ShouldEmit(preparerSummaryPlan))
        {
            await EmitPreparerSummaryAsync(session, sheetStats, context, preparerSummaryPlan, cancellationToken, progress);
        }

        if (ShouldEmit(completenessDifferencePlan))
        {
            await EmitCompletenessDifferenceAsync(session, sheetStats, context, completenessDifferencePlan, cancellationToken, progress);
        }

        // step2(可靠性)+ step3/4/4-1(高風險條件彙總);step3/4/4-1 共用一次 scenarios 欄集計算。
        var infSamplingPlan = GetSheetPlan(plan, WorkingPaperSheetCatalog.InfSampling);
        if (ShouldEmit(infSamplingPlan))
        {
            await EmitInfSamplingAsync(
                session,
                sheetStats,
                context,
                plan.Request.WorkbookMetadata,
                plan.Request.CustomFields ?? [],
                cancellationToken,
                progress);
        }

        var highRiskSummaryPlan = GetSheetPlan(plan, WorkingPaperSheetCatalog.HighRiskSummary);
        var highRiskVoucherPlan = GetSheetPlan(plan, WorkingPaperSheetCatalog.HighRiskVoucher);
        var highRiskVoucherDetailPlan = GetSheetPlan(plan, WorkingPaperSheetCatalog.HighRiskVoucherDetail);
        var needsTagColumns = ShouldEmit(highRiskSummaryPlan) || ShouldEmit(highRiskVoucherPlan) || ShouldEmit(highRiskVoucherDetailPlan);
        if (needsTagColumns)
        {
            var tagColumns = TagColumnSet.FromPlan(plan, highRiskSummaryPlan, highRiskVoucherPlan, highRiskVoucherDetailPlan);

            if (ShouldEmit(highRiskSummaryPlan))
            {
                EmitHighRiskSummary(
                    session, sheetStats, context, tagColumns, highRiskSummaryPlan, cancellationToken, progress);
            }
            if (ShouldEmit(highRiskVoucherPlan))
            {
                await EmitHighRiskVoucherAsync(
                    session, sheetStats, context, tagColumns, highRiskVoucherPlan, cancellationToken, progress);
            }
            if (ShouldEmit(highRiskVoucherDetailPlan))
            {
                await EmitHighRiskVoucherDetailAsync(
                    session,
                    sheetStats,
                    context,
                    plan.HighRiskVoucherDetailColumns,
                    plan.Request.WorkbookMetadata,
                    plan.Request.CustomFields ?? [],
                    tagColumns,
                    cancellationToken,
                    progress);
            }
        }

        var postClosingEntriesPlan = GetSheetPlan(plan, WorkingPaperSheetCatalog.PostClosingEntries);
        if (ShouldEmit(postClosingEntriesPlan))
        {
            EmitPostClosingEntriesSheet(session, sheetStats, postClosingEntriesPlan, cancellationToken, progress);
        }

        // 三張「自動化工具」參考資料表（照範本的工作表順序，接在 step5 後）。三張表的結構各不相同，各自寫出，不合成一個共用的寫法。
        if (ShouldEmit(GetSheetPlan(plan, WorkingPaperSheetCatalog.FieldInfo)))
        {
            await EmitFieldInfoSheetAsync(
                session,
                sheetStats,
                context,
                plan.FieldInfo ?? throw new InvalidOperationException("WorkingPaperPlan 缺少欄位資訊投影。"),
                cancellationToken,
                progress);
        }
        if (ShouldEmit(GetSheetPlan(plan, WorkingPaperSheetCatalog.CalendarInfo)))
        {
            await EmitCalendarInfoSheetAsync(session, sheetStats, context, cancellationToken, progress);
        }
        if (ShouldEmit(GetSheetPlan(plan, WorkingPaperSheetCatalog.AccountMapping)))
        {
            await EmitAccountMappingSheetAsync(session, sheetStats, context, cancellationToken, progress);
        }

        return sheetStats;
    }

    private static WorkingPaperSheetPlan GetSheetPlan(WorkingPaperPlan plan, string sheetName) =>
        plan.Sheets.SingleOrDefault(sheet =>
            string.Equals(sheet.SheetName, sheetName, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"WorkingPaperPlan 缺少工作表 '{sheetName}'。");

    private static bool ShouldEmit(WorkingPaperSheetPlan? sheetPlan) =>
        sheetPlan?.Emit ?? true;

    private static string PlannedText(
        WorkingPaperSheetPlan? sheetPlan,
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
                $"WorkingPaperPlan 工作表 '{sheetPlan.SheetName}' 缺少 methodology[{index}]。");
        }

        return sheetPlan.Methodology[index];
    }

    private static IReadOnlyList<(uint Row, uint Column, string Text)> PlannedLines(
        WorkingPaperSheetPlan? sheetPlan,
        IReadOnlyList<(uint Row, uint Column, string Text)> fallback)
    {
        if (sheetPlan is null)
        {
            return fallback;
        }

        if (sheetPlan.Methodology.Count < fallback.Count)
        {
            throw new InvalidOperationException(
                $"WorkingPaperPlan 工作表 '{sheetPlan.SheetName}' 的 methodology 數量不足。");
        }

        return fallback
            .Select((line, index) => (line.Row, line.Column, sheetPlan.Methodology[index]))
            .ToArray();
    }

    private static void AddStat(
        List<SheetStat> stats,
        SheetStat stat,
        Action<WorkingPaperProgress>? progress)
    {
        stats.Add(stat);
        progress?.Invoke(new WorkingPaperProgress(stat.SheetName, stats.Count, stat.RowsWritten));
    }

    private static void ReportSheetStarted(
        IReadOnlyCollection<SheetStat> stats,
        Action<WorkingPaperProgress>? progress,
        string sheetName) =>
        progress?.Invoke(new WorkingPaperProgress(sheetName, stats.Count, 0));

    private void ReportIntermediateRows(
        List<SheetStat> stats,
        Action<WorkingPaperProgress>? progress,
        string sheetName,
        long rowsWritten)
    {
        if (progress is not null && rowsWritten > 0 && rowsWritten % _progressRowInterval == 0)
        {
            progress(new WorkingPaperProgress(sheetName, stats.Count, rowsWritten));
        }
    }

    /// <summary>多欄欄標列：欄從 B 起，和 step1 系列的表頭一樣從 B 欄開始；欄標含換行，所以用 BoldWrap。</summary>
    private static IReadOnlyList<Cell> HeaderCells(SheetWriter sheet, uint row, IReadOnlyList<string> labels)
    {
        var cells = new List<Cell>(labels.Count);
        for (var i = 0; i < labels.Count; i++)
        {
            cells.Add(sheet.TextCell(row, (uint)(i + 2), labels[i], WorkingPaperStyles.BoldWrap));
        }

        return cells;
    }

    /// <summary>結論列:A 欄「結論：」標籤 + B 欄結論文字,一次寫出(同列須單次 WriteRow,避免重複 RowIndex)。</summary>
    private static void WriteConclusionRow(SheetWriter sheet, uint row, string text) =>
        sheet.WriteFixedRow(row,
        [
            sheet.TextCell(row, 1, "結論：", WorkingPaperStyles.Bold),
            sheet.TextCell(row, 2, text, WorkingPaperStyles.Bold)
        ]);

    private static decimal Display(long scaled, int moneyScale) =>
        moneyScale <= 0 ? scaled : MoneyScaling.FromScaled(scaled, moneyScale);

    /// <summary>
    /// 高風險情境的欄清單。把「step4 用全部所選 position」與「step4-1 只用所選情境中 rowHitCount&gt;0 的 position」
    /// 先算成依序排好的 position 清單；寫出程式依清單順序加欄標，再逐列看那一列的 matchedPositions
    /// 有沒有某個 position，有就寫 Y。新增情境欄時不必改寫出程式。
    ///
    /// <see cref="Scenarios"/> 供 step3 列(name/rationale/voucherHitCount,position 升冪);
    /// <see cref="AllPositions"/> = 全部所選 position 升冪(step4 欄集);
    /// <see cref="RowHitPositions"/> = rowHitCount&gt;0 的 position 升冪(step4-1 欄集);
    /// <see cref="HitVoucherPositions"/> = 有命中、而且整張命中傳票都標 Y 的 position。
    /// 全部取自 <see cref="WorkingPaperPlan"/>;step3 另需 rationale(D 欄),wire 摘要不含此欄,故用內部 <see cref="ScenarioRow"/> 承載。
    /// </summary>
    private sealed class TagColumnSet
    {
        private const uint HighRiskVoucherFixedColumns = 5;   // A-E

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
            long RowHitCount,
            string? ConditionLogic = null)
        {
            public string Description => ConditionLogic is null ? Name : Name + "\n篩選條件：" + ConditionLogic;
        }

        /// <summary>step3 列來源:position 升冪的情境摘要(name/rationale/voucherHitCount)。</summary>
        public IReadOnlyList<ScenarioRow> Scenarios { get; }

        public IReadOnlyList<int> AllPositions => _allPositions;

        public IReadOnlyList<int> RowHitPositions => _rowHitPositions;

        public IReadOnlyList<int> HitVoucherPositions => _hitVoucherPositions;

        public bool IsFromPlan { get; }

        public static TagColumnSet FromPlan(
            WorkingPaperPlan plan,
            WorkingPaperSheetPlan highRiskSummary,
            WorkingPaperSheetPlan highRiskVoucher,
            WorkingPaperSheetPlan highRiskVoucherDetail)
        {
            RequireSamePositions(highRiskSummary, plan.AllScenarioPositions);
            RequireSamePositions(highRiskVoucher, plan.AllScenarioPositions);
            RequireSamePositions(highRiskVoucherDetail, plan.RowHitScenarioPositions);

            var byPosition = plan.Scenarios.ToDictionary(scenario => scenario.Position);
            var scenarios = plan.AllScenarioPositions
                .Select(position => byPosition.TryGetValue(position, out var scenario)
                    ? new ScenarioRow(
                        scenario.Position,
                        scenario.Name,
                        scenario.Rationale,
                        scenario.VoucherHitCount,
                        scenario.RowHitCount,
                        scenario.ConditionLogic)
                    : throw new InvalidOperationException(
                        $"WorkingPaperPlan 缺少情境 position {position}。"))
                .ToArray();
            var hitVoucherPositions = plan.Scenarios
                .Where(scenario => scenario.TagsWholeHitVouchers)
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
            WorkingPaperSheetPlan sheetPlan,
            IReadOnlyList<int> expected)
        {
            if (!sheetPlan.ScenarioPositions.SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"WorkingPaperPlan 工作表 '{sheetPlan.SheetName}' 的情境位置集合不一致。");
            }
        }

        /// <summary>step4 欄標:全 position 升冪,標頭 C{position}{suffix}(suffix 空)。接在固定欄之後。</summary>
        public void AppendAllPositionHeaders(SheetWriter sheet, List<Cell> cells, uint row, string suffix) =>
            AppendHeaders(sheet, cells, row, _allPositions, HighRiskVoucherFixedColumns, suffix);

        /// <summary>step4 列:逐 position 依 matched 是否含之標 Y/空。</summary>
        public void AppendAllPositionMarks(SheetWriter sheet, List<Cell> cells, uint row, IReadOnlyList<int> matched) =>
            AppendMarks(sheet, cells, row, _allPositions, HighRiskVoucherFixedColumns, matched);

        private static void AppendHeaders(
            SheetWriter sheet, List<Cell> cells, uint row, IReadOnlyList<int> positions, uint fixedColumns, string suffix)
        {
            for (var i = 0; i < positions.Count; i++)
            {
                cells.Add(sheet.TextCell(
                    row, fixedColumns + (uint)positions[i], $"C{positions[i]}{suffix}", WorkingPaperStyles.Bold));
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

    // ================= SAX 寫出工具 =================

    private static string DigitsOnly(string value)
    {
        return string.Concat(value.Where(char.IsDigit));
    }

    /// <summary>
    /// 國定假日與補班日的日期顯示：資料庫存 yyyy-MM-dd，底稿寫成 yyyy/MM/dd。
    /// 匯入時已經把日期整理成 yyyy-MM-dd；讀到其他寫法時照原樣寫出，不猜也不改。
    /// </summary>
    private static string DisplayDate(string isoDate)
    {
        return DateTime.TryParseExact(
                isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)
            : isoDate;
    }

    /// <summary>
    /// 單張工作表的串流寫出程式，用 SAX 寫出，包住 OpenXmlPartWriter 並處理元素順序：
    /// 建立時開 worksheet 與 sheetData，只往前寫；WriteRow 與 WriteFixedRow 立刻寫出一列；
    /// AddMerge 累積合併範圍，數量有上限，屬於版面；<see cref="CloseAndSummarize"/> 先關 sheetData，
    /// 再寫 mergeCells（schema 要求在 sheetData 之後），最後關 worksheet。沒有呼叫它就釋放時，只放掉資源、不補結尾。
    /// 資料列計入 RowsWritten，固定文字與表頭列不計，和 SheetStat 的算法相同。
    /// 資料表的寫出程式一邊讀一邊逐列呼叫 WriteRow，不整批載入。
    /// </summary>
    // 測試直接建立它，核對寫入失敗時的附註；正式程式只在這個類別裡使用。
    internal sealed class SheetWriter : IDisposable
    {
        private readonly string _name;
        private readonly WorksheetPart? _part;
        private readonly Stream? _content;
        private readonly OpenXmlWriter? _writer;
        private readonly DirectTemplateWorksheetOverlayWriter? _overlay;
        private readonly WorkbookStyleMap _styles;
        private readonly Func<uint, uint, uint, uint>? _mapCellStyle;
        private readonly CancellationToken _cancellationToken;
        private readonly bool _preserveTemplate;
        private readonly string? _pageSetupRelationshipId;
        private readonly List<string> _merges = [];
        private readonly DataValidations? _templateValidations;
        private readonly WorkingPaperLockedArea? _lockedArea;
        private readonly (IReadOnlyList<(uint Min, uint Max, uint Style)> Styles, double Width)? _unlockedColumns;
        private readonly (uint First, uint Last)? _lockedHeaderRows;
        private uint _nextLockedHeaderRow;
        private (uint TemplateLastRow, uint LastRow)? _validationExtension;
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
        private bool _faulted;
        private bool _disposed;

        private SheetWriter(
            string name,
            WorksheetPart? part,
            DirectTemplateWorksheetOverlayWriter? overlay,
            WorkbookStyleMap styles,
            Func<uint, uint, uint, uint>? mapCellStyle,
            CancellationToken cancellationToken,
            bool preserveTemplate,
            string? pageSetupRelationshipId = null,
            DataValidations? templateValidations = null,
            WorkingPaperLockedArea? lockedArea = null,
            (IReadOnlyList<(uint Min, uint Max, uint Style)> Styles, double Width)? unlockedColumns = null)
        {
            _name = name;
            _templateValidations = templateValidations;
            _lockedArea = lockedArea;
            _unlockedColumns = unlockedColumns;
            // 續頁沒有範本格。第一頁表頭範圍裡的範本格是鎖定的，續頁在同一個範圍補上鎖定的空白格。
            if (part is not null && overlay is null
                && lockedArea is { } area && area.FirstRow <= area.LastTemplateRow)
            {
                _lockedHeaderRows = (area.FirstRow, area.LastTemplateRow);
                _nextLockedHeaderRow = area.FirstRow;
            }
            _part = part;
            _overlay = overlay;
            _styles = styles;
            _mapCellStyle = mapCellStyle;
            _cancellationToken = cancellationToken;
            _preserveTemplate = preserveTemplate;
            _pageSetupRelationshipId = pageSetupRelationshipId;
            if (part is not null && overlay is null)
            {
                _content = FormalWorkbookArchive.OpenWorksheetContent(part);
                try
                {
                    _writer = OpenXmlWriter.Create(_content);
                    _writer.WriteStartDocument();
                    _writer.WriteStartElement(new Worksheet());
                }
                catch
                {
                    // 建構失敗時呼叫端拿不到這個寫入器，已經開啟的項目要在這裡放掉。
                    ExportResources.ReleaseAll(_writer, _content);
                    throw;
                }
            }
        }

        internal static SheetWriter OverlayTemplate(
            string name,
            WorksheetPart part,
            DirectTemplateWorksheetOverlayPlan plan,
            WorkbookStyleMap styles,
            Func<uint, uint, uint, uint>? mapCellStyle,
            CancellationToken cancellationToken,
            DataValidations? templateValidations = null,
            WorkingPaperLockedArea? lockedArea = null) =>
            new(
                name,
                part,
                new DirectTemplateWorksheetOverlayWriter(
                    name,
                    part,
                    plan,
                    cancellationToken),
                styles,
                mapCellStyle,
                cancellationToken,
                preserveTemplate: false,
                templateValidations: templateValidations,
                lockedArea: lockedArea);

        /// <param name="unlockedColumns">
        /// 有鎖定區的續頁用：各欄要套上的不鎖定樣式，由左到右分段蓋滿每一欄；以及沒有設定寬度的欄要用的範本預設欄寬。
        /// </param>
        internal static SheetWriter CreateContinuation(
            string name,
            WorksheetPart part,
            WorkbookStyleMap styles,
            Func<uint, uint, uint, uint>? mapCellStyle,
            CancellationToken cancellationToken,
            string? pageSetupRelationshipId,
            DataValidations? templateValidations = null,
            WorkingPaperLockedArea? lockedArea = null,
            (IReadOnlyList<(uint Min, uint Max, uint Style)> Styles, double Width)? unlockedColumns = null) =>
            new(
                name,
                part,
                overlay: null,
                styles,
                mapCellStyle,
                cancellationToken,
                preserveTemplate: false,
                pageSetupRelationshipId,
                templateValidations,
                lockedArea,
                unlockedColumns);

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
            // 寫出欄設定時照這裡的順序一段一段寫，同一欄不能出現在兩段裡。
            if (_columns.Count > 0 && min <= _columns[^1].Max)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(min),
                    "Worksheet column widths must be set left to right without overlap.");
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

        /// <summary>寫一列資料列（計入 RowsWritten），給資料表一邊讀一邊逐列呼叫。</summary>
        public void WriteRow(uint rowIndex, IReadOnlyList<Cell> cells)
        {
            WriteRowCore(rowIndex, FillLockedArea(rowIndex, cells));
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

        /// <summary>
        /// 範本的下拉選單照原本的選項寫出；結束列等於範本最後一列的範圍延伸到這張表的最後一列。
        /// 資料沒有超過範本列數時照範本原樣。
        /// </summary>
        public void ExtendTemplateValidations(uint templateLastRow, uint lastRow)
        {
            if (_templateValidations is null)
            {
                throw new InvalidOperationException($"Worksheet '{_name}' 沒有範本下拉選單可以延伸。");
            }
            _validationExtension = (templateLastRow, lastRow);
        }

        public void Protect() => _protect = true;

        /// <summary>
        /// inline-string 文字 cell(空字串會被視為空 cell 不輸出值,但保留樣式)。超過 Excel 每格上限的文字寫到上限並註明已截斷，
        /// 記在這張工作表的截斷格數。照範本原樣保留的工作表不寫資料列，沒有工作表可記，文字照原樣寫出。
        /// </summary>
        public Cell TextCell(uint rowIndex, uint column, string? value, uint style = WorkingPaperStyles.Default)
        {
            var cell = new Cell
            {
                CellReference = Reference(column, rowIndex),
                StyleIndex = MapCellStyle(rowIndex, column, style)
            };
            if (!string.IsNullOrEmpty(value))
            {
                // 工作底稿的文字寫成 inline string。
                if (_part is null)
                {
                    ExcelText.SetVerbatimInlineText(cell, value);
                }
                else
                {
                    ExcelText.SetInlineText(cell, value, _part);
                }
            }

            return cell;
        }

        /// <summary>照原樣寫出、不截斷的文字 cell，給 JET 讀回用的配對資料與篩選條件說明切好的分段。</summary>
        public Cell VerbatimTextCell(uint rowIndex, uint column, string? value, uint style = WorkingPaperStyles.Default)
        {
            var cell = new Cell
            {
                CellReference = Reference(column, rowIndex),
                StyleIndex = MapCellStyle(rowIndex, column, style)
            };
            if (!string.IsNullOrEmpty(value))
            {
                ExcelText.SetVerbatimInlineText(cell, value);
            }

            return cell;
        }

        /// <summary>數值 cell(以整數/小數字面寫入;顯示格式交給 StyleIndex 的 numFmt)。供資料表金額欄用。</summary>
        public Cell NumberCell(uint rowIndex, uint column, decimal value, uint style = WorkingPaperStyles.Default)
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
        /// 寫入已經驗證過、用不變文化寫成的有限數值文字。只給超出 Decimal 範圍、但仍要寫成 Excel 數值儲存格的值使用；
        /// 一般金額用 decimal 的多載。
        /// </summary>
        public Cell NumberLiteralCell(
            uint rowIndex,
            uint column,
            string value,
            uint style = WorkingPaperStyles.Default)
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
        public Cell BlankCell(uint rowIndex, uint column, uint style = WorkingPaperStyles.Default)
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
            Close();
            return stat;
        }

        /// <summary>同 <see cref="CloseAndSummarize"/>,但以外部累計的資料列數覆寫(資料列由骨架累計而非逐次 WriteRow)。</summary>
        public SheetStat CloseAndSummarizeWith(long dataRows, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stat = new SheetStat(_name, dataRows);
            Close();
            return stat;
        }

        /// <summary>
        /// 寫完結尾並關閉這張工作表。寫結尾、把緩衝寫出去或關閉壓縮檔項目失敗時往外丟，因為這時失敗代表工作表不完整。
        /// 寫列時丟出例外後 XML 停在半途，再寫結束標記只會丟出新的例外，所以那之後不補結尾。
        /// </summary>
        private void Close()
        {
            try
            {
                if (!_disposed && !_faulted)
                {
                    CloseElements();
                    _writer?.Dispose();
                    _content?.Dispose();
                }
            }
            finally
            {
                Dispose();
            }
        }

        /// <summary>
        /// 只放掉資源，不補寫結尾，也不丟出例外。沒有先關閉就釋放，表示匯出已經失敗或取消，這張工作表的內容不要了；
        /// 補寫結尾還要把範本剩下的列讀完，取消時會再丟一次取消，後面的資源就放不掉。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ExportResources.ReleaseAll(_writer, _content, _overlay);
        }

        /// <summary>
        /// 舊版鎖定整個資料區，連沒有值的格子也鎖。資料列在鎖定欄裡缺的格子補成空白格，
        /// 樣式和其他寫入的格子一樣經過鎖定判斷；例如 step4 只用到幾個條件時，其餘條件欄也照舊版鎖住。
        /// 續頁的表頭範圍沒有範本格，也用這個方法補成鎖定的空白格。
        /// </summary>
        private IReadOnlyList<Cell> FillLockedArea(uint rowIndex, IReadOnlyList<Cell> cells)
        {
            if (_lockedArea is not { } area || rowIndex < area.FirstRow)
            {
                return cells;
            }
            var byColumn = new SortedDictionary<uint, Cell>();
            foreach (var cell in cells)
            {
                byColumn.Add(ColumnOf(cell), cell);
            }
            for (var column = area.FirstColumn; column <= area.LastColumn; column++)
            {
                if (!byColumn.ContainsKey(column))
                {
                    byColumn.Add(column, BlankCell(rowIndex, column));
                }
            }
            return byColumn.Count == cells.Count ? cells : [.. byColumn.Values];
        }

        private static uint ColumnOf(Cell cell)
        {
            var reference = cell.CellReference?.Value
                ?? throw new InvalidOperationException("資料列的儲存格缺少位置。");
            var column = 0U;
            foreach (var character in reference)
            {
                if (character is < 'A' or > 'Z')
                {
                    break;
                }
                column = checked(column * 26U + (uint)(character - 'A' + 1));
            }
            return column;
        }

        private void WriteRowCore(uint rowIndex, IReadOnlyList<Cell> cells)
        {
            if (_lockedHeaderRows is { } header)
            {
                // 表頭範圍裡沒寫到的列，在寫後面的列之前先補一列鎖定的空白格；同一列只補一次。
                while (_nextLockedHeaderRow <= header.Last && _nextLockedHeaderRow < rowIndex)
                {
                    var blankRow = _nextLockedHeaderRow++;
                    WriteRowCore(blankRow, FillLockedArea(blankRow, []));
                }
                if (rowIndex == _nextLockedHeaderRow && rowIndex <= header.Last)
                {
                    cells = FillLockedArea(rowIndex, cells);
                    _nextLockedHeaderRow++;
                }
            }

            ExcelWorksheetConstraints.EnsureCell(rowIndex, 1);

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

            try
            {
                // 開始寫範本時被取消也算寫到一半：之後不再補結束標記，取消例外才不會被結束標記的錯誤蓋掉。
                EnsureSheetDataStarted();
                if (_preserveTemplate)
                {
                    return;
                }

                if (_overlay is not null)
                {
                    _overlay.WriteRow(row);
                    return;
                }

                // 整列一次寫出：記憶體只放目前這一列；用型別寫出整列，比手動拆成開始標記、文字與結束標記安全。
                _writer!.WriteElement(row);
            }
            catch (Exception error) when (Fault(error, cells))
            {
                throw;
            }
        }

        /// <summary>寫到一半的輸出之後不再補結束標記。取消不是寫入失敗，不記工作表位置。</summary>
        private bool Fault(Exception error, IEnumerable<Cell> cells)
        {
            _faulted = true;
            if (error is not OperationCanceledException)
            {
                ExcelText.MarkWriteFailure(error, _part, cells);
            }

            return false;
        }

        private void CloseElements()
        {
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

            if (_templateValidations is not null)
            {
                var validations = (DataValidations)_templateValidations.CloneNode(true);
                if (_validationExtension is { } extension && extension.LastRow > extension.TemplateLastRow)
                {
                    foreach (var validation in validations.Elements<DataValidation>())
                    {
                        if (validation.SequenceOfReferences is { InnerText: { } sqref } references)
                        {
                            references.InnerText = ReportSheetWriter.ExtendSqref(
                                sqref,
                                extension.TemplateLastRow,
                                extension.LastRow);
                        }
                    }
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

        /// <summary>
        /// 寫出欄寬設定。有鎖定區的續頁沒有範本的欄樣式，這時每一欄都套上第一頁同一欄的不鎖定樣式，
        /// 沒有設定寬度的欄用範本的預設欄寬，沒寫值的格子才會和第一頁一樣可以填、對齊也相同。
        /// 欄寬設定由左到右、不重疊，<see cref="SetColumnWidth"/> 收下時已經檢查。
        /// </summary>
        private Columns? BuildColumns()
        {
            if (_columns.Count == 0 && _unlockedColumns is null)
            {
                return null;
            }

            var columns = new Columns();
            if (_unlockedColumns is not { } unlocked)
            {
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

            // 樣式分段蓋滿每一欄。逐段配上那幾欄的寬度設定，寬度或樣式不同的地方分開寫。
            var widthIndex = 0;
            foreach (var segment in unlocked.Styles)
            {
                var next = segment.Min;
                while (next <= segment.Max)
                {
                    while (widthIndex < _columns.Count && _columns[widthIndex].Max < next)
                    {
                        widthIndex++;
                    }
                    Column element;
                    if (widthIndex < _columns.Count && _columns[widthIndex].Min <= next)
                    {
                        var width = _columns[widthIndex];
                        element = new Column
                        {
                            Min = next,
                            Max = Math.Min(segment.Max, width.Max),
                            Width = width.Width,
                            CustomWidth = true,
                            Hidden = width.Hidden,
                            BestFit = width.BestFit
                        };
                    }
                    else
                    {
                        element = new Column
                        {
                            Min = next,
                            Max = widthIndex < _columns.Count
                                ? Math.Min(segment.Max, _columns[widthIndex].Min - 1)
                                : segment.Max,
                            Width = unlocked.Width
                        };
                    }
                    element.Style = segment.Style;
                    columns.Append(element);
                    next = element.Max!.Value + 1;
                }
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
