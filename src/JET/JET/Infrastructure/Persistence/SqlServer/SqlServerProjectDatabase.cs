using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// SQL Server 的每專案 schema 連線工廠與 schema 初始化(對應 SQLite 的
/// <see cref="SqliteProjectDatabase"/>)。隔離模型:共用 instance、單一資料庫,每專案一個
/// <c>prj_xxx</c> schema(由 <see cref="SqlServerProjectSchema.For"/> 衍生),事實表建在該 schema 內、不帶 project_id 欄;
/// schema → 專案的反查一律讀 <c>dbo.project_registry</c>(其 schema_name 欄即權威;已移除冗餘的
/// <c>dbo.project_schema_map</c> 反查表,見 <see cref="SqlServerControlPlaneSchema"/> 的一次性遷移)。
/// 連線字串(base)的 InitialCatalog 即為該單一資料庫;不再依專案切換庫名。
/// 刪除走<b>單一連線、單一顯式交易</b>(原子):drop 該 schema 全表 → <c>DROP SCHEMA</c> →
/// 同交易寫 audit_log → 刪 <c>dbo.project_access</c>／<c>dbo.project_registry</c> 對應列(見 <see cref="DeleteAsync"/>)——
/// 單庫成為 sqlServer 專案的唯一管家,刪案不再分兩連線兩交易。
/// 目標引擎為 SQL Server 2022(開發用 Developer、生產用 Standard/Enterprise,共用本實作、差異僅在連線字串);
/// SQL Server Express(含 LocalDB,EngineEdition=4)已淘汰——單庫模型撞其 10GB 上限,偵測到即以
/// sql_server_express_unsupported 擋下(見 EnsureSingleDatabaseAndControlPlaneAsync)。
/// schema 目前一路 forward-only（SQL Server 無 legacy 專案，新 schema 直接建到現行版本），但版本化遷移機制已就位：
/// <see cref="EnsureCreatedAsync"/> 對既有 schema 讀 <c>{s}.schema_info</c> 的 <c>schema_version</c>，
/// 落後現行 <see cref="SchemaVersion"/> 才於單一交易內重跑守欄冪等的 <see cref="SchemaSql"/> 補齊形狀並回填版本
/// （見 <see cref="MigrateExistingSchemaToCurrentAsync"/>），使下一次 schema 版本 bump 時線上既有 schema 能自動升級。
/// </summary>
public sealed partial class SqlServerProjectDatabase(SqlServerConnectionOptions options)
    : IProjectDatabaseInitializer, IProjectDatabaseDeleter
{
    /// <summary>
    /// 回傳指向單庫的連線(未開啟)。InitialCatalog 已是單庫;projectId 僅用於衍生 schema、不影響庫選擇。
    /// schema-per-project 模型下,事實表存取由呼叫端透過 <see cref="CreateCommand"/> 的 {s} token 限定到專案 schema。
    /// </summary>
    public SqlConnection CreateConnection(string projectId)
    {
        return CreateSingleDbConnection();
    }

    /// <summary>
    /// 回傳指向單庫的連線(未開啟);InitialCatalog 顯式設為
    /// <see cref="SqlServerConnectionOptions.SingleDatabaseName"/>(不依賴 base 連線字串既有的 InitialCatalog)。
    /// </summary>
    private SqlConnection CreateSingleDbConnection()
    {
        return new SqlConnection(BuildConnectionString(options.SingleDatabaseName));
    }

    /// <summary>
    /// 方案 C 收斂點:把 SQL 中的哨兵 {s} 全部替換為該專案 schema 的方括號識別字。
    /// schema 名先過白名單,杜絕識別字注入(schema 名不可參數化)。
    /// 呼叫端自行 AddParameters 與設 Transaction。
    /// </summary>
    public SqlCommand CreateCommand(SqlConnection connection, string projectId, string sqlWithTokens)
    {
        var schema = SqlServerProjectSchema.For(projectId);
        if (!SqlServerProjectSchema.IsValid(schema))
        {
            throw new JET.Domain.JetActionException(
                JetErrorCodes.InvalidProjectSchema, $"專案 '{projectId}' 衍生出的 schema 名不合法。");
        }

        var command = connection.CreateCommand();
        command.CommandText = sqlWithTokens.Replace("{s}", $"[{schema}]");
        return command;
    }

    private string BuildConnectionString(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(options.BaseConnectionString))
        {
            throw new JetActionException(
                JetErrorCodes.SqlServerNotConfigured,
                "未設定 SQL Server 連線(環境變數 JET_SQLSERVER_CONNECTION)。選用 sqlServer provider 的專案需要它。");
        }

        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new JetActionException(
                JetErrorCodes.SqlServerNotConfigured,
                "SQL Server 單一資料庫名未設定(SqlServerConnectionOptions.SingleDatabaseName)。選用 sqlServer provider 的專案需要它。");
        }

        return new SqlConnectionStringBuilder(options.BaseConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;
    }

}

/// <summary>
/// SQL Server 連線設定。BaseConnectionString 由 <c>JET_SQLSERVER_CONNECTION</c> 或測試參數提供；
/// null 或空白表示尚未設定。使用 SQL Server 的專案會在連線時收到明確錯誤。
/// SingleDatabaseName 是 schema-per-project 模型下「所有專案共用的那一個資料庫」名稱:顯式 value、
/// 不再從連線字串 InitialCatalog 隱性推斷(避免「連線字串沒帶 Database」時退化成 <c>CREATE DATABASE []</c>)。
/// 預設 JET_Test:僅供測試以 1 引數建構時落在隔離測試庫(jetapp 擁有);app 一律顯式帶入正式庫 JET(由 config)。
/// AssumeDatabaseExists(盡量不依賴 master):為 true 時所有存在性/就緒檢查一律當「已存在」、
/// 完全不連 master——供 jetapp 無 master 連線權限的鎖定環境,建庫責任移交 DBA 預建。讀 appsettings 的
/// <c>Sql:AssumeDatabaseExists</c>(預設 false)。
/// </summary>
public sealed record SqlServerConnectionOptions(
    string? BaseConnectionString,
    string SingleDatabaseName = "JET_Test",
    bool AssumeDatabaseExists = false);
