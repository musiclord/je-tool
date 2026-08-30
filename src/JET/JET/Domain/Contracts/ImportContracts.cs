namespace JET.Domain;

/// <summary>Legacy TableDef 可見的四種欄位型態。</summary>
internal enum LegacyFieldKind
{
    Text,
    Number,
    Date,
    Time
}

/// <summary>
/// reader 對單一非空 cell 的欄位定義觀察值。TextLength 一律記錄正規化後文字長度，
/// 讓後續同欄型態衝突降為 Text 時仍可取完整最大長度；DecimalPlaces 只對 Number 有值。
/// </summary>
internal sealed record TabularCellObservation(
    string FieldName,
    LegacyFieldKind Kind,
    int TextLength,
    int? DecimalPlaces);

/// <summary>
/// 一列原始匯入資料。Values 只含非空 cell（key = 正規化後的來源標頭），
/// SourceRowNumber 為來源檔內的實際列號（標頭 = 1），錯誤訊息可直接對應使用者所見。
/// </summary>
public sealed record StagingRow(
    int SourceRowNumber,
    IReadOnlyDictionary<string, string> Values)
{
    /// <summary>與 Values 同列的非空 cell 型態證據；空 cell 不產生 observation。</summary>
    internal IReadOnlyList<TabularCellObservation> FieldObservations { get; init; } = [];

    internal StagingRow(
        int sourceRowNumber,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<TabularCellObservation> fieldObservations)
        : this(sourceRowNumber, values)
    {
        FieldObservations = fieldObservations;
    }
}

/// <summary>
/// 批次內單一來源的描述（寫入 import_batch_source）。
/// FilePath 只供本次讀檔；FileName 是可持久化／顯示的 leaf name。
/// SheetName / EncodingName / Delimiter 記錄呼叫時指定的值，null = 交由偵測鏈判定。
/// </summary>
public sealed record ImportSourceDescriptor(
    string FilePath,
    string FileName,
    string? SheetName,
    string? EncodingName,
    string? Delimiter);

/// <summary>
/// 將 host 提供的顯示名稱收斂為單一 leaf name。呼叫端傳入的 fileName 不是路徑權威；
/// 它即使帶目錄也只能貢獻最後一段，無效時回退實際讀檔路徑的最後一段。
/// </summary>
internal static class ImportSourceFileName
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    internal static ImportSourceDescriptor Normalize(ImportSourceDescriptor source) =>
        source with { FileName = Resolve(source.FilePath, source.FileName) };

    internal static string Resolve(string filePath, string? suggestedFileName)
    {
        var leafName = TryGetLeafName(suggestedFileName) ?? TryGetLeafName(filePath);
        if (leafName is null)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "來源檔案名稱無效，無法建立可攜的匯入紀錄。");
        }

        return leafName;
    }

    private static string? TryGetLeafName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var leafName = Path.GetFileName(value.Trim());
            return string.IsNullOrWhiteSpace(leafName)
                || leafName is "." or ".."
                || leafName.IndexOfAny(InvalidFileNameChars) >= 0
                    ? null
                    : leafName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// 同一匯入 action 內的一個來源。Columns 是 reader 的 bounded 標頭結果；Rows 維持 streaming，
/// 由 provider repository 在整批共用的 transaction 內依序消費。
/// </summary>
public sealed record ImportSourceInput(
    ImportSourceDescriptor Source,
    IReadOnlyList<string> Columns,
    IAsyncEnumerable<StagingRow> Rows);

/// <summary>批次內單一來源的持久化資訊（manifest sources 形狀）。</summary>
public sealed record ImportSourceInfo(
    int SourceNo,
    string FileName,
    string? SheetName,
    string? Encoding,
    string? Delimiter,
    int RowCount,
    DateTimeOffset ImportedUtc);

/// <summary>
/// 匯入批次。一個 GL/TB 資料集對應一個批次，可由多個來源組成（guide §3.1.4）。
/// SourceFileName = 第一個來源檔名（向後相容的顯示欄位）；權威來源清單在 Sources。
/// </summary>
public sealed record ImportBatchInfo(
    string BatchId,
    DatasetKind Kind,
    string SourceFileName,
    DateTimeOffset ImportedUtc,
    int RowCount,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ImportSourceInfo> Sources);

/// <summary>匯入（replace 或 append）的結果：批次最新狀態 + 本次實際寫入列數。</summary>
public sealed record ImportBatchResult(
    ImportBatchInfo Batch,
    int AddedRowCount);

/// <summary>
/// 單一表格來源的讀取請求（manifest import.*.fromFile 的可選欄位）。
/// SheetName 僅 .xlsx 有效；EncodingName / Delimiter 僅 .csv/.txt 有效，
/// null 表示交由 reader 偵測（guide §3.1.1）。欄位適用性驗證在 handler，reader 只消費。
/// </summary>
public sealed record TabularSourceRequest(
    string FilePath,
    string? SheetName = null,
    string? EncodingName = null,
    char? Delimiter = null,
    int LeadingRowsToSkip = 0);

/// <summary>
/// 單一工作表的檢視結果（空工作表 Columns 為空清單）。
/// RowCountEstimate = 自 dimension 元素推估的資料列數（manifest import.inspectFile）：
/// 可能過時、僅顯示用、不得用於驗證；無 dimension 或無標頭列時為 null。
/// </summary>
public sealed record WorksheetInspection(
    string Name,
    IReadOnlyList<string> Columns,
    int? RowCountEstimate = null);

/// <summary>
/// 匯入前的唯讀檔案檢視（manifest import.inspectFile）。
/// .xlsx：Worksheets 有值、其餘 null；.csv/.txt：Columns/Encoding 有值、
/// Delimiter 為偵測結果（單欄檔 null）、Worksheets null。
/// </summary>
public sealed record TabularFileInspection(
    string FileType,
    IReadOnlyList<WorksheetInspection>? Worksheets,
    IReadOnlyList<string>? Columns,
    string? Encoding,
    string? Delimiter);

/// <summary>
/// 表格檔案讀取器。IAsyncEnumerable 形狀讓未來 SAX reader
/// 可直接替換而不動 handler 契約。
/// </summary>
public interface ITabularFileReader
{
    bool Supports(string filePath);

    Task<IReadOnlyList<string>> ReadColumnsAsync(TabularSourceRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<StagingRow> ReadRowsAsync(TabularSourceRequest request, CancellationToken cancellationToken);

    /// <summary>唯讀檢視檔案結構（工作表清單/欄名/偵測到的編碼與分隔符），零副作用、不回資料列。</summary>
    Task<TabularFileInspection> InspectAsync(string filePath, CancellationToken cancellationToken);
}

public interface IImportRepository
{
    /// <summary>
    /// 以 replace 語意匯入：在單一 transaction 內刪除同 dataset 的舊批次、staging rows、
    /// target rows 與 committed mapping，再以本次來源開立新批次（來源序號 1）。
    /// </summary>
    Task<ImportBatchResult> ReplaceBatchAsync(
        string projectId,
        DatasetKind kind,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken);

    /// <summary>
    /// 以 replace 語意原子匯入一到多個來源；所有來源共用一個 provider transaction，
    /// 任一來源失敗時保留呼叫前的完整批次狀態。
    /// </summary>
    Task<ImportBatchResult> ReplaceBatchAsync(
        string projectId,
        DatasetKind kind,
        IReadOnlyList<ImportSourceInput> sources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count != 1)
        {
            throw new NotSupportedException(
                "此 IImportRepository adapter 尚未實作多來源 replace；production provider 必須覆寫 batch overload。");
        }

        var input = sources[0];
        return ReplaceBatchAsync(
            projectId,
            kind,
            input.Source,
            input.Columns,
            input.Rows,
            cancellationToken);
    }

    /// <summary>
    /// 以 append 語意把來源加入該 dataset 的現有批次（guide §3.1.4）。
    /// 無批次 → no_import_batch；欄名集合不一致 → column_mismatch；0 資料列 → empty_workbook（rollback）。
    /// 成功時在同一 transaction 內清除該 dataset 的 target rows 與 committed mapping（下游失效）。
    /// </summary>
    Task<ImportBatchResult> AppendToBatchAsync(
        string projectId,
        DatasetKind kind,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken);

    /// <summary>
    /// 以 append 語意原子加入一到多個來源；所有來源與下游失效共用一個 provider transaction。
    /// </summary>
    Task<ImportBatchResult> AppendToBatchAsync(
        string projectId,
        DatasetKind kind,
        IReadOnlyList<ImportSourceInput> sources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count != 1)
        {
            throw new NotSupportedException(
                "此 IImportRepository adapter 尚未實作多來源 append；production provider 必須覆寫 batch overload。");
        }

        var input = sources[0];
        return AppendToBatchAsync(
            projectId,
            kind,
            input.Source,
            input.Columns,
            input.Rows,
            cancellationToken);
    }

    Task<ImportBatchInfo?> GetLatestBatchAsync(
        string projectId,
        DatasetKind kind,
        CancellationToken cancellationToken);
}

public interface IProjectDatabaseInitializer
{
    Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>
    /// 指定 provider 的後端是否已有此 projectId 的既有資料(SQLite:jet.db 檔;SQL Server:專案 schema——
    /// 含本機登記遺失後的孤兒殘留)。供 project.create 在寫入任何本機檔案之前攔截撞名與殘留。
    /// provider 由呼叫端顯式傳入而非路由解析:此檢查發生在 project.json 落定之前,resolver 無從讀取,
    /// 且解析結果會以 app 生命週期快取殘留、劫持同名後續建案的路由(見 ProjectCreateHandler)。
    /// </summary>
    Task<bool> DatabaseExistsAsync(string projectId, string databaseProvider, CancellationToken cancellationToken);
}

/// <summary>
/// 永久刪除某專案的資料庫（鏡射 <see cref="IProjectDatabaseInitializer"/>）。
/// 本地引擎刪資料庫檔（jet.sqlite／jet.duckdb）；SQL Server 刪單庫 JET 內的專案 schema（DROP TABLE＋DROP SCHEMA）。
/// provider 由 ProviderRouting 包裝依專案選擇（project.delete 用）。
/// </summary>
public interface IProjectDatabaseDeleter
{
    Task DeleteAsync(string projectId, CancellationToken cancellationToken);
}
