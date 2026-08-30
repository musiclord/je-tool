using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class WorkpaperWriter
{
    private async Task EmitStep1Async(
        WorkpaperWriteSession session, List<SheetStat> stats,
        WorkpaperContext context, WorkpaperSheetPlan? sheetPlan,
        bool? hasCompletenessDifferences,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var applyDifferenceAppearance = hasCompletenessDifferences
            ?? (await completenessDiffs.GetPageAsync(
                context.ProjectId,
                context.MoneyScale,
                context.PeriodStart,
                context.PeriodEnd,
                new PageRequest(Cursor: null, PageSize: 1),
                cancellationToken)).Rows.Count > 0;
        using var spool = WorkpaperDisplaySpool.Create(
            [
                "試算表科目\n編號",
                "試算表科目\n名稱",
                "試算表科目\n變動金額\n(A)",
                "該科目於會計分錄之\n本期借貸金額彙總\n(C)",
                "差異數\n(B)-(A)"
            ],
            [
                18D,
                28D,
                MaximumAmountDisplayColumnWidth,
                MaximumAmountDisplayColumnWidth,
                MaximumAmountDisplayColumnWidth
            ]);
        await FillDisplaySpoolAsync(
            spool,
            ProjectRowsAsync(
                StreamItemsAsync(
                    (cursor, ct) => completenessAccounts.GetPageAsync(
                        context.ProjectId,
                        context.MoneyScale,
                        context.PeriodStart,
                        context.PeriodEnd,
                        new PageRequest(cursor, PageRequest.DefaultPageSize),
                        ct),
                    cancellationToken),
                account => ProjectCompletenessRow(account, context.MoneyScale),
                cancellationToken),
            cancellationToken);
        var widths = spool.Widths;

        await EmitContinuedRowsAsync(
            stats,
            WorkpaperSheetCatalog.Step1,
            firstDataRow: 20,
            reservedRowsAfterData: 0,
            ReplayDisplaySpoolAsync(spool, cancellationToken),
            (name, _) =>
            {
                var sheet = session.OpenSheet(
                    name,
                    Step1Appearance(applyDifferenceAppearance));
                ConfigureStep1Layout(
                    sheet,
                    dataWidths: widths);
                WriteCommonHeader(
                    sheet,
                    context,
                    "此處底稿係記錄JE測試母體的完整性",
                    sheetPlan);
                sheet.WriteFixedRow(6, [sheet.TextCell(6, 1, "Step 1", WorkpaperStyles.Bold)]);
                sheet.WriteFixedRow(7,
                [
                    sheet.TextCell(
                        7,
                        1,
                        sheetPlan?.AuditCondition ?? "評估母體完整性：",
                        WorkpaperStyles.Bold)
                ]);
                WriteConclusionRow(sheet, 15, sheetPlan?.Conclusion ?? Step1Conclusion);
                sheet.WriteFixedRow(17,
                [
                    sheet.TextCell(
                        17,
                        2,
                        PlannedText(sheetPlan, 0, Step1ListNote),
                        WorkpaperStyles.Bold)
                ]);
                sheet.WriteFixedRow(19, HeaderCells(sheet, 19,
                [
                    "試算表科目\n編號", "試算表科目\n名稱", "試算表科目\n變動金額\n(A)",
                    "該科目於會計分錄之\n本期借貸金額彙總\n(C)", "差異數\n(B)-(A)"
                ]));
                return sheet;
            },
            (sheet, row, projected) => ProjectedCells(sheet, row, projected, firstColumn: 2),
            (sheet, rows, _, ct) =>
            {
                sheet.Protect();
                return sheet.CloseAndSummarizeWith(rows, ct);
            },
            omitWhenEmpty: false,
            progress: progress,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 「step1-1 借貸不平測試」:A1-A4 共同表頭 + Step1-1 程序固定文字 + 結論;
    /// **docBalancePage 有列才 emit 例外表**(條件 guard:逐頁串流,有列才寫欄標 + 列,無列只留結論文字)。
    /// </summary>
    private async Task EmitStep11Async(
        WorkpaperWriteSession session, List<SheetStat> stats,
        WorkpaperContext context, WorkpaperSheetPlan? sheetPlan, CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var documentRows = sheetPlan is { IncludeExceptionTable: false }
            ? ToItems(Array.Empty<UnbalancedDocument>(), cancellationToken)
            : StreamItemsAsync(
                (cursor, ct) => docBalances.GetPageAsync(
                    context.ProjectId, context.MoneyScale, context.PeriodStart, context.PeriodEnd,
                    new PageRequest(cursor, PageRequest.DefaultPageSize), ct),
                cancellationToken);
        using var spool = WorkpaperDisplaySpool.Create(
            ["傳票號碼", "借方金額", "貸方金額", "借貸差額"],
            [
                18D,
                MaximumAmountDisplayColumnWidth,
                MaximumAmountDisplayColumnWidth,
                MaximumAmountDisplayColumnWidth
            ]);
        await FillDisplaySpoolAsync(
            spool,
            ProjectRowsAsync(
                documentRows,
                document => ProjectUnbalancedRow(document, context.MoneyScale),
                cancellationToken),
            cancellationToken);
        var widths = spool.Widths;

        await EmitContinuedRowsAsync(
            stats,
            WorkpaperSheetCatalog.Step11,
            firstDataRow: 15,
            reservedRowsAfterData: 0,
            ReplayDisplaySpoolAsync(spool, cancellationToken),
            (name, includeTable) =>
            {
                includeTable = sheetPlan?.IncludeExceptionTable ?? includeTable;
                var sheet = session.OpenSheet(
                    name,
                    Step11Appearance(includeTable));
                ConfigureStep1Layout(
                    sheet,
                    dataWidths: widths);
                WriteCommonHeader(
                    sheet,
                    context,
                    "此處底稿係記錄JE測試母體的是否有借貸不平情形",
                    sheetPlan);
                sheet.WriteFixedRow(6, [sheet.TextCell(6, 1, "Step 1-1", WorkpaperStyles.Bold)]);
                sheet.WriteFixedRow(7,
                [
                    sheet.TextCell(
                        7,
                        1,
                        sheetPlan?.AuditCondition ?? "評估個別傳票是否借貸不平：",
                        WorkpaperStyles.Bold)
                ]);
                WriteConclusionRow(sheet, 12, sheetPlan?.Conclusion ?? Step11Conclusion);
                if (includeTable)
                {
                    sheet.WriteFixedRow(14, HeaderCells(sheet, 14,
                        ["傳票號碼", "借方金額", "貸方金額", "借貸差額"]));
                }
                return sheet;
            },
            (sheet, row, projected) => ProjectedCells(sheet, row, projected, firstColumn: 2),
            (sheet, rows, _, ct) =>
            {
                sheet.Protect();
                return sheet.CloseAndSummarizeWith(rows, ct);
            },
            omitWhenEmpty: false,
            progress: progress,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 「step1-2 分錄編製人員說明」:A1-A4 共同表頭 + Step1-2 程序固定文字 + 第 11 列欄標 +
    /// FetchAllAsync 全名單(B 編製人員/D 傳票數/E 金額彙總自動;C 自動或人工、F 部門、G 職稱、H 說明留空)。
    /// repository 契約未設筆數上限，因此寫出時仍依 Excel 列容量自動續頁，不假設名單必然少於單頁。
    /// </summary>
    private async Task EmitStep12Async(
        WorkpaperWriteSession session, List<SheetStat> stats,
        WorkpaperContext context, WorkpaperSheetPlan? sheetPlan, CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var glMapping = await mappingStates.FindAsync(
            context.ProjectId,
            DatasetKind.Gl,
            cancellationToken);
        var missingCreateByMapping = glMapping is null
            || !glMapping.Mapping.TryGetValue(GlMappingKeys.CreateBy, out var createBySource)
            || string.IsNullOrWhiteSpace(createBySource);
        var creators = await creatorSummaries.FetchAllAsync(
            context.ProjectId, context.PeriodStart, context.PeriodEnd, cancellationToken);
        using var spool = WorkpaperDisplaySpool.Create(
            [
                "編製人員\n(來自JE測試母體)",
                null,
                "經手之傳票數目\n(來自JE測試母體)",
                "經手之傳票金額彙總\n(來自JE測試母體)",
                null,
                null,
                null
            ],
            [
                20D,
                18D,
                MaximumCountDisplayColumnWidth,
                MaximumAmountDisplayColumnWidth,
                16D,
                16D,
                38D
            ]);
        await FillDisplaySpoolAsync(
            spool,
            ProjectRowsAsync(
                ToItems(creators, cancellationToken),
                creator => ProjectCreatorRow(creator, context.MoneyScale),
                cancellationToken),
            cancellationToken);
        var widths = spool.Widths;

        await EmitContinuedRowsAsync(
            stats,
            WorkpaperSheetCatalog.Step12,
            firstDataRow: 12,
            reservedRowsAfterData: 0,
            ReplayDisplaySpoolAsync(spool, cancellationToken),
            (name, _) =>
            {
                var sheet = session.OpenSheet(
                    name,
                    Step12Appearance(missingCreateByMapping));
                ConfigureStep12Layout(sheet, widths);
                WriteCommonHeader(
                    sheet,
                    context,
                    "此處底稿係記錄JE測試母體的是否有借貸不平情形",
                    sheetPlan);
                sheet.WriteFixedRow(6, [sheet.TextCell(6, 1, "Step 1-2", WorkpaperStyles.Bold)]);
                sheet.WriteFixedRow(7,
                [
                    sheet.TextCell(
                        7,
                        1,
                        sheetPlan?.AuditCondition ?? "評估是否有不適合的分錄編製人員：",
                        WorkpaperStyles.Bold)
                ]);
                sheet.WriteFixedRow(11, HeaderCells(sheet, 11,
                [
                    "編製人員\n(來自JE測試母體)", "屬於自動或人工\n(若原資料無此判別欄位則留白)",
                    "經手之傳票數目\n(來自JE測試母體)", "經手之傳票金額彙總\n(來自JE測試母體)",
                    "部門", "職稱或職務", "是否為適當編製人員之說明"
                ]));
                return sheet;
            },
            (sheet, row, projected) => ProjectedCells(sheet, row, projected, firstColumn: 2),
            (sheet, rows, _, ct) =>
            {
                sheet.Protect();
                return sheet.CloseAndSummarizeWith(rows, ct);
            },
            omitWhenEmpty: false,
            progress: progress,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 「step1-3 完整性測試之差異說明」(**條件表:有 diff≠0 科目才 emit**):A1-A4 共同表頭 +
    /// Step1-3 程序固定文字 + 結論 + 第 16 列欄標 + completenessDiffs **WHERE diff≠0**
    /// (B 科目編號/C 名稱/D 差異金額自動;E 原因、F 調節、G 調節後差異留空)。
    /// guard 由共用續頁原語先探測第一列;空集合時不建立、不註冊工作表。
    /// </summary>
    private async Task EmitStep13Async(
        WorkpaperWriteSession session, List<SheetStat> stats,
        WorkpaperContext context, WorkpaperSheetPlan? sheetPlan, CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        using var spool = WorkpaperDisplaySpool.Create(
            [
                "試算表科目編號",
                "試算表科目名稱",
                "於Step1之差異金額",
                null,
                null,
                null
            ],
            [
                18D,
                28D,
                MaximumAmountDisplayColumnWidth,
                34D,
                34D,
                34D
            ]);
        await FillDisplaySpoolAsync(
            spool,
            ProjectRowsAsync(
                StreamItemsAsync(
                    (cursor, ct) => completenessDiffs.GetPageAsync(
                        context.ProjectId,
                        context.MoneyScale,
                        context.PeriodStart,
                        context.PeriodEnd,
                        new PageRequest(cursor, PageRequest.DefaultPageSize),
                        ct),
                    cancellationToken),
                account => ProjectDiffExplanationRow(account, context.MoneyScale),
                cancellationToken),
            cancellationToken);
        var widths = spool.Widths;

        await EmitContinuedRowsAsync(
            stats,
            WorkpaperSheetCatalog.Step13,
            firstDataRow: 17,
            reservedRowsAfterData: 0,
            ReplayDisplaySpoolAsync(spool, cancellationToken),
            (name, _) =>
            {
                var sheet = session.OpenSheet(name);
                ConfigureStep13Layout(sheet, widths);
                WriteCommonHeader(
                    sheet,
                    context,
                    "此處底稿係記錄JE測試母體於完整性測試有部分科目出現差異的回應與理由",
                    sheetPlan);
                sheet.WriteFixedRow(6, [sheet.TextCell(6, 1, "Step 1-3", WorkpaperStyles.Bold)]);
                sheet.WriteFixedRow(7,
                [
                    sheet.TextCell(
                        7,
                        1,
                        sheetPlan?.AuditCondition ?? "評估於Step1完整性測試中有部分科目出現差異的原因：",
                        WorkpaperStyles.Bold)
                ]);
                WriteConclusionRow(sheet, 12, sheetPlan?.Conclusion ?? Step13Conclusion);
                sheet.WriteFixedRow(14, [sheet.TextCell(14, 2, "出現差異之個別科目說明：", WorkpaperStyles.Bold)]);
                sheet.WriteFixedRow(16, HeaderCells(sheet, 16,
                [
                    "試算表科目編號", "試算表科目名稱", "於Step1之差異金額",
                    "差異原因之說明", "說明如何進行調節以降低該差異", "調節後之差異金額或剩餘差異之說明"
                ]));
                return sheet;
            },
            (sheet, row, projected) => ProjectedCells(sheet, row, projected, firstColumn: 2),
            (sheet, rows, _, ct) =>
            {
                sheet.Protect();
                return sheet.CloseAndSummarizeWith(rows, ct);
            },
            omitWhenEmpty: sheetPlan is null,
            progress: progress,
            cancellationToken: cancellationToken);
    }

    // ================= step2 / step3 / step4 / step4-1 emitter(高風險矩陣家族,Task 4)=================

    /// <summary>
    /// 「step2 可靠性測試」:表頭跨 49-52 列(含多處合併,逐字對齊本機參考樣本);資料第 53 列起,逐頁 infSamplePage。
    /// 借/貸兩欄直接用 InfSampleRow.DebitScaled / CreditScaled(brief 設計決策;scaled→顯示)。
    /// A 樣本序號為列序 1..N(emitter 自累計);J 來源欄(infSamplePage 無 source_module)、H 核准日、T 說明手填留空。
    /// </summary>

    private static void ConfigureStep1Layout(
        SheetWriter sheet,
        IReadOnlyList<double> dataWidths)
    {
        sheet.SetDefaultRowHeight(18);
        sheet.SetColumnWidth(1, 1, 12);
        ApplyDisplayWidths(sheet, firstColumn: 2, widths: dataWidths);
        sheet.SetRowHeight(19, 44);
    }

    private static void ConfigureStep12Layout(
        SheetWriter sheet,
        IReadOnlyList<double> dataWidths)
    {
        sheet.SetDefaultRowHeight(18);
        sheet.SetColumnWidth(1, 1, 8);
        ApplyDisplayWidths(sheet, firstColumn: 2, widths: dataWidths);
        sheet.SetRowHeight(11, 48);
    }

    private static void ConfigureStep13Layout(
        SheetWriter sheet,
        IReadOnlyList<double> dataWidths)
    {
        sheet.SetDefaultRowHeight(18);
        sheet.SetColumnWidth(1, 1, 8);
        ApplyDisplayWidths(sheet, firstColumn: 2, widths: dataWidths);
        sheet.SetRowHeight(16, 42);
    }

    private static void WriteCommonHeader(
        SheetWriter sheet,
        WorkpaperContext context,
        string italicNote,
        WorkpaperSheetPlan? sheetPlan)
    {
        sheet.WriteFixedRow(1, [sheet.TextCell(1, 1, $"公司名稱 : {context.CompanyName}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(2, [sheet.TextCell(2, 1,
            $"測試資料期間 :  {context.PeriodStart} ~ {context.PeriodEnd}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(3, [sheet.TextCell(3, 1,
            $"財務報表準備期間 - 開始日 : {sheetPlan?.NaText ?? DisplayLastPeriodStart(context.LastPeriodStart)}",
            WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(4, [sheet.TextCell(4, 1, italicNote, WorkpaperStyles.Bold)]);
    }

    private static string DisplayLastPeriodStart(string? lastPeriodStart) =>
        string.IsNullOrWhiteSpace(lastPeriodStart) ? "N/A" : DigitsOnly(lastPeriodStart);

    /// <summary>
    /// 資料表共用骨架:寫欄標列(可略)+ 逐列資料(列號自 firstDataRow 內部累計);回資料列數。
    /// 列來源是 <see cref="IAsyncEnumerable{T}"/>(每元素為「給列號→cells」的工廠),呼叫端決定
    /// keyset 串流或全載入——本原語只管「不全載入地逐列寫出 + 累計列號」,是 step1 / step1-2 / step1-3 共用骨架。
    /// </summary>

    private static IReadOnlyList<WorkpaperDisplayCell> ProjectCompletenessRow(
        CompletenessDiffAccount acc,
        int moneyScale) =>
    [
        ProjectText(acc.AccountCode),
        ProjectText(acc.AccountName),
        ProjectAmount(acc.TbAmountScaled, moneyScale),
        ProjectAmount(acc.GlAmountScaled, moneyScale),
        ProjectAmount(acc.DiffScaled, moneyScale)
    ];

    /// <summary>step1-1 不平傳票列:B 傳票號 / C 借方 / D 貸方 / E 借貸差額(皆 scaled→顯示)。</summary>
    private static IReadOnlyList<WorkpaperDisplayCell> ProjectUnbalancedRow(
        UnbalancedDocument doc,
        int moneyScale) =>
    [
        ProjectText(doc.DocumentNumber),
        ProjectAmount(doc.DebitScaled, moneyScale),
        ProjectAmount(doc.CreditScaled, moneyScale),
        ProjectAmount(doc.DiffScaled, moneyScale)
    ];

    /// <summary>step1-2 編製人員列:B 人員 / C 空(自動或人工) / D 傳票數 / E 借方彙總 / F-H 空(部門/職稱/說明)。</summary>
    private static IReadOnlyList<WorkpaperDisplayCell> ProjectCreatorRow(
        CreatorSummaryExportRow creator,
        int moneyScale) =>
    [
        ProjectText(creator.CreatedBy),
        ProjectBlank(WorkpaperStyles.EditableNoFill),
        ProjectInteger(creator.EntryCount, groupedForWidth: true),
        ProjectAmount(creator.DebitTotalScaled, moneyScale),
        ProjectBlank(WorkpaperStyles.EditableNoFill),
        ProjectBlank(WorkpaperStyles.EditableNoFill),
        ProjectBlank(WorkpaperStyles.EditableNoFill)
    ];

    /// <summary>step1-3 差異說明列:B 編號 / C 名稱 / D 差異金額 / E-G 空(原因/調節/調節後差異)。</summary>
    private static IReadOnlyList<WorkpaperDisplayCell> ProjectDiffExplanationRow(
        CompletenessDiffAccount acc,
        int moneyScale) =>
    [
        ProjectText(acc.AccountCode),
        ProjectText(acc.AccountName),
        ProjectAmount(acc.DiffScaled, moneyScale),
        ProjectBlank(WorkpaperStyles.Editable),
        ProjectBlank(WorkpaperStyles.Editable),
        ProjectBlank(WorkpaperStyles.Editable)
    ];

    /// <summary>多欄欄標列工廠(欄自 B 起,對齊 step1 家族表頭都從 B 欄開始;BoldWrap 因欄標含換行)。</summary>

    private const string Step1Conclusion =
        "基於上述程序，查核團隊對於JE測試母體之完整性，尚需於Step1-3說明以取得足夠的查核證據。";

    private const string Step1ListNote =
        "#針對試算表科目金額本期異動與會計分錄(JE)進行推滾比對之清單列示如下：" +
        "(有部分科目之差異數不為0，請於step1-3說明其理由，以確認JE母體的完整性)";

    private const string Step11Conclusion =
        "基於上述程序，查核團隊已取得足夠的查核證據，確認無借貸不平之情形。";

    private const string Step13Conclusion =
        "基於上述程序，查核團隊對出現差異之科目均已取得足夠的查核證據，已確認其原因尚屬合理或進行調節使其無差異，" +
        "因此可確認JE測試母體之完整性。";

    // ---- step2/3/4 表頭固定文字(逐字對齊本機參考樣本;列號/欄/合併皆照樣本)----

    /// <summary>step2 測試說明 + 測試程序(A6-C47 區塊;欄標另見 WriteStep2ColumnHeaders)。</summary>
}
