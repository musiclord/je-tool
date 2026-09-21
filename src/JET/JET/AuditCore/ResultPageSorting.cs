namespace JET.AuditCore;

/// <summary>
/// 各明細表分頁查詢的排序與搜尋目錄（單一事實來源）。鍵名就是該查詢 wire row 的欄位名，前端表頭用同一個鍵；
/// SQL 運算式對應各 repository 實際的 FROM 別名（<c>g</c> 是 target_gl_entry，彙總查詢用外層子查詢的別名）。
/// 搜尋欄固定：分錄與傳票層的表比對傳票號碼，科目層的表比對科目編號。
/// </summary>
internal static class ResultPageSorting
{
    private static SortColumn Text(string key, string sql) => new(key, sql, SortValueKind.Text);
    private static SortColumn Integer(string key, string sql) => new(key, sql, SortValueKind.Integer);

    /// <summary>query.completenessDiffPage：完整性差異（diff CTE 的欄）。</summary>
    internal static readonly PageSortCatalog CompletenessDiff = new(
        Text("accountCode", "account_code"),
        [
            Text("accountCode", "account_code"),
            Text("accountName", "account_name"),
            Integer("tbAmount", "tb_s"),
            Integer("glAmount", "gl_s"),
            Integer("diff", "tb_s - gl_s"),
            Integer("notInTb", "not_in_tb")
        ],
        "account_code");

    /// <summary>query.docBalancePage：借貸不平傳票（外層子查詢別名 debit_s、credit_s、diff_s）。</summary>
    internal static readonly PageSortCatalog DocBalance = new(
        Text("documentNumber", "document_number"),
        [
            Text("documentNumber", "document_number"),
            Integer("debit", "debit_s"),
            Integer("credit", "credit_s"),
            Integer("diff", "diff_s")
        ],
        "document_number");

    /// <summary>空值明細也搜尋科目和摘要，缺傳票號碼時仍能找到資料。</summary>
    internal static readonly PageSortCatalog NullRecords = new(
        Integer("entryId", "entry_id"),
        [
            Text("documentNumber", "document_number"),
            Text("accountCode", "account_code"),
            Text("postDate", "post_date"),
            Text("description", "document_description")
        ],
        "document_number") { AdditionalSearchSql = ["account_code", "document_description"] };

    /// <summary>query.sourceQualityPage：g 是 target_gl_entry，s 是 staging_gl_raw_row。</summary>
    internal static readonly PageSortCatalog SourceQuality = new(
        Integer("entryId", "g.entry_id"),
        [
            Integer("sourceRowNumber", "s.source_row_number"),
            Text("documentNumber", "g.document_number"),
            Text("accountCode", "g.account_code"),
            Text("postDate", "g.post_date"),
            Text("description", "g.document_description")
        ],
        "g.document_number");

    /// <summary>query.infSamplePage。</summary>
    internal static readonly PageSortCatalog InfSample = new(
        Integer("entryId", "g.entry_id"),
        [
            Text("documentNumber", "g.document_number"),
            Text("accountCode", "g.account_code"),
            Text("accountName", "g.account_name"),
            Integer("debit", "g.debit_amount_scaled"),
            Integer("credit", "g.credit_amount_scaled"),
            Text("postDate", "g.post_date"),
            Text("approvalDate", "g.approval_date"),
            Text("createdBy", "g.created_by"),
            Text("approvedBy", "g.approved_by"),
            Text("description", "g.document_description")
        ],
        "g.document_number");

    /// <summary>query.prescreenPage：分錄欄，摘要的 wire 名稱是 documentDescription（沿用既有回應形狀）。</summary>
    internal static readonly PageSortCatalog Prescreen = new(
        Integer("entryId", "g.entry_id"),
        [
            Text("documentNumber", "g.document_number"),
            Text("lineItem", "g.line_item"),
            Text("postDate", "g.post_date"),
            Text("accountCode", "g.account_code"),
            Text("accountName", "g.account_name"),
            Integer("amount", "g.amount_scaled"),
            Text("drCr", "g.dr_cr"),
            Text("documentDescription", "g.document_description")
        ],
        "g.document_number");

    /// <summary>query.filterHitsPage：分錄欄（鍵名對齊 ResultPageColumnRegistry 的固定欄）。</summary>
    internal static readonly PageSortCatalog GlEntryRows = new(
        Integer("entryId", "g.entry_id"),
        [
            Text("documentNumber", "g.document_number"),
            Text("lineItem", "g.line_item"),
            Text("postDate", "g.post_date"),
            Text("accountCode", "g.account_code"),
            Text("accountName", "g.account_name"),
            Integer("amount", "g.amount_scaled"),
            Text("drCr", "g.dr_cr"),
            Text("description", "g.document_description")
        ],
        "g.document_number");

    /// <summary>query.tagMatrixVoucherPage：外層子查詢別名 first_post_date、first_created_by、voucher_total。</summary>
    internal static readonly PageSortCatalog TagMatrixVoucher = new(
        Text("documentNumber", "document_number"),
        [
            Text("documentNumber", "document_number"),
            Text("postDate", "first_post_date"),
            Text("createdBy", "first_created_by"),
            Integer("voucherTotal", "voucher_total")
        ],
        "document_number");

    /// <summary>query.tagMatrixRowPage。</summary>
    internal static readonly PageSortCatalog TagMatrixRow = new(
        Integer("entryId", "g.entry_id"),
        [
            Text("documentNumber", "g.document_number"),
            Text("lineItem", "g.line_item"),
            Text("postDate", "g.post_date"),
            Text("approvalDate", "g.approval_date"),
            Text("createdBy", "g.created_by"),
            Text("approvedBy", "g.approved_by"),
            Text("accountCode", "g.account_code"),
            Text("accountName", "g.account_name"),
            Integer("amount", "g.amount_scaled"),
            Text("description", "g.document_description")
        ],
        "g.document_number");

    /// <summary>query.filterVoucherPage 的傳票摘要：外層子查詢別名 first_post_date、hit_rows、total_rows、voucher_total。</summary>
    internal static readonly PageSortCatalog FilterVoucher = new(
        Text("documentNumber", "document_number"),
        [
            Text("documentNumber", "document_number"),
            Text("postDate", "first_post_date"),
            Integer("hitRowCount", "hit_rows"),
            Integer("totalRowCount", "total_rows"),
            Integer("voucherTotal", "voucher_total")
        ],
        "document_number");
}
