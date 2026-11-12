using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class WorkingPaperWriter
{
    // ================= step1 家族 emitter =================

    /// <summary>
    /// 「step1 完整性測試」:A1-A4 共同表頭 + Step1 程序固定文字 + 結論 + 第 19 列欄標 +
    /// completenessAccounts **全科目逐頁**(科目編號/名稱/TB 變動(A)/GL 彙總(C)/差異(B)-(A))。
    /// 差異欄用 repo 的 DiffScaled(= tb_s - gl_s),step1／step1-3 同一定義不漂移。
    /// </summary>
    private async Task EmitStep1Async(
        WorkingPaperWriteSession session, List<SheetStat> stats,
        WorkingPaperContext context, WorkingPaperSheetPlan? sheetPlan,
        bool? hasCompletenessDifferences,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var applyDifferenceAppearance = hasCompletenessDifferences
            ?? (await completenessDiffs.GetPageAsync(
                context.ProjectId,
                context.MoneyScale,
                context.PeriodStart,
                context.PeriodEnd,
                new PageRequest(Cursor: null, PageSize: 1),
                cancellationToken)).Rows.Count > 0;
        using var spool = WorkingPaperDisplaySpool.Create(
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
        // 逐科目比對無法執行時，計畫把清單關掉，只留表頭。
        if (sheetPlan is { IncludeExceptionTable: false }) spool.Complete();
        else await FillDisplaySpoolAsync(
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
            WorkingPaperSheetCatalog.Step1,
            firstDataRow: 20,
            reservedRowsAfterData: 0,
            ReplayDisplaySpoolAsync(spool, cancellationToken),
            (name, _) =>
            {
                var sheet = session.OpenSheet(
                    name,
                    Step1Appearance(applyDifferenceAppearance),
                    Step1LockedArea);
                ConfigureStep1Layout(
                    sheet,
                    dataWidths: widths);
                WriteCommonHeader(
                    sheet,
                    context,
                    "此處底稿係記錄JE測試母體的完整性",
                    sheetPlan);
                sheet.WriteFixedRow(6, [sheet.TextCell(6, 1, "Step 1", WorkingPaperStyles.Bold)]);
                sheet.WriteFixedRow(7,
                [
                    sheet.TextCell(
                        7,
                        1,
                        sheetPlan?.AuditCondition ?? "評估母體完整性：",
                        WorkingPaperStyles.Bold)
                ]);
                WriteConclusionRow(
                    sheet,
                    15,
                    sheetPlan?.Conclusion
                        ?? WorkingPaperResultTexts.Step1Conclusion(applyDifferenceAppearance));
                sheet.WriteFixedRow(17,
                [
                    sheet.TextCell(
                        17,
                        2,
                        PlannedText(
                            sheetPlan,
                            0,
                            WorkingPaperResultTexts.Step1ListNote(applyDifferenceAppearance)),
                        WorkingPaperStyles.Bold)
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
    /// 「step1-1 借貸不平測試」:A1-A4 共同表頭 + Step1-1 程序固定文字 + 依結果二選一的結論。
    /// 有不平傳票時照舊版 JET 的版面：B14 說明、第 16 列五欄表頭、
    /// 第 17 列起依傳票號碼與總帳日期彙總的明細，最後一欄留給審計員填寫理由。
    /// </summary>
    private async Task EmitStep11Async(
        WorkingPaperWriteSession session, List<SheetStat> stats,
        WorkingPaperContext context, WorkingPaperSheetPlan? sheetPlan, CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var voucherDateRows = sheetPlan is { IncludeExceptionTable: false }
            ? ToItems(Array.Empty<UnbalancedVoucherDateRow>(), cancellationToken)
            : voucherBalances.StreamVoucherDateRowsAsync(context.ProjectId, cancellationToken);
        using var spool = WorkingPaperDisplaySpool.Create(
            ["傳票號碼", "總帳日期", "借方金額", "貸方金額", "借貸不平的理由"],
            [
                18D,
                12D,
                MaximumAmountDisplayColumnWidth,
                MaximumAmountDisplayColumnWidth,
                30D
            ]);
        await FillDisplaySpoolAsync(
            spool,
            ProjectRowsAsync(
                voucherDateRows,
                row => ProjectUnbalancedVoucherDateRow(row, context.MoneyScale),
                cancellationToken),
            cancellationToken);
        var widths = spool.Widths;

        await EmitContinuedRowsAsync(
            stats,
            WorkingPaperSheetCatalog.Step11,
            firstDataRow: 17,
            reservedRowsAfterData: 0,
            ReplayDisplaySpoolAsync(spool, cancellationToken),
            (name, includeTable) =>
            {
                includeTable = sheetPlan?.IncludeExceptionTable ?? includeTable;
                var sheet = session.OpenSheet(
                    name,
                    Step11Appearance(includeTable),
                    Step11LockedArea);
                ConfigureStep1Layout(
                    sheet,
                    dataWidths: widths);
                WriteCommonHeader(
                    sheet,
                    context,
                    "此處底稿係記錄JE測試母體的是否有借貸不平情形",
                    sheetPlan);
                sheet.WriteFixedRow(6, [sheet.TextCell(6, 1, "Step 1-1", WorkingPaperStyles.Bold)]);
                sheet.WriteFixedRow(7,
                [
                    sheet.TextCell(
                        7,
                        1,
                        sheetPlan?.AuditCondition ?? "評估個別傳票是否借貸不平：",
                        WorkingPaperStyles.Bold)
                ]);
                WriteConclusionRow(
                    sheet,
                    12,
                    sheetPlan?.Conclusion ?? WorkingPaperResultTexts.Step11Conclusion(includeTable));
                if (includeTable)
                {
                    sheet.WriteFixedRow(14,
                    [
                        sheet.TextCell(
                            14,
                            2,
                            WorkingPaperResultTexts.Step11ExceptionTableTitle,
                            WorkingPaperStyles.Bold)
                    ]);
                    sheet.WriteFixedRow(16, HeaderCells(sheet, 16,
                        ["傳票號碼", "總帳日期", "借方金額", "貸方金額", "借貸不平的理由"]));
                }
                return sheet;
            },
            (sheet, row, projected) => ProjectedCells(sheet, row, projected, firstColumn: 2),
            (sheet, rows, _, ct) =>
            {
                // 舊版只在有借貸不平的傳票時保護這張表。
                if (rows > 0)
                {
                    sheet.Protect();
                }
                return sheet.CloseAndSummarizeWith(rows, ct);
            },
            omitWhenEmpty: false,
            progress: progress,
            cancellationToken: cancellationToken);
    }

    // 舊版 JET 在沒有配對傳票建立人員時寫進 B13 的原句，「編制」照舊版用字。
    internal const string MissingCreatedByNotice = "請注意，該測試作業於總帳資料欄位配對作業程序並挑選編制人員欄位。";

    /// <summary>
    /// 「step1-2 分錄編製人員說明」:A1-A4 共同表頭 + Step1-2 程序固定文字 + 第 11 列欄標 +
    /// FetchAllAsync 全名單(B 編製人員/D 傳票數/E 金額彙總自動;C 自動或人工、F 部門、G 職稱、H 說明留空)。
    /// repository 契約未設筆數上限，因此寫出時仍依 Excel 列容量自動續頁，不假設名單必然少於單頁。
    /// </summary>
    private async Task EmitStep12Async(
        WorkingPaperWriteSession session, List<SheetStat> stats,
        WorkingPaperContext context, WorkingPaperSheetPlan? sheetPlan, CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var glMapping = await mappingStates.FindAsync(
            context.ProjectId,
            DatasetKind.Gl,
            cancellationToken);
        var missingCreatedByMapping = glMapping is null
            || !glMapping.Mapping.TryGetValue(GlMappingKeys.CreatedBy, out var createdBySource)
            || string.IsNullOrWhiteSpace(createdBySource);
        // 沒有配對傳票建立人員時照舊版不列名單，也不寫「（空白）」那一列，只在 B13 寫提示句。
        IReadOnlyList<PreparerSummaryExportRow> preparers = missingCreatedByMapping
            ? []
            : await preparerSummaries.FetchAllAsync(
                context.ProjectId, context.PeriodStart, context.PeriodEnd, cancellationToken);
        using var spool = WorkingPaperDisplaySpool.Create(
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
                ToItems(preparers, cancellationToken),
                preparer => ProjectPreparerRow(preparer, context.MoneyScale),
                cancellationToken),
            cancellationToken);
        var widths = spool.Widths;

        await EmitContinuedRowsAsync(
            stats,
            WorkingPaperSheetCatalog.Step12,
            firstDataRow: 12,
            reservedRowsAfterData: 0,
            ReplayDisplaySpoolAsync(spool, cancellationToken),
            (name, _) =>
            {
                var sheet = session.OpenSheet(
                    name,
                    Step12Appearance(missingCreatedByMapping),
                    Step12LockedArea);
                ConfigureStep12Layout(sheet, widths);
                WriteCommonHeader(
                    sheet,
                    context,
                    "此處底稿係記錄分錄編製人員資訊",
                    sheetPlan);
                sheet.WriteFixedRow(6, [sheet.TextCell(6, 1, "Step 1-2", WorkingPaperStyles.Bold)]);
                sheet.WriteFixedRow(7,
                [
                    sheet.TextCell(
                        7,
                        1,
                        sheetPlan?.AuditCondition ?? "評估是否有不適合的分錄編製人員：",
                        WorkingPaperStyles.Bold)
                ]);
                sheet.WriteFixedRow(11, HeaderCells(sheet, 11,
                [
                    "編製人員\n(來自JE測試母體)", "屬於自動或人工\n(若原資料無此判別欄位則留白)",
                    "經手之傳票數目\n(來自JE測試母體)", "經手之傳票金額彙總\n(來自JE測試母體)",
                    "部門", "職稱或職務", "是否為適當編製人員之說明"
                ]));
                if (missingCreatedByMapping)
                {
                    sheet.WriteFixedRow(13, [sheet.TextCell(13, 2, MissingCreatedByNotice)]);
                }
                return sheet;
            },
            (sheet, row, projected) => ProjectedCells(sheet, row, projected, firstColumn: 2),
            (sheet, rows, _, ct) =>
            {
                // 舊版只在有編製人員名單時保護這張表。
                if (rows > 0)
                {
                    sheet.Protect();
                }
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
        WorkingPaperWriteSession session, List<SheetStat> stats,
        WorkingPaperContext context, WorkingPaperSheetPlan? sheetPlan, CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        using var spool = WorkingPaperDisplaySpool.Create(
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
            WorkingPaperSheetCatalog.Step13,
            firstDataRow: 17,
            reservedRowsAfterData: 0,
            ReplayDisplaySpoolAsync(spool, cancellationToken),
            (name, _) =>
            {
                var sheet = session.OpenSheet(name, lockedArea: Step13LockedArea);
                ConfigureStep13Layout(sheet, widths);
                WriteCommonHeader(
                    sheet,
                    context,
                    "此處底稿係記錄JE測試母體於完整性測試有部分科目出現差異的回應與理由",
                    sheetPlan);
                sheet.WriteFixedRow(6, [sheet.TextCell(6, 1, "Step 1-3", WorkingPaperStyles.Bold)]);
                sheet.WriteFixedRow(7,
                [
                    sheet.TextCell(
                        7,
                        1,
                        sheetPlan?.AuditCondition ?? "評估於Step1完整性測試中有部分科目出現差異的原因：",
                        WorkingPaperStyles.Bold)
                ]);
                WriteConclusionRow(sheet, 12, sheetPlan?.Conclusion ?? Step13Conclusion);
                sheet.WriteFixedRow(14, [sheet.TextCell(14, 2, "出現差異之個別科目說明：", WorkingPaperStyles.Bold)]);
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

    private static void ConfigureStep1Layout(
        SheetWriter sheet,
        IReadOnlyList<double> dataWidths)
    {
        sheet.SetDefaultRowHeight(FormalWorkbookTypography.RowHeight(WorkingPaperStyles.RegularFontSize));
        sheet.SetColumnWidth(1, 1, 12);
        ApplyDisplayWidths(sheet, firstColumn: 2, widths: dataWidths);
        sheet.SetRowHeight(19, 44);
    }

    private static void ConfigureStep12Layout(
        SheetWriter sheet,
        IReadOnlyList<double> dataWidths)
    {
        sheet.SetDefaultRowHeight(FormalWorkbookTypography.RowHeight(WorkingPaperStyles.RegularFontSize));
        sheet.SetColumnWidth(1, 1, 8);
        ApplyDisplayWidths(sheet, firstColumn: 2, widths: dataWidths);
        sheet.SetRowHeight(11, 48);
    }

    private static void ConfigureStep13Layout(
        SheetWriter sheet,
        IReadOnlyList<double> dataWidths)
    {
        sheet.SetDefaultRowHeight(FormalWorkbookTypography.RowHeight(WorkingPaperStyles.RegularFontSize));
        sheet.SetColumnWidth(1, 1, 8);
        ApplyDisplayWidths(sheet, firstColumn: 2, widths: dataWidths);
        sheet.SetRowHeight(16, 42);
    }

    /// <summary>
    /// A1-A4 共同表頭(四張 step1 表共用):公司名 / 測試期間 / 財報準備開始日 / 各表傳入的斜體說明。
    /// A3 對應舊版 JET 的 sLast_Accounting_Period_Date，只取 <see cref="WorkingPaperContext.PrepStartDate"/>；缺值時明示 N/A，不得改用 PeriodEnd。
    /// </summary>
    private static void WriteCommonHeader(
        SheetWriter sheet,
        WorkingPaperContext context,
        string italicNote,
        WorkingPaperSheetPlan? sheetPlan)
    {
        sheet.WriteFixedRow(1, [sheet.TextCell(1, 1, $"公司名稱 : {context.CompanyName}", WorkingPaperStyles.Bold)]);
        sheet.WriteFixedRow(2, [sheet.TextCell(2, 1,
            $"測試資料期間 :  {context.PeriodStart} ~ {context.PeriodEnd}", WorkingPaperStyles.Bold)]);
        sheet.WriteFixedRow(3, [sheet.TextCell(3, 1,
            $"財務報表準備期間 - 開始日 : {sheetPlan?.NaText ?? DisplayPrepStartDate(context.PrepStartDate)}",
            WorkingPaperStyles.Bold)]);
        sheet.WriteFixedRow(4, [sheet.TextCell(4, 1, italicNote, WorkingPaperStyles.Bold)]);
    }

    private static string DisplayPrepStartDate(string? prepStartDate) =>
        string.IsNullOrWhiteSpace(prepStartDate) ? "N/A" : DigitsOnly(prepStartDate);

    private static IReadOnlyList<WorkingPaperDisplayCell> ProjectCompletenessRow(
        CompletenessDiffAccount acc,
        int moneyScale) =>
    [
        ProjectText(acc.AccountNumber),
        ProjectText(acc.AccountName),
        ProjectAmount(acc.TbAmountScaled, moneyScale),
        ProjectAmount(acc.GlAmountScaled, moneyScale),
        ProjectAmount(acc.DiffScaled, moneyScale)
    ];

    /// <summary>
    /// step1-1 明細列：B 傳票號碼、C 總帳日期、D 借方合計、E 貸方合計、F 理由（留白可編輯）。
    /// 舊版 JET 的貸方欄是負數金額的加總，所以這裡以負數顯示。
    /// 總帳日期沿用本底稿其他工作表的 yyyy-MM-dd 文字。
    /// </summary>
    private static IReadOnlyList<WorkingPaperDisplayCell> ProjectUnbalancedVoucherDateRow(
        UnbalancedVoucherDateRow row,
        int moneyScale) =>
    [
        ProjectText(row.VoucherNumber),
        ProjectText(row.GlDate),
        ProjectAmount(row.DebitScaled, moneyScale),
        ProjectAmount(-row.CreditScaled, moneyScale),
        ProjectBlank(WorkingPaperStyles.Editable)
    ];

    /// <summary>step1-2 編製人員列:B 人員 / C 空(自動或人工) / D 傳票數 / E 借方彙總 / F-H 空(部門/職稱/說明)。</summary>
    internal static IReadOnlyList<WorkingPaperDisplayCell> ProjectPreparerRow(
        PreparerSummaryExportRow preparer,
        int moneyScale) =>
    [
        ProjectText(PreparerSummaryDisplay.NameOf(preparer.CreatedBy)),
        // C 欄照舊版寫人工或自動；未判定或沒有配對人工傳票欄位時照範本表頭留白。
        // 留白的格子照舊版和 B12:E 一起鎖定，不用可填的樣式。
        preparer.IsManual is { } isManual
            ? ProjectText(PreparerSummaryDisplay.ManualText(isManual))
            : ProjectBlank(),
        ProjectInteger(preparer.EntryCount, groupedForWidth: true),
        ProjectAmount(preparer.DebitTotalScaled, moneyScale),
        ProjectBlank(WorkingPaperStyles.Editable),
        ProjectBlank(WorkingPaperStyles.Editable),
        ProjectBlank(WorkingPaperStyles.Editable)
    ];

    /// <summary>step1-3 差異說明列:B 編號 / C 名稱 / D 差異金額 / E-G 空(原因/調節/調節後差異)。</summary>
    private static IReadOnlyList<WorkingPaperDisplayCell> ProjectDiffExplanationRow(
        CompletenessDiffAccount acc,
        int moneyScale) =>
    [
        ProjectText(acc.AccountNumber),
        ProjectText(acc.AccountName),
        ProjectAmount(acc.DiffScaled, moneyScale),
        ProjectBlank(WorkingPaperStyles.Editable),
        ProjectBlank(WorkingPaperStyles.Editable),
        ProjectBlank(WorkingPaperStyles.Editable)
    ];

    // 受保護時鎖定的資料區，欄與起始列照舊版 JET：step1 是 B15:F、step1-1 與 step1-2 是 B12:E、step1-3 是 B17:D。
    // 列只鎖到最後一筆資料；舊版的 step1-2 與 step1-3 會多鎖最後一筆的下一列，JET 不鎖。
    private static readonly WorkingPaperLockedArea Step1LockedArea = new(15, 2, 6, 19);
    private static readonly WorkingPaperLockedArea Step11LockedArea = new(12, 2, 5, 16);
    private static readonly WorkingPaperLockedArea Step12LockedArea = new(12, 2, 5, 11);
    private static readonly WorkingPaperLockedArea Step13LockedArea = new(17, 2, 4, 16);

    private const string Step13Conclusion =
        "基於上述程序，查核團隊對出現差異之科目均已取得足夠的查核證據，已確認其原因尚屬合理或進行調節使其無差異，" +
        "因此可確認JE測試母體之完整性。";
}
