using System.Globalization;
using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 一個 result page 的 frozen column registry，以及 Application 用來驗證／render RDE cells 的
/// committed mapping snapshot。CustomFields 是本 action 實際公開的子集合；RegistryFields 保留
/// 全部 current committed RDE，讓 port 回傳 removed／unknown／type-mismatched cell 時 fail closed。
/// </summary>
internal sealed record ResultPageColumnPlan(
    IReadOnlyList<ResultPageColumn> Columns,
    IReadOnlyList<GlRdeFieldMetadata> RegistryFields,
    IReadOnlyList<GlRdeFieldMetadata> CustomFields);

/// <summary>Filter／INF 結果表的固定欄與 committed RDE 欄唯一 Application registry。</summary>
internal static class ResultPageColumnRegistry
{
    private static readonly IReadOnlyList<ResultPageColumn> FilterFixed =
    [
        new("documentNumber", "傳票號碼", RdeFieldValueTypeNames.Text, false),
        new("lineItem", "傳票文件項次", RdeFieldValueTypeNames.Text, false),
        new("postDate", "過帳日期", RdeFieldValueTypeNames.Date, false),
        new("accountCode", "會計科目編號", RdeFieldValueTypeNames.Text, false),
        new("accountName", "會計科目名稱", RdeFieldValueTypeNames.Text, false),
        new("amount", "傳票金額", RdeFieldValueTypeNames.Money, false),
        new("drCr", "借貸別", RdeFieldValueTypeNames.Text, false),
        new("description", "傳票摘要", RdeFieldValueTypeNames.Text, false)
    ];

    private static readonly IReadOnlyList<ResultPageColumn> InfFixed =
    [
        new("documentNumber", "傳票號碼", RdeFieldValueTypeNames.Text, false),
        new("accountCode", "會計科目編號", RdeFieldValueTypeNames.Text, false),
        new("accountName", "會計科目名稱", RdeFieldValueTypeNames.Text, false),
        new("debit", "借方金額", RdeFieldValueTypeNames.Money, false),
        new("credit", "貸方金額", RdeFieldValueTypeNames.Money, false),
        new("postDate", "過帳日期", RdeFieldValueTypeNames.Date, false),
        new("approvalDate", "核准日期", RdeFieldValueTypeNames.Date, false),
        new("createdBy", "編製人員", RdeFieldValueTypeNames.Text, false),
        new("approvedBy", "核准人員", RdeFieldValueTypeNames.Text, false),
        new("description", "傳票摘要", RdeFieldValueTypeNames.Text, false)
    ];

    public static ResultPageColumnPlan ForInf(CommittedMapping? mapping)
    {
        var registry = ReadRegistry(mapping);
        return CreatePlan(InfFixed, registry, registry);
    }

    public static ResultPageColumnPlan ForFilter(
        SavedFilterScenario scenario,
        CommittedMapping? mapping,
        int moneyScale)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var registry = ReadRegistry(mapping);
        var byFieldId = registry.ToDictionary(static field => field.FieldId, StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);

        FilterScenarioSpec spec;
        try
        {
            using var definition = JsonDocument.Parse(scenario.DefinitionJson);
            spec = FilterScenarioPayloadParser.Parse(definition.RootElement, moneyScale);
        }
        catch (JsonException)
        {
            throw Stale("已保存的篩選情境定義無法解析，請重新保存情境。");
        }
        catch (JetActionException exception) when (exception.Code == JetErrorCodes.InvalidScenario)
        {
            throw Stale("已保存的篩選情境定義已不相容，請重新保存情境。");
        }

        foreach (var rule in spec.Groups.SelectMany(static group => group.Rules)
                     .Where(static rule => rule.Type == FilterRuleType.TypedField
                         || rule.Type == FilterRuleType.FieldValue && rule.FieldId is not null))
        {
            if (rule.FieldId is null || !byFieldId.TryGetValue(rule.FieldId, out var field))
            {
                throw Stale("篩選情境引用的自訂欄位已移除，請重新保存情境。");
            }

            if (rule.TypedOperator is null
                || !(rule.Type == FilterRuleType.FieldValue ? FieldValueConditions.Operators(field.ValueType) : TypedFieldOperatorSets.ForValueType(field.ValueType))
                    .Contains(rule.TypedOperator, StringComparer.Ordinal))
            {
                throw Stale("篩選情境的自訂欄位型別或運算子已變更，請重新保存情境。");
            }

            selected.Add(field.FieldId);
        }

        // AST 中的 first-use order 不具 wire 權威；永遠回到 committed RDE ordinal。
        var custom = registry.Where(field => selected.Contains(field.FieldId)).ToArray();
        return CreatePlan(FilterFixed, registry, custom);
    }

    private static IReadOnlyList<GlRdeFieldMetadata> ReadRegistry(CommittedMapping? mapping)
    {
        var fields = mapping?.GlOptions?.RdeFields ?? [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!GlMappingOptionsRules.IsCanonicalFieldId(field.FieldId)
                || !ids.Add(field.FieldId)
                || string.IsNullOrWhiteSpace(field.Label)
                || field.Label.Length > GlRdeStorageLimits.LabelUtf16CodeUnits
                || !RdeFieldValueTypeNames.IsCanonical(field.ValueType))
            {
                throw Stale("額外欄位的配對設定不完整，請回第三步重新確認 GL 欄位配對。");
            }
        }

        return fields;
    }

    private static ResultPageColumnPlan CreatePlan(
        IReadOnlyList<ResultPageColumn> fixedColumns,
        IReadOnlyList<GlRdeFieldMetadata> registry,
        IReadOnlyList<GlRdeFieldMetadata> custom)
    {
        var columns = fixedColumns.Concat(custom.Select(static field =>
            new ResultPageColumn(field.FieldId, field.Label, field.ValueType, true))).ToArray();
        return new ResultPageColumnPlan(columns, registry, custom);
    }

    private static JetActionException Stale(string message) =>
        new(JetErrorCodes.StaleResult, message);
}

/// <summary>
/// Provider-neutral RDE cell → wire-native value renderer。它先對 current committed registry
/// 驗證所有 present cells，再依 action custom subset 建立 exact key set；missing cell 明示 null。
/// </summary>
internal static class ResultPageCustomValueRenderer
{
    public static IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>> Render(
        IReadOnlyList<long> entryIds,
        IReadOnlyList<ResultPageRdeValue> values,
        ResultPageColumnPlan plan,
        int moneyScale)
    {
        ResultPageRdeValueBatch.Validate(entryIds);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(plan);
        if (moneyScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(moneyScale));
        }

        var requestedEntries = entryIds.ToHashSet();
        var registry = plan.RegistryFields.ToDictionary(static field => field.FieldId, StringComparer.Ordinal);
        var selected = plan.CustomFields.ToDictionary(static field => field.FieldId, StringComparer.Ordinal);
        var rendered = entryIds.ToDictionary(
            static entryId => entryId,
            _ => (IReadOnlyDictionary<string, object?>)plan.CustomFields.ToDictionary(
                static field => field.FieldId,
                static _ => (object?)null,
                StringComparer.Ordinal));
        var present = new HashSet<(long EntryId, string FieldId)>();

        foreach (var value in values)
        {
            if (!requestedEntries.Contains(value.EntryId)
                || !registry.TryGetValue(value.FieldId, out var field)
                || !string.Equals(value.ValueType, field.ValueType, StringComparison.Ordinal)
                || !present.Add((value.EntryId, value.FieldId)))
            {
                throw Stale();
            }

            var wireValue = ReadWireValue(value);
            if (selected.ContainsKey(value.FieldId))
            {
                ((Dictionary<string, object?>)rendered[value.EntryId])[value.FieldId] = wireValue;
            }
        }

        return rendered;

        object ReadWireValue(ResultPageRdeValue value) => value.ValueType switch
        {
            RdeFieldValueTypeNames.Text
                when value.TextValue is not null && value.DateValue is null && value.AmountScaled is null
                => value.TextValue,
            RdeFieldValueTypeNames.Date
                when value.TextValue is null && value.DateValue is not null && value.AmountScaled is null
                     && DateOnly.TryParseExact(
                         value.DateValue,
                         "yyyy-MM-dd",
                         CultureInfo.InvariantCulture,
                         DateTimeStyles.None,
                         out _)
                => value.DateValue,
            RdeFieldValueTypeNames.Money
                when value.TextValue is null && value.DateValue is null && value.AmountScaled is not null
                => (decimal)value.AmountScaled.Value / moneyScale,
            _ => throw Stale()
        };
    }

    private static JetActionException Stale() => new(
        JetErrorCodes.StaleResult,
        "額外欄位資料與目前配對設定不一致，請回第三步重新確認 GL 欄位配對。");
}
