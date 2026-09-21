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
        return $"g.{column} IN (SELECT f.{column} FROM {schemaPrefix}target_gl_entry f " +
            $"WHERE {populationScopePredicate(context, "f")} AND f.{column} IS NOT NULL " +
            $"AND TRIM(f.{column}) <> '' GROUP BY f.{column} HAVING {comparison})";
    }
}
