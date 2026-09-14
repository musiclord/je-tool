namespace JET.Domain;

/// <summary>
/// 專案 metadata，持久化為 projects/{projectId}/project.json。
/// ProjectCode 和 EntityName 為選填顯示資訊；沒有提供時保存空字串，ProjectId 仍是必要的案件名稱與識別。
/// 日期一律以 "yyyy-MM-dd" 字串保存，避免序列化時區歧義。
/// DatabaseProvider 標示會計資料所在引擎（"sqlite"／"duckdb" 本地檔；"sqlServer" 單庫）；
/// 舊版 project.json 缺此欄位時由 store 讀取時正規化為 sqlite。
/// SampleSeed 為 INF 抽樣的 per-project 種子（建案時隨機生成一次、終身固定）；
/// SampleSeedVersion 是 nullable 的演算法 marker，缺欄位代表 legacy v1，不能用 0 代替缺席。
/// 連 SampleSeed 都缺欄位的更舊專案由本 Domain policy 回退 <see cref="LegacySampleSeed"/>。
/// CalendarImported 是日期檔成功 replace 的持久 marker；nullable 是為了讓舊 project.json
/// 可用既有日期筆數推斷，新案件則一律明寫 false，讓合法零筆匯入與從未匯入可區分。
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

    /// <summary>INF v1／v2 共用的 seed 排他上界（模數 2147483647）。種子取 [1, 2147483646]；
    /// 這也保留 v1 避開 0／模數倍數而造成線性排序退化的既有政策。</summary>
    public const long SampleSeedExclusiveUpperBound = 2147483647;

    /// <summary>sampleSeed 欄位問世前之舊專案固定回退值；internal 以避免擴張 public API。</summary>
    internal const long LegacySampleSeed = 48271;

    /// <summary>持久 seed 優先；舊 project.json 缺欄位時使用 Domain 相容政策。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal long EffectiveSampleSeed => SampleSeed ?? LegacySampleSeed;

    /// <summary>日期解析選項（guide §3.1.3）。RocDateEnabled 缺欄位時 JSON 反序列化採預設 true，舊 project.json 免遷移。</summary>
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

public interface IProjectStore
{
    /// <summary>
    /// 原子發布新專案；若相同 projectId 已存在，拋出 <see cref="ProjectStoreCollisionException"/>。
    /// 呼叫端再依建立請求是否由使用者命名，決定可否歸屬到 payload 欄位。
    /// </summary>
    Task CreateAsync(ProjectDocument document, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken);

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
