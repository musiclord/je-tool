using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// Working Paper planning 的 bounded Application input。來源版本必須先由既有
/// ReportExportSupport stale 機制驗證；本 request 不建立第二套失效判定。
/// </summary>
internal sealed record WorkpaperRequest(
    string ProjectId,
    string PeriodStart,
    string PeriodEnd,
    string? LastPeriodStart,
    int MoneyScale,
    string ValidationRunId,
    string ScenarioRevision,
    IReadOnlyList<WorkpaperScenarioSelection> Scenarios,
    GlPopulationScope PopulationScope,
    ReportWorkbookMetadata? WorkbookMetadata = null,
    IReadOnlyList<GlRdeFieldMetadata>? CustomFields = null);

/// <summary>
/// 工作底稿逐列標記的範圍。DirectHitRows 保留篩選結果實際命中的列；
/// HitVoucherRows 只用於舊 IDEA 明確以整張傳票呈現的情境。
/// </summary>
internal enum WorkpaperScenarioTagScope
{
    DirectHitRows,
    HitVoucherRows
}

/// <summary>Application 已驗證且選入底稿的情境 metadata；上限與順序沿用既有 action contract。</summary>
internal sealed record WorkpaperScenarioSelection(
    int Position,
    string Name,
    string Rationale,
    WorkpaperScenarioTagScope TagScope = WorkpaperScenarioTagScope.DirectHitRows,
    string? ConditionLogic = null);

/// <summary>
/// Infrastructure planning port 回傳的 bounded first-row presence facts 與情境計數。
/// 完整母體列、分頁 rows 與 OpenXML layout 不進入 AuditCore；缺少的情境計數沿用
/// 既有 GetValueOrDefault 零值語意。
/// </summary>
internal sealed record WorkpaperPlanningFacts(
    bool HasCompletenessDifferences,
    bool HasUnbalancedDocuments,
    IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)> ScenarioHitCounts,
    IReadOnlyList<LegacyFieldDefinition> TargetTbDefinitions,
    IReadOnlyList<LegacyFieldDefinition> TargetGlDefinitions,
    string? VoucherDateSourceField = null);

/// <summary>供 step3 row 與 step4／step4-1 動態欄集共用的 finalized 情境計畫。</summary>
internal sealed record WorkpaperScenarioPlan(
    int Position,
    string Name,
    string Rationale,
    long VoucherHitCount,
    long RowHitCount,
    WorkpaperScenarioTagScope TagScope,
    string? ConditionLogic = null);

/// <summary>
/// 單張正準工作表的 audit planning decision。名稱與順序仍取 Domain catalog；
/// writer 保留欄標、cell 座標、layout、style、SAX 與 continuation paging。
/// </summary>
internal sealed record WorkpaperSheetPlan(
    string SheetName,
    bool Emit,
    bool IncludeExceptionTable,
    IReadOnlyList<int> ScenarioPositions,
    string? AuditCondition,
    IReadOnlyList<string> Methodology,
    string? Conclusion,
    string? NaText);

/// <summary>
/// Step 1 與 Step 1-1 依結果二選一的結論文字。照 legacy：有差異科目或不平傳票時才改寫範本原文
/// （idea-tool.bas:10409-10411、10468、10521-10524）。範本原文就是「沒有差異」「沒有不平」的版本。
/// </summary>
internal static class WorkpaperResultTexts
{
    internal static string Step1Conclusion(bool hasCompletenessDifferences) =>
        hasCompletenessDifferences
            ? "基於上述程序，查核團隊對於JE測試母體之完整性，尚需於Step1-3說明以取得足夠的查核證據。"
            : "基於上述程序，查核團隊對於JE測試母體之完整性，已取得足夠的查核證據。";

    internal static string Step1ListNote(bool hasCompletenessDifferences) =>
        "#針對試算表科目金額本期異動與會計分錄(JE)進行推滾比對之清單列示如下："
        + (hasCompletenessDifferences
            ? "(有部分科目之差異數不為0，請於step1-3說明其理由，以確認JE母體的完整性)"
            : "(已確認差異數均為0，可確認其完整性)");

    internal static string Step11Conclusion(bool hasUnbalancedDocuments) =>
        hasUnbalancedDocuments
            ? "基於上述程序，查核團隊發現有部分傳票借貸不平，但已取得足夠的查核證據，確認其理由尚屬合理。"
            : "基於上述程序，查核團隊已取得足夠的查核證據，確認無借貸不平之情形。";

    internal const string Step11ExceptionTableTitle = "出現借貸不平之個別傳票說明：";
}

/// <summary>step4-1 欄值來源；只有 RawField 會讀原始 row_json，其餘皆取 target normalized row。</summary>
internal enum WorkpaperStep41ValueSource
{
    RawField,
    DocumentNumber,
    LineItem,
    ApprovalDate,
    PostDate,
    CreatedBy,
    ApprovedBy,
    AccountCode,
    AccountName,
    SignedAmount,
    Description,
    SourceModule,
    IsManual,
    RdeField
}

/// <summary>
/// Finalized step4-1 actual 欄。Header 與排序由 Legacy target field facts 決定；
/// RawFieldName 只供非 canonical actual 欄回取原始值。
/// </summary>
internal sealed record WorkpaperStep41Column(
    string Header,
    string? RawFieldName,
    LegacyFieldKind Kind,
    int? DecimalPlaces,
    WorkpaperStep41ValueSource ValueSource,
    string? RdeFieldId = null);

/// <summary>
/// 記憶體內的 Working Paper plan。Plan 動詞先綁定 request；
/// Finalize 才依 raw planning facts 產生 ordered sheet decisions。
/// </summary>
internal sealed record WorkpaperPlan(
    WorkpaperRequest Request,
    IReadOnlyList<WorkpaperSheetPlan> Sheets,
    IReadOnlyList<WorkpaperScenarioPlan> Scenarios,
    IReadOnlyList<int> AllScenarioPositions,
    IReadOnlyList<int> RowHitScenarioPositions,
    string LastPeriodStartDisplay,
    string PopulationScopeDisplay,
    FieldInfoProjection? FieldInfo,
    IReadOnlyList<WorkpaperStep41Column> Step41Columns,
    bool IsFinalized);

/// <summary>
/// Working Paper 的 typed planning facts port。實作只讀 bounded counts；
/// writer 的 keyset rows、交易、檔案與 OpenXML 都不屬於此 port。
/// </summary>
internal interface IWorkpaperPlanningFactsPort
{
    Task<WorkpaperPlanningFacts> ExecuteAsync(
        WorkpaperPlan plan,
        CancellationToken cancellationToken);
}

/// <summary>
/// Production writer 的 internal typed seam。既有 public <see cref="IWorkpaperWriter"/>
/// 保持相容；layout、style、SAX、keyset paging、cancellation 與 progress 仍由實作負責。
/// </summary>
internal interface IWorkpaperPlanWriter : IWorkpaperWriter
{
    Task<ExportStats> WriteAsync(
        Stream output,
        WorkpaperContext context,
        WorkpaperPlan plan,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}

/// <summary>Production formal writer marker；相容測試／第三方 plan writer 不必承擔 workbook metadata。</summary>
internal interface IFormalWorkpaperPlanWriter : IWorkpaperPlanWriter
{
}

public static partial class JetAuditProgram
{
    private const string WorkpaperNaText = "N/A";

    private static readonly IReadOnlyList<string> NoWorkpaperMethodology =
        Array.AsReadOnly(Array.Empty<string>());

    private static readonly IReadOnlyList<int> NoScenarioPositions =
        Array.AsReadOnly(Array.Empty<int>());

    private static readonly IReadOnlyList<WorkpaperStep41Column> NoStep41Columns =
        Array.AsReadOnly(Array.Empty<WorkpaperStep41Column>());

    private static readonly IReadOnlyList<string> Step41PriorityFields =
        Array.AsReadOnly(new[]
        {
            "傳票號碼_JE",
            "傳票文件項次_JE_S",
            "傳票核准日_JE",
            "總帳日期_JE",
            "傳票建立人員_JE",
            "傳票核准人員_JE",
            "會計科目編號_JE",
            "會計科目名稱_JE",
            "傳票金額_JE",
            "傳票摘要_JE",
            "分錄來源模組_JE",
            "人工傳票否_JE_S"
        });


    private static readonly IReadOnlyList<string> IntroMethodology = Texts(
        "此JE測試的WorkPaper係透過JE Testing Tool產生之工作底稿，並依下列步驟分別記錄會計分錄測試" +
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
        "因為設定高風險範圍條件，從母體#2挑選之分錄傳票(執行重大性或其他固定金額不應作為挑選的門檻)");

    private const string Step5AuditCondition =
        "若查核團隊發現受查客戶在財務報表關帳後，尚有入帳之調整分錄(Post-closing entries)，" +
        "或未入帳直接對財務報表之調整(Other adjustments)，\n則可將此類調整記錄於此處，或說明無此類情形。\n";

    /// <summary>
    /// 綁定已由 Application 驗證為 current 的 validation 與 filter reference；不重查 artifact
    /// stale，也不讀資料列。
    /// </summary>
    internal static WorkpaperPlan Plan(WorkpaperRequest request)
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

        return new WorkpaperPlan(
            request,
            Array.AsReadOnly(Array.Empty<WorkpaperSheetPlan>()),
            Array.AsReadOnly(Array.Empty<WorkpaperScenarioPlan>()),
            NoScenarioPositions,
            NoScenarioPositions,
            DisplayLastPeriodStart(request.LastPeriodStart),
            populationScopeDisplay,
            FieldInfo: null,
            Step41Columns: NoStep41Columns,
            IsFinalized: false);
    }

    /// <summary>
    /// 舊 IDEA 的「未預期借貸組合」單一條件以整張命中傳票呈現。這個判斷同時供
    /// Criteria 摘要的列數口徑與 Working Paper 的逐列標記使用；篩選結果本身仍只
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
        if (group.MatchScope != FilterGroupMatchScope.Row || group.Rules.Count != 1)
        {
            return false;
        }

        var rule = group.Rules[0];
        return rule.Type == FilterRuleType.Prescreen
            && string.Equals(
                rule.PrescreenKey,
                PrescreenRuleKeys.UnexpectedAccountPair,
                StringComparison.Ordinal);
    }

    internal static WorkpaperScenarioTagScope ResolveWorkpaperTagScope(
        FilterScenarioSpec scenario) =>
        UsesLegacyWholeVoucherRows(scenario)
            ? WorkpaperScenarioTagScope.HitVoucherRows
            : WorkpaperScenarioTagScope.DirectHitRows;

    /// <summary>
    /// 只搬移既有 writer decision：完整性差異為零時省略 step1-3，
    /// step1-1 仍固定 emit、但僅在不平傳票有列時顯示例外表；step4 使用全部所選
    /// position，step4-1 只使用 row-hit count 大於零的 position。
    /// </summary>
    internal static WorkpaperPlan Finalize(
        WorkpaperPlan plan,
        WorkpaperPlanningFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(facts.ScenarioHitCounts);
        ArgumentNullException.ThrowIfNull(facts.TargetTbDefinitions);
        ArgumentNullException.ThrowIfNull(facts.TargetGlDefinitions);

        if (plan.IsFinalized)
        {
            throw new InvalidOperationException("WorkpaperPlan 已 Finalize。");
        }

        var request = plan.Request;
        var scenarios = request.Scenarios
            .OrderBy(scenario => scenario.Position)
            .Select(scenario =>
            {
                var counts = facts.ScenarioHitCounts.GetValueOrDefault(scenario.Position);
                return new WorkpaperScenarioPlan(
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
        var naText = string.Equals(
                plan.LastPeriodStartDisplay,
                WorkpaperNaText,
                StringComparison.Ordinal)
            ? WorkpaperNaText
            : null;
        var sheets = new[]
        {
            Sheet(
                WorkpaperSheetCatalog.Cover,
                auditCondition: plan.PopulationScopeDisplay),
            Sheet(
                WorkpaperSheetCatalog.Intro,
                methodology: IntroMethodology),
            Sheet(
                WorkpaperSheetCatalog.Step1,
                auditCondition: "評估母體完整性：",
                methodology: Texts(WorkpaperResultTexts.Step1ListNote(facts.HasCompletenessDifferences)),
                conclusion: WorkpaperResultTexts.Step1Conclusion(facts.HasCompletenessDifferences),
                naText: naText),
            Sheet(
                WorkpaperSheetCatalog.Step11,
                includeExceptionTable: facts.HasUnbalancedDocuments,
                auditCondition: "評估個別傳票是否借貸不平：",
                conclusion: WorkpaperResultTexts.Step11Conclusion(facts.HasUnbalancedDocuments),
                naText: naText),
            Sheet(
                WorkpaperSheetCatalog.Step12,
                auditCondition: "評估是否有不適合的分錄編製人員：",
                naText: naText),
            Sheet(
                WorkpaperSheetCatalog.Step13,
                emit: facts.HasCompletenessDifferences,
                auditCondition: "評估於Step1完整性測試中有部分科目出現差異的原因：",
                conclusion:
                    "基於上述程序，查核團隊對出現差異之科目均已取得足夠的查核證據，已確認其原因尚屬合理或進行調節使其無差異，" +
                    "因此可確認JE測試母體之完整性。",
                naText: naText),
            Sheet(
                WorkpaperSheetCatalog.Step2,
                methodology: Step2Methodology),
            Sheet(
                WorkpaperSheetCatalog.Step3,
                scenarioPositions: selected,
                auditCondition:
                    $"查核期間內（{request.PeriodStart} ~ {request.PeriodEnd}）之會計分錄",
                methodology: Step3Methodology(request)),
            Sheet(
                WorkpaperSheetCatalog.Step4,
                scenarioPositions: selected,
                methodology: Step4Methodology),
            Sheet(WorkpaperSheetCatalog.Step41, scenarioPositions: rowHitPositions),
            Sheet(
                WorkpaperSheetCatalog.Step5,
                auditCondition: Step5AuditCondition),
            Sheet(WorkpaperSheetCatalog.FieldInfo),
            Sheet(WorkpaperSheetCatalog.CalendarInfo),
            Sheet(WorkpaperSheetCatalog.AccountMapping)
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
                facts.VoucherDateSourceField,
                request.CustomFields ?? [],
                request.MoneyScale),
            IsFinalized = true
        };
    }

    private static WorkpaperSheetPlan Sheet(
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
            methodology ?? NoWorkpaperMethodology,
            conclusion,
            naText);

    private static IReadOnlyList<string> Texts(params string[] values) =>
        Array.AsReadOnly(values);

    private static IReadOnlyList<WorkpaperStep41Column> ProjectStep41Columns(
        IReadOnlyList<LegacyFieldDefinition> definitions,
        string? voucherDateSourceField,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        int moneyScale)
    {
        var voucherCandidates = string.IsNullOrEmpty(voucherDateSourceField)
            ? []
            : definitions
                .Where(definition =>
                    string.Equals(
                        definition.FieldName,
                        voucherDateSourceField,
                        StringComparison.Ordinal)
                    && string.IsNullOrEmpty(definition.Description))
                .ToArray();
        if (voucherCandidates.Length > 1)
        {
            throw new InvalidOperationException(
                $"step4-1 voucherDate 來源欄位 '{voucherDateSourceField}' 不唯一。");
        }

        var voucherOrdinal = voucherCandidates.SingleOrDefault()?.Ordinal;
        var eligible = definitions
            .Where(definition =>
                (definition.FieldName.EndsWith("_JE", StringComparison.Ordinal)
                 || definition.FieldName.EndsWith("_JE_S", StringComparison.Ordinal))
                && definition.Ordinal != voucherOrdinal)
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

        var byName = eligible.ToDictionary(
            definition => definition.FieldName,
            StringComparer.Ordinal);
        var ordered = new List<LegacyFieldDefinition>(eligible.Length);
        foreach (var priority in Step41PriorityFields)
        {
            if (byName.TryGetValue(priority, out var definition))
            {
                ordered.Add(definition);
            }
        }

        var priorityNames = Step41PriorityFields.ToHashSet(StringComparer.Ordinal);
        ordered.AddRange(eligible.Where(definition => !priorityNames.Contains(definition.FieldName)));
        return Array.AsReadOnly(
            ordered.Select(ToStep41Column)
                .Concat(customFields.Select(field => ToStep41RdeColumn(field, moneyScale)))
                .ToArray());
    }

    private static WorkpaperStep41Column ToStep41RdeColumn(
        GlRdeFieldMetadata field,
        int moneyScale) => field.ValueType switch
        {
            RdeFieldValueTypeNames.Text => new WorkpaperStep41Column(
                field.Label,
                RawFieldName: null,
                LegacyFieldKind.Text,
                DecimalPlaces: null,
                WorkpaperStep41ValueSource.RdeField,
                field.FieldId),
            RdeFieldValueTypeNames.Date => new WorkpaperStep41Column(
                field.Label,
                RawFieldName: null,
                LegacyFieldKind.Date,
                DecimalPlaces: null,
                WorkpaperStep41ValueSource.RdeField,
                field.FieldId),
            RdeFieldValueTypeNames.Money => new WorkpaperStep41Column(
                field.Label,
                RawFieldName: null,
                LegacyFieldKind.Number,
                DecimalPlacesForMoneyScale(moneyScale),
                WorkpaperStep41ValueSource.RdeField,
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
        if (remainder != 1 || decimals > WorkpaperStylesMaximumDecimalPlaces)
        {
            throw new ArgumentOutOfRangeException(
                nameof(moneyScale),
                "WorkingPaper RDE money requires a power-of-ten MoneyScale with at most four decimals.");
        }
        return decimals;
    }

    private const int WorkpaperStylesMaximumDecimalPlaces = 4;

    private static WorkpaperStep41Column ToStep41Column(LegacyFieldDefinition definition)
    {
        if (string.Equals(
                definition.FieldName,
                "人工傳票否_JE_S",
                StringComparison.Ordinal))
        {
            // actual target 已正規化為 0/1；即使讀到舊版或受損的 source-preserving
            // metadata，也不得回讀 raw y/yes/no 或把旗標輸出成文字。
            return new WorkpaperStep41Column(
                definition.FieldName,
                definition.Description ?? definition.FieldName,
                LegacyFieldKind.Number,
                DecimalPlaces: 0,
                WorkpaperStep41ValueSource.IsManual);
        }

        var source = definition.FieldName switch
        {
            "傳票金額_JE" => WorkpaperStep41ValueSource.SignedAmount,
            _ when !string.IsNullOrEmpty(definition.Description) =>
                WorkpaperStep41ValueSource.RawField,
            "傳票號碼_JE" => WorkpaperStep41ValueSource.DocumentNumber,
            "傳票文件項次_JE_S" => WorkpaperStep41ValueSource.LineItem,
            "傳票核准日_JE" => WorkpaperStep41ValueSource.ApprovalDate,
            "總帳日期_JE" => WorkpaperStep41ValueSource.PostDate,
            "傳票建立人員_JE" => WorkpaperStep41ValueSource.CreatedBy,
            "傳票核准人員_JE" => WorkpaperStep41ValueSource.ApprovedBy,
            "會計科目編號_JE" => WorkpaperStep41ValueSource.AccountCode,
            "會計科目名稱_JE" => WorkpaperStep41ValueSource.AccountName,
            "傳票摘要_JE" => WorkpaperStep41ValueSource.Description,
            "分錄來源模組_JE" => WorkpaperStep41ValueSource.SourceModule,
            _ => WorkpaperStep41ValueSource.RawField
        };

        return new WorkpaperStep41Column(
            definition.FieldName,
            definition.Description ?? definition.FieldName,
            definition.Kind,
            definition.DecimalPlaces,
            source);
    }

    private static IReadOnlyList<string> Step3Methodology(WorkpaperRequest request) =>
        Texts(
            "測試目的",
            "取得查核證據，以測試會計分錄及其他調整：\ni)  不存在因舞弊所導致的重大不實表達；\nii) 有適當的支持性文件；\niii) 反映了相關的事項、情況和交易，並且\niv) 按照財務報導架構，記錄在正確的會計期間。",
            "測試範圍",
            $"查核期間內（{request.PeriodStart} ~ {request.PeriodEnd}）之會計分錄",
            "選擇該測試範圍的理由",
            "本次高風險條件以專案查核期間內的會計分錄為母體；查核期間外與無有效總帳入帳日之列不納入本版情境命中與矩陣。",
            "Step 3",
            "辨認高風險條件",
            "風險評估及查核程序：",
            "查核團隊基於下列程序來辨識JE測試之攸關母體及高風險範圍條件：\n•在風險評估與查核團隊討論及計畫討論管理階層踰越控制風險，包含分錄及其他調整\n•了解分錄及其他調整步驟\n•特別詢問處理分錄的會計人員，以及詢問管理階層或其他人員\n•了解交易模式\n•辨認舞弊風險及舞弊因子\n•蒐集查核與特別矛盾之發現，以及辨認舞弊可能已經發生情況之證據\n\n根據上述程序，查核團隊已將所辨認出高風險範圍條件記錄於下方表格。",
            "辨認出高風險範圍條件，並篩選符合條件之分錄進行測試(相關之傳票分錄明細將列在step4)");

    private static string DisplayLastPeriodStart(string? lastPeriodStart) =>
        string.IsNullOrWhiteSpace(lastPeriodStart)
            ? WorkpaperNaText
            : string.Concat(lastPeriodStart.Where(char.IsDigit));
}
