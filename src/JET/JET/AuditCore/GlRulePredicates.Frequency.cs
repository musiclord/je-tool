using JET.Domain;

namespace JET.AuditCore;

internal sealed partial class GlRulePredicates
{
    public string EntityFrequency(FilterSqlParameterPlanBuilder parameters, FilterRuleSpec rule,
        FilterRuleContext context, string schemaPrefix)
    {
        var column = rule.Field switch
        {
            "accNum" => "account_code", "createBy" => "created_by", "approveBy" => "approved_by",
            _ => throw new InvalidOperationException("Unknown frequency field.")
        };
        var count = rule.CountUnit switch
        {
            "entries" => "COUNT(*)",
            // 與現有傳票清單一致：同案件同號為一張，NULL 號碼不計張。
            "vouchers" => "COUNT(DISTINCT f.document_number)",
            _ => throw new InvalidOperationException("Unknown frequency unit.")
        };
        var from = NextParam(parameters, rule.CountFrom!.Value);
        var comparison = rule.CountOperator switch
        {
            "equals" => $"{count} = {from}", "lessThan" => $"{count} < {from}",
            "greaterThan" => $"{count} > {from}", "lessThanOrEqual" => $"{count} <= {from}",
            "greaterThanOrEqual" => $"{count} >= {from}",
            "between" => $"{count} BETWEEN {from} AND {NextParam(parameters, rule.CountTo!.Value)}",
            _ => throw new InvalidOperationException("Unknown frequency comparison.")
        };
        // 人員（建立人員、核准人員）依去空白、不分大小寫的識別值分組，和人員清單、編製等於核准同一規則（2026-10-04 裁定 C3）；
        // 科目編號只去空白，大小寫規則不變。
        var isPerson = rule.Field is "createBy" or "approveBy";
        string Key(string alias) => isPerson ? $"UPPER({dialect.Trim($"{alias}.{column}")})" : dialect.Trim($"{alias}.{column}");
        return $"{Key("g")} IN (SELECT {Key("f")} FROM {schemaPrefix}target_gl_entry f " +
            $"WHERE {populationScopePredicate(context, "f")} AND f.{column} IS NOT NULL " +
            $"AND {dialect.Trim($"f.{column}")} <> '' GROUP BY {Key("f")} HAVING {comparison})";
    }
}
