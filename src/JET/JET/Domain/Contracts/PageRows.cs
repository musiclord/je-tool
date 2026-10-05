namespace JET.Domain;

/// <summary>
/// query.sourceQualityPage 的 current-successful-generation finding。
/// EntryId 只作 opaque keyset cursor，不進 wire；SourceRowNumber 是來源檔實際列號。
/// </summary>
public sealed record SourceQualityFindingRow(
    string Category,
    int SourceRowNumber,
    string SourceLabel,
    string? DocumentNumber,
    string? AccountCode,
    string? PostDate,
    string? Description,
    long EntryId);

/// <summary>
/// query.filterHitsPage 的單一命中行層明細。
/// 位置式記錄，欄序對齊回應的 wire row;金額為 scaled 整數,顯示換算由 handler 負責。
/// 游標鍵 entry_id 不入此記錄(由 repo 在 reader 末欄另取以編游標)。
/// </summary>
public sealed record FilterHitRow(
    string? DocumentNumber,
    string? LineItem,
    string? PostDate,
    string? AccountCode,
    string? AccountName,
    long AmountScaled,
    string DrCr,
    string? Description,
    long EntryId = 0);

/// <summary>query.prescreenPage 的命中行；entry_id 同時是穩定列識別與 keyset 游標來源。</summary>
public sealed record PrescreenHitRow(
    long EntryId,
    string? DocumentNumber,
    string? LineItem,
    string? PostDate,
    string? AccountCode,
    string? AccountName,
    long AmountScaled,
    string DrCr,
    string? Description);

/// <summary>
/// query.infSamplePage 的單一 INF 抽樣行層明細。
/// 借/貸以 debit_amount_scaled / credit_amount_scaled 拆欄(scaled 整數,顯示換算由 handler 負責)。
/// 游標鍵 entry_id 不入此記錄(由 repo 在 reader 末欄另取以編游標)。
/// </summary>
public sealed record InfSampleRow(
    string? DocumentNumber,
    string? AccountCode,
    string? AccountName,
    long DebitScaled,
    long CreditScaled,
    string? PostDate,
    string? ApprovalDate,
    string? CreatedBy,
    string? ApprovedBy,
    string? Description,
    long EntryId = 0);

/// <summary>query.tagMatrixVoucherPage 的傳票層矩陣列。matchedPositions 由 handler 另附;
/// 游標鍵 document_number 由 repo 末欄另取。voucherTotal 為傳票借方總額 scaled。</summary>
public sealed record VoucherTagRow(
    string? DocumentNumber,
    string? PostDate,
    string? CreatedBy,
    long VoucherTotalScaled);

/// <summary>query.tagMatrixRowPage 的行層矩陣列。命中傳票之所有行;matchedPositions 由 handler 另附;
/// 游標鍵 entry_id 由 repo 末欄另取。amount 為 signed scaled。</summary>
public sealed record RowTagRow(
    string? DocumentNumber,
    string? LineItem,
    string? PostDate,
    string? ApprovalDate,
    string? CreatedBy,
    string? ApprovedBy,
    string? AccountCode,
    string? AccountName,
    long AmountScaled,
    string? Description);
