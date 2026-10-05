namespace JET.Domain;

/// <summary>
/// 專案 metadata，持久化為 projects/{projectId}/project.json。
/// ProjectCode 和 EntityName 為選填顯示資訊；沒有提供時保存空字串，ProjectId 仍是必要的案件名稱與識別。
/// 日期一律以 "yyyy-MM-dd" 字串保存，避免序列化時區歧義。
/// DatabaseProvider 標示會計資料所在引擎（"sqlite"／"duckdb" 本地檔；"sqlServer" 單庫）；
/// project.json 缺此欄位代表舊版 JET 建立的案件，store 讀取時明確拒絕。
/// SampleSeed 為 INF 抽樣的 per-project 種子（建案時隨機生成一次、終身固定）；
/// SampleSeedVersion 是 INF 抽樣演算法版本。兩者由建案寫入，缺任一欄位都代表舊版 JET 建立的案件，
/// 讀取 project.json 時明確拒絕，不回退固定種子或舊排序法。
/// CalendarImported 是日期檔成功 replace 的持久 marker；新案件一律明寫 false，讓合法零筆匯入與
/// 從未匯入可區分。缺欄位時一律視為尚未匯入。
/// </summary>
public sealed record ProjectDocument(
    string ProjectId,
    string ProjectCode,
    string EntityName,
    string OperatorId,
    string PeriodStart,
    string PeriodEnd,
    string? LastAccountingPeriodDate,
    int MoneyScale,
    string RoundingMode,
    DateTimeOffset CreatedUtc,
    int CurrentStep,
    int SchemaVersion,
    string DatabaseProvider = ProjectDocument.DefaultDatabaseProvider,
    bool RocDateEnabled = true,
    DateTimeOffset? LastOpenedUtc = null,
    IReadOnlyList<int>? NonWorkingDays = null,
    long? SampleSeed = null,
    int? SampleSeedVersion = null,
    bool? CalendarImported = null)
{
    public const int DefaultMoneyScale = 10_000;
    public const string DefaultRoundingMode = "AwayFromZero";
    public const int CurrentSchemaVersion = 1;
    public const string DefaultDatabaseProvider = "sqlite";
    public const string SqlServerDatabaseProvider = "sqlServer";
    /// <summary>第二本地引擎（每專案一個 jet.duckdb；與 sqlite 共用本地 repository 家族與可攜性語意）。</summary>
    public const string DuckDbDatabaseProvider = "duckdb";

    /// <summary>INF 抽樣 seed 的排他上界（模數 2147483647）。種子取 [1, 2147483646]。</summary>
    public const long SampleSeedExclusiveUpperBound = 2147483647;

    /// <summary>日期解析選項。RocDateEnabled 缺欄位時 JSON 反序列化採預設 true，舊 project.json 免遷移。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateParseOptions DateParseOptions => new(RocDateEnabled);

    /// <summary>
    /// 新案件的純 Domain policy。Clock、entropy、provider 選擇、名稱、路徑、
    /// schema、registry、lock 與 session 由外層提供；本方法只套用既有
    /// period／money／date／sample defaults，不新增期間先後等新規則。
    /// </summary>
    internal static ProjectDocument CreateNew(
        string projectId,
        string projectCode,
        string entityName,
        string operatorId,
        string periodStart,
        string periodEnd,
        string? lastAccountingPeriodDate,
        string databaseProvider,
        DateTimeOffset createdUtc,
        long sampleSeed,
        int sampleSeedVersion) =>
        new(
            projectId,
            projectCode,
            entityName,
            operatorId,
            periodStart,
            periodEnd,
            lastAccountingPeriodDate,
            DefaultMoneyScale,
            DefaultRoundingMode,
            createdUtc,
            CurrentStep: 1,
            CurrentSchemaVersion,
            databaseProvider)
        {
            SampleSeed = sampleSeed,
            SampleSeedVersion = sampleSeedVersion,
            CalendarImported = false
        };
}

/// <summary>案件清單的本機讀取結果。讀不到文件時只保留識別碼與可供使用者復原的錯誤。</summary>
public sealed record ProjectStoreEntry(
    string ProjectId,
    ProjectDocument? Document,
    string? ReadErrorCode = null,
    string? ReadErrorMessage = null);

public interface IProjectStore
{
    /// <summary>
    /// 原子發布新專案；若相同 projectId 已存在，拋出 <see cref="ProjectStoreCollisionException"/>。
    /// 呼叫端再依建立請求是否由使用者命名，決定可否歸屬到 payload 欄位。
    /// </summary>
    Task CreateAsync(ProjectDocument document, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken);

    Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken);

    Task SaveAsync(ProjectDocument document, CancellationToken cancellationToken);

    /// <summary>永久刪除專案資料夾（project.json + jet.db）。硬刪不可復原；資料庫由 deleter 另行清除（見 project.delete）。</summary>
    Task DeleteAsync(string projectId, CancellationToken cancellationToken);
}

/// <summary>
/// 專案 store 的 typed 發布衝突；不攜帶 UI 欄位歸屬或 wire error code。
/// </summary>
public sealed class ProjectStoreCollisionException(string projectId)
    : Exception($"Project '{projectId}' already exists.")
{
    public string ProjectId { get; } = projectId;
}
