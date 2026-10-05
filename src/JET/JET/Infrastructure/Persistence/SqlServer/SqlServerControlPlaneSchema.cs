using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 單庫 dbo 管理表（專案登錄、存取名單、系統設定、操作紀錄與租約鎖）的<b>唯一</b> bootstrap 點：一次冪等建立
/// <c>project_registry</c>／<c>project_access</c>／<c>app_config</c>／<c>audit_log</c>／<c>project_lock</c> 五張 dbo 表，
/// 並執行 <c>project_schema_map</c> 的一次性遷移移除（該反查表已與 registry.schema_name 完全冗餘、
/// 生產零讀取——schema 反查改指 registry；孤兒偵測改由 <c>dev.db.reconcile</c> 掃 sys.schemas）。
/// <c>project_lock</c> 為專案租約鎖：持有人每 30 秒心跳、崩潰未釋放者逾時（120 秒）由他人接管。
/// <para>
/// 集中在一處的原因：刪案交易化後，<see cref="SqlServerProjectDatabase"/> 也需在單庫連線上
/// 讀寫 registry/access/audit_log，與 <see cref="SqlServerProjectRegistry"/>、<see cref="SqlServerAppConfigStore"/>
/// 三個類別共用同一份 DDL。集中於此杜絕三處各寫一份 CREATE 而漂移。
/// </para>
/// 冪等策略沿用 registry 既有寫法：每張表 <c>IF OBJECT_ID IS NULL CREATE</c> 並以 TRY/CATCH 吞併發建表
/// 競速（2714＝物件已存在，屬另一連線先建成，其餘照拋）。鍵欄釘 <c>Latin1_General_BIN2</c>。
/// </summary>
internal static class SqlServerControlPlaneSchema
{
    /// <summary>
    /// 在已開啟的單庫連線上確保五張 dbo 管理表就位並移除 <c>project_schema_map</c>。DDL bootstrap 逐句
    /// auto-commit（不需顯式交易，與 registry 既有 EnsureTablesAsync 同語意）。
    /// </summary>
    public static async Task EnsureAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = BootstrapSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // 五張表 + 遷移移除 map，各段 TRY/CATCH 吞併發競速（2714 建表競速、3701 併發 DROP 競速），其餘照拋。
    private const string BootstrapSql =
        """
        BEGIN TRY
            IF OBJECT_ID(N'dbo.project_registry','U') IS NULL
                CREATE TABLE dbo.project_registry (
                    project_id      NVARCHAR(100) COLLATE Latin1_General_BIN2 NOT NULL PRIMARY KEY,
                    schema_name     NVARCHAR(64)  COLLATE Latin1_General_BIN2 NOT NULL UNIQUE,
                    project_json    NVARCHAR(MAX) NOT NULL,
                    created_by      NVARCHAR(128) NOT NULL,
                    created_utc     DATETIME2 NOT NULL,
                    last_opened_utc DATETIME2 NULL,
                    row_version     ROWVERSION
                );
        END TRY BEGIN CATCH
            IF ERROR_NUMBER() <> 2714 THROW;
        END CATCH;
        BEGIN TRY
            IF OBJECT_ID(N'dbo.project_access','U') IS NULL
                CREATE TABLE dbo.project_access (
                    project_id  NVARCHAR(100) COLLATE Latin1_General_BIN2 NOT NULL,
                    principal   NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL,
                    granted_utc DATETIME2 NOT NULL,
                    PRIMARY KEY (project_id, principal)
                );
        END TRY BEGIN CATCH
            IF ERROR_NUMBER() <> 2714 THROW;
        END CATCH;
        BEGIN TRY
            IF OBJECT_ID(N'dbo.app_config','U') IS NULL
                CREATE TABLE dbo.app_config (
                    [key]        NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL PRIMARY KEY,
                    value_json   NVARCHAR(MAX) NOT NULL,
                    updated_by   NVARCHAR(128) NOT NULL,
                    updated_utc  DATETIME2 NOT NULL
                );
        END TRY BEGIN CATCH
            IF ERROR_NUMBER() <> 2714 THROW;
        END CATCH;
        BEGIN TRY
            IF OBJECT_ID(N'dbo.audit_log','U') IS NULL
                CREATE TABLE dbo.audit_log (
                    id           BIGINT IDENTITY(1,1) PRIMARY KEY,
                    occurred_utc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    login_name   NVARCHAR(128) NOT NULL,
                    host_name    NVARCHAR(128) NOT NULL,
                    project_id   NVARCHAR(100) NULL,
                    action       NVARCHAR(100) NOT NULL,
                    detail_json  NVARCHAR(MAX) NULL
                );
        END TRY BEGIN CATCH
            IF ERROR_NUMBER() <> 2714 THROW;
        END CATCH;
        BEGIN TRY
            IF OBJECT_ID(N'dbo.project_lock','U') IS NULL
                CREATE TABLE dbo.project_lock (
                    project_id    NVARCHAR(100) COLLATE Latin1_General_BIN2 NOT NULL PRIMARY KEY,
                    locked_by     NVARCHAR(128) NOT NULL,
                    machine_name  NVARCHAR(128) NOT NULL,
                    locked_utc    DATETIME2 NOT NULL,
                    heartbeat_utc DATETIME2 NOT NULL
                );
        END TRY BEGIN CATCH
            IF ERROR_NUMBER() <> 2714 THROW;
        END CATCH;
        BEGIN TRY
            IF OBJECT_ID(N'dbo.project_schema_map','U') IS NOT NULL
                DROP TABLE dbo.project_schema_map;
        END TRY BEGIN CATCH
            IF ERROR_NUMBER() <> 3701 THROW;
        END CATCH;
        """;
}
