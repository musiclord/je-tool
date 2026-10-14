using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 第五步情境單獨出現的句子（使用者 2026-10-13 勾選頁確認）。這幾句不在待修正清單裡，畫面不會另外補下一步，
/// 所以各自保留下一步。
/// </summary>
internal static class FilterScenarioRuleMessages
{
    /// <summary>情境尚未套用目前規則時查結果或產生報告。</summary>
    internal const string NotYetUpgraded =
        "已儲存的篩選情境尚未套用目前規則，請到「進階條件篩選」修改情境後再試。";

    /// <summary>
    /// 一次只存或刪一個情境時，畫面送來的位置或名稱和資料庫對不上（例如上一次移除在寫入後才被取消，畫面沒拿到新清單）。
    /// 這時不寫入，避免改到或刪到別的情境。
    /// </summary>
    internal const string ScenarioListOutOfDate =
        "畫面上的情境清單與已儲存的內容不一致，本次未儲存。請重新開啟案件後再操作。";

    /// <summary>重新計算時某個已存情境不再成立。原因照待修正清單的寫法，句尾句號由這裡統一。</summary>
    internal static string CannotRecalculate(int position, IReadOnlyList<string> reasons) =>
        $"情境 {position} 目前無法重新計算：{string.Join("；", reasons.Select(reason => reason.TrimEnd('。')))}。"
        + "設定仍保留，請到「進階條件篩選」修改後儲存。";
}

/// <summary>開案時檢查已儲存篩選情境能否改用目前規則的四種結果。</summary>
internal enum FilterScenarioUpgradeStatus
{
    /// <summary>沒有情境，或每個情境都已是目前規則版本、母體為查核期間，而且用目前規則檢查合格。</summary>
    Current,

    /// <summary>全部情境都能改用目前規則；<see cref="FilterScenarioUpgradeResult.Scenarios"/> 是改版後的整批。</summary>
    Upgraded,

    /// <summary>至少一個情境不符合目前規則，或條件內容讀不出來；整批不動，逐筆列出原因。</summary>
    NeedsEdit,

    /// <summary>同一批的保存時間不一致，或位置重複、超出範圍；整批不動，也不逐筆列出。</summary>
    Inconsistent
}

/// <param name="Corrupt">損毀條件的原因代號，只記進支援日誌，不進回應。</param>
internal sealed record FilterScenarioUpgradeProblem(
    int Position,
    string Name,
    IReadOnlyList<string> Messages,
    IReadOnlyList<FilterScenarioCorruptReason> Corrupt);

internal sealed record FilterScenarioUpgradeResult(
    FilterScenarioUpgradeStatus Status,
    IReadOnlyList<SavedFilterScenario>? Scenarios,
    IReadOnlyList<FilterScenarioUpgradeProblem> Problems);

/// <summary>
/// 一個已存情境逐筆檢查的結果。<see cref="Messages"/> 是空的就表示可以改用目前規則。
/// 損毀的條件不論幾條，<see cref="Messages"/> 只有一句「條件內容無法讀取」，原因代號在 <see cref="Corrupt"/>。
/// </summary>
internal sealed record FilterScenarioReviewItem(
    SavedFilterScenario Scenario,
    bool IsCurrentVersion,
    bool Unreadable,
    IReadOnlyList<string> Messages,
    IReadOnlyList<FilterScenarioCorruptReason> Corrupt)
{
    internal bool HasProblem => Messages.Count > 0;
}

/// <summary>一次只存、刪或重新檢查一個情境時，其他情境怎麼處理。</summary>
internal enum FilterScenarioWriteMode
{
    /// <summary>儲存或移除：正在編輯的那一個由呼叫端蓋好版本，其他情境的名稱、動機與定義原文一字不改。</summary>
    KeepOthersVerbatim,

    /// <summary>重新檢查：能套用目前規則的情境改用目前規則，不能套用的照原樣保留並列出原因。</summary>
    UpgradeApplicable
}

/// <summary>
/// 一次處理一個情境後要寫回的整批。<see cref="AllValid"/> 為 true 時每一筆都已是目前規則版本，
/// 可以連同命中一起發布；否則只寫定義，命中清空，結果維持鎖住。
/// </summary>
internal sealed record FilterScenarioBatchWrite(
    IReadOnlyList<SavedFilterScenario> Rows,
    bool AllValid,
    IReadOnlyList<FilterScenarioUpgradeProblem> Problems,
    int UpgradedOthers);

/// <summary>
/// 規則版本升級後，判斷已儲存情境能否直接改用目前規則。這是純函式：不讀寫資料庫，
/// 檢查與 <see cref="FilterRunMaterializeService"/> 補算前相同（Parse 加 Validate，forSave 為 false），
/// 避免「判斷可改版、補算卻失敗」。開案時只要一筆不符合，整批都不改版。
/// 母體不是查核期間的情境不自動改版，因為改版會把母體默默改成查核期間。
/// 逐筆檢查 <see cref="Review"/> 由開案與一次處理一個情境的三個動作共用。
/// </summary>
internal static partial class FilterScenarioRuleUpgrade
{
    [GeneratedRegex(@"^filter-\d{4}-\d{2}-\d{2}-v(?<number>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex KnownVersion();

    // 待修正清單的原因句（使用者 2026-10-13 勾選頁確認）。清單格式是「情境 N「名稱」：原因；原因。」，
    // 句尾句號由畫面統一處理；下一步由畫面在清單最後寫一次。
    internal const string NonAuditPeriodMessage =
        "篩選範圍不是查核期間內的分錄，不會自動改為查核期間";

    internal const string UnknownVersionMessage =
        "無法辨識儲存時的規則版本，不會自動更新";

    /// <summary>整個定義讀不出來，或有損毀的條件；同一個情境只寫一次（使用者 2026-10-13 裁定「損毀的情境」）。</summary>
    internal const string UnreadableDefinitionMessage =
        "條件內容無法讀取";

    /// <param name="scenarios">資料庫中已儲存的整批情境。</param>
    /// <param name="validationContext">用目前案件事實建立、母體為查核期間的驗證環境。</param>
    /// <param name="moneyScale">案件金額小數位數，供解析金額條件。</param>
    /// <param name="nowUtc">改版後整批共用的新保存時間。</param>
    internal static FilterScenarioUpgradeResult Evaluate(
        IReadOnlyList<SavedFilterScenario> scenarios,
        FilterValidationContext validationContext,
        int moneyScale,
        DateTimeOffset nowUtc)
    {
        if (scenarios.Count == 0)
        {
            return Current();
        }

        var savedTimes = scenarios
            .Select(scenario => scenario.SavedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var positions = scenarios.Select(scenario => scenario.Position).ToHashSet();
        if (savedTimes != 1
            || positions.Count != scenarios.Count
            || positions.Any(position => position < 1 || position > FilterScenarioLimits.MaxSavedScenarios))
        {
            return Inconsistent();
        }

        // 使用者 2026-10-13 裁定「開案也逐一檢查」：開案和儲存、移除、重新檢查用同一段逐筆檢查，已是目前版本的情境也檢查，
        // 因其他資料改變而不合格的情境開案時就列出，不會解鎖後查詢才失敗。條件內容讀不出來的情境也逐筆列出
        // （「損毀的情境」與「情境卡住」兩項裁定），審計員才能移除它。
        var problems = Problems(Review(scenarios, validationContext, moneyScale));
        if (problems.Count > 0)
        {
            return new FilterScenarioUpgradeResult(FilterScenarioUpgradeStatus.NeedsEdit, null, problems);
        }

        // 全部合格而且都已是目前版本：維持原狀，不換保存時間，也不重算命中。
        if (scenarios.All(RuleLogicVersions.IsCurrent))
        {
            return Current();
        }

        var upgraded = scenarios
            .OrderBy(scenario => scenario.Position)
            .Select(scenario => Stamp(scenario, nowUtc))
            .ToArray();
        return new FilterScenarioUpgradeResult(FilterScenarioUpgradeStatus.Upgraded, upgraded, []);
    }

    /// <summary>
    /// 逐筆檢查：條件內容讀不出來、母體不是查核期間、版本認不得或比目前新，以及用目前規則（forSave 為 false）
    /// 檢查條件內容。已是目前版本的情境也一樣檢查；要不要略過由呼叫端決定。
    /// </summary>
    internal static IReadOnlyList<FilterScenarioReviewItem> Review(
        IReadOnlyList<SavedFilterScenario> scenarios,
        FilterValidationContext validationContext,
        int moneyScale)
    {
        var currentNumber = VersionNumber(RuleLogicVersions.Filter)
            ?? throw new InvalidOperationException("目前的篩選規則版本不符合已知格式。");

        var items = new List<FilterScenarioReviewItem>(scenarios.Count);
        foreach (var scenario in scenarios)
        {
            items.Add(ReviewOne(scenario, validationContext, moneyScale, currentNumber));
        }

        return items;
    }

    /// <summary>
    /// 一次只存、刪或重新檢查一個情境後要寫回的整批。依 <paramref name="batch"/> 現有順序重新編號為 1 到 n，
    /// 全部換成同一個保存時間 <paramref name="nowUtc"/>；原本保存時間不一致或位置超出範圍的舊資料，經過任何一次寫入就恢復一致。
    /// 每一筆都逐筆檢查，讀不出來的情境算成一筆問題，不擋住其他情境。全部沒問題時整批改用目前規則
    /// （名稱、動機與條件不變）；有問題時依 <paramref name="mode"/> 處理其他情境。
    /// </summary>
    /// <param name="editedPosition">這次編輯的那一個在 <paramref name="batch"/> 裡的位置（從 1 起算）；移除與重新檢查為 null。</param>
    internal static FilterScenarioBatchWrite PlanWrite(
        IReadOnlyList<SavedFilterScenario> batch,
        int? editedPosition,
        FilterScenarioWriteMode mode,
        FilterValidationContext validationContext,
        int moneyScale,
        DateTimeOffset nowUtc)
    {
        var renumbered = batch
            .Select((scenario, index) => scenario with { Position = index + 1, SavedUtc = nowUtc })
            .ToArray();
        var review = Review(renumbered, validationContext, moneyScale);
        var problems = Problems(review);
        var allValid = problems.Count == 0;

        var rows = new SavedFilterScenario[renumbered.Length];
        var upgradedOthers = 0;
        for (var index = 0; index < renumbered.Length; index++)
        {
            var item = review[index];
            var stamp = allValid || (mode == FilterScenarioWriteMode.UpgradeApplicable && !item.HasProblem);
            rows[index] = stamp ? Stamp(renumbered[index], nowUtc) : renumbered[index];
            if (stamp && !item.IsCurrentVersion && renumbered[index].Position != editedPosition)
            {
                upgradedOthers++;
            }
        }

        return new FilterScenarioBatchWrite(rows, allValid, problems, upgradedOthers);
    }

    private static FilterScenarioReviewItem ReviewOne(
        SavedFilterScenario scenario,
        FilterValidationContext validationContext,
        int moneyScale,
        int currentNumber)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(scenario.DefinitionJson);
        }
        catch (JsonException)
        {
            return Unreadable(scenario, "definition_not_json");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unreadable(scenario, "definition_not_object");
            }

            var scope = ReadScope(root, out var scopeIsString);
            if (!scopeIsString)
            {
                return Unreadable(scenario, "population_scope_not_string");
            }

            var messages = new List<string>();
            var isAuditPeriod = scope is null
                || string.Equals(scope, GlPopulationScopeValues.AuditPeriod, StringComparison.Ordinal);
            if (!isAuditPeriod)
            {
                messages.Add(NonAuditPeriodMessage);
            }

            var version = root.TryGetProperty("logicVersion", out var versionElement)
                && versionElement.ValueKind == JsonValueKind.String
                    ? versionElement.GetString()
                    : null;
            // 目前版本只缺母體欄位時，改版只是補上查核期間，與讀取時的預設相同。
            var number = version is null ? null : VersionNumber(version);
            if (number is null || number > currentNumber)
            {
                messages.Add(UnknownVersionMessage);
            }

            var (definitionMessages, corrupt) = CheckDefinition(root, validationContext, moneyScale);
            messages.AddRange(definitionMessages);
            return new FilterScenarioReviewItem(scenario, RuleLogicVersions.IsCurrent(scenario), false, messages, corrupt);
        }
    }

    /// <summary>
    /// 待修正清單用的原因：損毀條件的通用句不論幾條，換成一次「條件內容無法讀取」，放在第一條損毀條件的位置；
    /// 其他原因照原樣。開案、一次處理一個情境與重新計算共用。
    /// </summary>
    internal static IReadOnlyList<string> ListReasons(IReadOnlyList<string> messages)
    {
        var reasons = new List<string>(messages.Count);
        var unreadableListed = false;
        foreach (var message in messages)
        {
            if (!FilterScenarioCorruption.IsCorruptMessage(message))
            {
                reasons.Add(message);
            }
            else if (!unreadableListed)
            {
                reasons.Add(UnreadableDefinitionMessage);
                unreadableListed = true;
            }
        }

        return reasons;
    }

    private static FilterScenarioReviewItem Unreadable(SavedFilterScenario scenario, string code) =>
        new(scenario, false, true, [UnreadableDefinitionMessage], [new FilterScenarioCorruptReason(null, null, code)]);

    private static IReadOnlyList<FilterScenarioUpgradeProblem> Problems(IReadOnlyList<FilterScenarioReviewItem> review) =>
        review
            .Where(item => item.HasProblem)
            .Select(item => new FilterScenarioUpgradeProblem(
                item.Scenario.Position, item.Scenario.Name, item.Messages, item.Corrupt))
            .ToArray();

    /// <summary>重新蓋上目前規則版本與查核期間，換成新的保存時間；名稱、動機與其他屬性原樣。</summary>
    private static SavedFilterScenario Stamp(SavedFilterScenario scenario, DateTimeOffset nowUtc)
    {
        using var document = JsonDocument.Parse(scenario.DefinitionJson);
        return new SavedFilterScenario(
            scenario.Position,
            scenario.Name,
            scenario.Rationale,
            RuleLogicVersions.StampFilterDefinition(document.RootElement),
            nowUtc);
    }

    private static (IReadOnlyList<string> Messages, IReadOnlyList<FilterScenarioCorruptReason> Corrupt) CheckDefinition(
        JsonElement definition,
        FilterValidationContext validationContext,
        int moneyScale)
    {
        FilterScenarioSpec spec;
        try
        {
            spec = FilterScenarioPayloadParser.Parse(definition, moneyScale);
        }
        catch (JetActionException exception)
        {
            var reasons = FilterScenarioCorruption.ReasonsOf(exception);
            return reasons.Count > 0 ? ([UnreadableDefinitionMessage], reasons) : ([exception.Message], []);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
        {
            return ([UnreadableDefinitionMessage], [new FilterScenarioCorruptReason(null, null, "definition_parse_failed")]);
        }

        var errors = FilterScenarioValidator.Validate(spec, validationContext, forSave: false);
        return (ListReasons(errors), FilterScenarioCorruption.ReasonsOf(errors));
    }

    /// <summary>缺漏、null 或空白都視為查核期間；不是字串時 <paramref name="isString"/> 為 false。</summary>
    private static string? ReadScope(JsonElement definition, out bool isString)
    {
        isString = true;
        if (!definition.TryGetProperty("populationScope", out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            isString = false;
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int? VersionNumber(string version)
    {
        var match = KnownVersion().Match(version);
        return match.Success
            && int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
    }

    private static FilterScenarioUpgradeResult Current() =>
        new(FilterScenarioUpgradeStatus.Current, null, []);

    private static FilterScenarioUpgradeResult Inconsistent() =>
        new(FilterScenarioUpgradeStatus.Inconsistent, null, []);
}
