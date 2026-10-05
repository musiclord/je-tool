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
            errors.Add($"{label}：統計欄位只能選科目編號、傳票建立人員或傳票核准人員。");
        else if (context.AvailableGlFields is { } available && !available.Contains(rule.Field, StringComparer.Ordinal))
            errors.Add($"{label}：{JetFieldCatalog.GlSemanticFieldMappingLabel(rule.Field)}尚未配對，請返回欄位配對修正。");
        if (rule.CountUnit is not ("entries" or "vouchers")) errors.Add($"{label}：請選擇分錄筆數或傳票張數（同號只算一張）。");
        if (rule.CountOperator is null || !Operators.ContainsKey(rule.CountOperator)) errors.Add($"{label}：統計比較方式不正確。");
        if (rule.CountFrom is not (>= 0)) errors.Add($"{label}：統計次數必須是大於或等於 0 的整數。");
        if (rule.CountOperator == "between")
        {
            if (rule.CountTo is not (>= 0))
                errors.Add($"{label}：統計區間上限必須是大於或等於 0 的整數。");
            else if (rule.CountFrom is >= 0 && rule.CountTo < rule.CountFrom)
                errors.Add($"{label}：統計區間上限不得小於下限；區間包含兩個端點。");
        }
    }
}
