namespace JET.Domain;

public sealed record GlMappingSpec(
    IReadOnlyDictionary<string, string> Mapping,
    GlAmountMode AmountMode)
{
    public GlMappingOptions Options { get; init; } = GlMappingOptions.NormalizeLegacy(Mapping);

    /// <summary>是否已把「傳票文件項次」(lineItem)對應到來源欄;未對應時投影層會自動逐傳票編號。</summary>
    public bool HasLineItem =>
        Mapping.TryGetValue(GlMappingKeys.LineItem, out var column) && !string.IsNullOrWhiteSpace(column);
}

public sealed record TbMappingSpec(
    IReadOnlyDictionary<string, string> Mapping,
    TbChangeMode ChangeMode);

/// <summary>
/// GL logical mapping keys，也是前端與 action payload 使用的名稱。docs/action-contract-manifest.md 以此檔為準。
/// </summary>
public static class GlMappingKeys
{
    public const string VoucherNumber = JetFieldCatalog.GlVoucherNumber;
    public const string LineItem = JetFieldCatalog.GlLineItem;
    public const string GlDate = JetFieldCatalog.GlDate;
    public const string ApprovalDate = JetFieldCatalog.GlApprovalDate;
    public const string AccountNumber = JetFieldCatalog.GlAccountNumber;
    public const string AccountName = JetFieldCatalog.GlAccountName;
    public const string Description = JetFieldCatalog.GlDescription;
    public const string SourceModule = JetFieldCatalog.GlSourceModule;
    public const string CreatedBy = JetFieldCatalog.GlCreatedBy;
    public const string ApprovedBy = JetFieldCatalog.GlApprovedBy;
    public const string IsManual = JetFieldCatalog.GlManual;
    public const string PostingStatus = JetFieldCatalog.GlPostingStatus;
    public const string Amount = JetFieldCatalog.GlAmount;
    public const string DebitAmount = JetFieldCatalog.GlDebitAmount;
    public const string CreditAmount = JetFieldCatalog.GlCreditAmount;
    public const string DrCr = JetFieldCatalog.GlDrCr;
    public const string DrCrDebitCode = JetFieldCatalog.GlDrCrDebitCode;
    public const string DrCrCreditCode = JetFieldCatalog.GlDrCrCreditCode;

    public static readonly IReadOnlyList<string> All = JetFieldCatalog.GlMappingKeys;
}

public static class TbMappingKeys
{
    public const string AccountNumber = JetFieldCatalog.TbAccountNumber;
    public const string AccountName = JetFieldCatalog.TbAccountName;
    public const string ChangeAmount = JetFieldCatalog.TbChangeAmount;
    public const string DebitAmount = JetFieldCatalog.TbDebitAmount;
    public const string CreditAmount = JetFieldCatalog.TbCreditAmount;

    // OpenClose（legacy SA=2）：期初 / 期末餘額兩欄。
    public const string OpeningBalance = JetFieldCatalog.TbOpeningBalance;
    public const string ClosingBalance = JetFieldCatalog.TbClosingBalance;

    // OpenCloseBySide（legacy SA=4）：期初借貸 + 期末借貸四欄。
    public const string OpeningDebitBalance = JetFieldCatalog.TbOpeningDebitBalance;
    public const string OpeningCreditBalance = JetFieldCatalog.TbOpeningCreditBalance;
    public const string ClosingDebitBalance = JetFieldCatalog.TbClosingDebitBalance;
    public const string ClosingCreditBalance = JetFieldCatalog.TbClosingCreditBalance;

    public static readonly IReadOnlyList<string> All = JetFieldCatalog.TbMappingKeys;
}

public sealed record CommittedMapping(
    DatasetKind Kind,
    IReadOnlyDictionary<string, string> Mapping,
    string ModeName,
    string SourceBatchId,
    DateTimeOffset CommittedUtc,
    int FormatVersion = MappingMetadataFormat.CurrentVersion,
    GlMappingOptions? GlOptions = null);

public interface IMappingStateStore
{
    Task SaveAsync(string projectId, CommittedMapping mapping, CancellationToken cancellationToken);

    Task<CommittedMapping?> FindAsync(string projectId, DatasetKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// 重新匯入（取代或附加）會讓已確認的配對失效；失效前最後一次確認的配對留在這裡，重開案件時當草稿帶回，
    /// 審計員不必逐欄重選。它不是有效配對，任何計算都不能讀它。沒有保存這份資料的實作回 null，
    /// 畫面就和以前一樣從空白草稿開始；SQL Server 目前暫緩開發，維持回 null。
    /// </summary>
    Task<CommittedMapping?> FindPreviousAsync(string projectId, DatasetKind kind, CancellationToken cancellationToken) =>
        Task.FromResult<CommittedMapping?>(null);
}
