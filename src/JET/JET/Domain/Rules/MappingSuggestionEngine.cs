namespace JET.Domain;

public sealed record MappingFieldDefinition(string Key, string Label);

/// <summary>
/// 欄位配對自動建議。pass 1 精確比對、pass 2 contains；
/// 每個來源欄位只能被一個 logical key 認領。
/// 特定金額鍵（借方/貸方）先於泛用 amount 處理，避免「借方金額」被 amount 搶走。
/// </summary>
public static class MappingSuggestionEngine
{
    private static readonly IReadOnlyDictionary<string, string[]> Hints =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [GlMappingKeys.DocNum] = ["傳票號碼", "傳票編號", "憑證號碼", "voucher", "document no", "docnum"],
            [GlMappingKeys.LineId] = ["項次", "序號", "line item", "line no"],
            [GlMappingKeys.PostDate] = ["日期", "總帳日期", "過帳日", "post date", "posting date", "gl date"],
            [GlMappingKeys.DocDate] = ["核准日", "傳票核准日", "approval date", "approve date"],
            [GlMappingKeys.VoucherDate] = ["傳票日", "傳票日期", "憑證日期", "voucher date", "document date"],
            [GlMappingKeys.AccNum] = ["會計科目編號", "會計項目", "科目代號", "科目編號", "account code", "account no"],
            [GlMappingKeys.AccName] = ["會計科目名稱", "項目名稱", "科目名稱", "account name"],
            [GlMappingKeys.Description] = ["摘要", "說明", "description", "memo", "remark"],
            [GlMappingKeys.JeSource] = ["來源模組", "來源", "模組", "source", "module"],
            [GlMappingKeys.CreateBy] = ["建立人員", "製單", "編製", "created by", "preparer"],
            [GlMappingKeys.ApproveBy] = ["核准人員", "approved by", "approver"],
            [GlMappingKeys.Manual] = ["人工", "manual"],
            [GlMappingKeys.DebitAmount] = ["借方金額", "借方", "debit"],
            [GlMappingKeys.CreditAmount] = ["貸方金額", "貸方", "credit"],
            [GlMappingKeys.Amount] = ["金額", "變動金額", "amount"],
            [GlMappingKeys.DcField] = ["借貸別", "借貸", "dc flag"],
            // dcDebitCode 是代碼字面值，不做欄位建議。
            [TbMappingKeys.DebitAmt] = ["借方金額", "借方", "debit"],
            [TbMappingKeys.CreditAmt] = ["貸方金額", "貸方", "credit"]
        };

    /// <summary>
    /// 泛用 amount 最後處理且只允許精確比對：contains 會誤認
    /// 「期初金額」「期末金額」等含「金額」字樣的欄位。
    /// </summary>
    private static readonly string[] ExactOnlyKeys = [GlMappingKeys.Amount];

    public static IReadOnlyDictionary<string, string> Suggest(
        IReadOnlyList<MappingFieldDefinition> fields,
        IReadOnlyList<string> columns)
    {
        var suggested = new Dictionary<string, string>(StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        var ordered = fields
            .OrderBy(f => Array.IndexOf(ExactOnlyKeys, f.Key) >= 0 ? 1 : 0)
            .ToList();

        // pass 1：精確比對（trim + 不分大小寫）
        foreach (var field in ordered)
        {
            if (suggested.ContainsKey(field.Key))
            {
                continue;
            }

            var match = FindColumn(field, columns, claimed, exact: true);
            if (match is not null)
            {
                suggested[field.Key] = match;
                claimed.Add(match);
            }
        }

        // pass 2：contains 比對（exact-only keys 不參與）
        foreach (var field in ordered)
        {
            if (suggested.ContainsKey(field.Key) || Array.IndexOf(ExactOnlyKeys, field.Key) >= 0)
            {
                continue;
            }

            var match = FindColumn(field, columns, claimed, exact: false);
            if (match is not null)
            {
                suggested[field.Key] = match;
                claimed.Add(match);
            }
        }

        return suggested;
    }

    private static string? FindColumn(
        MappingFieldDefinition field,
        IReadOnlyList<string> columns,
        HashSet<string> claimed,
        bool exact)
    {
        if (!Hints.TryGetValue(field.Key, out var keywords))
        {
            return null;
        }

        var candidates = new List<string>(keywords);
        if (!string.IsNullOrWhiteSpace(field.Label))
        {
            candidates.Insert(0, field.Label);
        }

        foreach (var keyword in candidates)
        {
            foreach (var column in columns)
            {
                if (claimed.Contains(column))
                {
                    continue;
                }

                var normalizedColumn = column.Trim();
                var normalizedKeyword = keyword.Trim();

                var matched = exact
                    ? normalizedColumn.Equals(normalizedKeyword, StringComparison.OrdinalIgnoreCase)
                    : normalizedColumn.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase);

                if (matched)
                {
                    return column;
                }
            }
        }

        return null;
    }
}
