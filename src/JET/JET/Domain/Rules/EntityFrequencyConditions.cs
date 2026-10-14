namespace JET.Domain;

/// <summary>所選母體中的科目或人員頻率；與固定預篩選低頻規則分開。</summary>
public static class EntityFrequencyConditions
{
    public static readonly IReadOnlyList<string> Fields = ["accNum", "createBy", "approveBy"];
    public static readonly IReadOnlyDictionary<string, string> Operators = new Dictionary<string, string>
    {
        ["equals"] = "等於", ["lessThan"] = "小於", ["greaterThan"] = "大於", ["between"] = "介於",
        ["lessThanOrEqual"] = "小於或等於", ["greaterThanOrEqual"] = "大於或等於"
    };

    internal static void Validate(FilterRuleSpec rule, FilterValidationContext context, string label, List<string> errors)
    {
        if (rule.Field is null || !Fields.Contains(rule.Field))
            FilterScenarioCorruption.Add(errors, label, "entity_frequency_field_invalid");
        else if (context.AvailableGlFields is { } available && !available.Contains(rule.Field, StringComparer.Ordinal))
            errors.Add($"{label}：{FilterScenarioValidator.UnmappedFieldReason(rule.Field)}");
        if (rule.CountUnit is not ("entries" or "vouchers")) FilterScenarioCorruption.Add(errors, label, "entity_frequency_unit_invalid");
        if (rule.CountOperator is null || !Operators.ContainsKey(rule.CountOperator)) FilterScenarioCorruption.Add(errors, label, "entity_frequency_operator_invalid");
        if (rule.CountFrom is not (>= 0)) errors.Add($"{label}：次數須為 0 以上的整數");
        if (rule.CountOperator == "between")
        {
            if (rule.CountTo is not (>= 0))
                errors.Add($"{label}：上限須為 0 以上的整數");
            else if (rule.CountFrom is >= 0 && rule.CountTo < rule.CountFrom)
                errors.Add($"{label}：上限小於下限");
        }
    }
}
