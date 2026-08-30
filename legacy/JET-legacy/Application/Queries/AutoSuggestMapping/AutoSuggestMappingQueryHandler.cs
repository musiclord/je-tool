namespace JET.Application.Queries.AutoSuggestMapping
{
    public sealed class AutoSuggestMappingQueryHandler
    {
        private static readonly Dictionary<string, string[]> MatchKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["傳票號碼"] = ["voucher", "doc num", "document", "傳票號碼", "je", "je_num", "journal"],
            ["傳票文件項次"] = ["line", "item", "line_id", "項次", "明細"],
            ["總帳日期"] = ["post date", "posting", "過帳日", "總帳日", "postdate", "gl_date", "entry_date"],
            ["傳票核准日"] = ["approve", "approval", "核准日", "doc_date", "document_date"],
            ["會計科目編號"] = ["acc num", "account no", "acct", "科目編號", "account_id", "acct_no"],
            ["會計科目名稱"] = ["acc name", "account name", "科目名稱", "acct_name"],
            ["傳票摘要"] = ["desc", "remark", "narration", "摘要", "description", "memo"],
            ["分錄來源模組"] = ["source", "module", "來源"],
            ["傳票建立人員"] = ["created by", "creator", "建立人", "prepare", "maker"],
            ["傳票核准人員"] = ["approved by", "approver", "核准人", "reviewer"],
            ["傳票金額（單欄）"] = ["amount", "金額", "amt"],
            ["借方金額"] = ["debit", "dr", "借方"],
            ["貸方金額"] = ["credit", "cr", "貸方"],
            ["借貸別欄位"] = ["dc", "dr_cr", "debit_credit", "借貸"],
            ["年度變動金額"] = ["change", "movement", "變動", "net"],
        };

        public Task<object> HandleAsync(AutoSuggestMappingQuery query, CancellationToken cancellationToken)
        {
            var suggested = new Dictionary<string, string>();

            foreach (var field in query.Fields)
            {
                if (!MatchKeywords.TryGetValue(field.Label, out var keywords))
                    continue;

                var hit = query.Columns.FirstOrDefault(col =>
                    keywords.Any(k => col.Contains(k, StringComparison.OrdinalIgnoreCase)));

                if (hit is not null)
                    suggested[field.Key] = hit;
            }

            return Task.FromResult<object>(new { suggested });
        }
    }
}
