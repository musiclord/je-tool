using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// GL 各種規則的 SQL 條件都定義在這裡；隨資料庫不同的片段經 <see cref="ISqlDialect"/> 產生。
/// 每個方法回傳一段 WHERE 條件，分錄表的別名固定是 g，條件用到的值依序加進參數清單。
/// 建立 DbCommand 與綁定參數只由 Infrastructure 負責。
/// prescreen.run 的計數和第五步的篩選條件用的是同一份片段。
/// SQL 裡的識別字一律出自 GlFieldWhitelist 或這個檔案的常數；使用者輸入的值只會成為參數。
/// 「同一張傳票裡有沒有某種分錄」一律寫成不引用 g 的子查詢，先取出一次符合的傳票號碼，再用 IN 或 NOT IN 接回本列。
/// 子查詢若引用 g，每一筆分錄都要重掃一次自己的傳票，工作量會隨單張傳票的分錄數平方成長。
/// 子查詢排除傳票號碼是 NULL 的分錄，本列的傳票號碼也先確認不是 NULL，IN 與 NOT IN 的結果才只有真和假。
/// </summary>
internal sealed partial class GlRulePredicates(
    ISqlDialect dialect,
    Func<FilterConditionContext, string, string> populationScopePredicate)
{
    /// <summary>期末後核准（prep_period_approval）：核准日 ≥ 期末財報準備日。</summary>
    public string PrepPeriodApproval(FilterSqlParameterPlanBuilder command, string prepStartDate)
    {
        var p = NextParam(command, prepStartDate);
        return $"g.approval_date >= {p}";
    }

    /// <summary>摘要特定描述（suspicious_keywords）：摘要含任一預設關鍵字，和第五步「傳票摘要」的「包含任一文字」同一個比對。</summary>
    public string SuspiciousKeywords(FilterSqlParameterPlanBuilder command)
    {
        return ContainsAnyKeyword(command, "g.description", SuspiciousKeywordDefaults.Defaults);
    }

    /// <summary>
    /// 未預期借貸組合（unexpected_account_pair）照舊版 IDEA R3 寫：本列是收入貸方（amount_scaled &lt; 0），
    /// 同一張傳票、同一個母體範圍裡，沒有應收款項、現金或預收款且金額大於 0 的分錄，
    /// 但至少有一筆金額大於 0 的分錄（舊版先取這類傳票裡金額大於 0 的分錄，再依傳票號碼和收入貸方對上）。
    /// 整張傳票沒有正金額分錄時借貸一定不平，借貸不平測試會列出來，這條規則不挑。
    /// 正常對方科目要大於 0，0 元的應收或現金不算有正常對方科目，和舊版相同。
    /// 直接命中的只有收入貸方這一列；第四步與預篩選報告要列整張傳票時，由列出命中分錄的地方展開，這個判斷式只挑收入貸方。
    /// 「收入無一般對方科目」是另一條常用篩選條件：對方科目不含現金，借方側採 amount_scaled >= 0，也不要求正金額分錄。
    /// 現銷傳票與沒有正金額分錄的傳票只有它挑中；傳票的應收或預收借方只有 0 元、另有其他正金額分錄時，只有這條規則挑中。
    /// 其他借貸組合條件（accountPair、specialAccountCategoryPair）照樣以 amount_scaled >= 0 判定借方側。
    /// 需科目配對已匯入。
    /// </summary>
    public string UnexpectedAccountPair(
        FilterSqlParameterPlanBuilder command,
        FilterConditionContext context,
        string schemaPrefix = "")
    {
        var revenue = NextParam(command, AccountCategoryBuiltIns.RevenueRole);
        var counterparts = CategoryListParams(command);

        return $"""
            (g.voucher_number IS NOT NULL
             AND EXISTS (SELECT 1 FROM {schemaPrefix}account_mapping m
                     {CategoryJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_number = g.account_number AND tm.semantic_role = {revenue})
             AND g.amount_scaled < 0
             AND g.voucher_number IN (
                 SELECT p.voucher_number FROM {schemaPrefix}gl p
                 WHERE p.voucher_number IS NOT NULL
                   AND {populationScopePredicate(context, "p")}
                   AND p.amount_scaled > 0)
             AND g.voucher_number NOT IN (
                 SELECT d.voucher_number FROM {schemaPrefix}gl d
                 JOIN {schemaPrefix}account_mapping md ON md.account_number = d.account_number
                 {CategoryJoin(schemaPrefix, "md", "td")}
                 WHERE d.voucher_number IS NOT NULL
                   AND {populationScopePredicate(context, "d")}
                   AND d.amount_scaled > 0
                   AND td.semantic_role IN ({counterparts})))
            """;
    }

    private string CategoryListParams(FilterSqlParameterPlanBuilder command)
    {
        return string.Join(", ", RevenueCounterpartRoles.Select(role => NextParam(command, role)));
    }

    /// <summary>
    /// 收入的一般對方科目分類：應收款項、現金與預收款項，只有這一個定義。
    /// 未預期借貸組合用它判斷同一張傳票有沒有一般的對方科目；科目配對也用它判斷案件有沒有設定對方科目。
    /// </summary>
    internal static IReadOnlyList<string> RevenueCounterpartRoles { get; } =
    [
        AccountCategoryBuiltIns.ReceivablesRole,
        AccountCategoryBuiltIns.CashRole,
        AccountCategoryBuiltIns.ReceiptInAdvanceRole
    ];

    /// <summary>
    /// 科目配對分析（account_pair）的三種模式。借方或貸方的判定都一樣：`amount_scaled >= 0` 算借方，`&lt; 0` 算貸方。
    /// debitAnchor 與 creditAnchor 兩種模式以其中一側為準：除了那一側符合分類的分錄，也列出同一張傳票另一側的全部分錄。
    /// </summary>
    public string AccountPair(
        FilterSqlParameterPlanBuilder command,
        string pairMode,
        IReadOnlyList<string> debitCategoryIds,
        IReadOnlyList<string> creditCategoryIds,
        FilterConditionContext context,
        string schemaPrefix = "", string? categorySelection = null)
    {
        var side = CategorySides(command, debitCategoryIds, creditCategoryIds, context, schemaPrefix, categorySelection);

        return pairMode switch
        {
            AccountPairModes.Exact =>
                $"({side.DocHasDebitSide()} AND {side.DocHasCreditSide()} AND ({side.RowIsDebitSide()} OR {side.RowIsCreditSide()}))",
            AccountPairModes.DebitAnchor =>
                $"({side.DocHasDebitSide()} AND ({side.RowIsDebitSide()} OR g.amount_scaled < 0))",
            AccountPairModes.CreditAnchor =>
                $"({side.DocHasCreditSide()} AND ({side.RowIsCreditSide()} OR g.amount_scaled >= 0))",
            _ => throw new InvalidOperationException($"未處理的配對模式 {pairMode}。")
        };
    }

    /// <summary>
    /// 考量特殊科目類別配對（special_account_category_pair）：借方與貸方各選一組分類，另有「另一側沒有」的模式。
    /// 以下 A 是借方類別，B 是貸方類別。借方或貸方的判定和 <see cref="AccountPair"/> 相同：
    /// `amount_scaled >= 0` 算借方，`&lt; 0` 算貸方。三種模式：
    ///   drAndCr：傳票同時有 A 借與 B 貸。標記 A 借的列與 B 貸的列。
    ///   drNotCr：傳票有 A 借，而且沒有任何 B 貸。標記 A 借的列，以及同一張傳票貸方那一側的全部列。
    ///   notDrCr：傳票有 B 貸，而且沒有任何 A 借。標記 B 貸的列，以及同一張傳票借方那一側的全部列。
    /// 「沒有」在這個方法裡用 NOT 加在 DocHas 的片段前面判斷，呼叫端不必處理。
    /// 後兩種模式照舊版連另一側一起標記，和 <see cref="AccountPair"/> 的 debitAnchor、creditAnchor 模式相同。
    /// drAndCr 的 SQL 和 AccountPair 的 exact 模式相同。兩者在畫面上是不同的條件，模式名稱不同，這裡還多了「沒有」的模式，
    /// 所以刻意各寫一份；共用的只有 <see cref="CategorySides"/> 產生的四個片段。
    /// </summary>
    public string SpecialAccountCategoryPair(
        FilterSqlParameterPlanBuilder command,
        string pairMode,
        IReadOnlyList<string> debitCategoryIds,
        IReadOnlyList<string> creditCategoryIds,
        FilterConditionContext context,
        string schemaPrefix = "", string? categorySelection = null)
    {
        var side = CategorySides(command, debitCategoryIds, creditCategoryIds, context, schemaPrefix, categorySelection);

        return pairMode switch
        {
            SpecialAccountCategoryPairModes.DrAndCr =>
                $"({side.DocHasDebitSide()} AND {side.DocHasCreditSide()} AND ({side.RowIsDebitSide()} OR {side.RowIsCreditSide()}))",
            SpecialAccountCategoryPairModes.DrNotCr =>
                $"({side.DocHasDebitSide()} AND NOT {side.DocHasCreditSide()} AND ({side.RowIsDebitSide()} OR g.amount_scaled < 0))",
            SpecialAccountCategoryPairModes.NotDrCr =>
                $"({side.DocHasCreditSide()} AND NOT {side.DocHasDebitSide()} AND ({side.RowIsCreditSide()} OR g.amount_scaled >= 0))",
            _ => throw new InvalidOperationException($"未處理的特殊科目類別配對模式 {pairMode}。")
        };
    }

    /// <summary>
    /// 借方類別 A 與貸方類別 B 的四個 SQL 片段，AccountPair 與 SpecialAccountCategoryPair 共用。
    /// RowIs 開頭的兩個片段判斷本列 g 是不是在那一側、而且科目屬於所選的任一分類；
    /// DocHas 開頭的兩個片段判斷同一張傳票裡有沒有這樣的分錄。「沒有」的模式是在 DocHas 的片段前面加 NOT。
    /// 傳票號碼是 NULL 的分錄不屬於任何傳票：DocHas 的片段是假，前面加 NOT 就是真。
    /// 每個片段每呼叫一次，就把那一側的全部分類加進參數，所以呼叫的順序就是參數的順序。
    /// 分類清單已由 Domain 去掉重複並排序，同一組分類不管原本怎麼排，產生的 SQL 都逐字相同。
    /// 兩側都只判斷「是否屬於清單裡的任一分類」（`IN`），不把兩側的分類兩兩配對，
    /// 所以同一列 GL 不會因為選了多個分類而重複輸出。
    /// 兩個方法共用這些片段，但各自決定怎麼組合，彼此的 SQL 互不影響。
    /// </summary>
    private CategorySidePredicates CategorySides(
        FilterSqlParameterPlanBuilder command,
        IReadOnlyList<string> debitCategoryIds,
        IReadOnlyList<string> creditCategoryIds,
        FilterConditionContext context,
        string schemaPrefix = "", string? categorySelection = null)
    {
        string RowIsDebitSide() =>
            $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}account_mapping m
                     {CategoryJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_number = g.account_number
                       AND {CategorySelectionRoles(command, "tm", debitCategoryIds, "借方", schemaPrefix, categorySelection)})
             AND g.amount_scaled >= 0)
            """;

        string RowIsCreditSide() =>
            $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}account_mapping m
                     {CategoryJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_number = g.account_number
                       AND {CategorySelectionRoles(command, "tm", creditCategoryIds, "貸方", schemaPrefix, categorySelection)})
             AND g.amount_scaled < 0)
            """;

        string DocHasDebitSide() =>
            $"""
            (g.voucher_number IS NOT NULL
             AND g.voucher_number IN (
                 SELECT d.voucher_number FROM {schemaPrefix}gl d
                 JOIN {schemaPrefix}account_mapping md ON md.account_number = d.account_number
                 {CategoryJoin(schemaPrefix, "md", "td")}
                 WHERE d.voucher_number IS NOT NULL
                   AND {populationScopePredicate(context, "d")}
                   AND {CategorySelectionRoles(command, "td", debitCategoryIds, "借方", schemaPrefix, categorySelection)}
                   AND d.amount_scaled >= 0))
            """;

        string DocHasCreditSide() =>
            $"""
            (g.voucher_number IS NOT NULL
             AND g.voucher_number IN (
                 SELECT c.voucher_number FROM {schemaPrefix}gl c
                 JOIN {schemaPrefix}account_mapping mc ON mc.account_number = c.account_number
                 {CategoryJoin(schemaPrefix, "mc", "tc")}
                 WHERE c.voucher_number IS NOT NULL
                   AND {populationScopePredicate(context, "c")}
                   AND {CategorySelectionRoles(command, "tc", creditCategoryIds, "貸方", schemaPrefix, categorySelection)}
                   AND c.amount_scaled < 0))
            """;

        return new CategorySidePredicates(
            RowIsDebitSide, RowIsCreditSide, DocHasDebitSide, DocHasCreditSide);
    }

    /// <summary>
    /// 依條件指定的選取方式比對：分類本身（node）、下層（subtree）或審計角色（role）。
    /// 沒有指定選取方式時和 role 相同，比對審計角色 semantic_role，角色相同的自訂分類都會命中。
    /// 分類的 ID 一律用參數綁定；改分類的顯示名稱不影響命中。
    /// 分類清單是空的就直接丟出例外。「沒有」的模式在 DocHas 的片段前面加 NOT，遇到空清單會變成全部命中，
    /// 所以一定要在組 SQL 之前擋下：驗證器先擋一次，這裡是最後一道。
    /// </summary>
    private static string CategorySelectionRoles(
        FilterSqlParameterPlanBuilder command,
        string categoryAlias,
        IReadOnlyList<string> categoryIds,
        string sideLabel,
        string schemaPrefix, string? categorySelection = null)
    {
        if (categoryIds.Count == 0)
        {
            throw new InvalidOperationException($"科目配對條件的{sideLabel}分類集合為空，無法編譯。");
        }

        var parameters = string.Join(", ", categoryIds.Select(categoryId => NextParam(command, categoryId)));
        if (categorySelection == "node") return $"{categoryAlias}.category_id IN ({parameters})";
        if (categorySelection == "subtree") return $"{categoryAlias}.category_id IN (SELECT descendant_id FROM {schemaPrefix}account_category_path WHERE ancestor_id IN ({parameters}))";
        if (categorySelection is not (null or "role")) throw new InvalidOperationException("分類選取方式無效。");
        return $"{categoryAlias}.semantic_role IN ("
            + $"SELECT ts.semantic_role FROM {schemaPrefix}account_category ts "
            + $"WHERE ts.category_id IN ({parameters}))";
    }

    /// <summary>產生四個片段的函式：借方與貸方，各有「本列」與「同一張傳票」兩種。呼叫時才把參數加進清單。</summary>
    private sealed record CategorySidePredicates(
        Func<string> RowIsDebitSide,
        Func<string> RowIsCreditSide,
        Func<string> DocHasDebitSide,
        Func<string> DocHasCreditSide);

    // 驗證器已先檢查過同樣的條件；走到這裡是驗證器與執行時檢查不一致的程式錯誤。畫面只顯示非預期錯誤那一句，
    // 檢查代號記進支援日誌。check 只能是程式寫死的代號。
    private static JetActionException Invalid(string check) => FilterCompileCheck.Create(check);

    /// <summary>週末（weekend_*）：日期落在案件設定的週末日，補班日仍納入。dateExpr 只接受本層常數或欄位目錄解析出的欄位。</summary>
    public string Weekend(string dateExpr, IReadOnlyList<int>? nonWorkingDays)
    {
        return dialect.WeekendPredicate(dateExpr, NonWorkingDays.Resolve(nonWorkingDays));
    }

    /// <summary>假日（holiday_*）：日期落在已上傳假日檔的假日。</summary>
    public string Holiday(string dateExpr, string schemaPrefix = "") => CalendarDay(dateExpr, "holiday", schemaPrefix);

    /// <summary>補班日：日期落在已上傳假日檔的補班日。</summary>
    public string MakeupDay(string dateExpr, string schemaPrefix = "") => CalendarDay(dateExpr, "makeup", schemaPrefix);

    private static string CalendarDay(string dateExpr, string dayType, string schemaPrefix) =>
        $"EXISTS (SELECT 1 FROM {schemaPrefix}calendar_day d WHERE d.day_type = '{dayType}' AND d.date = {dateExpr})";

    /// <summary>文字欄位空白：NULL 或去空白後為空。去空白的字元集合和 .NET 相同（dialect.Trim）。</summary>
    public string BlankText(string columnExpr) => SqlBlankText.IsBlank(dialect, columnExpr);

    /// <summary>空白摘要（blank_description）：和第五步「傳票摘要」的「空白」同一個判斷。</summary>
    public string BlankDescription() => BlankText("g.description");

    /// <summary>非授權編製人員（non_authorized_preparer）：created_by 非空白且不在授權清單。
    /// 人員識別值的比對去頭尾空白、不分大小寫，和人員清單、編製等於核准同一規則。
    /// 條件開頭先用 EXISTS 確認授權清單有資料。清單是空的時候，NOT IN 空清單會變成全部命中，
    /// 所以清單是空的就讓整個條件不成立、沒有命中，和 prescreen.run 回報 na 的結果一致。
    /// 即使驗證器或 handler 的前置檢查被跳過，這個條件也不會誤命中。</summary>
    public string NonAuthorizedPreparer(string schemaPrefix = "") =>
        $"(EXISTS (SELECT 1 FROM {schemaPrefix}authorized_preparer) " +
        $"AND {SqlBlankText.IsNotBlank(dialect, "g.created_by")} " +
        $"AND {PersonIdentity.Key(dialect, "g.created_by")} NOT IN (SELECT {PersonIdentity.Key(dialect, "name")} FROM {schemaPrefix}authorized_preparer))";

    /// <summary>filter drCrOnly 條件。</summary>
    public string DrCrOnly(FilterSqlParameterPlanBuilder command, string drCr)
    {
        var p = NextParam(command, drCr == "debit" ? "DEBIT" : "CREDIT");
        return $"g.dr_cr = {p}";
    }

    /// <summary>filter manualAuto 條件；is_manual 為 NULL（來源未提供旗標）的列永不匹配。</summary>
    public string ManualAuto(FilterSqlParameterPlanBuilder command, bool isManual)
    {
        var p = NextParam(command, isManual ? 1 : 0);
        return $"g.is_manual = {p}";
    }

    /// <summary>
    /// 季末前借記收入（revenue_debit_near_quarter_end）：科目屬於收入類、在借方（amount_scaled >= 0），
    /// 而且 gl_date 落在任一個季底前的日期區間內。日期區間由 Domain 的 QuarterEndWindows 算出，起訖日用參數綁定。
    /// 沒有任何日期區間時不會命中，例如天數不合法，或區間和查核期間沒有重疊。需要已匯入科目配對。
    /// </summary>
    public string RevenueDebitNearQuarterEnd(
        FilterSqlParameterPlanBuilder command,
        IReadOnlyList<QuarterEndWindows.Window> windows,
        string schemaPrefix = "")
    {
        if (windows.Count == 0)
        {
            return "1 = 0";
        }

        var revenue = NextParam(command, AccountCategoryBuiltIns.RevenueRole);
        var windowClauses = string.Join(" OR ", windows.Select(w =>
            $"(g.gl_date >= {NextParam(command, w.FromIso)} AND g.gl_date <= {NextParam(command, w.ToIso)})"));

        return $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}account_mapping m
                     {CategoryJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_number = g.account_number AND tm.semantic_role = {revenue})
             AND g.amount_scaled >= 0
             AND ({windowClauses}))
            """;
    }

    /// <summary>
    /// 收入無一般對方科目（revenue_without_normal_counterpart）：本列是收入類的貸方（amount_scaled &lt; 0），
    /// 而且同一張傳票裡，沒有任何借方（amount_scaled >= 0）分錄的科目屬於應收款項或預收款。現金不算一般對方科目。
    /// 和未預期借貸組合(<see cref="UnexpectedAccountPair"/>)不同：對方科目不含現金、0 元的借方也算對方科目，
    /// 也不要求傳票裡有正金額分錄。現銷傳票與沒有正金額分錄的傳票只有這條挑中；應收或預收借方只有 0 元、另有其他正金額分錄時，只有未預期借貸組合挑中。
    /// 需科目配對已匯入。
    /// </summary>
    public string RevenueWithoutNormalCounterpart(
        FilterSqlParameterPlanBuilder command,
        FilterConditionContext context,
        string schemaPrefix = "")
    {
        var revenue = NextParam(command, AccountCategoryBuiltIns.RevenueRole);
        var receivables = NextParam(command, AccountCategoryBuiltIns.ReceivablesRole);
        var receiptInAdvance = NextParam(command, AccountCategoryBuiltIns.ReceiptInAdvanceRole);

        return $"""
            (g.voucher_number IS NOT NULL
             AND EXISTS (SELECT 1 FROM {schemaPrefix}account_mapping m
                     {CategoryJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_number = g.account_number AND tm.semantic_role = {revenue})
             AND g.amount_scaled < 0
             AND g.voucher_number NOT IN (
                 SELECT d.voucher_number FROM {schemaPrefix}gl d
                 JOIN {schemaPrefix}account_mapping md ON md.account_number = d.account_number
                 {CategoryJoin(schemaPrefix, "md", "td")}
                 WHERE d.voucher_number IS NOT NULL
                   AND {populationScopePredicate(context, "d")}
                   AND d.amount_scaled >= 0
                   AND td.semantic_role IN ({receivables}, {receiptInAdvance})))
            """;
    }

    /// <summary>
    /// 收入之人工分錄(manual_revenue_entry):科目分類 = Revenue 且 is_manual = 1
    /// (來源未提供人工旗標的列為 NULL,永不匹配,同 manualAuto)。需科目配對已匯入。
    /// </summary>
    public string ManualRevenueEntry(FilterSqlParameterPlanBuilder command, string schemaPrefix = "")
    {
        var revenue = NextParam(command, AccountCategoryBuiltIns.RevenueRole);
        return $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}account_mapping m
                     {CategoryJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_number = g.account_number AND tm.semantic_role = {revenue})
             AND g.is_manual = 1)
            """;
    }

    /// <summary>
    /// 科目配對一律用 category_id 連到分類表；寫入科目配對的兩條路徑都一定會寫 category_id。
    /// 連到分類表之後，規則比對的是 semantic_role 或 category_id，不比對顯示名稱。
    /// </summary>
    private static string CategoryJoin(
        string schemaPrefix,
        string mappingAlias,
        string categoryAlias) =>
        $"JOIN {schemaPrefix}account_category {categoryAlias} ON "
        + $"{categoryAlias}.category_id = {mappingAlias}.category_id";

    /// <summary>
    /// 特定金額尾數（trailing_digits）：把金額主單位整數（捨去小數）的末 k 位和審計員指定的 k 位樣態逐字比對，
    /// 任一組樣態相等就命中；小數位、顯示補零、scale 與格式化結果都不納入比對。
    ///
    /// 相當於舊版 IDEA 的 @Right(@Str(@int(amount),1,0), k) = pattern。以整數運算表達:
    ///   整數化金額 intAmount = ABS(amount_scaled) / scale   ——整數除法,等同 @int(ABS(amount)),捨去小數;
    ///   尾數比對   intAmount % 10^k = 樣態之數值             ——末 k 位相等。
    /// 字尾長度:數字要「以 k 位樣態結尾」須本身至少有 k 位(等同 @Right 在字串短於 k 時回傳整串、
    /// 長度不符即不相等)。故 k≥2 時加 intAmount ≥ 10^(k-1);k=1 時每個整數(含 0)都 ≥1 位、不設下界
    /// (與舊版 @Right(@Str(0,1,0),1) = "0" 對整數 0 命中樣態 "0" 一致)。
    /// 樣態已由 validator 保證為純數字。
    /// </summary>
    public string TrailingDigits(
        FilterSqlParameterPlanBuilder command,
        IReadOnlyList<string> patterns,
        int moneyScale,
        string amountColumn = "g.amount_scaled")
    {
        var scale = NextParam(command, (long)moneyScale);

        var clauses = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p =>
            {
                var trimmed = p.Trim();
                var digitCount = trimmed.Length;
                var tenK = 1L;
                for (var i = 0; i < digitCount; i++)
                {
                    tenK *= 10;
                }

                var modulus = NextParam(command, tenK);
                var tail = NextParam(command, long.Parse(trimmed, System.Globalization.CultureInfo.InvariantCulture));
                // 整數化金額：捨去小數的主單位整數(= @int(ABS(amount)))。
                var intAmount = dialect.IntegerQuotient($"ABS({amountColumn})", scale);
                var tailMatch = $"{intAmount} % {modulus} = {tail}";

                if (digitCount == 1)
                {
                    // k=1：整數末位比對,無長度下界(每個整數含 0 都 ≥1 位)。
                    return $"({tailMatch})";
                }

                // k≥2：整數須至少 k 位(≥ 10^(k-1)),否則字串短於樣態、長度不符即不相等。
                var minKDigits = NextParam(command, tenK / 10);
                return $"({intAmount} >= {minKDigits} AND {tailMatch})";
            })
            .ToList();

        return clauses.Count == 0 ? "1 = 0" : $"({string.Join(" OR ", clauses)})";
    }

    /// <summary>
    /// 整數部分包含任一串數字：舊版 JET「特定數字篩選」的「包含」。相當於舊版 IDEA 的
    /// <c>@RegExpr(@AllTrim(@Upper(@str(@int(amount),1,0))), "888|28")</c>，不同的是 JET 先取絕對值、輸入當成一般數字比對，
    /// 和整數尾數相同地忽略正負號及小數：整數化金額 ABS(amount_scaled) / scale 轉成文字，包含任一串數字即命中。
    /// 樣態已由 validator 保證為純數字；文字轉換用 VARCHAR(40)（BIGINT 最多 19 位）。
    /// </summary>
    public string IntegerPartContains(
        FilterSqlParameterPlanBuilder command,
        IReadOnlyList<string> patterns,
        int moneyScale,
        string amountColumn = "g.amount_scaled")
    {
        var scale = NextParam(command, (long)moneyScale);
        var integerText = $"CAST({dialect.IntegerQuotient($"ABS({amountColumn})", scale)} AS VARCHAR(40))";
        var clauses = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => dialect.ContainsIgnoreCase(integerText, NextParam(command, p.Trim())))
            .ToList();

        return clauses.Count == 0 ? "1 = 0" : $"({string.Join(" OR ", clauses)})";
    }

    /// <summary>
    /// 金額尾數連續 N 個 0：預篩選「分錄金額中有連續 0 的尾數」與第五步金額的「尾數連續多個 0」共用。
    /// 就是 N 個 0 的尾數樣態，另加整數部分不是 0。N 至少 2 時尾數樣態本身要求整數至少 N 位，這個下界不改變結果；
    /// N 為 1 時尾數樣態會讓整數部分為 0 的金額命中，由下界排除。
    /// </summary>
    public string ZeroRun(
        FilterSqlParameterPlanBuilder command,
        int zeros,
        int moneyScale,
        string amountColumn = "g.amount_scaled")
    {
        var tail = TrailingDigits(command, [TrailingZeroThreshold.ZeroPattern(zeros)], moneyScale, amountColumn);
        return $"({tail} AND ABS({amountColumn}) >= {NextParam(command, (long)moneyScale)})";
    }

    /// <summary>
    /// 編製與核准同一人(preparer_equals_approver):created_by 與 approved_by 皆非空白
    /// 且(忽略大小寫與前後空白)相等。createdBy/approvedBy 未配對時對應欄為 NULL,零命中。
    /// </summary>
    public string PreparerEqualsApprover() =>
        $"({SqlBlankText.IsNotBlank(dialect, "g.created_by")} " +
        "AND g.approved_by IS NOT NULL " +
        $"AND {PersonIdentity.Key(dialect, "g.created_by")} = {PersonIdentity.Key(dialect, "g.approved_by")})";

    /// <summary>
    /// 文字欄位包含任一關鍵字：逐字比對、不分大小寫、關鍵字去頭尾空白，NULL 視為空字串。摘要特定描述與第五步「包含任一文字」
    /// 都用這一個方法；沒有可用的關鍵字時不命中。
    /// </summary>
    public string ContainsAnyKeyword(
        FilterSqlParameterPlanBuilder command,
        string columnExpr,
        IReadOnlyList<string> keywords)
    {
        var clauses = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => dialect.ContainsIgnoreCase(
                columnExpr, NextParam(command, SourceText.ComparisonKey(k))))
            .ToList();

        return clauses.Count == 0 ? "1 = 0" : $"({string.Join(" OR ", clauses)})";
    }

    /// <summary>參數名以現有計畫項目數量遞增，避免跨片段衝突。</summary>
    private static string NextParam(FilterSqlParameterPlanBuilder command, object value) =>
        command.AddValue(value);
}
