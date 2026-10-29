using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class FilterScenarioValidatorTests
{
    /// <summary>一般前置：有期末財報準備日、未匯入科目配對、未匯入授權編製人員清單。</summary>
    // 預篩選收斂計畫階段四拿掉四個欄位旗標，改由規則需求表讀可用欄位清單（2026-10-23）；不帶清單就不檢查欄位，
    // 等同原本四個旗標都是 true。第一次失敗：收據 20261023-031856475-04f811a69e3346428de91091f0d02655（編譯失敗）。
    private static readonly FilterValidationContext Ready =
        new(HasLastPeriodStart: true, HasAccountMapping: false, HasAuthorizedPreparers: false);

    /// <summary>全部 GL 欄位都能用，只扣掉指定的欄位。</summary>
    private static string[] GlFieldsWithout(params string[] missing) => JetFieldCatalog.GlFields
        .Select(static field => field.SemanticIdentity)
        .Where(field => !missing.Contains(field, StringComparer.Ordinal))
        .ToArray();

    private static FilterRuleSpec TextRule(string field = "description", string keyword = "調整") =>
        new(FilterJoin.And, FilterRuleType.Text, null, field, [keyword], TextMatchMode.Contains,
            null, null, null, null, null, null);

    private static FilterRuleSpec PrescreenRule(string key) =>
        new(FilterJoin.And, FilterRuleType.Prescreen, key, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null);

    private static FilterScenarioSpec Scenario(
        string name = "高風險摘要",
        string rationale = "鎖定異常摘要的分錄",
        params FilterRuleSpec[] rules)
    {
        var effective = rules.Length > 0 ? rules : [TextRule()];
        return new FilterScenarioSpec(name, rationale, [new FilterGroupSpec(FilterJoin.And, effective)]);
    }

    [Fact]
    public void Validate_WellFormedScenario_ReturnsNoErrors()
    {
        var scenario = new FilterScenarioSpec(
            "示範情境",
            "涵蓋所有規則型別的合法組合",
            [
                new FilterGroupSpec(FilterJoin.And,
                [
                    PrescreenRule(PrescreenRuleKeys.SuspiciousKeywords),
                    PrescreenRule(PrescreenRuleKeys.HolidayPosting) with { Join = FilterJoin.Or }
                ]),
                new FilterGroupSpec(FilterJoin.And,
                [
                    new FilterRuleSpec(FilterJoin.And, FilterRuleType.NumRange, null, "amount", [],
                        TextMatchMode.Contains, null, null, 100_000_000L, null, null, null),
                    new FilterRuleSpec(FilterJoin.And, FilterRuleType.DateRange, null, "postDate", [],
                        TextMatchMode.Contains, "2025-01-01", "2025-12-31", null, null, null, null),
                    new FilterRuleSpec(FilterJoin.And, FilterRuleType.DrCrOnly, null, null, [],
                        TextMatchMode.Contains, null, null, null, null, "debit", null),
                    new FilterRuleSpec(FilterJoin.And, FilterRuleType.ManualAuto, null, null, [],
                        TextMatchMode.Contains, null, null, null, null, null, true)
                ])
            ]);

        Assert.Empty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Validate_EmptyName_ReturnsError()
    {
        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(name: "  "), Ready));
    }

    [Fact]
    public void Validate_EmptyRationale_ReturnsError()
    {
        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rationale: ""), Ready));
    }

    [Fact]
    public void Validate_NoGroups_ReturnsError()
    {
        var scenario = new FilterScenarioSpec("情境", "動機", []);

        Assert.NotEmpty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Validate_GroupWithoutRules_ReturnsError()
    {
        var scenario = new FilterScenarioSpec("情境", "動機", [new FilterGroupSpec(FilterJoin.And, [])]);

        Assert.NotEmpty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    /* ---- join 結合方式 fail-loud（manifest 結合律段，2026-07-06 收緊）------ */

    [Fact]
    public void Validate_GroupUnknownJoin_ReturnsError()
    {
        // 解析層對未知 join 只標記不擲錯（materialize 重解析路徑須容忍），fail-loud 歸驗證層。
        // 訊息須指明位置、欄位（join）與收到的值，前端回歸才有可辨識的線索。
        var scenario = new FilterScenarioSpec(
            "情境", "動機", [new FilterGroupSpec(FilterJoin.And, [TextRule()], UnknownJoin: "XOR")]);

        var errors = FilterScenarioValidator.Validate(scenario, Ready);

        // 2026-10-04 第 7 批 L72：只改位置說法，保留未知 join 與完整訊息斷言。
        // 第一次失敗：20261004-085118314-9c43f4a2a69c4f718bab4f9f3d6aaa11。
        // 使用者 2026-10-13 勾選頁確認（文字批次 C）：只有存檔損毀或程式送錯才會出現的錯誤，畫面只寫通用句，原句與英文代碼改記支援日誌；
        // 改鎖通用句與程式寫死的原因代號，檢查仍逐項區分。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
        Assert.Contains("第 1 組：此條件內容有誤，無法儲存。請刪除此條件後重新設定。", errors);
        Assert.Contains(new FilterScenarioCorruptReason(1, null, "group_join_invalid"), FilterScenarioCorruption.ReasonsOf(errors));
        Assert.DoesNotContain(errors, e => e.Contains("XOR", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RuleUnknownJoin_ReturnsError()
    {
        var rule = TextRule() with { UnknownJoin = "ANDD" };

        var errors = FilterScenarioValidator.Validate(Scenario(rules: rule), Ready);

        // 2026-10-04 第 7 批 L72：完整訊息仍須指出第幾組、第幾條與原輸入。
        // 第一次失敗：20261004-085118314-9c43f4a2a69c4f718bab4f9f3d6aaa11。
        // 使用者 2026-10-13 勾選頁確認（文字批次 C）：只有存檔損毀或程式送錯才會出現的錯誤，畫面只寫通用句，原句與英文代碼改記支援日誌；
        // 改鎖通用句與程式寫死的原因代號，檢查仍逐項區分。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
        Assert.Contains("第 1 組第 1 條：此條件內容有誤，無法儲存。請刪除此條件後重新設定。", errors);
        Assert.Contains(new FilterScenarioCorruptReason(1, 1, "rule_join_invalid"), FilterScenarioCorruption.ReasonsOf(errors));
        Assert.DoesNotContain(errors, e => e.Contains("ANDD", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnknownJoin_MergesWithOtherErrors()
    {
        // 合併列出：group 層與 rule 層的未知 join 各自成錯，且不吞掉名稱必填錯誤。
        var scenario = new FilterScenarioSpec(
            "", "動機",
            [new FilterGroupSpec(FilterJoin.And, [TextRule() with { UnknownJoin = "聯集" }], UnknownJoin: "XOR")]);

        var errors = FilterScenarioValidator.Validate(scenario, Ready);

        Assert.Contains(NameRequiredError, errors);
        // 使用者 2026-10-13 勾選頁確認（文字批次 C）：只有存檔損毀或程式送錯才會出現的錯誤，畫面只寫通用句，原句與英文代碼改記支援日誌；
        // 改鎖通用句與程式寫死的原因代號，檢查仍逐項區分。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
        Assert.Contains("第 1 組：此條件內容有誤，無法儲存。請刪除此條件後重新設定。", errors);
        Assert.Contains("第 1 組第 1 條：此條件內容有誤，無法儲存。請刪除此條件後重新設定。", errors);
        Assert.Contains(new FilterScenarioCorruptReason(1, null, "group_join_invalid"), FilterScenarioCorruption.ReasonsOf(errors));
        Assert.Contains(new FilterScenarioCorruptReason(1, 1, "rule_join_invalid"), FilterScenarioCorruption.ReasonsOf(errors));
    }

    [Fact]
    public void Validate_MixedKnownJoins_RemainValid()
    {
        // 決策表的合法混合代表：組內 AND→OR，組間 OR→AND；第三輪只文件化，不硬化或正規化。
        var scenario = new FilterScenarioSpec(
            "混合 join",
            "保留逐邊左折疊相容性",
            [
                new FilterGroupSpec(FilterJoin.And,
                [
                    TextRule("description", "甲"),
                    TextRule("description", "乙") with { Join = FilterJoin.Or }
                ]),
                new FilterGroupSpec(FilterJoin.Or, [TextRule("description", "丙")]),
                new FilterGroupSpec(FilterJoin.And, [TextRule("description", "丁")])
            ]);

        Assert.Empty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Validate_UnknownTextField_ReturnsError()
    {
        // 注入形字串必須被白名單擋下，不能流到 SQL 識別字位置。
        var scenario = Scenario(rules: TextRule(field: "document_number; DROP TABLE target_gl_entry"));

        Assert.NotEmpty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Validate_TextRuleWithoutKeywords_ReturnsError()
    {
        var rule = TextRule() with { Keywords = [] };

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    [Fact]
    public void Validate_SummaryPrescreenKey_ReturnsError()
    {
        // creatorSummary 是彙總規則（非 row tag），不可作列述詞。
        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: PrescreenRule("creatorSummary")), Ready));
    }

    [Theory]
    [InlineData("r1")]
    [InlineData("r2")]
    [InlineData("r4")]
    [InlineData("r7post")]
    [InlineData("r7doc")]
    [InlineData("r8post")]
    [InlineData("r8doc")]
    [InlineData("descNull")]
    public void Validate_RetiredCodeKey_ReturnsError(string retiredKey)
    {
        // 2026-06-11 全面改名後，舊代號鍵不再是合法 prescreenKey（遷移已翻譯既有情境）。
        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: PrescreenRule(retiredKey)), Ready));
    }

    [Fact]
    public void Validate_NumRangeWithoutBounds_ReturnsError()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.NumRange, null, "amount", [],
            TextMatchMode.Contains, null, null, null, null, null, null);

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    [Fact]
    public void Validate_NumRangeOnTextField_ReturnsError()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.NumRange, null, "description", [],
            TextMatchMode.Contains, null, null, 1L, null, null, null);

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    [Fact]
    public void Validate_DateRangeWithMalformedDate_ReturnsError()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.DateRange, null, "postDate", [],
            TextMatchMode.Contains, "2025/01/01", null, null, null, null, null);

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    [Fact]
    public void Validate_PostPeriodApprovalWithoutLastPeriodStart_ReturnsError()
    {
        var scenario = Scenario(rules: PrescreenRule(PrescreenRuleKeys.PostPeriodApproval));

        Assert.NotEmpty(FilterScenarioValidator.Validate(
            scenario, Ready with { HasLastPeriodStart = false }));
    }

    [Fact]
    public void Validate_PostPeriodApprovalWithLastPeriodStart_IsValid()
    {
        var scenario = Scenario(rules: PrescreenRule(PrescreenRuleKeys.PostPeriodApproval));

        Assert.Empty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Validate_UnexpectedAccountPairWithoutAccountMapping_ReturnsError()
    {
        var scenario = Scenario(rules: PrescreenRule(PrescreenRuleKeys.UnexpectedAccountPair));

        Assert.NotEmpty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Validate_UnexpectedAccountPairWithAccountMapping_IsValid()
    {
        var scenario = Scenario(rules: PrescreenRule(PrescreenRuleKeys.UnexpectedAccountPair));

        // 2026-10-04 第 7 批 R7：有效 fixture 須有收入及至少一種對方分類，沿第四步定義。
        // 第一次失敗：20261004-085118314-9c43f4a2a69c4f718bab4f9f3d6aaa11；Assert.Empty 目的不變。
        Assert.Empty(FilterScenarioValidator.Validate(
            scenario, Ready with { HasAccountMapping = true, HasRevenueCategory = true, HasCounterpartCategory = true }));
    }

    [Fact]
    public void Validate_NonAuthorizedPreparerWithoutAuthorizedList_ReturnsError()
    {
        // 非授權編製人員閘控（鏡射 unexpectedAccountPair）：授權清單未匯入 → 空名單會反轉 NOT IN 述詞，必須擋。
        var scenario = Scenario(rules: PrescreenRule(PrescreenRuleKeys.NonAuthorizedPreparer));

        Assert.NotEmpty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Validate_NonAuthorizedPreparerWithAuthorizedList_IsValid()
    {
        var scenario = Scenario(rules: PrescreenRule(PrescreenRuleKeys.NonAuthorizedPreparer));

        Assert.Empty(FilterScenarioValidator.Validate(
            scenario, Ready with { HasAuthorizedPreparers = true }));
    }

    [Fact]
    public void Validate_DrCrOnlyWithUnknownSide_ReturnsError()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.DrCrOnly, null, null, [],
            TextMatchMode.Contains, null, null, null, null, "both", null);

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    // 2026-10-02 起規則只認分類身分陣列，單選分類欄位已刪除；決策表的分類名稱改在這裡
    // 換成內建分類身分的單元素陣列，不認得的名稱原樣當作分類身分，表格內容不變。
    private static IReadOnlyList<string> CategoryIdsFor(string? category) => category switch
    {
        null => [],
        "Revenue" => [AccountTaxonomyBuiltIns.RevenueId],
        "Receivables" => [AccountTaxonomyBuiltIns.ReceivablesId],
        "Cash" => [AccountTaxonomyBuiltIns.CashId],
        _ => [category]
    };

    private static FilterRuleSpec AccountPairRule(
        string? pairMode, string? debitCategory, string? creditCategory) =>
        new(FilterJoin.And, FilterRuleType.AccountPair, null, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null,
            PairMode: pairMode)
        {
            DebitCategoryIds = CategoryIdsFor(debitCategory),
            CreditCategoryIds = CategoryIdsFor(creditCategory)
        };

    // 決策表（guide §6.1）：模式 × 必填分類 × 科目配對 presence。
    //  mode         | debit       | credit    | hasMapping | 預期
    //  exact        | Receivables | Revenue   | true       | 合法
    //  debitAnchor  | Cash        | (缺)      | true       | 合法（錨定只需單側）
    //  creditAnchor | (缺)        | Revenue   | true       | 合法
    //  exact        | Receivables | (缺)      | true       | 錯誤（exact 需雙分類）
    //  exact        | 非白名單     | Revenue   | true       | 錯誤
    //  其他模式      | —           | —         | true       | 錯誤
    //  exact        | Receivables | Revenue   | false      | 錯誤（未匯入科目配對）
    [Theory]
    [InlineData("exact", "Receivables", "Revenue", true, true)]
    [InlineData("debitAnchor", "Cash", null, true, true)]
    [InlineData("creditAnchor", null, "Revenue", true, true)]
    [InlineData("exact", "Receivables", null, true, false)]
    [InlineData("exact", "NotACategory", "Revenue", true, false)]
    [InlineData("bothAnchor", "Receivables", "Revenue", true, false)]
    [InlineData("exact", "Receivables", "Revenue", false, false)]
    public void Validate_AccountPairDecisionTable(
        string? pairMode, string? debit, string? credit, bool hasMapping, bool expectValid)
    {
        var errors = FilterScenarioValidator.Validate(
            Scenario(rules: AccountPairRule(pairMode, debit, credit)),
            Ready with { HasAccountMapping = hasMapping, HasAnyAccountCategory = hasMapping });

        Assert.Equal(expectValid, errors.Count == 0);
    }

    /* ---- 考量特殊科目類別配對（specialAccountCategoryPair）---------------- */

    // 使用者 2026-10-13 勾選頁確認（文字批次 C）：四種科目配對條件缺分類時同一句。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
    private const string SpecialPairMappingError = "科目配對中尚無任何科目設定分類，請先在第四步設定";

    private static FilterRuleSpec SpecialPairRule(
        string? pairMode, string? debitCategory, string? creditCategory) =>
        new(FilterJoin.And, FilterRuleType.SpecialAccountCategoryPair, null, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null,
            PairMode: pairMode)
        {
            DebitCategoryIds = CategoryIdsFor(debitCategory),
            CreditCategoryIds = CategoryIdsFor(creditCategory)
        };

    private static IReadOnlyList<string> ValidateSpecialPair(
        string? pairMode, string? debit, string? credit, bool hasMapping) =>
        FilterScenarioValidator.Validate(
            Scenario(rules: SpecialPairRule(pairMode, debit, credit)),
            Ready with { HasAccountMapping = hasMapping, HasAnyAccountCategory = hasMapping });

    private static bool HasError(IReadOnlyList<string> errors, string fragment) =>
        errors.Any(e => e.Contains(fragment, StringComparison.Ordinal));

    // 決策表：{科目配對 T/F} × {借方分類 valid/invalid} × {貸方分類 valid/invalid} × {模式 valid/invalid}。
    // 鎖「具體錯誤的有無」而非只看筆數。關鍵不變量：模式非法時 validator 早退，
    // 不再產生分類錯誤（故 illegal-mode 列的分類維度被遮蔽——以「不應出現分類錯誤」斷言鎖住此行為）。
    //  mode    | debit       | credit      | map | 期望錯誤集合
    //  drAndCr | Revenue     | Cash        | T   | （無）
    //  drNotCr | Revenue     | Cash        | T   | （無）
    //  notDrCr | Revenue     | Cash        | T   | （無）
    //  drAndCr | Revenue     | Cash        | F   | mapping
    //  drAndCr | BAD         | Cash        | T   | debit
    //  drAndCr | Revenue     | BAD         | T   | credit
    //  drAndCr | BAD         | BAD         | T   | debit + credit
    //  drAndCr | (缺)        | Cash        | T   | debit（否定/正向皆需雙類別）
    //  drAndCr | Revenue     | (缺)        | T   | credit
    //  BADMODE | Revenue     | Cash        | T   | mode（且不得有分類錯誤——早退遮蔽）
    //  BADMODE | BAD         | BAD         | F   | mapping + mode（mapping 先於模式判定；分類被遮蔽）
    //  (缺)    | Revenue     | Cash        | T   | mode（null 視為非法模式）
    [Theory]
    [InlineData("drAndCr", "Revenue", "Cash", true, false, false, false, false)]
    [InlineData("drNotCr", "Revenue", "Cash", true, false, false, false, false)]
    [InlineData("notDrCr", "Revenue", "Cash", true, false, false, false, false)]
    [InlineData("drAndCr", "Revenue", "Cash", false, true, false, false, false)]
    [InlineData("drAndCr", "NotACategory", "Cash", true, false, false, true, false)]
    [InlineData("drAndCr", "Revenue", "NotACategory", true, false, false, false, true)]
    [InlineData("drAndCr", "NotACategory", "NotACategory", true, false, false, true, true)]
    [InlineData("drAndCr", null, "Cash", true, false, false, true, false)]
    [InlineData("drAndCr", "Revenue", null, true, false, false, false, true)]
    [InlineData("badMode", "Revenue", "Cash", true, false, true, false, false)]
    [InlineData("badMode", "NotACategory", "NotACategory", false, true, true, false, false)]
    [InlineData(null, "Revenue", "Cash", true, false, true, false, false)]
    public void Validate_SpecialAccountCategoryPairDecisionTable(
        string? pairMode, string? debit, string? credit, bool hasMapping,
        bool expectMappingError, bool expectModeError, bool expectDebitError, bool expectCreditError)
    {
        var errors = ValidateSpecialPair(pairMode, debit, credit, hasMapping);

        Assert.Equal(expectMappingError, HasError(errors, SpecialPairMappingError));
        // 使用者 2026-10-13 勾選頁確認（文字批次 C）：配對模式無效只會是內容損毀，改鎖原因代號。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
        Assert.Equal(expectModeError, FilterScenarioCorruption.ReasonsOf(errors).Any(r => r.Code == "special_pair_mode_invalid"));
        // 分類錯誤有兩種形態：未選（空集合）與不存在於目前專案 taxonomy；兩者都以側別前綴鎖定。
        Assert.Equal(expectDebitError, HasError(errors, "借方分類"));
        Assert.Equal(expectCreditError, HasError(errors, "貸方分類"));

        // 整體合法性 = 無任何上述錯誤。
        var expectValid = !expectMappingError && !expectModeError && !expectDebitError && !expectCreditError;
        Assert.Equal(expectValid, errors.Count == 0);
    }

    [Fact]
    public void Validate_SpecialAccountCategoryPair_IllegalMode_SuppressesCategoryChecks()
    {
        // 鎖早退語意：模式非法時，即便雙分類都非白名單，也只報模式錯誤、不報分類錯誤。
        var errors = ValidateSpecialPair("notAMode", "NotACategory", "AlsoBad", hasMapping: true);

        // 使用者 2026-10-13 勾選頁確認（文字批次 C）：只有存檔損毀或程式送錯才會出現的錯誤，畫面只寫通用句，原句與英文代碼改記支援日誌；
        // 改鎖通用句與程式寫死的原因代號，檢查仍逐項區分。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
        Assert.Contains(new FilterScenarioCorruptReason(1, 1, "special_pair_mode_invalid"), FilterScenarioCorruption.ReasonsOf(errors));
        Assert.Contains("第 1 組第 1 條：此條件內容有誤，無法儲存。請刪除此條件後重新設定。", errors);
        Assert.DoesNotContain(errors, e => e.Contains("借方分類", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains("貸方分類", StringComparison.Ordinal));
    }

    /* ---- 借貸組合雙側多選（debitCategoryIds／creditCategoryIds）------------- */

    private const string CustomPettyCashId = "custom.0123456789abcdef0123456789abcdef";

    private static FilterValidationContext ReadyWithMapping(params string[] extraCategoryIds) =>
        Ready with
        {
            HasAccountMapping = true,
            HasAnyAccountCategory = true,
            TaxonomyCategoryIds =
            [
                .. AccountTaxonomyBuiltIns.All.Select(static category => category.CategoryId),
                .. extraCategoryIds
            ]
        };

    private static FilterRuleSpec PairIdsRule(
        FilterRuleType type,
        string pairMode,
        IReadOnlyList<string> debitCategoryIds,
        IReadOnlyList<string> creditCategoryIds) =>
        new FilterRuleSpec(
            FilterJoin.And, type, null, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null,
            PairMode: pairMode)
        {
            DebitCategoryIds = debitCategoryIds,
            CreditCategoryIds = creditCategoryIds
        };

    [Fact]
    public void Validate_PairMultiSelect_AcceptsSeveralCategoriesPerSide()
    {
        var rule = PairIdsRule(
            FilterRuleType.AccountPair,
            AccountPairModes.Exact,
            [AccountTaxonomyBuiltIns.CashId, AccountTaxonomyBuiltIns.ReceivablesId],
            [AccountTaxonomyBuiltIns.RevenueId]);

        Assert.Empty(FilterScenarioValidator.Validate(Scenario(rules: rule), ReadyWithMapping()));
    }

    [Fact]
    public void Validate_PairMultiSelect_AcceptsDuplicatesAndAnyOrder()
    {
        var rule = PairIdsRule(
            FilterRuleType.SpecialAccountCategoryPair,
            SpecialAccountCategoryPairModes.DrNotCr,
            [AccountTaxonomyBuiltIns.ReceivablesId, AccountTaxonomyBuiltIns.CashId, AccountTaxonomyBuiltIns.ReceivablesId],
            [AccountTaxonomyBuiltIns.RevenueId]);

        Assert.Empty(FilterScenarioValidator.Validate(Scenario(rules: rule), ReadyWithMapping()));
        // 去重後才是判定用的集合，重複選取不會放大 SQL 參數。
        Assert.Equal(
            [AccountTaxonomyBuiltIns.CashId, AccountTaxonomyBuiltIns.ReceivablesId],
            AccountPairCategorySelection.Canonicalize(rule.DebitCategoryIds));
    }

    [Fact]
    public void Validate_PairMultiSelect_EmptyArrayIsRejectedInsteadOfMatchingEverything()
    {
        var rule = PairIdsRule(
            FilterRuleType.SpecialAccountCategoryPair,
            SpecialAccountCategoryPairModes.NotDrCr,
            [],
            [AccountTaxonomyBuiltIns.RevenueId]);

        var errors = FilterScenarioValidator.Validate(Scenario(rules: rule), ReadyWithMapping());

        // 使用者 2026-10-13 勾選頁確認（文字批次 C）：改鎖確認後的整句。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
        Assert.Contains("第 1 組第 1 條：尚未選擇借方分類", errors);
    }

    [Fact]
    public void Validate_PairMultiSelect_UnknownCategoryIdIsRejected()
    {
        var rule = PairIdsRule(
            FilterRuleType.AccountPair,
            AccountPairModes.DebitAnchor,
            [CustomPettyCashId],
            []);

        var errors = FilterScenarioValidator.Validate(Scenario(rules: rule), ReadyWithMapping());

        // 2026-10-03 主線裁定 T9：分類身分是內部代號，已刪除的分類拿不到顯示名稱；訊息改成不含代號的白話並寫出下一步
        // （第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        // 2026-10-03 主線審查再改：同一側缺幾項都只報一則，「有一項」在缺兩項以上時不正確，改成不寫數量的說法
        // （第一次失敗：收據 20261003-025601604-276084fa725a4007a4587e23efcf3f08）。
        // 使用者 2026-10-13 勾選頁確認（文字批次 C）：原因只寫原因，改鎖確認後的整句。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
        Assert.Contains("第 1 組第 1 條：所選借方分類中含有已刪除的分類", errors);
        Assert.DoesNotContain(errors, e => e.Contains(CustomPettyCashId, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PairMultiSelect_CustomCategoryInProjectTaxonomyIsAccepted()
    {
        var rule = PairIdsRule(
            FilterRuleType.AccountPair,
            AccountPairModes.DebitAnchor,
            [CustomPettyCashId],
            []);

        Assert.Empty(FilterScenarioValidator.Validate(
            Scenario(rules: rule),
            ReadyWithMapping(CustomPettyCashId)));
    }

    [Fact]
    public void Validate_PairMultiSelect_ExceedingPerSideLimitIsRejected()
    {
        var tooMany = Enumerable
            .Range(0, FilterScenarioLimits.MaxCategoryIdsPerSide + 1)
            .Select(static index => $"custom.{index:x32}")
            .ToArray();
        var rule = PairIdsRule(
            FilterRuleType.AccountPair,
            AccountPairModes.DebitAnchor,
            tooMany,
            []);

        var errors = FilterScenarioValidator.Validate(
            Scenario(rules: rule),
            ReadyWithMapping(tooMany));

        // 使用者 2026-10-13 勾選頁確認（文字批次 C）：只有存檔損毀或程式送錯才會出現的錯誤，畫面只寫通用句，原句與英文代碼改記支援日誌；
        // 改鎖通用句與程式寫死的原因代號，檢查仍逐項區分。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
        Assert.Contains("第 1 組第 1 條：此條件內容有誤，無法儲存。請刪除此條件後重新設定。", errors);
        Assert.Contains(new FilterScenarioCorruptReason(1, 1, "category_selection_too_many"), FilterScenarioCorruption.ReasonsOf(errors));
    }

    [Fact]
    public void Validate_PairAnchorMode_IgnoresUnusedSide()
    {
        // 錨定模式只判定使用中的那一側；另一側留空不是錯誤（讀回也不宣稱未使用的分類）。
        var rule = PairIdsRule(
            FilterRuleType.AccountPair,
            AccountPairModes.CreditAnchor,
            [],
            [AccountTaxonomyBuiltIns.RevenueId]);

        Assert.Empty(FilterScenarioValidator.Validate(Scenario(rules: rule), ReadyWithMapping()));
    }

    // BVA：自訂尾數位數 1–12（0 下鄰拒、1 邊界收、12 邊界收、13 上鄰拒、缺漏拒）。
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(12, true)]
    [InlineData(13, false)]
    [InlineData(null, false)]
    public void Validate_CustomTrailingZerosDigitsBoundaries(int? digits, bool expectValid)
    {
        // First failure: 20261014-135805134-9060396119bc4e8f8dae2eecd29f1776. The replacement keeps the original 1..12 digit boundary.
        var scenario = JET.Application.FilterScenarioPayloadParser.Parse(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            name = "Synthetic zeros", rationale = "Fixed boundary table", groups = new[] { new { rules = new object[] {
                new { type = "fieldValue", field = "amount", @operator = "endsWithDigits", value = new string('0', Math.Max(0, digits ?? 0)), amountBasis = "absolute" },
                new { type = "fieldValue", field = "amount", @operator = "greaterThanOrEqual", value = "1", amountBasis = "absolute" }
            } } }
        }), 100);
        var errors = FilterScenarioValidator.Validate(scenario, Ready);

        Assert.Equal(expectValid, errors.Count == 0);
    }

    // 使用者 2026-10-19 裁定「視為無法辨識的日期」：第五步日期清單貼上沒有年份或沒有日的寫法時，
    // 前端保留原文送出，這裡逐值寫出無法辨識，不再補成今年或 1 日
    // （修正前失敗：收據 20261019-151809317-0d2e1e0ff5e0496d994d8a4e4a2810e5）。
    [Fact]
    public void Validate_DateListWithIncompleteDates_NamesEachValue()
    {
        var scenario = JET.Application.FilterScenarioPayloadParser.Parse(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            name = "Synthetic dates", rationale = "Fixed date list", groups = new[] { new { rules = new object[] {
                new { type = "fieldValue", field = "postDate", @operator = "in", values = new[] { "2025-06-11", "12/31", "Dec 2025" } }
            } } }
        }), 100);
        var errors = FilterScenarioValidator.Validate(scenario, Ready);

        Assert.Equal(
            [
                "第 1 組第 1 條：清單第 2 個值：日期「12/31」無法辨識，請以 2026-12-31 的格式輸入",
                "第 1 組第 1 條：清單第 3 個值：日期「Dec 2025」無法辨識，請以 2026-12-31 的格式輸入"
            ],
            errors);
    }

    // 預篩選收斂計畫階段九把兩種舊式筆數條件列為停用寫法：不論門檻合不合法，驗證都只回報停用原因與重建方式，
    // 門檻的邊界檢查由「科目與人員統計」的驗證承接。原本的邊界測試（0 拒、1 與 11 收、缺漏拒）第一次失敗：
    // Focused 收據 20261024-195604731-a54ac30c8a994b46878d21f5b82967bf（1 與 11 不再合格）。
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(null)]
    public void Validate_CustomPreparerEntryCount_IsRetiredRegardlessOfMaxEntries(int? maxEntries)
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.CustomPreparerEntryCount, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null, MaxEntries: maxEntries);

        var error = Assert.Single(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));

        Assert.Contains(FilterSinglePurposeRules.PreparerEntryCount, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(null)]
    public void Validate_CustomAccountEntryCount_IsRetiredRegardlessOfMaxEntries(int? maxEntries)
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.CustomAccountEntryCount, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null, MaxEntries: maxEntries);

        var error = Assert.Single(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));

        Assert.Contains(FilterSinglePurposeRules.AccountEntryCount, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_CustomKeywordsWithoutKeywords_ReturnsError()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.CustomKeywords, null, null,
            [" "], TextMatchMode.Contains, null, null, null, null, null, null);

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    [Fact]
    public void Validate_ManualAutoWithoutValue_ReturnsError()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.ManualAuto, null, null, [],
            TextMatchMode.Contains, null, null, null, null, null, null);

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    /* ---- 常用篩選條件的卡片與專用型別 ------------------------------------------- */

    // 2026-10-29 卡片改稱常用篩選條件、拿掉字母（使用者裁定程式內部一起改）：來源代碼改成 common，名稱、輔助函式與案例改用卡片代號，斷言不變。
    // 第一次失敗：Build 收據 20261029-055859529-4f7e4a2a4d624199811e82719d4b37ad（還引用舊常數，編譯不過）。

    private static FilterRuleSpec CommonRule(FilterRuleType type, int? windowDays = null, params string[] keywords) =>
        new(FilterJoin.And, type, null, null, keywords, TextMatchMode.Contains,
            null, null, null, null, null, null, WindowDays: windowDays);

    private static FilterRuleSpec CommonMappingRule(string card) => card switch
    {
        "manualRevenueEntry" => CommonRule(FilterRuleType.ManualRevenueEntry),
        "specificPreparer" => TextRule(GlMappingKeys.CreateBy, "特定人員") with { Mode = TextMatchMode.Exact },
        "blankDescription" => PrescreenRule(PrescreenRuleKeys.BlankDescription),
        "preparerEqualsApprover" => CommonRule(FilterRuleType.PreparerEqualsApprover),
        _ => throw new ArgumentOutOfRangeException(nameof(card), card, "未知的卡片欄位前置案例。")
    };

    private static FilterScenarioSpec CommonMappingScenario(string card)
    {
        var scenario = Scenario(rules: CommonMappingRule(card));

        // 特定人員與空白摘要重用一般 text／prescreen 型別，只有 source:common 能看出來自卡片；
        // 收入之人工分錄與編製核准同一人是卡片專用型別，即使呼叫端沒送 source 也不能繞過欄位前置閘。
        return card is "specificPreparer" or "blankDescription"
            ? scenario with { Source = FilterScenarioSources.Common }
            : scenario;
    }

    private static FilterValidationContext CommonMappingReady => Ready with
    {
        HasAccountMapping = true,
        HasAnyAccountCategory = true,
        HasRevenueCategory = true,
        HasCounterpartCategory = true,
        AvailableGlFields = GlFieldsWithout()
    };

    // 決策表：收入之人工分錄、特定人員、空白摘要各缺自己的 mapping；編製核准同一人分別缺建立人、核准人與兩欄皆缺。
    // 除指定缺口外其餘前置均齊，讓 oracle 精準鎖定「缺少前置資料」與欄位名稱。
    [Theory]
    [InlineData("manualRevenueEntry", false, true, true, true, "人工/自動分錄", null)]
    [InlineData("specificPreparer", true, false, true, true, "傳票建立人員", null)]
    [InlineData("blankDescription", true, true, true, false, "傳票摘要", null)]
    [InlineData("preparerEqualsApprover", true, false, true, true, "傳票建立人員", null)]
    [InlineData("preparerEqualsApprover", true, true, false, true, "傳票核准人員", null)]
    [InlineData("preparerEqualsApprover", true, false, false, true, "傳票建立人員", "傳票核准人員")]
    public void Validate_CommonCardMappingPrerequisite_MissingFieldNamesEveryGap(
        string card,
        bool hasManualFlag,
        bool hasCreatedBy,
        bool hasApprovedBy,
        bool hasDescription,
        string expectedField,
        string? secondExpectedField)
    {
        // 預篩選收斂計畫階段四拿掉四個欄位旗標（2026-10-23），缺哪一欄就從可用欄位清單扣掉哪一欄。
        // 第一次失敗：收據 20261023-031856475-04f811a69e3346428de91091f0d02655（編譯失敗）。
        var missing = new List<string>();
        if (!hasManualFlag) missing.Add(JetFieldCatalog.GlManual);
        if (!hasCreatedBy) missing.Add(JetFieldCatalog.GlCreateBy);
        if (!hasApprovedBy) missing.Add(JetFieldCatalog.GlApproveBy);
        if (!hasDescription) missing.Add(JetFieldCatalog.GlDescription);
        var context = CommonMappingReady with { AvailableGlFields = GlFieldsWithout([.. missing]) };

        var errors = FilterScenarioValidator.Validate(CommonMappingScenario(card), context);
        var combined = string.Join("；", errors);

        // 使用者 2026-10-13 勾選頁確認（文字批次 C）：缺欄位時寫「「欄位」尚未選擇來源欄」。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
        Assert.Contains($"「{expectedField}」尚未選擇來源欄", combined, StringComparison.Ordinal);
        Assert.Contains(expectedField, combined, StringComparison.Ordinal);
        if (secondExpectedField is not null)
        {
            Assert.Contains(secondExpectedField, combined, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("manualRevenueEntry")]
    [InlineData("specificPreparer")]
    [InlineData("blankDescription")]
    [InlineData("preparerEqualsApprover")]
    public void Validate_CommonCardMappingPrerequisite_AllRequiredFieldsMapped_IsValid(string card)
    {
        Assert.Empty(FilterScenarioValidator.Validate(
            CommonMappingScenario(card),
            CommonMappingReady));
    }

    // 原本鎖住「不是卡片來源的一般條件不檢查欄位配對」。使用者 2026-10-22 裁定第五步遇到 GL 沒有配對的欄位時
    // 「停用並寫原因」，取代 2026-10-04 的「不修」，所以不論來源都要擋下並寫出欄位名稱；測試名稱與斷言照新裁定改寫。
    // 第一次失敗：Focused 收據 20261023-032924364-4f3864a9ba70481b9e50cf02d0305c2c（兩組資料都得到欄位尚未選擇來源欄的錯誤）。
    [Theory]
    [InlineData("specificPreparer", "「傳票建立人員」尚未選擇來源欄")]
    [InlineData("blankDescription", "「傳票摘要」尚未選擇來源欄")]
    public void Validate_ReusedRuleWithoutCommonSource_StillRequiresTheMappedField(string card, string expectedReason)
    {
        // 預篩選收斂計畫階段四拿掉欄位旗標（2026-10-23），缺欄位改從可用欄位清單扣掉。
        // 第一次失敗：收據 20261023-031856475-04f811a69e3346428de91091f0d02655（編譯失敗）。
        var context = card == "specificPreparer"
            ? CommonMappingReady with { AvailableGlFields = GlFieldsWithout(JetFieldCatalog.GlCreateBy) }
            : CommonMappingReady with { AvailableGlFields = GlFieldsWithout(JetFieldCatalog.GlDescription) };

        Assert.Equal(
            [$"第 1 組第 1 條：{expectedReason}"],
            FilterScenarioValidator.Validate(Scenario(rules: CommonMappingRule(card)), context));
    }

    public static TheoryData<FilterValidationContext, bool, bool, bool, bool, bool> AccountMappingEligibilityCases =>
        new()
        {
            { Ready, false, false, false, false, false },
            { Ready with { HasAccountMapping = true }, false, false, false, false, false },
            { Ready with { HasAccountMapping = true, HasAnyAccountCategory = true }, true, true, false, false, false },
            { Ready with { HasAccountMapping = true, HasAnyAccountCategory = true, HasRevenueCategory = true }, true, true, true, false, true },
            { Ready with { HasAccountMapping = true, HasAnyAccountCategory = true, HasCounterpartCategory = true }, true, true, false, false, false },
            { Ready with { HasAccountMapping = true, HasAnyAccountCategory = true, HasRevenueCategory = true, HasCounterpartCategory = true }, true, true, true, true, true }
        };

    [Theory]
    [MemberData(nameof(AccountMappingEligibilityCases))]
    public void Validate_AccountMappingRules_UsePerRuleContentEligibility(
        FilterValidationContext context,
        bool accountPairValid,
        bool specialPairValid,
        bool quarterEndRevenueValid,
        bool revenueWithoutCounterpartValid,
        bool manualRevenueValid)
    {
        var accountPair = new FilterRuleSpec(
            FilterJoin.And, FilterRuleType.AccountPair, null, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null,
            PairMode: AccountPairModes.Exact)
        {
            // 2026-10-02 起單選分類欄位已刪除，改用分類身分陣列表達同一組借貸分類。
            DebitCategoryIds = [AccountTaxonomyBuiltIns.CashId],
            CreditCategoryIds = [AccountTaxonomyBuiltIns.RevenueId]
        };
        var specialPair = accountPair with
        {
            Type = FilterRuleType.SpecialAccountCategoryPair,
            PairMode = SpecialAccountCategoryPairModes.DrAndCr
        };

        Assert.Equal(accountPairValid,
            FilterScenarioValidator.Validate(Scenario(rules: accountPair), context).Count == 0);
        Assert.Equal(specialPairValid,
            FilterScenarioValidator.Validate(Scenario(rules: specialPair), context).Count == 0);
        Assert.Equal(quarterEndRevenueValid,
            FilterScenarioValidator.Validate(
                Scenario(rules: CommonRule(FilterRuleType.RevenueDebitNearQuarterEnd, windowDays: 5)), context).Count == 0);
        Assert.Equal(revenueWithoutCounterpartValid,
            FilterScenarioValidator.Validate(
                Scenario(rules: CommonRule(FilterRuleType.RevenueWithoutNormalCounterpart)), context).Count == 0);
        Assert.Equal(manualRevenueValid,
            FilterScenarioValidator.Validate(
                Scenario(rules: CommonRule(FilterRuleType.ManualRevenueEntry)), context).Count == 0);
    }

    [Fact]
    public void Validate_RevenueDebitNearQuarterEnd_WithoutAccountMapping_ReturnsError()
    {
        // 季末前借記收入需科目配對已匯入（鏡射 accountPair 閘控）。
        var rule = CommonRule(FilterRuleType.RevenueDebitNearQuarterEnd, windowDays: 5);

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    // BVA：季末視窗天數 1–92（0 下鄰拒、1 邊界收、92 上界收、93 上鄰拒、缺漏拒）。科目配對已匯入。
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(92, true)]
    [InlineData(93, false)]
    [InlineData(null, false)]
    public void Validate_RevenueDebitNearQuarterEnd_WindowDaysBoundaries(int? windowDays, bool expectValid)
    {
        var rule = CommonRule(FilterRuleType.RevenueDebitNearQuarterEnd, windowDays: windowDays);

        var errors = FilterScenarioValidator.Validate(
            Scenario(rules: rule), Ready with
            {
                HasAccountMapping = true,
                HasAnyAccountCategory = true,
                HasRevenueCategory = true
            });

        Assert.Equal(expectValid, errors.Count == 0);
    }

    // 決策表：收入無一般對方科目與收入之人工分錄需科目配對（false→錯誤、true→合法）。
    [Theory]
    [InlineData("revenueWithoutNormalCounterpart", false, false)]
    [InlineData("revenueWithoutNormalCounterpart", true, true)]
    [InlineData("manualRevenueEntry", false, false)]
    [InlineData("manualRevenueEntry", true, true)]
    public void Validate_CommonRevenueConditions_RequireAccountMapping(
        string typeName, bool hasMapping, bool expectValid)
    {
        var type = typeName == "revenueWithoutNormalCounterpart"
            ? FilterRuleType.RevenueWithoutNormalCounterpart
            : FilterRuleType.ManualRevenueEntry;

        var errors = FilterScenarioValidator.Validate(
            Scenario(rules: CommonRule(type)), Ready with
            {
                HasAccountMapping = hasMapping,
                HasAnyAccountCategory = hasMapping,
                HasRevenueCategory = hasMapping,
                HasCounterpartCategory = hasMapping
            });

        Assert.Equal(expectValid, errors.Count == 0);
    }

    // 決策表：特定金額尾數樣態（合法多組、1 位邊界、12 位邊界、13 位拒、非數字拒、空清單拒）。
    [Theory]
    [InlineData(true, "999999", "000000")]
    [InlineData(true, "0")]
    [InlineData(true, "123456789012")]
    [InlineData(false, "1234567890123")]
    [InlineData(false, "99x")]
    [InlineData(false)]
    public void Validate_TrailingDigits_PatternRules(bool expectValid, params string[] patterns)
    {
        // First failure: 20261014-135805134-9060396119bc4e8f8dae2eecd29f1776. Keep the tail-pattern boundary table on the current field condition.
        var rule = JET.Application.FilterScenarioPayloadParser.Parse(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            name = "Synthetic tail", rationale = "Fixed boundary table", groups = new[] { new { rules = new[] {
                new { type = "fieldValue", field = "amount", @operator = "endsWithDigits", value = string.Join(",", patterns), amountBasis = "absolute" }
            } } }
        }), 100);
        var errors = FilterScenarioValidator.Validate(rule, Ready);

        Assert.Equal(expectValid, errors.Count == 0);
    }

    [Fact]
    public void Validate_PreparerEqualsApprover_WithCreatorAndApproverMappings_IsValid()
    {
        // 編製與核准同一人無 rule 參數，但 createBy／approveBy 兩個 mapping 前置都必須齊備。
        Assert.Empty(FilterScenarioValidator.Validate(
            Scenario(rules: CommonRule(FilterRuleType.PreparerEqualsApprover)), CommonMappingReady));
    }

    /* ---- 常用篩選條件來源豁免名稱/動機必填（manifest scenario.source）------- */

    private const string NameRequiredError = "情境名稱必填。";
    private const string RationaleRequiredError = "篩選動機說明必填。";

    // 決策表：source ∈ {null, "common"} × name ∈ {空, 有} × rationale ∈ {空, 有}。
    // 常用篩選條件來源 ⇒ 無論名稱/動機是否留白，皆不產生「必填」錯誤（固定的條件清單，留痕替補在落地層補）。
    // 其他來源 ⇒ 留白才報對應的「必填」錯誤（既往行為，不得鬆動）。
    //  source | name | rationale | 期望含名稱必填 | 期望含動機必填
    [Theory]
    [InlineData(null, "", "", true, true)]
    [InlineData(null, "情境", "", false, true)]
    [InlineData(null, "", "動機", true, false)]
    [InlineData(null, "情境", "動機", false, false)]
    [InlineData("common", "", "", false, false)]
    [InlineData("common", "情境", "", false, false)]
    [InlineData("common", "", "動機", false, false)]
    [InlineData("common", "情境", "動機", false, false)]
    [InlineData(" common ", "", "", false, false)]
    public void Validate_SourceExemptsNameAndRationale(
        string? source, string name, string rationale, bool expectNameError, bool expectRationaleError)
    {
        // 規則本身固定合法（TextRule），故僅可能出現名稱/動機兩種錯誤——可對訊息身分精準斷言。
        var scenario = new FilterScenarioSpec(name, rationale, [new FilterGroupSpec(FilterJoin.And, [TextRule()])], source);

        var errors = FilterScenarioValidator.Validate(scenario, Ready);

        Assert.Equal(expectNameError, errors.Contains(NameRequiredError));
        Assert.Equal(expectRationaleError, errors.Contains(RationaleRequiredError));
    }

    [Fact]
    public void Validate_UnknownSource_TreatedAsAuthored_StillRequiresNameAndRationale()
    {
        // 「非 common 即查核員自擬」：未知來源值不得意外豁免必填（白名單只認得 "common"）。
        // 2026-10-29 以前的案件記的是另一個來源代碼，這一版不認得，也走這條路。
        var scenario = new FilterScenarioSpec(
            "  ", "  ", [new FilterGroupSpec(FilterJoin.And, [TextRule()])], Source: "adhoc");

        var errors = FilterScenarioValidator.Validate(scenario, Ready);

        Assert.Contains(NameRequiredError, errors);
        Assert.Contains(RationaleRequiredError, errors);
    }

    [Fact]
    public void Validate_CommonSource_DoesNotSuppressOtherErrors()
    {
        // 豁免只限名稱/動機兩項：常用篩選條件來源仍須通過其餘所有驗證（此處用未知 field 觸發）。
        var scenario = new FilterScenarioSpec(
            "", "", [new FilterGroupSpec(FilterJoin.And, [TextRule(field: "no_such_field")])], Source: "common");

        var errors = FilterScenarioValidator.Validate(scenario, Ready);

        Assert.DoesNotContain(NameRequiredError, errors);
        Assert.DoesNotContain(RationaleRequiredError, errors);
        Assert.NotEmpty(errors); // 未知 field 仍被擋下
    }
}
