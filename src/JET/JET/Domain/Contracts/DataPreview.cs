namespace JET.Domain;

/// <summary>
/// 使用者資料預覽（query.dataPreview）的業務資料集白名單。
/// 與 dev 檢視的差異：不暴露實體資料表名，只開放審計工作流程會接觸的資料。
/// </summary>
public enum DataPreviewDataset
{
    GlStaging,
    TbStaging,
    GlEntries,
    GlExcludedEntries,
    TbBalances,
    AccountMappings,
    AuthorizedPreparers,

    /// <summary>DATE_DIMENSION：事務所假日／補班日（staging_calendar_raw_day）。</summary>
    DateDimension,

    /// <summary>資料庫結構總覽：由 <c>JetSchemaCatalog</c> metadata 驅動，非任何資料表的列資料。</summary>
    SchemaOverview
}

public static class DataPreviewDatasetNames
{
    public static bool TryParse(string? wireName, out DataPreviewDataset dataset)
    {
        switch (wireName)
        {
            case "glStaging":
                dataset = DataPreviewDataset.GlStaging;
                return true;
            case "tbStaging":
                dataset = DataPreviewDataset.TbStaging;
                return true;
            case "glEntries":
                dataset = DataPreviewDataset.GlEntries;
                return true;
            case "glExcludedEntries":
                dataset = DataPreviewDataset.GlExcludedEntries;
                return true;
            case "tbBalances":
                dataset = DataPreviewDataset.TbBalances;
                return true;
            case "accountMappings":
                dataset = DataPreviewDataset.AccountMappings;
                return true;
            case "authorizedPreparers":
                dataset = DataPreviewDataset.AuthorizedPreparers;
                return true;
            case "dateDimension":
                dataset = DataPreviewDataset.DateDimension;
                return true;
            case "schemaOverview":
                dataset = DataPreviewDataset.SchemaOverview;
                return true;
            default:
                dataset = default;
                return false;
        }
    }
}

/// <summary>資料集專屬統計的 closed typed contract。</summary>
public abstract record DataPreviewStats;

/// <summary>
/// glEntries 的概況統計（進階篩選的把關資訊）。
/// 金額為 ABS(amount_scaled) 的顯示值——篩選的數值區間比較的就是絕對值。
/// </summary>
public sealed record GlEntriesPreviewStats(
    decimal AmountAbsMin,
    decimal AmountAbsMax,
    string? PostDateMin,
    string? PostDateMax,
    long VoucherCount) : DataPreviewStats;

/// <summary>
/// glExcludedEntries 的互斥排除原因總數。兩值在空 dataset 時仍明示為 0。
/// </summary>
public sealed record GlExcludedEntriesPreviewStats(
    long ExcludedByPeriodCount,
    long ExcludedByPostingStatusCount) : DataPreviewStats;

/// <summary>
/// 有界預覽結果：rows ≤ limit、cell 一律字串（NULL → null）。
/// 一般資料集無資料 = 空 Columns/Rows + TotalCount 0（不是錯誤）；
/// glExcludedEntries 仍保留固定 Columns 與非 null 的零值 Stats。
/// </summary>
public sealed record DataPreviewResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    long TotalCount,
    DataPreviewStats? Stats);

public interface IDataPreviewRepository
{
    Task<DataPreviewResult> GetPreviewAsync(
        string projectId,
        DataPreviewDataset dataset,
        int moneyScale,
        int limit,
        CancellationToken cancellationToken);
}
