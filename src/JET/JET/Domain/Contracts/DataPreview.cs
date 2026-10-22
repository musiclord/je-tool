namespace JET.Domain;

/// <summary>
/// 使用者資料預覽（query.dataPreview）的業務資料集白名單。
/// 與 dev 檢視的差異：不暴露實體資料表名，只開放審計工作流程會接觸的資料。
/// </summary>
public enum DataPreviewDataset
{
    PbcGl,
    PbcTb,
    Gl,
    GlExcluded,
    Tb,
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
            case "pbcGl":
                dataset = DataPreviewDataset.PbcGl;
                return true;
            case "pbcTb":
                dataset = DataPreviewDataset.PbcTb;
                return true;
            case "gl":
                dataset = DataPreviewDataset.Gl;
                return true;
            case "glExcluded":
                dataset = DataPreviewDataset.GlExcluded;
                return true;
            case "tb":
                dataset = DataPreviewDataset.Tb;
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

/// <summary>
/// GL(未納入測試) 每一列的未納入原因。日期不符分成前三種（使用者 2026-10-21 裁定分開標示並各自計數，
/// 資料預覽可以只看其中一種）；GL 範圍與審計結果不變。
/// </summary>
public enum GlExclusionReason
{
    BlankPostDate,
    BeforePeriod,
    AfterPeriod,
    PostingStatus
}

public static class GlExclusionReasonNames
{
    public static bool TryParse(string? wireName, out GlExclusionReason reason)
    {
        switch (wireName)
        {
            case "blankPostDate":
                reason = GlExclusionReason.BlankPostDate;
                return true;
            case "beforePeriod":
                reason = GlExclusionReason.BeforePeriod;
                return true;
            case "afterPeriod":
                reason = GlExclusionReason.AfterPeriod;
                return true;
            case "postingStatus":
                reason = GlExclusionReason.PostingStatus;
                return true;
            default:
                reason = default;
                return false;
        }
    }

    public static string ToWireName(GlExclusionReason reason) => reason switch
    {
        GlExclusionReason.BlankPostDate => "blankPostDate",
        GlExclusionReason.BeforePeriod => "beforePeriod",
        GlExclusionReason.AfterPeriod => "afterPeriod",
        GlExclusionReason.PostingStatus => "postingStatus",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "未知的 GL 未納入原因。")
    };
}

/// <summary>資料集專屬統計的 closed typed contract。</summary>
public abstract record DataPreviewStats;

/// <summary>
/// gl（GL）的概況統計（進階篩選的把關資訊）。
/// 金額為 ABS(amount_scaled) 的顯示值——篩選的數值區間比較的就是絕對值。
/// </summary>
public sealed record GlPreviewStats(
    decimal AmountAbsMin,
    decimal AmountAbsMax,
    string? PostDateMin,
    string? PostDateMax,
    long VoucherCount) : DataPreviewStats;

/// <summary>
/// glExcluded（GL(未納入測試)）四種互斥未納入原因的筆數，空 dataset 時仍明示為 0。
/// 只看一種原因時，這四個數字仍描述全部未納入測試的分錄。
/// </summary>
public sealed record GlExcludedPreviewStats(
    long BlankPostDateCount,
    long BeforePeriodCount,
    long AfterPeriodCount,
    long PostingStatusCount) : DataPreviewStats
{
    public long CountOf(GlExclusionReason reason) => reason switch
    {
        GlExclusionReason.BlankPostDate => BlankPostDateCount,
        GlExclusionReason.BeforePeriod => BeforePeriodCount,
        GlExclusionReason.AfterPeriod => AfterPeriodCount,
        GlExclusionReason.PostingStatus => PostingStatusCount,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "未知的 GL 未納入原因。")
    };
}

/// <summary>
/// 有界預覽結果：rows ≤ limit、cell 一律字串（NULL → null）。
/// 一般資料集無資料 = 空 Columns/Rows + TotalCount 0（不是錯誤）；
/// glExcluded 仍保留固定 Columns 與非 null 的零值 Stats。
/// TotalCount 與 Stats 只在第一頁（offset 0）計算；之後的頁為 null，由畫面沿用第一頁的數字。
/// glExcluded 只看一種原因時，TotalCount 是這種原因的筆數。
/// </summary>
public sealed record DataPreviewResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    long? TotalCount,
    DataPreviewStats? Stats);

public interface IDataPreviewRepository
{
    /// <param name="exclusionReason">只用於 <see cref="DataPreviewDataset.GlExcluded"/>：只回這種原因的分錄。</param>
    Task<DataPreviewResult> GetPreviewAsync(
        string projectId,
        DataPreviewDataset dataset,
        int moneyScale,
        int limit,
        CancellationToken cancellationToken,
        int offset = 0,
        GlExclusionReason? exclusionReason = null);
}
