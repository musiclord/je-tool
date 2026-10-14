using System.Text.Json.Nodes;

namespace Jet.Harness.TaiwanSme;

/// <summary>一個要存進第五步的篩選情境，以及獨立算出的預期命中分錄。</summary>
internal sealed record SmeScenario(string Name, JsonObject Wire, Func<SmeOracle, HashSet<string>> Expected);

/// <summary>
/// 兩組情境：KCT 十項（SQLite 案件）與審計員自訂的十項（DuckDB 案件）。條件形狀比照前端
/// newFilterRule 的預設欄位與送出前的整理，讓存進案件的定義和在畫面上操作存下的一樣。
/// categoryIds 依分類名稱查表，分類 ID 由後端在分類設定儲存時產生。
/// </summary>
internal static class SmeScenarios
{
    private static readonly string KeywordText = string.Join(",", SmeOracle.DefaultKeywords);

    public static IReadOnlyList<SmeScenario> Kct(IReadOnlyDictionary<string, string> categoryIds)
    {
        string Id(string label) => categoryIds[label];
        return
        [
            KctScenario("KCT A 季底前 10 天借記收入",
                "季底與期末前 10 天內收入科目有借方分錄，可能是認列後的不當迴轉；案件團隊依客戶退貨週期訂為 10 天。",
                [Rule("revenueDebitNearQuarterEnd", rule => rule["windowDays"] = "10")], ["A"],
                oracle => oracle.Select(line => oracle.Role(line) == "revenue" && line.Debit > 0m && SmeOracle.NearQuarterEnd(line.PostDate, 10))),
            KctScenario("KCT B 借記固定資產且貸記費用",
                "借固定資產、貸營業費用或製造費用的組合不符合正常營業流程，可能把費用不當資本化；在建工程另列分類，不在固定資產底下。",
                [Rule("specialAccountCategoryPair", rule =>
                {
                    rule["debitCategoryIds"] = new JsonArray(Id(SmeChart.FixedAssets));
                    rule["creditCategoryIds"] = new JsonArray(Id(SmeChart.OperatingExpenses), Id(SmeChart.ManufacturingOverhead));
                })], ["B"],
                oracle => oracle.Select(line => oracle.DebitAndCredit(line,
                    [SmeChart.FixedAssets], [SmeChart.OperatingExpenses, SmeChart.ManufacturingOverhead]))),
            KctScenario("KCT C 貸記收入但借方非應收或預收",
                "收入貸方的對方不是應收票據、應收帳款或預收款項，屬於不尋常的收入過帳；現金銷貨也會列入，由審計員覆核。",
                [Rule("revenueWithoutNormalCounterpart")], ["C"],
                oracle => oracle.Select(line => oracle.RevenueCreditWithout(line, ["receivables", "receipt_in_advance"]))),
            KctScenario("KCT D 收入之人工分錄",
                "這家公司的銷貨收入由 ERP 銷貨單自動拋轉，人工收入分錄不屬於正常流程。",
                [Rule("manualRevenueEntry")], ["D"],
                oracle => oracle.Select(line => oracle.Role(line) == "revenue" && line.IsManual)),
            KctScenario("KCT E 總經理與財務經理建立之分錄",
                "高階主管不預期會自己編製傳票，且有能力直接或間接操縱會計紀錄。",
                [FieldValue("createBy", "in", values: [SmePeople.GeneralManager, SmePeople.FinanceManager])], ["E"],
                oracle => oracle.Select(line => line.CreatedBy is SmePeople.GeneralManager or SmePeople.FinanceManager)),
            KctScenario("KCT F 特定摘要（預設字詞加更正、補登）",
                "摘要出現調整、迴轉、沖銷、更正、補登等字詞的分錄，常是期末調整或事後補記。",
                [FieldValue("description", "contains", values: (KeywordText + ",更正,補登").Split(','))], ["F"],
                oracle => oracle.Select(line => SmeOracle.ContainsAny(line.Description, SmeOracle.DefaultKeywords.Append("更正").Append("補登")))),
            KctScenario("KCT G 空白摘要之人工分錄",
                "人工分錄沒有寫摘要，無法從帳上看出交易內容；系統拋轉的空白摘要另行排除。",
                [FieldValue("description", "isBlank"), Rule("manualAuto")], ["G", null],
                oracle => oracle.Select(line => SmeOracle.IsBlank(line.Description) && line.IsManual)),
            KctScenario("KCT H 金額尾數 000000 或 999999",
                "整數百萬或刻意落在門檻下的金額，常見於估計或人為安排的分錄。",
                [FieldValue("amount", "endsWithDigits", value: "000000,999999")], ["H"],
                oracle => oracle.Select(line =>
                {
                    var whole = SmeOracle.WholeAmount(line);
                    return whole >= 100_000 && (whole % 1_000_000 == 0 || whole % 1_000_000 == 999_999);
                })),
            KctScenario("KCT I 非營業日之分錄",
                "週末與國定假日入帳的分錄；2 月 8 日補班日照常上班，不列入。",
                [NonBusinessDayGroup()], ["I"],
                oracle => oracle.Select(line => SmeCalendar.IsNonBusinessDay(line.PostDate))),
            KctScenario("KCT J 編製與核准同一人",
                "同一人編製並核准傳票，缺少職能分工。",
                [Rule("preparerEqualsApprover")], ["J"],
                oracle => oracle.Select(line => SmeOracle.SamePerson(line.CreatedBy, line.ApprovedBy))),
        ];
    }

    public static IReadOnlyList<SmeScenario> Custom(IReadOnlyDictionary<string, string> categoryIds)
    {
        string Id(string label) => categoryIds[label];
        var expenseRoots = new[] { SmeChart.OperatingExpenses, SmeChart.ManufacturingOverhead };
        return
        [
            CustomScenario("年底大額人工分錄",
                "12 月 20 日以後入帳、金額 100 萬元以上的人工分錄，是期末調整與操縱的高風險區。",
                [[
                    Rule("dateRange", rule => { rule["from"] = "2025-12-20"; rule["to"] = "2025-12-31"; }),
                    Rule("manualAuto"),
                    Rule("numRange", rule => rule["from"] = "1000000"),
                ]],
                oracle => oracle.Select(line =>
                    line.PostDate >= new DateOnly(2025, 12, 20) && line.IsManual && line.Amount >= 1_000_000m)),
            CustomScenario("非營業日重大交易或總經理經手",
                "非營業日入帳且金額 50 萬元以上，或總經理親自建立的分錄，兩者擇一即列入覆核。",
                [
                    [NonBusinessDayGroup(), Rule("numRange", rule => rule["from"] = "500000")],
                    [Rule("text", rule =>
                    {
                        rule["field"] = "createBy";
                        rule["mode"] = "exact";
                        rule["keywords"] = SmePeople.GeneralManager;
                    })],
                ],
                oracle => oracle.Select(line =>
                    (SmeCalendar.IsNonBusinessDay(line.PostDate) && line.Amount >= 500_000m)
                    || line.CreatedBy == SmePeople.GeneralManager),
                secondGroupJoin: "OR"),
            CustomScenario("關係人資金往來",
                "借記其他應收款-關係人的傳票，列出整張傳票檢查資金流向與核准。",
                [[Voucher("debit", "any", FieldValue("accNum", "in", values: ["1191"]))]],
                oracle => oracle.Select(line =>
                    oracle.VoucherOf(line).Any(other => other.Debit > 0m && other.AccountCode == "1191"))),
            CustomScenario("未授權人員建立之分錄",
                "傳票建立人員不在 ERP 權限清單，包括總經理、工讀生與帳號未停用的離職人員。",
                [[Rule("prescreen", rule => rule["prescreenKey"] = "nonAuthorizedPreparer")]],
                oracle => oracle.Prescreen()["nonAuthorizedPreparer"]),
            CustomScenario("應付帳款轉列其他收入",
                "同一張傳票借記應付帳款、貸記其他收入，可能是把未付貨款私下轉成收入。",
                [[
                    FieldValue("accNum", "in", values: ["2170", "2171", "2180"], drCr: "debit"),
                    FieldValue("accNum", "in", values: ["7190"], drCr: "credit"),
                ]],
                oracle => oracle.Select(line =>
                    line.Debit > 0m && line.AccountCode is "2170" or "2171" or "2180"
                    && oracle.VoucherOf(line).Any(other => other.Credit > 0m && other.AccountCode == "7190")),
                voucherWitnesses: true),
            CustomScenario("現金及銀行支出未寫摘要",
                "貸記現金或銀行存款卻沒有摘要的分錄，無法從帳上判斷付款用途。",
                [[
                    AccountSide("credit", Id(SmeChart.Cash)),
                    FieldValue("description", "isBlank"),
                ]],
                oracle => oracle.Select(line =>
                    line.Credit > 0m && oracle.InSubtree(line, SmeChart.Cash) && SmeOracle.IsBlank(line.Description))),
            CustomScenario("分錄 20 筆以下的編製人員",
                "全年分錄很少的編製人員不熟悉入帳流程，或是不應入帳的人員。",
                [[Rule("customPreparerEntryCount", rule => rule["maxEntries"] = "20")]],
                oracle =>
                {
                    var counts = oracle.PreparerCounts();
                    return oracle.Select(line => !SmeOracle.IsBlank(line.CreatedBy) && counts[line.CreatedBy.Trim()] <= 20);
                }),
            CustomScenario("週末或假日核准",
                "在週末或國定假日核准的傳票，核准程序可能沒有正常執行。",
                [[
                    Rule("prescreen", rule => rule["prescreenKey"] = "weekendApproval"),
                    Rule("prescreen", rule => { rule["prescreenKey"] = "holidayApproval"; rule["join"] = "OR"; }),
                ]],
                oracle => oracle.Select(line => line.ApprovalDate is { } approval
                    && (SmeCalendar.IsWeekend(approval) || SmeCalendar.IsHoliday(approval)))),
            CustomScenario("費用借方整千且 10 萬元以上",
                "營業費用與製造費用借方金額是整千且 10 萬元以上，常見於估列或沒有原始憑證的費用。",
                [[
                    AccountSide("debit", Id(SmeChart.OperatingExpenses), Id(SmeChart.ManufacturingOverhead)),
                    FieldValue("amount", "endsWithDigits", value: "000"),
                    Rule("numRange", rule => rule["from"] = "100000"),
                ]],
                oracle => oracle.Select(line =>
                {
                    var whole = SmeOracle.WholeAmount(line);
                    return line.Debit > 0m && oracle.InSubtree(line, expenseRoots)
                        && whole != 0 && whole % 1_000 == 0 && line.Amount >= 100_000m;
                })),
            CustomScenario("營業費用科目之間互轉",
                "借貸兩方都是營業費用的傳票，可能是事後更正或在費用之間移轉。",
                [[Rule("specialAccountCategoryPair", rule =>
                {
                    rule["debitCategoryIds"] = new JsonArray(Id(SmeChart.OperatingExpenses));
                    rule["creditCategoryIds"] = new JsonArray(Id(SmeChart.OperatingExpenses));
                })]],
                oracle => oracle.Select(line =>
                    oracle.DebitAndCredit(line, [SmeChart.OperatingExpenses], [SmeChart.OperatingExpenses]))),
        ];
    }

    /// <summary>前端 newFilterRule(type) 的預設欄位，再套上覆寫值。</summary>
    public static JsonObject Rule(string type, Action<JsonObject>? overrides = null)
    {
        var rule = new JsonObject
        {
            ["join"] = "AND",
            ["type"] = type,
            ["prescreenKey"] = "suspiciousKeywords",
            ["field"] = type == "dateRange" ? "postDate" : type == "numRange" ? "amount" : "description",
            ["keywords"] = type == "trailingDigits" ? "000000" : type == "customKeywords" ? KeywordText : string.Empty,
            ["values"] = new JsonArray(),
            ["mode"] = "contains",
            ["normalization"] = "preserve",
            ["from"] = string.Empty,
            ["to"] = string.Empty,
            ["drCr"] = "debit",
            ["isManual"] = "true",
            ["pairMode"] = type == "specialAccountCategoryPair" ? "drAndCr" : "exact",
        };
        if (type is "accountPair" or "specialAccountCategoryPair")
        {
            rule["categorySelection"] = "subtree";
        }

        rule["debitCategoryIds"] = new JsonArray("builtin.receivables");
        rule["creditCategoryIds"] = new JsonArray("builtin.revenue");
        rule["fieldId"] = string.Empty;
        rule["operator"] = string.Empty;
        rule["amountBasis"] = "absolute";
        rule["value"] = string.Empty;
        rule["digits"] = "3";
        rule["maxEntries"] = type is "customPreparerEntryCount" or "customAccountEntryCount" ? "11" : string.Empty;
        rule["windowDays"] = string.Empty;
        overrides?.Invoke(rule);
        return rule;
    }

    /// <summary>前端 FilterValues.wire 的送出形狀：只帶比較方式實際用到的值。</summary>
    private static JsonObject FieldValue(string field, string op, string[]? values = null, string? drCr = null, string? value = null)
    {
        var rule = new JsonObject
        {
            ["type"] = "fieldValue",
            ["join"] = "AND",
            ["operator"] = op,
            ["includeBlank"] = false,
        };
        rule["field"] = field;
        if (value is not null) rule["value"] = value;
        if (field == "amount") rule["amountBasis"] = "absolute";
        if (values is not null)
        {
            rule["values"] = new JsonArray(values.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
        }

        return drCr is null ? rule : Group(Direction(drCr), rule);
    }

    private static JsonObject AccountSide(string drCr, params string[] categoryIds) => Group(Direction(drCr), new JsonObject
    {
        ["type"] = "accountSide",
        ["join"] = "AND",
        ["categoryMode"] = "is",
        ["categoryIds"] = new JsonArray(categoryIds.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
        ["categorySelection"] = "subtree",
    });

    private static JsonObject Direction(string side) => new() { ["type"] = "drCrOnly", ["drCr"] = side, ["join"] = "AND" };
    private static JsonObject Group(params JsonObject[] rules) => new()
    {
        ["type"] = "group", ["join"] = "AND", ["rules"] = new JsonArray(rules.Select(rule => (JsonNode)rule).ToArray())
    };

    /// <summary>KCT I 的條件括號：總帳日期是非營業日（排除補班日）。</summary>
    private static JsonObject NonBusinessDayGroup() => new()
    {
        ["type"] = "group",
        ["join"] = "AND",
        ["rules"] = new JsonArray(FieldValue("postDate", "isNonBusinessDay")),
    };

    private static JsonObject Voucher(string side, string quantifier, params JsonObject[] rules) => new()
    {
        ["type"] = "voucher",
        ["join"] = "AND",
        ["rules"] = new JsonArray(new[] { Direction(side) }.Concat(rules).Select(rule => (JsonNode)rule).ToArray()),
        ["quantifier"] = quantifier,
    };

    private static SmeScenario KctScenario(
        string name, string rationale, JsonObject[] rules, string?[] letters, Func<SmeOracle, HashSet<string>> expected)
    {
        var wire = new JsonObject
        {
            ["name"] = name,
            ["rationale"] = rationale,
            ["groups"] = new JsonArray(new JsonObject
            {
                ["join"] = "AND",
                ["rules"] = new JsonArray(rules.Select(rule => (JsonNode)rule).ToArray()),
            }),
            ["editorOrigins"] = Origins([letters]),
            ["source"] = "kct",
        };
        return new SmeScenario(name, wire, expected);
    }

    private static SmeScenario CustomScenario(
        string name,
        string rationale,
        JsonObject[][] groups,
        Func<SmeOracle, HashSet<string>> expected,
        string? secondGroupJoin = null,
        bool voucherWitnesses = false)
    {
        var wireGroups = new JsonArray();
        for (var i = 0; i < groups.Length; i++)
        {
            var group = new JsonObject { ["join"] = i > 0 && secondGroupJoin is not null ? secondGroupJoin : "AND" };
            group["rules"] = new JsonArray(groups[i].Select((rule, index) => (JsonNode)(voucherWitnesses && index > 0
                ? new JsonObject { ["type"] = "voucher", ["join"] = "AND", ["quantifier"] = "any", ["rules"] = new JsonArray(rule) }
                : rule)).ToArray());
            wireGroups.Add(group);
        }

        var wire = new JsonObject
        {
            ["name"] = name,
            ["rationale"] = rationale,
            ["groups"] = wireGroups,
            ["editorOrigins"] = Origins(groups.Select(group => group.Select(_ => (string?)null).ToArray()).ToArray()),
        };
        return new SmeScenario(name, wire, expected);
    }

    private static JsonObject Origins(string?[][] letters) => new()
    {
        ["version"] = 1,
        ["legacyKctSource"] = false,
        ["nameIsAutomatic"] = false,
        ["rationaleIsAutomatic"] = false,
        ["groups"] = new JsonArray(letters.Select(group => (JsonNode)new JsonObject
        {
            ["letters"] = new JsonArray(group.Select(letter => letter is null ? null : (JsonNode)JsonValue.Create(letter)!).ToArray()),
        }).ToArray()),
    };
}
