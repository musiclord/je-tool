using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 工作底稿 planning 的 bounded Application input。來源版本必須先由既有
/// ReportExportSupport stale 機制驗證；本 request 不建立第二套失效判定。
/// <see cref="ScenarioRevision"/> 是 null 表示案件還沒有儲存任何篩選情境；
/// <see cref="CompletenessNaReason"/> 有值表示這次驗證無法執行 GL 與 TB 逐科目比對。
/// </summary>
internal sealed record WorkingPaperRequest(
    string ProjectId,
    string PeriodStart,
    string PeriodEnd,
    string? PrepStartDate,
    int MoneyScale,
    string ValidationResultId,
    string? ScenarioRevision,
    IReadOnlyList<WorkingPaperScenarioSelection> Scenarios,
    GlPopulationScope PopulationScope,
    ReportWorkbookMetadata? WorkbookMetadata = null,
    IReadOnlyList<GlRdeFieldMetadata>? CustomFields = null,
    string? CompletenessNaReason = null);

/// <summary>
/// 工作底稿逐列標記的範圍。DirectHitRows 保留篩選結果實際命中的列；
/// HitVoucherRows 只用於舊 IDEA 明確以整張傳票呈現的情境。
/// </summary>
internal enum WorkingPaperScenarioTagScope
{
    DirectHitRows,
    HitVoucherRows
}

/// <summary>Application 已驗證且選入底稿的情境 metadata；取自案件已存的情境，個數與順序跟著已存情境。</summary>
internal sealed record WorkingPaperScenarioSelection(
    int Position,
    string Name,
    string Rationale,
    WorkingPaperScenarioTagScope TagScope = WorkingPaperScenarioTagScope.DirectHitRows,
    string? ConditionLogic = null);

/// <summary>
/// Infrastructure planning port 回傳的 bounded first-row presence facts 與情境計數。
/// 完整母體列、分頁 rows 與 OpenXML layout 不進入 AuditCore；缺少的情境計數沿用
/// 既有 GetValueOrDefault 零值語意。
/// </summary>
internal sealed record WorkingPaperPlanningFacts(
    bool HasCompletenessDifferences,
    bool HasUnbalancedVouchers,
    IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)> ScenarioHitCounts,
    IReadOnlyList<FieldInfo> TargetTbDefinitions,
    IReadOnlyList<FieldInfo> TargetGlDefinitions);

/// <summary>供 step3 row 與 step4／step4-1 動態欄集共用的 finalized 情境計畫。</summary>
internal sealed record WorkingPaperScenarioPlan(
    int Position,
    string Name,
    string Rationale,
    long VoucherHitCount,
    long RowHitCount,
    WorkingPaperScenarioTagScope TagScope,
    string? ConditionLogic = null);

/// <summary>
/// 單張正準工作表的 audit planning decision。名稱與順序仍取 Domain catalog；
/// writer 保留欄標、cell 座標、layout、style、SAX 與 continuation paging。
/// </summary>
internal sealed record WorkingPaperSheetPlan(
    string SheetName,
    bool Emit,
    bool IncludeExceptionTable,
    IReadOnlyList<int> ScenarioPositions,
    string? AuditCondition,
    IReadOnlyList<string> Methodology,
    string? Conclusion,
    string? NaText);

/// <summary>
/// 底稿計畫與寫出程式共用的固定文字。Step 1 與 Step 1-1 依結果二選一的結論文字照舊版 JET：有差異科目或不平傳票時才改寫範本原文。
/// 範本原文就是「沒有差異」「沒有不平」的版本。
/// </summary>
internal static class WorkingPaperResultTexts
{
    // Step 3 的 B7「測試範圍」與 B9「選擇該測試範圍的理由」照舊版原文寫
    // （B9 句中的空白也照原文），JET 另外寫的查核期間與排除範圍另起一行接在後面。
    internal static string Step3TestScope(string periodStart, string periodEnd) =>
        "期末財務報表準備期間與整個財務報導期間之會計分錄\n"
        + $"查核期間內（{periodStart} ~ {periodEnd}）之會計分錄";

    internal const string Step3ScopeRationale =
        "查核團隊考量舞弊風險相關之分錄，除了會於期末財務報表準備期間發生外，某些與舞弊風險因子相關之分錄類型(例如：收入)"
        + "亦會在整個財務報導期間中被記錄。這些都將會對財務報表的正確性造成影響。因此，除測試期末財務報表準備期間之分錄外，"
        + " 亦將對整個財務報表期間之會計分錄母體，篩選與舞弊風險類型相關之分錄。\n"
        + "本次高風險條件以查核期間內的會計分錄為母體；查核期間外與【總帳日期】空白的分錄，不列入本版各情境的結果與彙總。";

    internal static string Step1Conclusion(bool hasCompletenessDifferences) =>
        hasCompletenessDifferences
            ? "基於上述程序，查核團隊對於JE測試母體之完整性，尚需於Step1-3說明以取得足夠的查核證據。"
            : "基於上述程序，查核團隊對於JE測試母體之完整性，已取得足夠的查核證據。";

    internal static string Step1ListNote(bool hasCompletenessDifferences) =>
        "#針對試算表科目金額本期異動與會計分錄(JE)進行推滾比對之清單列示如下："
        + (hasCompletenessDifferences
            ? "(有部分科目之差異數不為0，請於step1-3說明其理由，以確認JE母體的完整性)"
            : "(已確認差異數均為0，可確認其完整性)");

    internal static string Step11Conclusion(bool hasUnbalancedVouchers) =>
        hasUnbalancedVouchers
            ? "基於上述程序，查核團隊發現有部分傳票借貸不平，但已取得足夠的查核證據，確認其理由尚屬合理。"
            : "基於上述程序，查核團隊已取得足夠的查核證據，確認無借貸不平之情形。";

    internal const string Step11ExceptionTableTitle = "出現借貸不平之個別傳票說明：";

    // 沒有 TB 也照常產出底稿，第一步寫無法比對的說明，科目清單留空、不產生 Step1-3。
    internal const string Step1NotApplicableConclusion =
        "基於上述程序，查核團隊對於JE測試母體之完整性，尚需另行說明以取得足夠的查核證據。";

    internal static string Step1NotApplicableListNote(string naReason) =>
        string.Equals(naReason, AccountComparisonProcedure.MissingTbImportReason, StringComparison.Ordinal)
            ? "#本案件未匯入試算表，無法執行試算表科目金額本期異動與會計分錄(JE)之推滾比對。"
            : "#本案件試算表欄位配對尚未確認，無法執行試算表科目金額本期異動與會計分錄(JE)之推滾比對。";
}

/// <summary>step4-1 欄值來源；只有 RawField 會讀原始 row_json，其餘皆取 target normalized row。</summary>
internal enum WorkingPaperStep41ValueSource
{
    RawField,
    VoucherNumber,
    LineItem,
    ApprovalDate,
    GlDate,
    CreatedBy,
    ApprovedBy,
    AccountNumber,
    AccountName,
    SignedAmount,
    Description,
    SourceModule,
    IsManual,
    PostingStatus,
    RdeField
}

/// <summary>
/// Finalized step4-1 actual 欄。Header 與排序由 Legacy target field facts 決定；
/// RawFieldName 只供非 canonical actual 欄回取原始值。
/// </summary>
internal sealed record WorkingPaperStep41Column(
    string Header,
    string? RawFieldName,
    LegacyFieldKind Kind,
    int? DecimalPlaces,
    WorkingPaperStep41ValueSource ValueSource,
    string? RdeFieldId = null);

/// <summary>
/// 記憶體內的工作底稿 plan。Plan 動詞先綁定 request；
/// Finalize 才依 raw planning facts 產生 ordered sheet decisions。
/// </summary>
internal sealed record WorkingPaperPlan(
    WorkingPaperRequest Request,
    IReadOnlyList<WorkingPaperSheetPlan> Sheets,
    IReadOnlyList<WorkingPaperScenarioPlan> Scenarios,
    IReadOnlyList<int> AllScenarioPositions,
    IReadOnlyList<int> RowHitScenarioPositions,
    string PrepStartDateDisplay,
    string PopulationScopeDisplay,
    FieldInfoProjection? FieldInfo,
    IReadOnlyList<WorkingPaperStep41Column> Step41Columns,
    bool IsFinalized);

/// <summary>
/// 工作底稿的 typed planning facts port。實作只讀 bounded counts；
/// writer 的 keyset rows、交易、檔案與 OpenXML 都不屬於此 port。
/// </summary>
internal interface IWorkingPaperPlanningFactsPort
{
    Task<WorkingPaperPlanningFacts> ExecuteAsync(
        WorkingPaperPlan plan,
        CancellationToken cancellationToken);
}

/// <summary>
/// 底稿寫出器的唯一入口：依已 Finalize 的計畫把整份底稿串流寫進輸出；layout、style、SAX、
/// keyset paging、cancellation 與 progress 仍由實作負責。
/// </summary>
internal interface IWorkingPaperPlanWriter
{
    Task<ExportStats> WriteAsync(
        Stream output,
        WorkingPaperContext context,
        WorkingPaperPlan plan,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress = null);
}

/// <summary>Production formal writer marker；相容測試／第三方 plan writer 不必承擔 workbook metadata。</summary>
internal interface IFormalWorkingPaperPlanWriter : IWorkingPaperPlanWriter
{
}

public static partial class JetAuditProgram
{
    private const string WorkingPaperNaText = "N/A";

    private static readonly IReadOnlyList<string> NoWorkingPaperMethodology =
        Array.AsReadOnly(Array.Empty<string>());

    private static readonly IReadOnlyList<int> NoScenarioPositions =
        Array.AsReadOnly(Array.Empty<int>());

    private static readonly IReadOnlyList<WorkingPaperStep41Column> NoStep41Columns =
        Array.AsReadOnly(Array.Empty<WorkingPaperStep41Column>());

    // 舊版 step4-1 的固定欄序：十二個標準欄，第十三是過帳碼_JE；其餘 JET 欄位照欄位序號。
    private static readonly IReadOnlyList<string> Step41PriorityFields =
        Array.AsReadOnly(new[]
        {
            JetFieldCatalog.GlVoucherNumber,
            JetFieldCatalog.GlLineItem,
            JetFieldCatalog.GlApprovalDate,
            JetFieldCatalog.GlDate,
            JetFieldCatalog.GlCreatedBy,
            JetFieldCatalog.GlApprovedBy,
            JetFieldCatalog.GlAccountNumber,
            JetFieldCatalog.GlAccountName,
            JetFieldCatalog.GlAmount,
            JetFieldCatalog.GlDescription,
            JetFieldCatalog.GlSourceModule,
            JetFieldCatalog.GlManual,
            JetFieldCatalog.GlPostingStatus
        });


    private static readonly IReadOnlyList<string> IntroMethodology = Texts(
        "此JE測試的WorkingPaper係透過JE Testing Tool產生之工作底稿，並依下列步驟分別記錄會計分錄測試" +
        "(JE Testing)所需之程序。\n(有關高風險JE測試的篩選判斷由查核團隊在該工具執行過程中完成定義。)");

    private static readonly IReadOnlyList<string> Step2Methodology = Texts(
        "測試說明：",
        "1. 依照KAEG-I [ISA | 815.13507]，JE攸關母體有納入高風險條件(HRC)的欄位即攸關資料元素(RDE)，需先確認RDE的可靠性後，方執行高風險篩選條件。\n    若為初步篩選(Screening)之預篩選程序，因屬於風險評估，則於該階段可不用確認RDE可靠性。",
        "2. 上述提到之高風險條件的RDE大多屬於非財務性質，例如篩選條件考量過帳日期、分錄摘要的關鍵字、分錄標註為人工分錄者、特定人員。",
        "3. 若高風險條件除上述非財務性質欄位外，亦有使用會計科目編號、科目名稱或金額等欄位來進行篩選，則這些納入篩選條件的欄位亦屬於RDE。",
        "4. JE的RDE可靠性確認包括確認完整性及正確性，由於完整性已於JE攸關母體完整性測試程序中執行，故此處可靠性測試係針對JE RDE的正確性進行測試。",
        "5. 依照KAEG-I [ISA | 2701.1500]，需依照屬性抽樣表格[ISA | 4164.1300]選取樣本(選樣方法可採隨機、隨意或系統抽樣)核對會計傳票附件以確認RDE的正確性。",
        "   (由於JE具有管理階層逾越控制之顯著風險，因此其固有風險為Significant，對照上述表格後的最低測試樣本量為59筆。)",
        "測試程序：(若JE高風險條件(HRC)不包含下列A~G程序提到的欄位，則該測試程序可設為N/A)",
        "- 財務類型RDE (如會計科目編號、科目名稱、借貸方代號、分錄金額)",
        "A.",
        "確認是否已於JE母體完整性測試時，完成此類RDE的測試。若無，則確認分錄金額是否與傳票附件符合，其餘則核至已核准的會計科目表。",
        "(此類型RDE通常已於JE母體完整性測試過程與TB(試算表)比對時完成可靠性測試)",
        "- 非財務類型RDE",
        "B.",
        "過帳日期/過帳時間：屬內部交易過帳者，核至經核准的內部交易日期。若屬於外部交易者，則核對相關交易憑證的日期",
        "C.",
        "傳票建立日期/分錄時間：根據案件情況及所選樣本來判斷選擇以下一項或多項程序來執行，以驗證其可靠性。",
        "D.",
        "分錄編製人員(或過帳人員)：根據案件情況及所選樣本來判斷選擇以下一項或多項程序來執行，以驗證其可靠性。",
        "E.",
        "分錄來源：核對分錄至傳票附件內容(若為人工分錄)與受查者系統畫面資訊(若為自動分錄)，以確認是否符合其所標註的分錄來源或是對於人工/自動分錄之標記。",
        "F.",
        "分錄備註/說明：選取以下一項或多項程序來執行，以驗證其可靠性。",
        "G.",
        "除上述以外之RDE(勾選以下適合程序來執行測試)",
        "註1:",
        "建議透過詢問JE資料流與流程作業，來決定JE高風險範圍條件所要篩選的日期要使用過帳日(Posting date)還是編製日/立帳日(Create date/Document date)。",
        "註2:",
        "若下列樣本未涵蓋自動分錄且自動分錄未於其他程序執行測試者，當高風險條件有篩選到自動分錄，應補執行上述對自動分錄提到的程序。",
        "註3:",
        "若有其他用在高風險條件的RDE欄位，請自行於工作表：可靠性樣本_所有欄位中複製貼上至此底稿中。");

    private static readonly IReadOnlyList<string> Step4Methodology = Texts(
        "測試目的",
        "取得查核證據，以測試會計分錄及其他調整：\ni)  不存在因舞弊所導致的重大不實表達；\nii) 有適當的支持性文件；\niii) 反映了相關的事項、情況和交易，並且\niv) 按照財務報導架構，記錄在正確的會計期間。",
        "測試程序",
        "A.核至相關傳票之複核紀錄，以確認該分錄之過帳係經適當核准。\nB.核至相關傳票附件，以確認該分錄所載內容與附件一致，分錄係記錄於正確的\n    會計期間，並確認依附件所登錄的科目係屬適當及相關。\nC.詢問負責人員編製分錄之細節，以確認該分錄編製無存在不合理之情形。",
        "決定進行測試之高風險範圍條件",
        "查核程序",
        "有無舞弊\n或不實表達\n(Yes/No)",
        // 範本 A12 整句寫著空白傳票號碼的列法，這裡和範本相同。
        "因為設定高風險範圍條件，從母體#2挑選之分錄傳票(執行重大性或其他固定金額不應作為挑選的門檻)" +
        "；空白傳票號碼的命中分錄逐筆列出，不計傳票張數，金額為該筆借方金額。");

    private const string Step5AuditCondition =
        "若查核團隊發現受查客戶在財務報表關帳後，尚有入帳之調整分錄(Post-closing entries)，" +
        "或未入帳直接對財務報表之調整(Other adjustments)，\n則可將此類調整記錄於此處，或說明無此類情形。\n";

    /// <summary>
    /// 綁定已由 Application 驗證為 current 的 validation 與 filter reference；不重查報告檔
    /// stale，也不讀資料列。
    /// </summary>
    internal static WorkingPaperPlan Plan(WorkingPaperRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scenarios);
        if (request.WorkbookMetadata is not null)
        {
            ReportWorkbookMetadataInvariant.ValidateCustomFields(
                request.WorkbookMetadata,
                request.CustomFields);
        }
        else if (request.CustomFields is { Count: > 0 })
        {
            throw new InvalidOperationException(
                "WorkingPaper custom fields require canonical workbook metadata.");
        }

        var populationScopeDisplay = GlPopulationScopeValues.DisplayName(request.PopulationScope);

        return new WorkingPaperPlan(
            request,
            Array.AsReadOnly(Array.Empty<WorkingPaperSheetPlan>()),
            Array.AsReadOnly(Array.Empty<WorkingPaperScenarioPlan>()),
            NoScenarioPositions,
            NoScenarioPositions,
            DisplayPrepStartDate(request.PrepStartDate),
            populationScopeDisplay,
            FieldInfo: null,
            Step41Columns: NoStep41Columns,
            IsFinalized: false);
    }

    /// <summary>
    /// 情境只有一組、這組只有一條預篩選條件，而且規則定義標明單獨使用時列整張命中傳票，就以整張命中傳票呈現。
    /// 目前只有舊 IDEA 的「未預期借貸組合」是這樣，標示寫在
    /// <see cref="PrescreenRuleDefinition.ListsWholeHitVouchersWhenAlone"/>。這個判斷同時供
    /// 條件篩選報告摘要的列數口徑與工作底稿的逐列標記使用；篩選結果本身仍只
    /// 記錄直接命中的收入貸方列。複合情境維持同列條件，避免擴張其他規則的結果。
    /// </summary>
    internal static bool UsesLegacyWholeVoucherRows(
        FilterScenarioSpec scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (scenario.Groups.Count != 1)
        {
            return false;
        }

        var group = scenario.Groups[0];
        if (group.Conditions.Count != 1)
        {
            return false;
        }

        var rule = group.Conditions[0];
        return rule.Type == FilterConditionType.Prescreen
            && rule.PrescreenKey is { } key
            && PrescreenRuleDefinitions.ByKey.TryGetValue(key, out var definition)
            && definition.ListsWholeHitVouchersWhenAlone;
    }

    internal static WorkingPaperScenarioTagScope ResolveWorkingPaperTagScope(
        FilterScenarioSpec scenario) =>
        UsesLegacyWholeVoucherRows(scenario)
            ? WorkingPaperScenarioTagScope.HitVoucherRows
            : WorkingPaperScenarioTagScope.DirectHitRows;

    /// <summary>
    /// writer 的工作表取捨：完整性差異為零時省略 step1-3，
    /// step1-1 固定 emit、但僅在不平傳票有列時顯示例外表；step4 使用全部所選
    /// position，step4-1 只使用 row-hit count 大於零的 position。
    /// </summary>
    internal static WorkingPaperPlan Finalize(
        WorkingPaperPlan plan,
        WorkingPaperPlanningFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(facts.ScenarioHitCounts);
        ArgumentNullException.ThrowIfNull(facts.TargetTbDefinitions);
        ArgumentNullException.ThrowIfNull(facts.TargetGlDefinitions);

        if (plan.IsFinalized)
        {
            throw new InvalidOperationException("WorkingPaperPlan 已 Finalize。");
        }

        var request = plan.Request;
        var scenarios = request.Scenarios
            .OrderBy(scenario => scenario.Position)
            .Select(scenario =>
            {
                var counts = facts.ScenarioHitCounts.GetValueOrDefault(scenario.Position);
                return new WorkingPaperScenarioPlan(
                    scenario.Position,
                    scenario.Name,
                    scenario.Rationale,
                    counts.VoucherHitCount,
                    counts.RowHitCount,
                    scenario.TagScope,
                    scenario.ConditionLogic);
            })
            .ToArray();
        var selected = scenarios.Select(scenario => scenario.Position).ToArray();
        var rowHitPositions = scenarios
            .Where(scenario => scenario.RowHitCount > 0)
            .Select(scenario => scenario.Position)
            .ToArray();
        // 逐科目比對無法執行時，只有 GL 的科目不算差異，Step1-3 不產生。
        var accountComparisonNaReason = request.CompletenessNaReason;
        var hasCompletenessDifferences = accountComparisonNaReason is null && facts.HasCompletenessDifferences;
        var naText = string.Equals(
                plan.PrepStartDateDisplay,
                WorkingPaperNaText,
                StringComparison.Ordinal)
            ? WorkingPaperNaText
            : null;
        var sheets = new[]
        {
            Sheet(
                WorkingPaperSheetCatalog.Cover,
                auditCondition: plan.PopulationScopeDisplay),
            Sheet(
                WorkingPaperSheetCatalog.Intro,
                methodology: IntroMethodology),
            accountComparisonNaReason is null
                ? Sheet(
                    WorkingPaperSheetCatalog.Step1,
                    includeExceptionTable: true,
                    auditCondition: "評估母體完整性：",
                    methodology: Texts(WorkingPaperResultTexts.Step1ListNote(hasCompletenessDifferences)),
                    conclusion: WorkingPaperResultTexts.Step1Conclusion(hasCompletenessDifferences),
                    naText: naText)
                : Sheet(
                    WorkingPaperSheetCatalog.Step1,
                    includeExceptionTable: false,
                    auditCondition: "評估母體完整性：",
                    methodology: Texts(WorkingPaperResultTexts.Step1NotApplicableListNote(accountComparisonNaReason)),
                    conclusion: WorkingPaperResultTexts.Step1NotApplicableConclusion,
                    naText: naText),
            Sheet(
                WorkingPaperSheetCatalog.Step11,
                includeExceptionTable: facts.HasUnbalancedVouchers,
                auditCondition: "評估個別傳票是否借貸不平：",
                conclusion: WorkingPaperResultTexts.Step11Conclusion(facts.HasUnbalancedVouchers),
                naText: naText),
            Sheet(
                WorkingPaperSheetCatalog.Step12,
                auditCondition: "評估是否有不適合的分錄編製人員：",
                naText: naText),
            Sheet(
                WorkingPaperSheetCatalog.Step13,
                emit: hasCompletenessDifferences,
                auditCondition: "評估於Step1完整性測試中有部分科目出現差異的原因：",
                conclusion:
                    "基於上述程序，查核團隊對出現差異之科目均已取得足夠的查核證據，已確認其原因尚屬合理或進行調節使其無差異，" +
                    "因此可確認JE測試母體之完整性。",
                naText: naText),
            Sheet(
                WorkingPaperSheetCatalog.Step2,
                methodology: Step2Methodology),
            Sheet(
                WorkingPaperSheetCatalog.Step3,
                scenarioPositions: selected,
                auditCondition: WorkingPaperResultTexts.Step3TestScope(request.PeriodStart, request.PeriodEnd),
                methodology: Step3Methodology(request)),
            Sheet(
                WorkingPaperSheetCatalog.Step4,
                scenarioPositions: selected,
                methodology: Step4Methodology),
            Sheet(WorkingPaperSheetCatalog.Step41, scenarioPositions: rowHitPositions),
            Sheet(
                WorkingPaperSheetCatalog.Step5,
                auditCondition: Step5AuditCondition),
            Sheet(WorkingPaperSheetCatalog.FieldInfo),
            Sheet(WorkingPaperSheetCatalog.CalendarInfo),
            Sheet(WorkingPaperSheetCatalog.AccountMapping)
        };

        return plan with
        {
            Sheets = Array.AsReadOnly(sheets),
            Scenarios = Array.AsReadOnly(scenarios),
            AllScenarioPositions = Array.AsReadOnly(selected),
            RowHitScenarioPositions = Array.AsReadOnly(rowHitPositions),
            FieldInfo = ProjectFieldInfo(
                facts.TargetTbDefinitions,
                facts.TargetGlDefinitions),
            Step41Columns = ProjectStep41Columns(
                facts.TargetGlDefinitions,
                request.CustomFields ?? [],
                request.WorkbookMetadata?.GlMapping.GlOptions?.RdeFields,
                request.WorkbookMetadata?.GlMapping.GlOptions?.FillDownFields ?? [],
                request.MoneyScale),
            IsFinalized = true
        };
    }

    private static WorkingPaperSheetPlan Sheet(
        string sheetName,
        bool emit = true,
        bool includeExceptionTable = false,
        IReadOnlyList<int>? scenarioPositions = null,
        string? auditCondition = null,
        IReadOnlyList<string>? methodology = null,
        string? conclusion = null,
        string? naText = null) =>
        new(
            sheetName,
            emit,
            includeExceptionTable,
            scenarioPositions ?? NoScenarioPositions,
            auditCondition,
            methodology ?? NoWorkingPaperMethodology,
            conclusion,
            naText);

    private static IReadOnlyList<string> Texts(params string[] values) =>
        Array.AsReadOnly(values);

    /// <summary>
    /// step4-1 的欄位依欄位資訊的來歷挑出 JET 欄位，不看欄名結尾。
    /// 標準欄依舊版的固定順序排在前面，其餘 JET 欄位照欄位序號；
    /// 攸關資料元素照舊只列所選情境用到的，表頭照舊版加 _JE。
    /// </summary>
    private static IReadOnlyList<WorkingPaperStep41Column> ProjectStep41Columns(
        IReadOnlyList<FieldInfo> definitions,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        IReadOnlyList<GlRdeFieldMetadata>? registryFields,
        IReadOnlyList<string> fillDownFields,
        int moneyScale)
    {
        var eligible = definitions
            .Where(definition => definition.IsJetField
                && definition.JetField is not null
                && JetFieldCatalog.FindGlFieldBySemanticIdentity(definition.JetField) is not null)
            .OrderBy(definition => definition.Ordinal)
            .ToArray();
        var duplicate = eligible
            .GroupBy(definition => definition.FieldName, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"step4-1 post-mapping 欄位名稱 '{duplicate.Key}' 重複。");
        }

        var ordered = new List<FieldInfo>(eligible.Length);
        foreach (var priority in Step41PriorityFields)
        {
            ordered.AddRange(eligible.Where(definition =>
                string.Equals(definition.JetField, priority, StringComparison.Ordinal)));
        }

        var priorityFields = Step41PriorityFields.ToHashSet(StringComparer.Ordinal);
        ordered.AddRange(eligible.Where(definition => !priorityFields.Contains(definition.JetField!)));
        var fillDown = fillDownFields.ToHashSet(StringComparer.Ordinal);
        var rdeHeaders = GlRdeOutputNames.For(customFields, registryFields);
        return Array.AsReadOnly(
            ordered.Select(definition => ToStep41Column(definition, fillDown))
                .Concat(customFields.Select((field, index) => ToStep41RdeColumn(field, rdeHeaders[index], moneyScale)))
                .ToArray());
    }

    private static WorkingPaperStep41Column ToStep41RdeColumn(
        GlRdeFieldMetadata field,
        string header,
        int moneyScale) => field.ValueType switch
        {
            RdeFieldValueTypeNames.Text => new WorkingPaperStep41Column(
                header,
                RawFieldName: null,
                LegacyFieldKind.Text,
                DecimalPlaces: null,
                WorkingPaperStep41ValueSource.RdeField,
                field.FieldId),
            RdeFieldValueTypeNames.Date => new WorkingPaperStep41Column(
                header,
                RawFieldName: null,
                LegacyFieldKind.Date,
                DecimalPlaces: null,
                WorkingPaperStep41ValueSource.RdeField,
                field.FieldId),
            RdeFieldValueTypeNames.Amount => new WorkingPaperStep41Column(
                header,
                RawFieldName: null,
                LegacyFieldKind.Number,
                DecimalPlacesForMoneyScale(moneyScale),
                WorkingPaperStep41ValueSource.RdeField,
                field.FieldId),
            _ => throw new InvalidOperationException(
                $"WorkingPaper RDE field '{field.FieldId}' has unsupported value type '{field.ValueType}'.")
        };

    private static int DecimalPlacesForMoneyScale(int moneyScale)
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
        if (remainder != 1 || decimals > WorkingPaperStylesMaximumDecimalPlaces)
        {
            throw new ArgumentOutOfRangeException(
                nameof(moneyScale),
                "WorkingPaper RDE money requires a power-of-ten MoneyScale with at most four decimals.");
        }
        return decimals;
    }

    private const int WorkingPaperStylesMaximumDecimalPlaces = 4;

    /// <summary>
    /// 欄值來源依對應的 JET 欄位決定。系統產生的欄讀整理後的值，所以同總帳日期的傳票核准日寫總帳日期的值；
    /// 配對後改名的欄照舊讀原始資料列，只有勾了往下填補的欄位讀填補後的值；
    /// 人工/自動寫「人工」「自動」，空白仍留白。
    /// </summary>
    private static WorkingPaperStep41Column ToStep41Column(
        FieldInfo definition,
        IReadOnlySet<string> fillDownFields)
    {
        var field = JetFieldCatalog.FindGlFieldBySemanticIdentity(definition.JetField!)
            ?? throw new InvalidOperationException($"step4-1 欄位 '{definition.FieldName}' 對應的 JET 欄位 '{definition.JetField}' 不在欄位目錄。");
        if (field.SemanticIdentity == JetFieldCatalog.GlManual)
        {
            return new WorkingPaperStep41Column(
                definition.FieldName,
                definition.Description ?? definition.FieldName,
                LegacyFieldKind.Text,
                DecimalPlaces: 0,
                WorkingPaperStep41ValueSource.IsManual);
        }

        var normalized = field.SemanticIdentity switch
        {
            JetFieldCatalog.GlVoucherNumber => WorkingPaperStep41ValueSource.VoucherNumber,
            JetFieldCatalog.GlLineItem => WorkingPaperStep41ValueSource.LineItem,
            JetFieldCatalog.GlApprovalDate => WorkingPaperStep41ValueSource.ApprovalDate,
            JetFieldCatalog.GlDate => WorkingPaperStep41ValueSource.GlDate,
            JetFieldCatalog.GlCreatedBy => WorkingPaperStep41ValueSource.CreatedBy,
            JetFieldCatalog.GlApprovedBy => WorkingPaperStep41ValueSource.ApprovedBy,
            JetFieldCatalog.GlAccountNumber => WorkingPaperStep41ValueSource.AccountNumber,
            JetFieldCatalog.GlAccountName => WorkingPaperStep41ValueSource.AccountName,
            JetFieldCatalog.GlDescription => WorkingPaperStep41ValueSource.Description,
            JetFieldCatalog.GlSourceModule => WorkingPaperStep41ValueSource.SourceModule,
            JetFieldCatalog.GlPostingStatus => WorkingPaperStep41ValueSource.PostingStatus,
            JetFieldCatalog.GlAmount => WorkingPaperStep41ValueSource.SignedAmount,
            _ => WorkingPaperStep41ValueSource.RawField
        };
        // 沒有記來源欄名的改名欄位讀不到原始資料列，只能讀整理後的值；投影器一定會記來源欄名，這種定義只出現在手工建的欄位事實。
        var readsNormalized = definition.Origin == FieldInfoOrigin.Generated
            || string.IsNullOrEmpty(definition.Description)
            || field.MappingSlots.Any(slot => fillDownFields.Contains(slot.Key));
        return new WorkingPaperStep41Column(
            definition.FieldName,
            definition.Description ?? definition.FieldName,
            definition.Kind,
            definition.DecimalPlaces,
            readsNormalized ? normalized : WorkingPaperStep41ValueSource.RawField);
    }

    private static IReadOnlyList<string> Step3Methodology(WorkingPaperRequest request) =>
        Texts(
            "測試目的",
            "取得查核證據，以測試會計分錄及其他調整：\ni)  不存在因舞弊所導致的重大不實表達；\nii) 有適當的支持性文件；\niii) 反映了相關的事項、情況和交易，並且\niv) 按照財務報導架構，記錄在正確的會計期間。",
            "測試範圍",
            WorkingPaperResultTexts.Step3TestScope(request.PeriodStart, request.PeriodEnd),
            "選擇該測試範圍的理由",
            WorkingPaperResultTexts.Step3ScopeRationale,
            "Step 3",
            "辨認高風險條件",
            "風險評估及查核程序：",
            "查核團隊基於下列程序來辨識JE測試之攸關母體及高風險範圍條件：\n•在風險評估與查核團隊討論及計畫討論管理階層踰越控制風險，包含分錄及其他調整\n•了解分錄及其他調整步驟\n•特別詢問處理分錄的會計人員，以及詢問管理階層或其他人員\n•了解交易模式\n•辨認舞弊風險及舞弊因子\n•蒐集查核與特別矛盾之發現，以及辨認舞弊可能已經發生情況之證據\n\n根據上述程序，查核團隊已將所辨認出高風險範圍條件記錄於下方表格。",
            "辨認出高風險範圍條件，並篩選符合條件之分錄進行測試(相關之傳票分錄明細將列在step4)");

    private static string DisplayPrepStartDate(string? prepStartDate) =>
        string.IsNullOrWhiteSpace(prepStartDate)
            ? WorkingPaperNaText
            : string.Concat(prepStartDate.Where(char.IsDigit));
}
