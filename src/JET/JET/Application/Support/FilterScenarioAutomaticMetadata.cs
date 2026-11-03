using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 第五步情境名稱與篩選動機留白時，依條件補上的文字。裁定 2026-10-29-16 與 2026-10-30-14：名稱或動機留白也能儲存，
/// 由系統依條件產生；畫面上自動帶入的文字和這裡產生的文字必須相同，所以這裡逐字照前端
/// filter-step-sources.js 的 <c>automaticMetadata</c>、<c>commonScenarioRationale</c> 與 <c>uniqueAutomaticName</c> 寫，
/// 兩邊共用 tools/tests/fixtures/filter-scenario-automatic-metadata.json 的固定答案。
/// </summary>
internal static class FilterScenarioAutomaticMetadata
{
    /// <summary>前端 <c>GENERATED_RATIONALE_PREFIXES</c> 的兩種句首。</summary>
    internal static readonly IReadOnlyList<string> GeneratedRationalePrefixes =
        ["檢視符合以下條件的分錄：", "依已設定的條件組合篩選分錄，檢視"];

    /// <summary>
    /// 名稱或動機留白時補上自動文字，有填的欄位原樣保留。<paramref name="otherNames"/> 是清單上其他已存情境的名稱，
    /// 自動名稱撞名時照前端附上數字；編輯中的情境不和自己比。
    /// </summary>
    internal static (string Name, string Rationale) Resolve(
        JsonElement scenario,
        FilterScenarioSpec spec,
        FilterScenarioMetadataContext context,
        IEnumerable<string> otherNames)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        var needsName = string.IsNullOrWhiteSpace(spec.Name);
        var needsRationale = string.IsNullOrWhiteSpace(spec.Rationale);
        if (!needsName && !needsRationale)
        {
            return (spec.Name, spec.Rationale);
        }

        var generated = Generate(scenario, context);
        return (
            needsName ? UniqueName(generated.Name, otherNames) : spec.Name,
            needsRationale ? generated.Rationale : spec.Rationale);
    }

    internal static (string Name, string Rationale) Generate(JsonElement scenario, FilterScenarioMetadataContext context)
    {
        var rules = new List<(JsonElement Rule, string? Card)>();
        var groupIndex = 0;
        foreach (var group in Items(scenario, "groups"))
        {
            var cards = CardsOfGroup(scenario, groupIndex++);
            var conditionIndex = 0;
            foreach (var rule in Items(group, "conditions"))
            {
                var card = conditionIndex < cards.Count ? cards[conditionIndex] : null;
                rules.Add((rule, card is not null && FilterCommonConditions.Find(card) is not null ? card : null));
                conditionIndex++;
            }
        }

        if (rules.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        var topics = rules.Select(item => Topic(item.Rule, item.Card, context)).ToList();
        var name = string.Join("、", topics.Take(2)) + (rules.Count > 2 ? "等" + rules.Count + "項條件" : string.Empty);
        if (Length(name) > 64)
        {
            name = topics[0] + "等" + rules.Count + "項條件";
        }

        if (rules.Any(item => item.Card is not null))
        {
            return (name, CommonRationale(rules, context));
        }

        var expression = context.Render(scenario, compact: true).Trim();
        var rationale = expression.Length > 0 ? GeneratedRationalePrefixes[0] + expression + "。" : string.Empty;
        if (Length(rationale) > 180)
        {
            var description = string.Join("；", topics);
            if (Length(description) > 130)
            {
                description = string.Join("；", topics.Take(2)) + "等" + rules.Count + "項條件";
            }

            rationale = GeneratedRationalePrefixes[1] + description + "。";
        }

        return (name, rationale);
    }

    // 有卡片時依卡片順序每張一行，有評估說明的寫評估說明，其餘寫卡片名稱；自訂條件每條一行「自訂：」加條件說明。
    private static string CommonRationale(
        IReadOnlyList<(JsonElement Rule, string? Card)> rules,
        FilterScenarioMetadataContext context)
    {
        var lines = FilterCommonConditions.All
            .Where(card => rules.Any(item => item.Card == card.Key))
            .Select(card => card.DefaultRationale ?? card.Label)
            .ToList();
        lines.AddRange(rules
            .Where(item => item.Card is null)
            .Select(item => "自訂：" + context.RenderCondition(item.Rule, compact: false)));
        return string.Join("\n", lines);
    }

    private static string Topic(JsonElement rule, string? card, FilterScenarioMetadataContext context)
    {
        if (card is not null)
        {
            return FilterCommonConditions.Find(card)!.Label;
        }

        var text = context.RenderCondition(rule, compact: true);
        if (Length(text) <= 28)
        {
            return text;
        }

        if (rule.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            && type.GetString() == "fieldValue")
        {
            var label = FilterConditionRenderer.FieldValueFieldLabel(rule, context.RdeFieldLabels) ?? "欄位條件";
            return Length(label) <= 28 ? label : Take(label, 24) + "…";
        }

        return Take(text, 24) + "…";
    }

    // 撞名時照前端：取名稱前 60 個字，加空白與從 2 起的數字，比對時忽略前後空白與大小寫。
    private static string UniqueName(string name, IEnumerable<string> otherNames)
    {
        if (name.Length == 0)
        {
            return name;
        }

        var existing = otherNames.Select(other => (other ?? string.Empty).Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var candidate = name;
        for (var suffix = 2; existing.Contains(candidate.Trim().ToUpperInvariant()); suffix++)
        {
            candidate = Take(name, 60) + " " + suffix;
        }

        return candidate;
    }

    private static IReadOnlyList<string?> CardsOfGroup(JsonElement scenario, int groupIndex)
    {
        if (!scenario.TryGetProperty("editorOrigins", out var origins) || origins.ValueKind != JsonValueKind.Object
            || !origins.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array
            || groupIndex >= groups.GetArrayLength())
        {
            return [];
        }

        var group = groups[groupIndex];
        if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("cards", out var cards)
            || cards.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return cards.EnumerateArray()
            .Select(card => card.ValueKind == JsonValueKind.String ? card.GetString() : null)
            .ToArray();
    }

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var items)
            && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
            : [];

    // 前端用 Array.from 數字數，這裡同樣以 Unicode 字元計算。
    private static int Length(string value) => value.EnumerateRunes().Count();

    private static string Take(string value, int count) =>
        string.Concat(value.EnumerateRunes().Take(count).Select(rune => rune.ToString()));
}

/// <summary>產生自動名稱與動機時讀回條件需要的案件資料，和工作底稿的條件說明讀同一套對照。</summary>
internal sealed record FilterScenarioMetadataContext(
    IReadOnlyDictionary<string, string> CategoryLabels,
    IReadOnlyDictionary<string, string> RdeFieldLabels,
    IReadOnlyList<AccountCategory> Categories,
    string? PreparationDate,
    string PeriodStart,
    string PeriodEnd)
{
    internal static FilterScenarioMetadataContext Create(
        ProjectDocument document,
        CommittedMapping? glMapping,
        AccountCategorySnapshot categorySnapshot) => new(
            categorySnapshot.Categories.ToDictionary(category => category.CategoryId, category => category.Label),
            (glMapping?.GlOptions?.RdeFields ?? []).ToDictionary(field => field.FieldId, field => field.Label),
            categorySnapshot.Categories,
            document.LastAccountingPeriodDate,
            document.PeriodStart,
            document.PeriodEnd);

    internal string Render(JsonElement scenario, bool compact) =>
        FilterConditionRenderer.Render(scenario, CategoryLabels, RdeFieldLabels, Categories,
            PreparationDate, PeriodStart, PeriodEnd, compact);

    internal string RenderCondition(JsonElement rule, bool compact) =>
        FilterConditionRenderer.RenderCondition(rule, CategoryLabels, RdeFieldLabels, Categories,
            PreparationDate, PeriodStart, PeriodEnd, compact);
}

/// <summary>
/// 常用篩選條件十張卡片的代號、卡片名稱與評估說明，逐字鏡像前端 ui-core.js 的 <c>FILTER_COMMON_CONDITIONS</c>，
/// 順序也相同；自動動機依這個順序列卡片。兩邊是否一致由 FilterCommonConditionsMirrorTests 檢查。
/// </summary>
internal static class FilterCommonConditions
{
    internal sealed record Card(string Key, string Label, string? DefaultRationale);

    internal static IReadOnlyList<Card> All { get; } =
    [
        new("revenueDebitNearQuarterEnd", "在該季度前 X 天借記收入的會計分錄",
            "期末前 X天內收入科目有迴轉分錄，表示收入認列過程中可能存在不適當的調整或更正(案件團隊須提供天數)"),
        new("fixedAssetDebitExpenseCredit", "借記固定資產(PPE，不含在建)且貸記費用之分錄",
            "借記固定資產科目(如不動產、廠房和設備(PPE))但貸記營業費用或維修費用等費用科目之分錄組合不符合公司營業流程的瞭解，因此屬於為非預期的過帳，可能為舞弊類型分錄及其他調整的特質。"),
        new("revenueWithoutNormalCounterpart", "貸方為收入但借方非一般對方科目之分錄",
            "查核團隊已發現與收入科目相關的未預期或不尋常過帳(貸方為收入，但借方科目非為應收票據/帳款、預收貨款/合約負債)"),
        new("manualRevenueEntry", "收入之人工分錄",
            "根據查核核團隊的瞭解，收入科目的過帳一般是自動產生，如為人工分錄非屬正常營業流程的會計分錄"),
        new("specificPreparer", "特定人員(財務長/執行長/高階主管等)建立之分錄",
            "執行長、財務長及高階主管通常不預期會經手會計分錄編製或過帳。且因高階主管所處的位置常有能力能直接或間接操縱會計記錄來進行財務報表舞弊。誘因或壓力愈大，則愈有可能合理化其舞弊行為。"),
        new("specificKeywords", "特定摘要(如迴轉、調整等)", null),
        new("blankDescription", "空白摘要", null),
        new("specificTrailingDigits", "特定尾數(如 999999/000000 結尾)", null),
        new("nonBusinessDay", "非營業日之分錄", null),
        new("preparerEqualsApprover", "編製人員與核准人員相同", null)
    ];

    internal static Card? Find(string key) =>
        All.FirstOrDefault(card => string.Equals(card.Key, key, StringComparison.Ordinal));
}
