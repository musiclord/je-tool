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

/// <summary>資料預覽 GL 與 GL(未納入測試) 一欄的值從哪裡讀，讀出後怎麼轉成回應的字串。</summary>
public enum DataPreviewGlValueSource
{
    /// <summary>分錄表的文字或日期欄，原樣讀出。</summary>
    EntryText,

    /// <summary>分錄表存的金額整數，換算成金額。</summary>
    EntryAmount,

    /// <summary>分錄表的人工或自動判定，讀成 manual 或 automatic；沒有判定時是 null。</summary>
    EntryManualAuto,

    /// <summary>分錄表的未納入原因，讀成四種原因的回應值。</summary>
    EntryExclusionReason,

    /// <summary>攸關資料元素的值，依欄位型別讀文字、日期或金額；這筆分錄沒有值時是 null。</summary>
    Rde
}

/// <summary>
/// 資料預覽 GL 與 GL(未納入測試) 的一欄。Key 是回應 columns 的識別；Label 和欄位配對畫面同名；ValueType 是
/// <see cref="RdeFieldValueTypeNames"/> 的 text、date 或 money。Storage 是分錄表的欄名，攸關資料元素欄位則是它的 field_id。
/// 欄位清單由 <see cref="DataPreviewGlColumns"/> 依已確認的配對產生。
/// </summary>
public sealed record DataPreviewGlColumn(
    string Key,
    string Label,
    string ValueType,
    DataPreviewGlValueSource Source,
    string Storage);

/// <summary>
/// 有界預覽結果：rows ≤ limit、cell 一律字串（NULL → null）。
/// 一般資料集無資料 = 空 Columns/Rows + TotalCount 0（不是錯誤）；
/// glExcluded 仍保留依配對算出的 Columns 與非 null 的零值 Stats。
/// TotalCount 與 Stats 只在第一頁（offset 0）計算；之後的頁為 null，由畫面沿用第一頁的數字。
/// glExcluded 只看一種原因時，TotalCount 是這種原因的筆數。
/// GL 與 GL(未納入測試) 另帶 ColumnDetails，和 Columns 同順序寫出每欄的名稱與型別；其他資料集為 null。
/// 金額欄是存下的整數除以 MoneyScale 的精確十進位字串，不經過浮點數，四位小數的顯示交給畫面。
/// </summary>
public sealed record DataPreviewResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    long? TotalCount,
    DataPreviewStats? Stats,
    IReadOnlyList<DataPreviewGlColumn>? ColumnDetails = null);

public interface IDataPreviewRepository
{
    /// <param name="exclusionReason">只用於 <see cref="DataPreviewDataset.GlExcluded"/>：只回這種原因的分錄。</param>
    /// <param name="glColumns">
    /// GL 與 GL(未納入測試) 必須提供：依已確認的配對算出的欄位（<see cref="DataPreviewGlColumns"/>），倉儲照它產生查詢；
    /// 其他資料集不使用。
    /// </param>
    Task<DataPreviewResult> GetPreviewAsync(
        string projectId,
        DataPreviewDataset dataset,
        int moneyScale,
        int limit,
        CancellationToken cancellationToken,
        int offset = 0,
        GlExclusionReason? exclusionReason = null,
        IReadOnlyList<DataPreviewGlColumn>? glColumns = null);
}
