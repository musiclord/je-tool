using JET.Domain;

namespace JET.AuditCore;

internal sealed partial class GlRulePredicates
{
    /// <summary>第五步「科目與人員統計」條件：欄位、計算單位、比較方式與次數都來自條件本身。</summary>
    public string EntityFrequency(FilterSqlParameterPlanBuilder parameters, FilterConditionSpec rule,
        FilterConditionContext context, string schemaPrefix) =>
        EntityFrequency(parameters, rule.Field ?? string.Empty, rule.CountUnit ?? string.Empty, rule.CountOperator ?? string.Empty,
            rule.CountFrom!.Value, rule.CountTo, context, schemaPrefix);

    /// <summary>
    /// 科目或人員在所選母體內的分錄筆數或傳票張數符合比較式；三種寫法收成這一個方法（裁定 2026-10-22-14）。
    /// 第四步原本的「編製分錄較少的人員」「使用較少的科目」兩條規則已照舊版拿掉（裁定 2026-10-29-27），
    /// 第五步欄位條件裡的同名常用查法仍用這個方法，預設 11 筆以下只是畫面帶入的參數。
    /// 人員（建立人員、核准人員）依去空白、不分大小寫的識別值分組，和人員清單、編製等於核准同一規則（裁定 2026-10-04-01）；
    /// 科目編號只去空白，大小寫規則不變。空白的科目或人員不列入。門檻參數綁定；子查詢與外層共用母體範圍，
    /// GROUP BY、HAVING 與 COUNT 皆 ANSI 共通。
    /// </summary>
    public string EntityFrequency(FilterSqlParameterPlanBuilder parameters, string field, string countUnit, string countOperator,
        int countFrom, int? countTo, FilterConditionContext context, string schemaPrefix)
    {
        var column = field switch
        {
            JetFieldCatalog.GlAccountNumber => "account_number",
            JetFieldCatalog.GlCreatedBy => "created_by",
            JetFieldCatalog.GlApprovedBy => "approved_by",
            _ => throw new InvalidOperationException("Unknown frequency field.")
        };
        var count = countUnit switch
        {
            "entries" => "COUNT(*)",
            // 與現有傳票清單一致：同案件同號為一張，NULL 號碼不計張。
            "vouchers" => "COUNT(DISTINCT f.voucher_number)",
            _ => throw new InvalidOperationException("Unknown frequency unit.")
        };
        var from = NextParam(parameters, countFrom);
        var comparison = countOperator switch
        {
            "equals" => $"{count} = {from}", "lessThan" => $"{count} < {from}",
            "greaterThan" => $"{count} > {from}", "lessThanOrEqual" => $"{count} <= {from}",
            "greaterThanOrEqual" => $"{count} >= {from}",
            "between" => $"{count} BETWEEN {from} AND {NextParam(parameters, countTo!.Value)}",
            _ => throw new InvalidOperationException("Unknown frequency comparison.")
        };
        var isPerson = field is JetFieldCatalog.GlCreatedBy or JetFieldCatalog.GlApprovedBy;
        string Key(string alias) => isPerson ? dialect.Upper(dialect.Trim($"{alias}.{column}")) : dialect.Trim($"{alias}.{column}");
        return $"{Key("g")} IN (SELECT {Key("f")} FROM {schemaPrefix}gl f " +
            $"WHERE {populationScopePredicate(context, "f")} AND {SqlBlankText.IsNotBlank(dialect, $"f.{column}")} " +
            $"GROUP BY {Key("f")} HAVING {comparison})";
    }
}
