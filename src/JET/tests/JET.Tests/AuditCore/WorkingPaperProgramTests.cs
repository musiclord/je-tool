using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class WorkingPaperProgramTests
{
    [Fact]
    public void Plan_BindsCurrentSourceReferences()
    {
        var request = Request();

        var plan = JetAuditProgram.Plan(request);

        Assert.Same(request, plan.Request);
        Assert.False(plan.IsFinalized);
        Assert.Empty(plan.Sheets);
        Assert.NotNull(typeof(WorkingPaperPlan).GetProperty("FieldInfo"));
    }

    [Fact]
    public void Finalize_NoCompletenessDifference_OmitsOnlyStep13()
    {
        var finalized = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request(prepStartDate: null)),
            Facts(completenessDiffs: 0, unbalancedVouchers: 0));

        Assert.True(finalized.IsFinalized);
        Assert.Equal(WorkingPaperSheetCatalog.All, finalized.Sheets.Select(sheet => sheet.SheetName));
        Assert.False(Sheet(finalized, WorkingPaperSheetCatalog.Step13).Emit);
        Assert.DoesNotContain(
            finalized.Sheets,
            sheet => string.Equals(
                sheet.SheetName,
                "step1-3-1完整性差異調節",
                StringComparison.Ordinal));
        Assert.True(Sheet(finalized, WorkingPaperSheetCatalog.Step11).Emit);
        Assert.False(Sheet(finalized, WorkingPaperSheetCatalog.Step11).IncludeExceptionTable);
        Assert.Equal(
            WorkingPaperSheetCatalog.All.Count - 1,
            finalized.Sheets.Count(sheet => sheet.Emit));
        Assert.Equal("N/A", Sheet(finalized, WorkingPaperSheetCatalog.Step1).NaText);
        Assert.Equal("N/A", Sheet(finalized, WorkingPaperSheetCatalog.Step11).NaText);
        Assert.Equal("N/A", Sheet(finalized, WorkingPaperSheetCatalog.Step12).NaText);
        Assert.Equal("N/A", Sheet(finalized, WorkingPaperSheetCatalog.Step13).NaText);

        // 沒有差異科目時，legacy 不改寫範本原文（idea-tool.bas:10409-10415）。
        Assert.Equal(
            "基於上述程序，查核團隊對於JE測試母體之完整性，已取得足夠的查核證據。",
            Sheet(finalized, WorkingPaperSheetCatalog.Step1).Conclusion);
        Assert.Equal(
            "#針對試算表科目金額本期異動與會計分錄(JE)進行推滾比對之清單列示如下：(已確認差異數均為0，可確認其完整性)",
            Assert.Single(Sheet(finalized, WorkingPaperSheetCatalog.Step1).Methodology));
        Assert.Equal(
            "基於上述程序，查核團隊已取得足夠的查核證據，確認無借貸不平之情形。",
            Sheet(finalized, WorkingPaperSheetCatalog.Step11).Conclusion);
    }

    [Fact]
    public void Finalize_WithCompletenessAndUnbalancedRows_EmitsFullCatalogAndOptionalTable()
    {
        var finalized = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request()),
            Facts(completenessDiffs: 1, unbalancedVouchers: 1));

        Assert.All(finalized.Sheets, sheet => Assert.True(sheet.Emit));
        Assert.True(Sheet(finalized, WorkingPaperSheetCatalog.Step11).IncludeExceptionTable);
        Assert.Null(Sheet(finalized, WorkingPaperSheetCatalog.Step1).NaText);
        Assert.Equal("20240101", finalized.PrepStartDateDisplay);
        Assert.Equal("查核期間", finalized.PopulationScopeDisplay);
        Assert.Equal(
            "基於上述程序，查核團隊對於JE測試母體之完整性，尚需於Step1-3說明以取得足夠的查核證據。",
            Sheet(finalized, WorkingPaperSheetCatalog.Step1).Conclusion);
        Assert.Equal(
            "#針對試算表科目金額本期異動與會計分錄(JE)進行推滾比對之清單列示如下：(有部分科目之差異數不為0，請於step1-3說明其理由，以確認JE母體的完整性)",
            Assert.Single(Sheet(finalized, WorkingPaperSheetCatalog.Step1).Methodology));
        // 裁定 2026-10-02-08：照 legacy 依結果寫結論。原本預期「確認無借貸不平之情形」，但本案例有不平傳票；
        // legacy 在有不平傳票時改寫這句（idea-tool.bas:10468、idea-script.bas:9089）。
        Assert.Equal(
            "基於上述程序，查核團隊發現有部分傳票借貸不平，但已取得足夠的查核證據，確認其理由尚屬合理。",
            Sheet(finalized, WorkingPaperSheetCatalog.Step11).Conclusion);
        Assert.Equal(
            "基於上述程序，查核團隊對出現差異之科目均已取得足夠的查核證據，已確認其原因尚屬合理或進行調節使其無差異，" +
            "因此可確認JE測試母體之完整性。",
            Sheet(finalized, WorkingPaperSheetCatalog.Step13).Conclusion);
    }

    [Fact]
    public void Finalize_DynamicScenarioColumns_MatchExistingWriterRules()
    {
        var finalized = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request(scenarioPositions: [3, 1, 2])),
            Facts(
                scenarioHits: new Dictionary<int, (long VoucherHitCount, long RowHitCount)>
                {
                    [1] = (2, 1),
                    [2] = (3, 0)
                    // Existing writer treats a missing selected count as zero.
                }));

        Assert.Equal(
            new[] { 1, 2, 3 },
            Sheet(finalized, WorkingPaperSheetCatalog.Step3).ScenarioPositions);
        Assert.Equal(
            new[] { 1, 2, 3 },
            Sheet(finalized, WorkingPaperSheetCatalog.Step4).ScenarioPositions);
        Assert.Equal(
            new[] { 1 },
            Sheet(finalized, WorkingPaperSheetCatalog.Step41).ScenarioPositions);
        Assert.Equal(new[] { 1, 2, 3 }, finalized.AllScenarioPositions);
        Assert.Equal(new[] { 1 }, finalized.RowHitScenarioPositions);
        Assert.Collection(
            finalized.Scenarios,
            scenario =>
            {
                Assert.Equal(1, scenario.Position);
                Assert.Equal("情境 1", scenario.Name);
                Assert.Equal("理由 1", scenario.Rationale);
                Assert.Null(scenario.ConditionLogic);
                Assert.Equal(2, scenario.VoucherHitCount);
                Assert.Equal(1, scenario.RowHitCount);
            },
            scenario =>
            {
                Assert.Equal(2, scenario.Position);
                Assert.Equal(3, scenario.VoucherHitCount);
                Assert.Equal(0, scenario.RowHitCount);
            },
            scenario =>
            {
                Assert.Equal(3, scenario.Position);
                Assert.Equal(0, scenario.VoucherHitCount);
                Assert.Equal(0, scenario.RowHitCount);
            });
    }

    [Fact]
    public void ResolveWorkingPaperTagScope_ExpandsOnlyStandaloneUnexpectedAccountPair()
    {
        var unexpectedPair = Scenario(
            PrescreenRule(PrescreenRuleKeys.UnexpectedAccountPair));
        var blankDescription = Scenario(
            PrescreenRule(PrescreenRuleKeys.BlankDescription));
        var combined = Scenario(
            PrescreenRule(PrescreenRuleKeys.UnexpectedAccountPair),
            PrescreenRule(PrescreenRuleKeys.BlankDescription));

        Assert.Equal(
            WorkingPaperScenarioTagScope.HitVoucherRows,
            JetAuditProgram.ResolveWorkingPaperTagScope(unexpectedPair));
        Assert.True(JetAuditProgram.UsesLegacyWholeVoucherRows(unexpectedPair));
        Assert.Equal(
            WorkingPaperScenarioTagScope.DirectHitRows,
            JetAuditProgram.ResolveWorkingPaperTagScope(blankDescription));
        Assert.False(JetAuditProgram.UsesLegacyWholeVoucherRows(blankDescription));
        Assert.Equal(
            WorkingPaperScenarioTagScope.DirectHitRows,
            JetAuditProgram.ResolveWorkingPaperTagScope(combined));
        Assert.False(JetAuditProgram.UsesLegacyWholeVoucherRows(combined));
    }

    [Fact]
    public void Finalize_CarriesScenarioTagScopeIntoFinalPlan()
    {
        var request = Request() with
        {
            Scenarios =
            [
                new WorkingPaperScenarioSelection(
                    1,
                    "情境 1",
                    "理由 1",
                    WorkingPaperScenarioTagScope.HitVoucherRows)
            ]
        };

        var finalized = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(request),
            Facts(
                scenarioHits: new Dictionary<int, (long VoucherHitCount, long RowHitCount)>
                {
                    [1] = (1, 1)
                }));

        Assert.Equal(
            WorkingPaperScenarioTagScope.HitVoucherRows,
            Assert.Single(finalized.Scenarios).TagScope);
    }

    [Fact]
    public void Finalize_Step41Columns_UsesLegacyPriorityThenRemainingOrdinal()
    {
        // 裁定 2026-10-08-03 移除傳票日期與回溯過帳：step4-1 不再有傳票日期來源欄的排除，原本 ordinal 3 的 VOUCHER_DATE_JE 隨之拿掉。
        var definitions = new[]
        {
            Field(1, "ROW_TOKEN_JE"),
            Field(2, "會計科目名稱_JE"),
            Field(4, "傳票金額_JE", LegacyFieldKind.Number, description: "system generated"),
            Field(5, "傳票號碼_JE", description: "Document"),
            Field(6, "CUSTOM_FLAG_JE_S"),
            Field(7, "總帳日期_JE"),
            Field(8, "IGNORED_COLUMN"),
            Field(9, "傳票文件項次_JE_S", LegacyFieldKind.Number),
            Field(
                10,
                "人工傳票否_JE_S",
                LegacyFieldKind.Text,
                description: "ManualFlag")
        };

        var finalized = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request()),
            Facts(targetGlDefinitions: definitions));

        Assert.Collection(
            finalized.Step41Columns,
            column =>
            {
                Assert.Equal("傳票號碼_JE", column.Header);
                Assert.Equal(WorkingPaperStep41ValueSource.RawField, column.ValueSource);
                Assert.Equal("Document", column.RawFieldName);
            },
            column =>
            {
                Assert.Equal("傳票文件項次_JE_S", column.Header);
                Assert.Equal(WorkingPaperStep41ValueSource.LineItem, column.ValueSource);
            },
            column =>
            {
                Assert.Equal("總帳日期_JE", column.Header);
                Assert.Equal(WorkingPaperStep41ValueSource.GlDate, column.ValueSource);
            },
            column =>
            {
                Assert.Equal("會計科目名稱_JE", column.Header);
                Assert.Equal(WorkingPaperStep41ValueSource.AccountName, column.ValueSource);
            },
            column =>
            {
                Assert.Equal("傳票金額_JE", column.Header);
                Assert.Equal(WorkingPaperStep41ValueSource.SignedAmount, column.ValueSource);
            },
            column =>
            {
                Assert.Equal("人工傳票否_JE_S", column.Header);
                Assert.Equal(WorkingPaperStep41ValueSource.IsManual, column.ValueSource);
                Assert.Equal(LegacyFieldKind.Text, column.Kind);
                Assert.Equal(0, column.DecimalPlaces);
            },
            column =>
            {
                Assert.Equal("ROW_TOKEN_JE", column.Header);
                Assert.Equal(WorkingPaperStep41ValueSource.RawField, column.ValueSource);
                Assert.Equal("ROW_TOKEN_JE", column.RawFieldName);
            },
            column =>
            {
                Assert.Equal("CUSTOM_FLAG_JE_S", column.Header);
                Assert.Equal(WorkingPaperStep41ValueSource.RawField, column.ValueSource);
            });
    }

    [Fact]
    public void Finalize_Step41Columns_DuplicatePostMappingHeader_FailsClosed()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            JetAuditProgram.Finalize(
                JetAuditProgram.Plan(Request()),
                Facts(
                    targetGlDefinitions:
                    [
                        Field(1, "DUPLICATE_JE"),
                        Field(2, "DUPLICATE_JE", description: "SourceB")
                    ])));

        Assert.Contains("DUPLICATE_JE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Finalize_CarriesExistingMethodologyWithoutChangingVisibleText()
    {
        var finalized = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request()),
            Facts(completenessDiffs: 1));

        Assert.Equal(
            "此JE測試的WorkingPaper係透過JE Testing Tool產生之工作底稿，並依下列步驟分別記錄會計分錄測試" +
            "(JE Testing)所需之程序。\n(有關高風險JE測試的篩選判斷由查核團隊在該工具執行過程中完成定義。)",
            Assert.Single(Sheet(finalized, WorkingPaperSheetCatalog.Intro).Methodology));
        Assert.Equal(
            "測試說明：",
            Sheet(finalized, WorkingPaperSheetCatalog.Step2).Methodology[0]);
        Assert.Equal(
            "查核期間內（2025-01-01 ~ 2025-12-31）之會計分錄",
            Sheet(finalized, WorkingPaperSheetCatalog.Step3).AuditCondition);
        // 裁定 2026-10-19-17 刪掉方法說明裡的「專案」，畫面與底稿統一用「案件」或直接寫查核期間。
        // 首次失敗：20261019-122628964-b12373a9d2f2420182c3d05d5982134a；仍逐字比對整句。
        // 裁定 2026-10-30-34 確認的對照表改寫整句：「矩陣」改稱高風險條件彙總，「無有效總帳日期之列」改用名詞表的未納入原因「總帳日期空白」。
        // 第一次失敗：Focused 收據 20261031-101615482-c77433faf82d4c4f86b5fcc87daf2e32。
        Assert.Contains(
            "本次高風險條件以查核期間內的會計分錄為母體；查核期間外與總帳日期空白的分錄，不列入本版各情境的結果與彙總。",
            Sheet(finalized, WorkingPaperSheetCatalog.Step3).Methodology);
        Assert.Equal(
            "因為設定高風險範圍條件，從母體#2挑選之分錄傳票(執行重大性或其他固定金額不應作為挑選的門檻)",
            Sheet(finalized, WorkingPaperSheetCatalog.Step4).Methodology[^1]);
        Assert.Equal(
            "若查核團隊發現受查客戶在財務報表關帳後，尚有入帳之調整分錄(Post-closing entries)，" +
            "或未入帳直接對財務報表之調整(Other adjustments)，\n則可將此類調整記錄於此處，或說明無此類情形。\n",
            Sheet(finalized, WorkingPaperSheetCatalog.Step5).AuditCondition);
    }

    private static WorkingPaperRequest Request(
        string? prepStartDate = "2024-01-01",
        IReadOnlyList<int>? scenarioPositions = null) =>
        new(
            ProjectId: "project",
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            PrepStartDate: prepStartDate,
            MoneyScale: 10_000,
            ValidationResultId: "validation-run",
            ScenarioRevision: "revision",
            Scenarios: (scenarioPositions ?? [1, 2, 3])
                .Select(position => new WorkingPaperScenarioSelection(
                    position,
                    $"情境 {position}",
                    $"理由 {position}"))
                .ToArray(),
            PopulationScope: GlPopulationScope.AuditPeriod);

    private static WorkingPaperPlanningFacts Facts(
        long completenessDiffs = 0,
        long unbalancedVouchers = 0,
        IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)>? scenarioHits = null,
        IReadOnlyList<FieldInfo>? targetGlDefinitions = null) =>
        new(
            completenessDiffs > 0,
            unbalancedVouchers > 0,
            scenarioHits
                ?? new Dictionary<int, (long VoucherHitCount, long RowHitCount)>(),
            Array.Empty<FieldInfo>(),
            targetGlDefinitions ?? Array.Empty<FieldInfo>());

    private static FieldInfo Field(
        int ordinal,
        string name,
        LegacyFieldKind kind = LegacyFieldKind.Text,
        string? description = null) =>
        new(
            ordinal,
            name,
            description,
            kind,
            kind == LegacyFieldKind.Text ? 128 : null,
            kind == LegacyFieldKind.Number ? 4 : null);

    private static FilterScenarioSpec Scenario(params FilterConditionSpec[] rules) =>
        new(
            "情境",
            "理由",
            [new FilterGroupSpec(FilterJoin.And, rules)]);

    private static FilterConditionSpec PrescreenRule(string key) =>
        new(
            FilterJoin.And,
            FilterConditionType.Prescreen,
            key,
            Field: null,
            Keywords: [],
            TextMatchMode.Contains,
            FromDate: null,
            ToDate: null,
            FromAmountScaled: null,
            ToAmountScaled: null,
            DrCr: null,
            IsManual: null);

    private static WorkingPaperSheetPlan Sheet(WorkingPaperPlan plan, string sheetName) =>
        plan.Sheets.Single(sheet =>
            string.Equals(sheet.SheetName, sheetName, StringComparison.Ordinal));
}
