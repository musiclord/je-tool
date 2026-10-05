namespace JET.Domain;

public sealed record GlMappingSpec(
    IReadOnlyDictionary<string, string> Mapping,
    GlAmountMode AmountMode)
{
    public GlMappingOptions Options { get; init; } = GlMappingOptions.NormalizeLegacy(Mapping);

    /// <summary>是否已把「傳票文件項次」(lineID)對應到來源欄;未對應時投影層會自動逐傳票編號。</summary>
    public bool HasLineItem =>
        Mapping.TryGetValue(GlMappingKeys.LineId, out var column) && !string.IsNullOrWhiteSpace(column);
}

public sealed record TbMappingSpec(
    IReadOnlyDictionary<string, string> Mapping,
    TbChangeMode ChangeMode);

/// <summary>
/// GL logical mapping keys，也是前端與 action payload 使用的名稱。docs/action-contract-manifest.md 以此檔為準。
/// </summary>
public static class GlMappingKeys
{
    public const string DocNum = JetFieldCatalog.GlDocNum;
    public const string LineId = JetFieldCatalog.GlLineId;
    public const string PostDate = JetFieldCatalog.GlPostDate;
    public const string DocDate = JetFieldCatalog.GlDocDate;
    public const string VoucherDate = JetFieldCatalog.GlVoucherDate;
    public const string AccNum = JetFieldCatalog.GlAccNum;
    public const string AccName = JetFieldCatalog.GlAccName;
    public const string Description = JetFieldCatalog.GlDescription;
    public const string JeSource = JetFieldCatalog.GlJeSource;
    public const string CreateBy = JetFieldCatalog.GlCreateBy;
    public const string ApproveBy = JetFieldCatalog.GlApproveBy;
    public const string Manual = JetFieldCatalog.GlManual;
    public const string PostingStatus = JetFieldCatalog.GlPostingStatus;
    public const string Amount = JetFieldCatalog.GlAmount;
    public const string DebitAmount = JetFieldCatalog.GlDebitAmount;
    public const string CreditAmount = JetFieldCatalog.GlCreditAmount;
    public const string DcField = JetFieldCatalog.GlDcField;
    public const string DcDebitCode = JetFieldCatalog.GlDcDebitCode;
    public const string DcCreditCode = JetFieldCatalog.GlDcCreditCode;

    public static readonly IReadOnlyList<string> All = JetFieldCatalog.GlMappingKeys;
}

public static class TbMappingKeys
{
    public const string AccNum = JetFieldCatalog.TbAccNum;
    public const string AccName = JetFieldCatalog.TbAccName;
    public const string Amount = JetFieldCatalog.TbAmount;
    public const string DebitAmt = JetFieldCatalog.TbDebitAmount;
    public const string CreditAmt = JetFieldCatalog.TbCreditAmount;

    // OpenClose（legacy SA=2）：期初 / 期末餘額兩欄。
    public const string OpeningBalance = JetFieldCatalog.TbOpeningBalance;
    public const string ClosingBalance = JetFieldCatalog.TbClosingBalance;

    // OpenCloseBySide（legacy SA=4）：期初借貸 + 期末借貸四欄。
    public const string OpeningDebit = JetFieldCatalog.TbOpeningDebit;
    public const string OpeningCredit = JetFieldCatalog.TbOpeningCredit;
    public const string ClosingDebit = JetFieldCatalog.TbClosingDebit;
    public const string ClosingCredit = JetFieldCatalog.TbClosingCredit;

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
