using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 非直接比對頁上的一格差異：只有儲存格位置、兩邊的值是哪一種，以及數字誰大誰小，沒有任何儲存格的值。
/// 這些頁照範本寫，位置是範本的固定位置。
/// </summary>
internal sealed record PrivateCaseResultCellDifference(
    string Address,
    string ExpectedKind,
    string ActualKind,
    string Relation);

/// <summary>一張非直接比對頁的逐格比對結果，頁的位置是舊版工作簿裡的工作表位置。</summary>
internal sealed record PrivateCaseResultSheetSummary(
    int SheetPosition,
    int ComparedCellCount,
    int BlockingCellCount,
    IReadOnlyList<PrivateCaseResultCellDifference> BlockingCells,
    IReadOnlyList<PrivateCaseReportDecisionSummary> AcceptedCells)
{
    public bool Matches => BlockingCellCount == 0;

    /// <summary>這張頁全部未放行的差異，依欄與差異的種類計數；BlockingCells 只列前幾格。</summary>
    internal IReadOnlyList<PrivateCaseResultCellTally> BlockingByColumn { get; init; } = [];
}

/// <summary>一欄裡同一種差異的格數，Column 是欄的字母。</summary>
internal sealed record PrivateCaseResultCellTally(string Column, string Relation, int Count);

/// <summary>
/// 逐格比對各報告照範本寫的頁：驗證結果的筆數與金額、預篩選各規則的命中數、篩選情境的傳票數與分錄數，以及範本的固定文字。
/// 公式格比快取的計算結果。已裁定、舊版程式說明得了，或因驗證框架的輸入而必然不同的儲存格，逐格列為放行並附決定名稱；
/// 有條件的放行只在差異確實是那一種時成立。其他差異都不放行，只標出差異的種類：只差日期寫法、只差名稱前後的固定文字、
/// 數字誰大誰小，或表內少了、多了哪一列。
/// </summary>
internal static class PrivateCaseResultCellComparison
{
    /// <summary>Prepared by 寫建案者（裁定 2026-10-06-02、2026-11-13-04）；驗證框架用測試主機的身分建案，和舊版的編製人員不同。</summary>
    internal const string PreparedByCaseCreator = "prepared-by-case-creator";

    /// <summary>Prepared date 寫按下匯出那一天（裁定 2026-11-01-04、2026-11-02-03）。</summary>
    internal const string PreparedDateExportDay = "prepared-date-export-day";

    /// <summary>驗證框架把來源檔複製成固定的去識別名稱再匯入，JET 寫的是複本的檔名。</summary>
    internal const string HarnessInputFileName = "harness-input-file-name";

    /// <summary>預篩選摘要第 14 到 16 列照範本留 A2 到 A4 的退役文字（裁定 2026-10-22-23）。</summary>
    internal const string RetiredPrescreenRows = "retired-prescreen-rows";

    /// <summary>
    /// 條件篩選摘要 B 欄寫 JET 的條件說明，用「且」「或」與統一的關鍵字寫法（裁定 2026-10-11-05、2026-11-03-01）；
    /// 舊版寫 IDEA 的條件紀錄。條件本身另由舊版紀錄的解析結果核對。
    /// </summary>
    internal const string JetConditionText = "jet-condition-text";

    internal const string ValidationCreditTotalSign = "validation-credit-total-sign";
    internal const string ActualVoucherCount = "actual-voucher-count";
    internal const string LegacyReportWithoutBlankDescriptionRow = "legacy-report-without-r7-row";
    internal const string HarnessClientName = "harness-client-name";
    internal const string CoverWithoutDocumentName = "cover-a6-not-written";
    internal const string Step3LegacyTextFirst = "step3-legacy-text-first";
    internal const string Step4BlankVoucherNote = "step4-blank-voucher-note";
    internal const string Step12Description = "step1-2-description";
    internal const string AccountMappingInstruction = "account-mapping-instruction";
    internal const string AuditorFilledCategory = "auditor-filled-category";

    /// <summary>
    /// 預篩選摘要 C8 財報準備開始日那一句：JET 照範本句子、日期寫 yyyy-MM-dd，舊版在日期前後各加一個空白、日期寫 8 位數字
    /// （裁定 2026-11-16-07）。
    /// </summary>
    internal const string PrescreenPrepStartSentence = "prescreen-prep-start-sentence";

    /// <summary>底稿 Step 4 從第 13 列起的總帳日期：JET 寫 yyyy-MM-dd，舊版寫 8 位數字（裁定 2026-11-16-07）。</summary>
    internal const string Step4GlDateFormat = "step4-gl-date-format";

    /// <summary>底稿 Step 3：JET 只寫有用到的情境代號，舊版留著範本在 B19 到 B28 印好的 C1 到 C10（裁定 2026-11-16-07）。</summary>
    internal const string Step3UnusedScenarioCodes = "step3-unused-scenario-codes";

    /// <summary>
    /// 底稿 Step 1-2 的編製人員表：JET 按分錄筆數降冪、再按編製者升冪，舊版照 IDEA 彙總的順序（裁定 2026-11-16-07）。
    /// 兩邊的列相同、只差順序時記成這一項。
    /// </summary>
    internal const string Step12PreparerOrder = "step1-2-preparer-order";

    /// <summary>
    /// 三份報告表頭的客戶名稱格：JET 在「Client:」後面空一格再接名稱，舊版不空格（裁定 2026-11-16-07）。
    /// 名稱照 harness-client-name 的條件，必須是驗證框架給 JET 的名稱。
    /// </summary>
    internal const string ClientLabelSpace = "client-label-space";

    /// <summary>
    /// 三份報告的 Year End，以及底稿與資料驗證報告 step1-3 說明頁的「測試資料期間」：JET 的日期寫 yyyy-MM-dd，
    /// 舊版寫 8 位數字（裁定 2026-11-16-07）。
    /// </summary>
    internal const string ReportPeriodDateFormat = "report-period-date-format";

    /// <summary>
    /// 預篩選摘要第 12 列（R5）的 D 欄：舊版原句在欄位名稱外加【】，JET 寫的欄位名稱不加任何符號（裁定 2026-11-16-09）。
    /// 舊版那一句去掉【】之後要和 JET 一字不差。
    /// </summary>
    internal const string FieldNameWithoutBrackets = "field-name-without-brackets";

    // 舊版底稿 step4 A12 原句之後，JET 照裁定 2026-11-09-12 在範本接的那一句。
    private const string Step4BlankVoucherSentence = "；空白傳票號碼的命中分錄逐筆列出，不計傳票張數，金額為該筆借方金額。";

    // 使用者在裁定 2026-11-06-17 指定的 step1-2 A4 說明。
    private const string Step12DescriptionText = "此處底稿係記錄分錄編製人員資訊";

    // JET 科目配對範本 A2 的說明開頭；說明是照裁定 2026-11-12-34 讓審計員在 List 工作表新增分類而改寫的。
    private const string AccountMappingInstructionStart =
        "請在 C 欄選擇標準科目分類；要新增分類，請在 List 工作表的清單最後加上名稱";

    // 舊版 IDEA 張數算不出來時寫的字樣，見舊版條件篩選摘要與底稿 step3 的寫法。
    private const string LegacyOverVoucherLimit = "Over 2,500";
    private const decimal LegacyVoucherLimit = 2500m;

    /// <summary>判斷一格差異能不能放行時看得到的內容：兩邊這一格的值、驗證框架給 JET 的客戶名稱，以及舊版這一頁每一格的值。</summary>
    private sealed record CellContext(
        CellValue? Expected,
        CellValue? Actual,
        string? ClientName,
        IReadOnlyDictionary<(int Row, int Column), CellValue> ExpectedSheet,
        int Row);

    /// <summary>
    /// 一條放行規則：位置符合，而且 Condition 成立時才放行；Condition 是 null 表示這些位置的差異都放行。
    /// ClientNamePrefix 有值時，這一格是「固定文字加客戶名稱」的表頭格。
    /// </summary>
    private sealed record AcceptedCells(
        Func<int, int, bool> Matches,
        string DecisionId,
        Func<CellContext, bool>? Condition = null,
        string? ClientNamePrefix = null);

    /// <summary>
    /// 表內的列不照位置比、只比兩邊有哪些列的範圍：從 FirstRow 起，FirstColumn 到 LastColumn 欄。
    /// 兩邊的列相同、只差順序時，記成 DecisionId 這一項放行。
    /// </summary>
    private sealed record UnorderedRows(int FirstRow, int FirstColumn, int LastColumn, string DecisionId)
    {
        internal bool Contains((int Row, int Column) key) =>
            key.Row >= FirstRow && key.Column >= FirstColumn && key.Column <= LastColumn;
    }

    private sealed record ResultSheet(
        string SheetName,
        IReadOnlyList<AcceptedCells> Accepted,
        UnorderedRows? Unordered = null);

    // 沒有列在這裡的頁不逐格比，原因：
    // INF 報告主表與底稿 step2 只有範本文字與隨機樣本，樣本另由抽樣規則核對（裁定 2026-08-28-03）；
    // 兩張「自動化工具-檔案欄位資訊」是欄位型態、長度與小數位數的對照表，不是審計結果；
    // 「自動化工具-假期假日資訊」列的是假日清單這項輸入，名稱由驗證框架產生；科目配對範本的 List 是可選的分類清單。
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ResultSheet>> ResultSheets =
        new Dictionary<string, IReadOnlyList<ResultSheet>>(StringComparer.Ordinal)
        {
            ["validation-report"] =
            [
                new("ValidationReport",
                [
                    Cell("E4", PreparedByCaseCreator),
                    Cell("E5", PreparedDateExportDay),
                    Cell("E9", HarnessInputFileName),
                    Cell("E14", HarnessInputFileName),
                    // 貸方合計舊版寫負數，JET 寫正數，裁定 2026-11-05-64 先不改；只放行剛好是相反數的情形。
                    Cell("E12", ValidationCreditTotalSign, static context =>
                        context.Expected?.Number is { } left
                        && context.Actual?.Number is { } right
                        && left != 0m
                        && left == -right),
                    ClientCell("E1", "Client:"),
                    ReportClientCell("E1"),
                    PeriodDateCell("E2"),
                ]),
                new(WorkingPaperSheetCatalog.Step13, [ClientCell("A1", "公司名稱 : "), PeriodDateCell("A2")]),
                new("完整性測試出現差異時之指引", []),
            ],
            ["prescreen-report"] =
            [
                new("Pre-screening_Report",
                [
                    Cell("E4", PreparedByCaseCreator),
                    Cell("E5", PreparedDateExportDay),
                    new((row, _) => row is >= 14 and <= 16, RetiredPrescreenRows),
                    // 舊版程式在第 17 列寫空白摘要（R7）那一列；這份舊版報告整列都沒有時，JET 補上的那一列放行。
                    new((row, _) => row == 17, LegacyReportWithoutBlankDescriptionRow, static context =>
                        context.Expected is null
                        && !context.ExpectedSheet.Keys.Any(key => key.Row == context.Row)),
                    Cell("C8", PrescreenPrepStartSentence, IsSameSentenceExceptTheDateSpacingAndFormat),
                    Cell("D12", FieldNameWithoutBrackets, IsSameSentenceWithoutBrackets),
                    ClientCell("E1", "Client:"),
                    ReportClientCell("E1"),
                    PeriodDateCell("E2"),
                ]),
            ],
            ["criteria-selection-report"] =
            [
                new("Summary Inforamtion",
                [
                    Cell("D1", PreparedByCaseCreator),
                    Cell("D2", PreparedDateExportDay),
                    new((row, column) => column == 2 && row is >= 5 and <= 14, JetConditionText),
                    new((row, column) => column == 3 && row is >= 5 and <= 14, ActualVoucherCount, IsActualVoucherCountOverLimit),
                    ClientCell("C1", "Client:"),
                    ReportClientCell("C1"),
                    PeriodDateCell("C2"),
                ]),
            ],
            ["working-paper"] =
            [
                new(WorkingPaperSheetCatalog.Cover,
                [
                    ClientCell("A1", "公司名稱 : "),
                    PeriodDateCell("A2"),
                    // 裁定 2026-11-05-29：A6 不再寫底稿說明檔的檔名，只保留儲存格格式。
                    Cell("A6", CoverWithoutDocumentName, static context => context.Actual is null),
                ]),
                new(WorkingPaperSheetCatalog.Intro, []),
                new(WorkingPaperSheetCatalog.Step1, [ClientCell("A1", "公司名稱 : "), PeriodDateCell("A2")]),
                new(WorkingPaperSheetCatalog.Step11, [ClientCell("A1", "公司名稱 : "), PeriodDateCell("A2")]),
                new(WorkingPaperSheetCatalog.Step12,
                [
                    ClientCell("A1", "公司名稱 : "),
                    PeriodDateCell("A2"),
                    Cell("A4", Step12Description, static context =>
                        context.Actual?.Value == Step12DescriptionText),
                ],
                // 編製人員表從第 12 列起，B 到 E 欄是人員、自動或人工、筆數與借方合計。JET 按分錄筆數降冪、再按編製者升冪，
                // 舊版照 IDEA 彙總的順序（裁定 2026-11-16-07），所以只比兩邊有哪些列。
                new UnorderedRows(12, 2, 5, Step12PreparerOrder)),
                new(WorkingPaperSheetCatalog.Step13, [ClientCell("A1", "公司名稱 : "), PeriodDateCell("A2")]),
                new(WorkingPaperSheetCatalog.Step3,
                [
                    ClientCell("A1", "公司名稱 : "),
                    // 裁定 2026-11-05-59：B7 與 B9 寫回舊版原文，JET 的說明另起一行接在後面。
                    new((row, column) => column == 2 && row is 7 or 9, Step3LegacyTextFirst, static context =>
                        context is { Expected: { Kind: "text" } legacy, Actual: { Kind: "text" } jet }
                        && jet.Value.StartsWith(legacy.Value + "\n", StringComparison.Ordinal)),
                    new((row, column) => column == 3 && row >= 19, JetConditionText),
                    new((row, column) => column == 5 && row >= 19, ActualVoucherCount, IsActualVoucherCountOverLimit),
                    new((row, column) => column == 2 && row is >= Step3FirstScenarioRow and <= Step3LastScenarioRow,
                        Step3UnusedScenarioCodes, IsUnusedTemplateScenarioCode),
                ]),
                new(WorkingPaperSheetCatalog.Step4,
                [
                    ClientCell("A1", "公司名稱 : "),
                    Cell("A12", Step4BlankVoucherNote, static context =>
                        context is { Expected: { Kind: "text" } legacy, Actual: { } jet }
                        && jet.Value == legacy.Value + Step4BlankVoucherSentence),
                    // 第 13 列起是傳票列，C 欄是總帳日期。
                    new((row, column) => column == 3 && row >= 13, Step4GlDateFormat, IsSameDateWrittenAsIso),
                ]),
                new(WorkingPaperSheetCatalog.Step5, [ClientCell("A1", "公司名稱 : ")]),
                new(WorkingPaperSheetCatalog.AccountMapping, []),
            ],
            ["account-mapping"] =
            [
                new("AccountMapping",
                [
                    Cell("A2", AccountMappingInstruction, static context =>
                        context.Actual?.Value.StartsWith(AccountMappingInstructionStart, StringComparison.Ordinal) == true),
                    // 舊版這份是審計員填過分類的工作檔（裁定 2026-09-02-05 定為工作檔），JET 匯出的範本 C 欄留空給審計員選。
                    new((row, column) => column == 3 && row >= 4, AuditorFilledCategory, static context =>
                        context.Actual is null),
                ]),
            ],
        };

    // 裁定 2026-10-29-29：舊版 IDEA 算不出超過 2,500 張的張數而寫「Over 2,500」，JET 寫實際張數；JET 的張數必須真的超過 2,500。
    private static bool IsActualVoucherCountOverLimit(CellContext context) =>
        context is { Expected: { Kind: "text", Value: LegacyOverVoucherLimit }, Actual: { Kind: "number", Number: { } count } }
        && count > LegacyVoucherLimit;

    // 兩份底稿範本都在 step3 的 B19 到 B28 印好情境代號，B19 是 C1，B28 是 C10。
    private const int Step3FirstScenarioRow = 19;
    private const int Step3LastScenarioRow = 28;

    // step3 一列的條件、選擇原因與命中傳票數，依序在 C、D、E 欄。
    private static readonly int[] Step3ScenarioDetailColumns = [3, 4, 5];

    // 裁定 2026-11-16-07：舊版的 8 位數字日期換成 yyyy-MM-dd，再去掉日期前後的空白之後，要和 JET 寫的整句一字不差。
    private static bool IsSameSentenceExceptTheDateSpacingAndFormat(CellContext context) =>
        context is { Expected: { Kind: "text" } legacy, Actual: { Kind: "text" } jet }
        && CompactDatesAsIso(legacy.Value) is { } legacyWithIsoDates
        && WithoutSpacesAroundIsoDates(legacyWithIsoDates) == jet.Value;

    // 裁定 2026-11-16-09：舊版的句子有【】，去掉【】之後要和 JET 寫的一字不差。只差欄位名稱外的括號，
    // 欄位名稱或其他字不同時照樣擋下。
    private static bool IsSameSentenceWithoutBrackets(CellContext context) =>
        context is { Expected: { Kind: "text" } legacy, Actual: { Kind: "text" } jet }
        && legacy.Value.Contains('【', StringComparison.Ordinal)
        && legacy.Value.Replace("【", string.Empty, StringComparison.Ordinal)
            .Replace("】", string.Empty, StringComparison.Ordinal) == jet.Value;

    // 裁定 2026-11-16-07：舊版的 8 位數字日期換成 yyyy-MM-dd 之後，要和 JET 寫的一字不差；舊版存成數字或文字都一樣。
    private static bool IsSameDateWrittenAsIso(CellContext context) =>
        context is { Expected: { } legacy, Actual: { Kind: "text" } jet }
        && CompactDatesAsIso(legacy.Value) is { } legacyAsIso
        && legacyAsIso == jet.Value;

    // 裁定 2026-11-16-07：舊版這一格是範本在這一列印好的代號，同一列的條件、選擇原因與命中傳票數都空白，JET 這一格沒有寫。
    private static bool IsUnusedTemplateScenarioCode(CellContext context) =>
        context is { Expected: { Kind: "text" } legacy, Actual: null }
        && legacy.Value == TemplateScenarioCode(context.Row)
        && Step3ScenarioDetailColumns.All(column => !context.ExpectedSheet.ContainsKey((context.Row, column)));

    private static string TemplateScenarioCode(int row) =>
        "C" + (row - Step3FirstScenarioRow + 1).ToString(CultureInfo.InvariantCulture);

    private const int MaximumReportedCells = 30;

    internal static IReadOnlyList<PrivateCaseResultSheetSummary> Compare(
        string reportSlug,
        string expectedPath,
        string actualPath,
        Func<string, int, int, bool>? contentMask,
        string? clientName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualPath);
        if (!ResultSheets.TryGetValue(reportSlug, out var sheets))
        {
            return [];
        }

        var names = sheets.Select(static sheet => sheet.SheetName).ToHashSet(StringComparer.Ordinal);
        var expected = ReadSheets(expectedPath, names, contentMask);
        var actual = ReadSheets(actualPath, names, contentMask);
        var summaries = new List<PrivateCaseResultSheetSummary>();
        foreach (var sheet in sheets)
        {
            if (!expected.Sheets.TryGetValue(sheet.SheetName, out var expectedCells)
                || !actual.Sheets.TryGetValue(sheet.SheetName, out var actualCells))
            {
                // 工作表集合的差異由工作簿層級的比對處理。
                continue;
            }

            var blocking = new List<PrivateCaseResultCellDifference>();
            var accepted = new Dictionary<string, (int Groups, long Cells)>(StringComparer.Ordinal);
            var table = sheet.Unordered;
            var addresses = expectedCells.Keys
                .Union(actualCells.Keys)
                .Where(key => table is null || !table.Contains(key))
                .OrderBy(static key => key)
                .ToArray();
            foreach (var (row, column) in addresses)
            {
                var left = expectedCells.GetValueOrDefault((row, column));
                var right = actualCells.GetValueOrDefault((row, column));
                if (string.Equals(left?.Value, right?.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                var context = new CellContext(left, right, clientName, expectedCells, row);
                var rules = sheet.Accepted.Where(rule => rule.Matches(row, column)).ToArray();
                var decision = rules.FirstOrDefault(rule => rule.Condition?.Invoke(context) ?? true)?.DecisionId;
                if (decision is not null)
                {
                    var current = accepted.GetValueOrDefault(decision);
                    accepted[decision] = (1, current.Cells + 1);
                    continue;
                }
                blocking.Add(new PrivateCaseResultCellDifference(
                    Reference(row, column),
                    left?.Kind ?? "empty",
                    right?.Kind ?? "empty",
                    Relation(left, right, rules, clientName)));
            }

            var tableCellCount = 0;
            if (table is not null)
            {
                // 表內的列：舊版有、JET 沒有同樣內容的列，以及 JET 多出來的列，各記一筆；位置是那一列第一欄。
                var (missing, extra, reordered, cellCount) = CompareTableRows(expectedCells, actualCells, table);
                tableCellCount = cellCount;
                if (reordered > 0)
                {
                    accepted[table.DecisionId] = (1, reordered);
                }
                blocking.AddRange(missing.Select(row => new PrivateCaseResultCellDifference(
                    Reference(row, table.FirstColumn), "row", "empty", "row-only-in-expected")));
                blocking.AddRange(extra.Select(row => new PrivateCaseResultCellDifference(
                    Reference(row, table.FirstColumn), "empty", "row", "row-only-in-actual")));
            }

            summaries.Add(new PrivateCaseResultSheetSummary(
                expected.Positions[sheet.SheetName],
                addresses.Length + tableCellCount,
                blocking.Count,
                blocking.Take(MaximumReportedCells).ToArray(),
                accepted
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => new PrivateCaseReportDecisionSummary(
                        pair.Key,
                        pair.Value.Groups,
                        pair.Value.Cells))
                    .ToArray())
            {
                BlockingByColumn = blocking
                    .GroupBy(static cell => (Column: ColumnLetters(cell.Address), cell.Relation))
                    .Select(static group => new PrivateCaseResultCellTally(
                        group.Key.Column,
                        group.Key.Relation,
                        group.Count()))
                    .OrderBy(static tally => tally.Column.Length)
                    .ThenBy(static tally => tally.Column, StringComparer.Ordinal)
                    .ThenBy(static tally => tally.Relation, StringComparer.Ordinal)
                    .ToArray(),
            });
        }

        return summaries;
    }

    private sealed record CellValue(string Kind, string Value, decimal? Number);

    private sealed record WorkbookCells(
        IReadOnlyDictionary<string, IReadOnlyDictionary<(int Row, int Column), CellValue>> Sheets,
        IReadOnlyDictionary<string, int> Positions);

    private static WorkbookCells ReadSheets(
        string path,
        IReadOnlySet<string> names,
        Func<string, int, int, bool>? contentMask)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("Result cell comparison workbook has no workbook part.");
        var shared = workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>()
            .Select(item => string.Concat(item.Descendants<Text>().Select(static text => text.Text)))
            .ToArray() ?? [];
        var sheets = new Dictionary<string, IReadOnlyDictionary<(int, int), CellValue>>(StringComparer.Ordinal);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var position = 0;
        foreach (var sheet in workbookPart.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            position++;
            var sheetName = sheet.Name?.Value;
            if (sheetName is null
                || !names.Contains(sheetName)
                || sheets.ContainsKey(sheetName)
                || sheet.Id?.Value is not { } relationshipId
                || !workbookPart.TryGetPartById(relationshipId, out var part)
                || part is not WorksheetPart worksheetPart)
            {
                continue;
            }

            positions[sheetName] = position;
            var cells = new Dictionary<(int, int), CellValue>();
            using var reader = new OpenXmlPartReader(worksheetPart);
            var fallbackRow = 0;
            while (reader.Read())
            {
                if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                {
                    continue;
                }
                var row = (Row)reader.LoadCurrentElement()!;
                var rowIndex = (int)(row.RowIndex?.Value ?? (uint)(fallbackRow + 1));
                fallbackRow = rowIndex;
                var fallbackColumn = 0;
                foreach (var cell in row.Elements<Cell>())
                {
                    var column = ParseColumn(cell.CellReference?.Value) ?? fallbackColumn + 1;
                    fallbackColumn = column;
                    if (contentMask?.Invoke(sheetName, rowIndex, column) == true)
                    {
                        continue;
                    }
                    var value = ReadValue(cell, shared);
                    if (value is not null)
                    {
                        cells[(rowIndex, column)] = value;
                    }
                }
            }
            sheets[sheetName] = cells;
        }

        return new WorkbookCells(sheets, positions);
    }

    private static CellValue? ReadValue(Cell cell, IReadOnlyList<string> shared)
    {
        var raw = cell.CellValue?.InnerText ?? string.Empty;
        var dataType = cell.DataType?.Value;
        string kind;
        string text;
        if (dataType == CellValues.SharedString)
        {
            kind = "text";
            text = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0
                && index < shared.Count
                    ? shared[index]
                    : throw new InvalidDataException("Result cell comparison met an unresolvable shared string.");
        }
        else if (dataType == CellValues.InlineString)
        {
            kind = "text";
            text = cell.InlineString is null
                ? string.Empty
                : string.Concat(cell.InlineString.Descendants<Text>().Select(static item => item.Text));
        }
        else if (dataType == CellValues.String)
        {
            kind = "text";
            text = raw;
        }
        else if (dataType == CellValues.Boolean)
        {
            kind = "boolean";
            text = raw == "1" ? "TRUE" : "FALSE";
        }
        else if (dataType == CellValues.Error)
        {
            kind = "error";
            text = raw;
        }
        else
        {
            kind = "number";
            text = raw;
        }

        var formula = cell.CellFormula?.Text?.Trim() ?? string.Empty;
        if (text.Trim().Length == 0)
        {
            // 沒有快取結果的公式格比公式本身。
            return formula.Length == 0 ? null : new CellValue("formula", "=" + formula, null);
        }

        if (kind == "number")
        {
            if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact))
            {
                return new CellValue(kind, exact.ToString("G29", CultureInfo.InvariantCulture), exact);
            }
            return new CellValue(kind, text, null);
        }
        if (kind != "text")
        {
            return new CellValue(kind, text, null);
        }

        var normalized = LegacyAuditParityNormalizedWorkbookReader.NormalizeSemanticValue("text", text).Trim();
        return new CellValue(
            kind,
            normalized,
            decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : null);
    }

    private static string Relation(
        CellValue? expected,
        CellValue? actual,
        IReadOnlyList<AcceptedCells> rules,
        string? clientName)
    {
        if (expected is null)
        {
            return "actual-only";
        }
        if (actual is null)
        {
            return "expected-only";
        }
        // 表頭的客戶名稱格：名稱放行的條件不成立，又看得出兩邊各自寫了名稱時，差的是名稱前後的固定文字。
        if (clientName is { Length: > 0 }
            && rules.Any(rule => rule.ClientNamePrefix is { } prefix
                && expected.Value.StartsWith(prefix.TrimEnd(), StringComparison.Ordinal))
            && actual.Value.Contains(clientName.Trim(), StringComparison.Ordinal))
        {
            return "fixed-text-different";
        }
        // 同一個日期換一種寫法，例如 8 位數字、yyyy-MM-dd 或 Excel 日期序列值。
        var expectedDates = CanonicalDates(expected);
        var actualDates = CanonicalDates(actual);
        if (string.Equals(expectedDates, actualDates, StringComparison.Ordinal))
        {
            return "date-format";
        }
        // 固定文字只差空白，或只差空白與日期寫法。
        if (string.Equals(WithoutSpaces(expected.Value), WithoutSpaces(actual.Value), StringComparison.Ordinal))
        {
            return "spacing";
        }
        if (string.Equals(WithoutSpaces(expectedDates), WithoutSpaces(actualDates), StringComparison.Ordinal))
        {
            return "spacing-and-date-format";
        }
        if (expected.Number is { } left && actual.Number is { } right)
        {
            return right < left ? "actual-lower" : "actual-higher";
        }
        return "different";
    }

    private static readonly System.Text.RegularExpressions.Regex SeparatedDate = new(
        @"(?<!\d)(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})(?!\d)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex CompactDate = new(
        @"(?<!\d)(\d{4})(\d{2})(\d{2})(?!\d)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // 只用來說明差異的種類：把格內看得出來的日期都改寫成 yyyy-MM-dd。
    private static string CanonicalDates(CellValue value)
    {
        if (value.Kind == "number"
            && value.Number is { } number
            && decimal.Truncate(number) == number
            && number is >= 61m and <= 2958465m)
        {
            return DateTime.FromOADate((double)number).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var text = SeparatedDate.Replace(value.Value, static match => IsoDate(
            match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value) ?? match.Value);
        return CompactDate.Replace(text, static match => IsoDate(
            match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value) ?? match.Value);
    }

    private static string WithoutSpaces(string value) =>
        string.Concat(value.Where(static character => !char.IsWhiteSpace(character)));

    private static readonly System.Text.RegularExpressions.Regex IsoDateWithSurroundingSpaces = new(
        @"\s*(\d{4}-\d{2}-\d{2})\s*",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // 把格內每一個 8 位數字的日期改寫成 yyyy-MM-dd；格內沒有 8 位數字的日期時是 null。
    private static string? CompactDatesAsIso(string value)
    {
        var converted = CompactDate.Replace(value, static match => IsoDate(
            match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value) ?? match.Value);
        return converted == value ? null : converted;
    }

    // 只去掉 yyyy-MM-dd 日期前後的空白，句子其他地方的空白照留。
    private static string WithoutSpacesAroundIsoDates(string value) =>
        IsoDateWithSurroundingSpaces.Replace(value, "$1");

    private static string ColumnLetters(string address) =>
        string.Concat(address.TakeWhile(static character => character is >= 'A' and <= 'Z'));

    private static string? IsoDate(string year, string month, string day) =>
        int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
        && int.TryParse(month, NumberStyles.None, CultureInfo.InvariantCulture, out var m)
        && int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out var d)
        && y is >= 1900 and <= 9999
        && m is >= 1 and <= 12
        && d >= 1
        && d <= DateTime.DaysInMonth(y, m)
            ? new DateOnly(y, m, d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// 比兩邊表內有哪些列。Reordered 只在兩邊的列完全相同時才有值，是兩邊同一個位置上不是同一列的列數。
    /// </summary>
    private static (IReadOnlyList<int> Missing, IReadOnlyList<int> Extra, int Reordered, int CellCount) CompareTableRows(
        IReadOnlyDictionary<(int Row, int Column), CellValue> expectedCells,
        IReadOnlyDictionary<(int Row, int Column), CellValue> actualCells,
        UnorderedRows table)
    {
        var expectedRows = TableRows(expectedCells, table);
        var actualRows = TableRows(actualCells, table);
        var remaining = actualRows
            .GroupBy(static row => row.Key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<int>(group.Select(static row => row.Row)),
                StringComparer.Ordinal);
        var missing = new List<int>();
        foreach (var (row, key) in expectedRows)
        {
            if (remaining.TryGetValue(key, out var rows) && rows.Count > 0)
            {
                rows.Dequeue();
                continue;
            }
            missing.Add(row);
        }
        var extra = remaining.Values.SelectMany(static rows => rows).Order().ToArray();
        var cellCount = expectedCells.Keys.Union(actualCells.Keys).Count(table.Contains);
        var sameRows = missing.Count == 0 && extra.Length == 0;
        var reordered = sameRows
            ? expectedRows.Zip(actualRows).Count(static pair => pair.First.Key != pair.Second.Key)
            : 0;
        return (missing, extra, reordered, cellCount);
    }

    private static IReadOnlyList<(int Row, string Key)> TableRows(
        IReadOnlyDictionary<(int Row, int Column), CellValue> cells,
        UnorderedRows table) =>
        cells.Keys
            .Where(table.Contains)
            .Select(static key => key.Row)
            .Distinct()
            .Order()
            .Select(row => (row, string.Join(
                "\u001f",
                Enumerable.Range(table.FirstColumn, table.LastColumn - table.FirstColumn + 1)
                    .Select(column => cells.TryGetValue((row, column), out var value) ? value.Value : string.Empty))))
            .ToArray();

    private static AcceptedCells Cell(
        string reference,
        string decisionId,
        Func<CellContext, bool>? condition = null)
    {
        var (row, column) = ParseReference(reference);
        return new AcceptedCells(
            (candidateRow, candidateColumn) => candidateRow == row && candidateColumn == column,
            decisionId,
            condition);
    }

    /// <summary>
    /// 表頭「固定文字加客戶名稱」的一格。驗證框架給 JET 的客戶名稱和舊版案件的名稱不同，這是輸入條件不同；
    /// 舊版這一格以同一段固定文字開頭，而且 JET 寫的正好是固定文字加上驗證框架給的名稱時才放行。
    /// </summary>
    private static AcceptedCells ClientCell(string reference, string prefix)
    {
        var (row, column) = ParseReference(reference);
        return new AcceptedCells(
            (candidateRow, candidateColumn) => candidateRow == row && candidateColumn == column,
            HarnessClientName,
            context => context is { Expected: { Kind: "text" } legacy, Actual: { } jet, ClientName: { Length: > 0 } name }
                && legacy.Value.StartsWith(prefix, StringComparison.Ordinal)
                && jet.Value == (prefix + name).Trim(),
            prefix);
    }

    // 三份報告表頭的固定文字：舊版寫「Client:」直接接名稱，JET 寫「Client: 」空一格再接名稱。
    private const string LegacyReportClientLabel = "Client:";
    private const string JetReportClientLabel = "Client: ";

    /// <summary>
    /// 三份報告表頭的客戶名稱格（裁定 2026-11-16-07）：舊版「Client:」後面直接接名稱，
    /// JET 寫的正好是「Client: 」加上驗證框架給的名稱時才放行；名稱不同是驗證框架的輸入條件，和 <see cref="ClientCell"/> 相同。
    /// </summary>
    private static AcceptedCells ReportClientCell(string reference)
    {
        var (row, column) = ParseReference(reference);
        return new AcceptedCells(
            (candidateRow, candidateColumn) => candidateRow == row && candidateColumn == column,
            ClientLabelSpace,
            static context => context is { Expected: { Kind: "text" } legacy, Actual: { } jet, ClientName: { Length: > 0 } name }
                && LegacyWritesTheNameRightAfterTheLabel(legacy.Value)
                && jet.Value == JetReportClientLabel + name.Trim(),
            LegacyReportClientLabel);
    }

    private static bool LegacyWritesTheNameRightAfterTheLabel(string value) =>
        value.Length > LegacyReportClientLabel.Length
        && value.StartsWith(LegacyReportClientLabel, StringComparison.Ordinal)
        && !char.IsWhiteSpace(value[LegacyReportClientLabel.Length]);

    /// <summary>
    /// 表頭的 Year End 或「測試資料期間」那一格（裁定 2026-11-16-07）：舊版的 8 位數字日期換成 yyyy-MM-dd 之後，
    /// 要和 JET 寫的一字不差。
    /// </summary>
    private static AcceptedCells PeriodDateCell(string reference) =>
        Cell(reference, ReportPeriodDateFormat, IsSameDateWrittenAsIso);

    private static (int Row, int Column) ParseReference(string reference)
    {
        var column = ParseColumn(reference) ?? throw new ArgumentException("Invalid cell reference.", nameof(reference));
        var row = int.Parse(
            reference.AsSpan(reference.IndexOfAny("0123456789".ToCharArray())),
            NumberStyles.None,
            CultureInfo.InvariantCulture);
        return (row, column);
    }

    private static string Reference(int row, int column)
    {
        var letters = string.Empty;
        for (var value = column; value > 0; value = (value - 1) / 26)
        {
            letters = (char)('A' + (value - 1) % 26) + letters;
        }
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }

    private static int? ParseColumn(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }
        var value = 0;
        foreach (var character in reference)
        {
            if (!char.IsAsciiLetter(character))
            {
                break;
            }
            value = checked(value * 26 + char.ToUpperInvariant(character) - 'A' + 1);
        }
        return value == 0 ? null : value;
    }
}
