namespace JET.AuditCore;

/// <summary>
/// 各明細表分頁查詢的排序與搜尋目錄（單一事實來源）。鍵名就是該查詢 wire row 的欄位名，前端表頭用同一個鍵；
/// SQL 運算式對應各 repository 實際的 FROM 別名（<c>g</c> 是 gl，彙總查詢用外層子查詢的別名）。
/// 搜尋欄固定：分錄與傳票層的表比對傳票號碼，科目層的表比對科目編號。
/// </summary>
internal static class ResultPageSorting
{
    private static SortColumn Text(string key, string sql) => new(key, sql, SortValueKind.Text);
    private static SortColumn Integer(string key, string sql) => new(key, sql, SortValueKind.Integer);

    /// <summary>
    /// query.completenessDiffPage 與底稿、報告讀全科目表：完整性差異（diff CTE 的欄）。
    /// 科目編號可能空白，也可能有兩列空白科目，所以穩定鍵用固定順序的序號 page_row，
    /// 由 <c>CompletenessAccountPageQuery</c> 算出。
    /// </summary>
    internal static readonly PageSortCatalog CompletenessDiff = new(
        Integer("pageRow", "page_row"),
        [
            Text("accountNumber", "account_number"),
            Text("accountName", "account_name"),
            Integer("tbAmount", "tb_s"),
            Integer("glAmount", "gl_s"),
            Integer("diff", "tb_s - gl_s"),
            Integer("notInTb", "not_in_tb")
        ],
        "account_number");

    /// <summary>query.voucherBalancePage：借貸不平傳票（外層子查詢別名 debit_s、credit_s、diff_s）。</summary>
    internal static readonly PageSortCatalog VoucherBalance = new(
        Text("voucherNumber", "voucher_number"),
        [
            Text("voucherNumber", "voucher_number"),
            Integer("debit", "debit_s"),
            Integer("credit", "credit_s"),
            Integer("diff", "diff_s")
        ],
        "voucher_number");

    /// <summary>空值明細也搜尋科目和摘要，缺傳票號碼時仍能找到資料。</summary>
    internal static readonly PageSortCatalog BlankRecords = new(
        Integer("entryId", "entry_id"),
        [
            Text("voucherNumber", "voucher_number"),
            Text("accountNumber", "account_number"),
            Text("glDate", "gl_date"),
            Text("description", "description")
        ],
        "voucher_number") { AdditionalSearchSql = ["account_number", "description"] };

    /// <summary>query.sourceQualityPage：g 是 gl，s 是 pbc_gl。</summary>
    internal static readonly PageSortCatalog SourceQuality = new(
        Integer("entryId", "g.entry_id"),
        [
            Integer("sourceRowNumber", "s.source_row_number"),
            Text("voucherNumber", "g.voucher_number"),
            Text("accountNumber", "g.account_number"),
            Text("glDate", "g.gl_date"),
            Text("description", "g.description")
        ],
        "g.voucher_number");

    /// <summary>query.infSamplePage。</summary>
    internal static readonly PageSortCatalog InfSample = new(
        Integer("entryId", "g.entry_id"),
        [
            Text("voucherNumber", "g.voucher_number"),
            Text("accountNumber", "g.account_number"),
            Text("accountName", "g.account_name"),
            Integer("debit", "g.debit_amount_scaled"),
            Integer("credit", "g.credit_amount_scaled"),
            Text("glDate", "g.gl_date"),
            Text("approvalDate", "g.approval_date"),
            Text("createdBy", "g.created_by"),
            Text("approvedBy", "g.approved_by"),
            Text("description", "g.description")
        ],
        "g.voucher_number");

    /// <summary>query.prescreenPage：分錄欄，摘要的 wire 名稱和其他結果頁一樣是 description。</summary>
    internal static readonly PageSortCatalog Prescreen = new(
        Integer("entryId", "g.entry_id"),
        [
            Text("voucherNumber", "g.voucher_number"),
            Text("lineItem", "g.line_item"),
            Text("glDate", "g.gl_date"),
            Text("accountNumber", "g.account_number"),
            Text("accountName", "g.account_name"),
            Integer("amount", "g.amount_scaled"),
            Text("drCr", "g.dr_cr"),
            Text("description", "g.description")
        ],
        "g.voucher_number");

    /// <summary>query.filterHitsPage：分錄欄（鍵名對齊 ResultPageColumnRegistry 的固定欄）。</summary>
    internal static readonly PageSortCatalog GlEntryRows = new(
        Integer("entryId", "g.entry_id"),
        [
            Text("voucherNumber", "g.voucher_number"),
            Text("lineItem", "g.line_item"),
            Text("glDate", "g.gl_date"),
            Text("accountNumber", "g.account_number"),
            Text("accountName", "g.account_name"),
            Integer("amount", "g.amount_scaled"),
            Text("drCr", "g.dr_cr"),
            Text("description", "g.description")
        ],
        "g.voucher_number");

    /// <summary>query.highRiskSummaryVoucherPage：外層子查詢別名 first_gl_date、first_created_by、voucher_total。</summary>
    internal static readonly PageSortCatalog HighRiskSummaryVoucher = new(
        Text("voucherNumber", "voucher_number"),
        [
            Text("voucherNumber", "voucher_number"),
            Text("glDate", "first_gl_date"),
            Text("createdBy", "first_created_by"),
            Integer("voucherTotal", "voucher_total")
        ],
        "voucher_number");

    /// <summary>query.highRiskSummaryRowPage。</summary>
    internal static readonly PageSortCatalog HighRiskSummaryRow = new(
        Integer("entryId", "g.entry_id"),
        [
            Text("voucherNumber", "g.voucher_number"),
            Text("lineItem", "g.line_item"),
            Text("glDate", "g.gl_date"),
            Text("approvalDate", "g.approval_date"),
            Text("createdBy", "g.created_by"),
            Text("approvedBy", "g.approved_by"),
            Text("accountNumber", "g.account_number"),
            Text("accountName", "g.account_name"),
            Integer("amount", "g.amount_scaled"),
            Text("description", "g.description")
        ],
        "g.voucher_number");

    /// <summary>query.filterVoucherPage 的傳票摘要：外層子查詢別名 first_gl_date、hit_rows、total_rows、voucher_total。</summary>
    internal static readonly PageSortCatalog FilterVoucher = new(
        Text("voucherNumber", "voucher_number"),
        [
            Text("voucherNumber", "voucher_number"),
            Text("glDate", "first_gl_date"),
            Integer("hitRowCount", "hit_rows"),
            Integer("totalRowCount", "total_rows"),
            Integer("voucherTotal", "voucher_total")
        ],
        "voucher_number");
}
