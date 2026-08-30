using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class FilterScenarioValidatorTests
{
    /// <summary>一般前置：有期末財報準備日、未匯入科目配對、未匯入授權編製人員清單。</summary>
    private static readonly FilterValidationContext Ready =
        new(HasLastPeriodStart: true, HasAccountMapping: false, HasAuthorizedPreparers: false)
        {
            HasManualFlag = true,
            HasCreatedBy = true,
            HasApprovedBy = true,
            HasDescription = true
        };

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

        Assert.Contains(
            "條件群組 1：join 結合方式「XOR」無效，允許值：AND、OR。", errors);
    }

    [Fact]
    public void Validate_RuleUnknownJoin_ReturnsError()
    {
        var rule = TextRule() with { UnknownJoin = "ANDD" };

        var errors = FilterScenarioValidator.Validate(Scenario(rules: rule), Ready);

        Assert.Contains(
            "條件群組 1 規則 1：join 結合方式「ANDD」無效，允許值：AND、OR。", errors);
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
        Assert.Contains(errors, e => e.Contains("「XOR」無效", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("「聯集」無效", StringComparison.Ordinal));
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

        Assert.Empty(FilterScenarioValidator.Validate(
            scenario, Ready with { HasAccountMapping = true }));
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

    private static FilterRuleSpec AccountPairRule(
        string? pairMode, string? debitCategory, string? creditCategory) =>
        new(FilterJoin.And, FilterRuleType.AccountPair, null, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null,
            PairMode: pairMode, DebitCategory: debitCategory, CreditCategory: creditCategory);

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

    private const string SpecialPairMappingError = "特殊科目類別配對需要科目配對 target 至少一筆非空白分類。";

    private static FilterRuleSpec SpecialPairRule(
        string? pairMode, string? debitCategory, string? creditCategory) =>
        new(FilterJoin.And, FilterRuleType.SpecialAccountCategoryPair, null, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null,
            PairMode: pairMode, DebitCategory: debitCategory, CreditCategory: creditCategory);

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
        Assert.Equal(expectModeError, HasError(errors, "允許值：drAndCr、drNotCr、notDrCr"));
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

        Assert.Contains(errors, e => e.Contains("允許值：drAndCr、drNotCr、notDrCr", StringComparison.Ordinal));
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

        Assert.Contains(errors, e => e.Contains("借方分類至少需選擇一項", StringComparison.Ordinal));
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

        Assert.Contains(
            errors,
            e => e.Contains($"借方分類「{CustomPettyCashId}」不存在於目前專案的科目分類", StringComparison.Ordinal));
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

        Assert.Contains(
            errors,
            e => e.Contains($"借方分類最多 {FilterScenarioLimits.MaxCategoryIdsPerSide} 項", StringComparison.Ordinal));
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

    [Fact]
    public void Validate_LegacyScalarPair_ProjectsToBuiltInCategoryIdentity()
    {
        var rule = new FilterRuleSpec(
            FilterJoin.And, FilterRuleType.SpecialAccountCategoryPair, null, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null,
            PairMode: SpecialAccountCategoryPairModes.DrAndCr,
            DebitCategory: AccountMappingCategories.Revenue,
            CreditCategory: AccountMappingCategories.Cash);

        Assert.Empty(FilterScenarioValidator.Validate(Scenario(rules: rule), ReadyWithMapping()));
        Assert.Equal([AccountTaxonomyBuiltIns.RevenueId], rule.EffectiveDebitCategoryIds);
        Assert.Equal([AccountTaxonomyBuiltIns.CashId], rule.EffectiveCreditCategoryIds);
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
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.CustomTrailingZeros, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null, Digits: digits);

        var errors = FilterScenarioValidator.Validate(Scenario(rules: rule), Ready);

        Assert.Equal(expectValid, errors.Count == 0);
    }

    // BVA：自訂低頻編製者門檻 maxEntries ≥ 1(0 下鄰拒、1 邊界收、11 預設收、缺漏拒)。
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(11, true)]
    [InlineData(null, false)]
    public void Validate_CustomPreparerEntryCountMaxEntriesBoundaries(int? maxEntries, bool expectValid)
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.CustomPreparerEntryCount, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null, MaxEntries: maxEntries);

        var errors = FilterScenarioValidator.Validate(Scenario(rules: rule), Ready);

        Assert.Equal(expectValid, errors.Count == 0);
    }

    // BVA：自訂科目張數門檻 maxEntries ≥ 1(0 下鄰拒、1 邊界收、11 預設收、缺漏拒)。
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(11, true)]
    [InlineData(null, false)]
    public void Validate_CustomAccountEntryCount_BoundaryOnMaxEntries(int? maxEntries, bool expectValid)
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.CustomAccountEntryCount, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null, MaxEntries: maxEntries);

        var errors = FilterScenarioValidator.Validate(Scenario(rules: rule), Ready);

        Assert.Equal(expectValid, errors.Count == 0);
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

    /* ---- KCT 小組條件（清單 A/C/D/H/J） ---------------------------------- */

    private static FilterRuleSpec KctRule(FilterRuleType type, int? windowDays = null, params string[] keywords) =>
        new(FilterJoin.And, type, null, null, keywords, TextMatchMode.Contains,
            null, null, null, null, null, null, WindowDays: windowDays);

    private static FilterRuleSpec KctMappingRule(string letter) => letter switch
    {
        "D" => KctRule(FilterRuleType.ManualRevenueEntry),
        "E" => TextRule(GlMappingKeys.CreateBy, "特定人員") with { Mode = TextMatchMode.Exact },
        "G" => PrescreenRule(PrescreenRuleKeys.BlankDescription),
        "J" => KctRule(FilterRuleType.PreparerEqualsApprover),
        _ => throw new ArgumentOutOfRangeException(nameof(letter), letter, "未知的 KCT mapping prerequisite 案例。")
    };

    private static FilterScenarioSpec KctMappingScenario(string letter)
    {
        var scenario = Scenario(rules: KctMappingRule(letter));

        // E／G 重用一般 text／prescreen 型別，只有 source:kct 能識別為 KCT 預設；
        // D／J 是專屬 KCT type，即使 legacy caller 沒送 source 也不能繞過欄位前置閘。
        return letter is "E" or "G"
            ? scenario with { Source = FilterScenarioSources.Kct }
            : scenario;
    }

    private static FilterValidationContext KctMappingReady => Ready with
    {
        HasAccountMapping = true,
        HasAnyAccountCategory = true,
        HasRevenueCategory = true,
        HasCounterpartCategory = true,
        HasManualFlag = true,
        HasCreatedBy = true,
        HasApprovedBy = true,
        HasDescription = true
    };

    // 決策表：D／E／G 各缺自己的 mapping；J 分別缺建立人、核准人與兩欄皆缺。
    // 除指定缺口外其餘前置均齊，讓 oracle 精準鎖定「缺少前置資料」與欄位名稱。
    [Theory]
    [InlineData("D", false, true, true, true, "人工/自動分錄", null)]
    [InlineData("E", true, false, true, true, "傳票建立人員", null)]
    [InlineData("G", true, true, true, false, "傳票摘要", null)]
    [InlineData("J", true, false, true, true, "傳票建立人員", null)]
    [InlineData("J", true, true, false, true, "傳票核准人員", null)]
    [InlineData("J", true, false, false, true, "傳票建立人員", "傳票核准人員")]
    public void Validate_KctMappingPrerequisite_MissingFieldNamesEveryGap(
        string letter,
        bool hasManualFlag,
        bool hasCreatedBy,
        bool hasApprovedBy,
        bool hasDescription,
        string expectedField,
        string? secondExpectedField)
    {
        var context = KctMappingReady with
        {
            HasManualFlag = hasManualFlag,
            HasCreatedBy = hasCreatedBy,
            HasApprovedBy = hasApprovedBy,
            HasDescription = hasDescription
        };

        var errors = FilterScenarioValidator.Validate(KctMappingScenario(letter), context);
        var combined = string.Join("；", errors);

        Assert.Contains("缺少前置資料", combined, StringComparison.Ordinal);
        Assert.Contains(expectedField, combined, StringComparison.Ordinal);
        if (secondExpectedField is not null)
        {
            Assert.Contains(secondExpectedField, combined, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("D")]
    [InlineData("E")]
    [InlineData("G")]
    [InlineData("J")]
    public void Validate_KctMappingPrerequisite_AllRequiredFieldsMapped_IsValid(string letter)
    {
        Assert.Empty(FilterScenarioValidator.Validate(
            KctMappingScenario(letter),
            KctMappingReady));
    }

    [Theory]
    [InlineData("E")]
    [InlineData("G")]
    public void Validate_ReusedRuleWithoutKctSource_DoesNotApplyKctOnlyMappingGate(string letter)
    {
        var context = letter == "E"
            ? KctMappingReady with { HasCreatedBy = false }
            : KctMappingReady with { HasDescription = false };

        Assert.Empty(FilterScenarioValidator.Validate(
            Scenario(rules: KctMappingRule(letter)),
            context));
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
            PairMode: AccountPairModes.Exact, DebitCategory: AccountMappingCategories.Cash,
            CreditCategory: AccountMappingCategories.Revenue);
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
                Scenario(rules: KctRule(FilterRuleType.RevenueDebitNearQuarterEnd, windowDays: 5)), context).Count == 0);
        Assert.Equal(revenueWithoutCounterpartValid,
            FilterScenarioValidator.Validate(
                Scenario(rules: KctRule(FilterRuleType.RevenueWithoutNormalCounterpart)), context).Count == 0);
        Assert.Equal(manualRevenueValid,
            FilterScenarioValidator.Validate(
                Scenario(rules: KctRule(FilterRuleType.ManualRevenueEntry)), context).Count == 0);
    }

    [Fact]
    public void Validate_RevenueDebitNearQuarterEnd_WithoutAccountMapping_ReturnsError()
    {
        // 清單 A 需科目配對已匯入（鏡射 accountPair 閘控）。
        var rule = KctRule(FilterRuleType.RevenueDebitNearQuarterEnd, windowDays: 5);

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
        var rule = KctRule(FilterRuleType.RevenueDebitNearQuarterEnd, windowDays: windowDays);

        var errors = FilterScenarioValidator.Validate(
            Scenario(rules: rule), Ready with
            {
                HasAccountMapping = true,
                HasAnyAccountCategory = true,
                HasRevenueCategory = true
            });

        Assert.Equal(expectValid, errors.Count == 0);
    }

    // 決策表：清單 C/D 需科目配對（false→錯誤、true→合法）。
    [Theory]
    [InlineData("revenueWithoutNormalCounterpart", false, false)]
    [InlineData("revenueWithoutNormalCounterpart", true, true)]
    [InlineData("manualRevenueEntry", false, false)]
    [InlineData("manualRevenueEntry", true, true)]
    public void Validate_KctRevenueConditions_RequireAccountMapping(
        string typeName, bool hasMapping, bool expectValid)
    {
        var type = typeName == "revenueWithoutNormalCounterpart"
            ? FilterRuleType.RevenueWithoutNormalCounterpart
            : FilterRuleType.ManualRevenueEntry;

        var errors = FilterScenarioValidator.Validate(
            Scenario(rules: KctRule(type)), Ready with
            {
                HasAccountMapping = hasMapping,
                HasAnyAccountCategory = hasMapping,
                HasRevenueCategory = hasMapping,
                HasCounterpartCategory = hasMapping
            });

        Assert.Equal(expectValid, errors.Count == 0);
    }

    // 決策表：清單 H 尾數樣態（合法多組、1 位邊界、12 位邊界、13 位拒、非數字拒、空清單拒）。
    [Theory]
    [InlineData(true, "999999", "000000")]
    [InlineData(true, "0")]
    [InlineData(true, "123456789012")]
    [InlineData(false, "1234567890123")]
    [InlineData(false, "99x")]
    [InlineData(false)]
    public void Validate_TrailingDigits_PatternRules(bool expectValid, params string[] patterns)
    {
        var errors = FilterScenarioValidator.Validate(
            Scenario(rules: KctRule(FilterRuleType.TrailingDigits, keywords: patterns)), Ready);

        Assert.Equal(expectValid, errors.Count == 0);
    }

    [Fact]
    public void Validate_PreparerEqualsApprover_WithCreatorAndApproverMappings_IsValid()
    {
        // 清單 J 無 rule 參數，但 createBy／approveBy 兩個 mapping 前置都必須齊備。
        Assert.Empty(FilterScenarioValidator.Validate(
            Scenario(rules: KctRule(FilterRuleType.PreparerEqualsApprover)), KctMappingReady));
    }

    /* ---- KCT 來源豁免名稱/動機必填（manifest scenario.source）------------- */

    private const string NameRequiredError = "情境名稱必填。";
    private const string RationaleRequiredError = "篩選動機說明必填。";

    // 決策表：source ∈ {null, "kct"} × name ∈ {空, 有} × rationale ∈ {空, 有}。
    // KCT 來源 ⇒ 無論名稱/動機是否留白，皆不產生「必填」錯誤（固定方法論清單，留痕替補在落地層補）。
    // 非 KCT 來源 ⇒ 留白才報對應的「必填」錯誤（既往行為，不得鬆動）。
    //  source | name | rationale | 期望含名稱必填 | 期望含動機必填
    [Theory]
    [InlineData(null, "", "", true, true)]
    [InlineData(null, "情境", "", false, true)]
    [InlineData(null, "", "動機", true, false)]
    [InlineData(null, "情境", "動機", false, false)]
    [InlineData("kct", "", "", false, false)]
    [InlineData("kct", "情境", "", false, false)]
    [InlineData("kct", "", "動機", false, false)]
    [InlineData("kct", "情境", "動機", false, false)]
    [InlineData(" kct ", "", "", false, false)]
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
        // 「非 kct 即查核員自擬」：未知來源值不得意外豁免必填（白名單只認得 "kct"）。
        var scenario = new FilterScenarioSpec(
            "  ", "  ", [new FilterGroupSpec(FilterJoin.And, [TextRule()])], Source: "adhoc");

        var errors = FilterScenarioValidator.Validate(scenario, Ready);

        Assert.Contains(NameRequiredError, errors);
        Assert.Contains(RationaleRequiredError, errors);
    }

    [Fact]
    public void Validate_KctSource_DoesNotSuppressOtherErrors()
    {
        // 豁免只限名稱/動機兩項：KCT 來源仍須通過其餘所有驗證（此處用未知 field 觸發）。
        var scenario = new FilterScenarioSpec(
            "", "", [new FilterGroupSpec(FilterJoin.And, [TextRule(field: "no_such_field")])], Source: "kct");

        var errors = FilterScenarioValidator.Validate(scenario, Ready);

        Assert.DoesNotContain(NameRequiredError, errors);
        Assert.DoesNotContain(RationaleRequiredError, errors);
        Assert.NotEmpty(errors); // 未知 field 仍被擋下
    }
}
