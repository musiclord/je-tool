using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// <see cref="IControlPlaneReconciler"/> 的 SQL Server 實作（<c>dev.db.reconcile</c>，開發用的資料庫漂移檢查）。
/// 比對單庫 <c>sys.schemas</c>（<c>prj_%</c>）、<c>dbo.project_registry</c> 與本機 projects 資料夾
/// （<see cref="IProjectStore"/> 的 sqlServer 專案）。開始比對前會先確保 dbo 管理表存在，這一步可能建表，
/// 也會移除已停用的 <c>project_schema_map</c>；比對出的三種漂移只回報給人決定，不自動刪 schema、登錄列或資料夾。
/// <para>schema → 專案的反查一律以純函式 <see cref="SqlServerProjectSchema.For"/> 衍生 + registry.schema_name 對照,
/// 不依賴已移除的 project_schema_map。</para>
/// 單庫尚未建立（切換伺服器/全新環境）時 schema/registry 兩集合視為空——本機所有 sqlServer 資料夾即殭屍。
/// 直接 try-open 單庫（不連 master;dev 工具刻意輕量）,庫不存在的 SqlException 視為空集合。
/// </summary>
public sealed class SqlServerControlPlaneReconciler(
    SqlServerConnectionOptions options,
    IProjectStore projectStore) : IControlPlaneReconciler
{
    public async Task<ControlPlaneReconcileReport> ReconcileAsync(CancellationToken cancellationToken)
    {
        // 本機 sqlServer 專案（其衍生 schema 是殭屍判定的左手邊）。
        var localSqlServerProjectIds = (await projectStore.ListAsync(cancellationToken))
            .Where(d => d.DatabaseProvider == ProjectDocument.SqlServerDatabaseProvider)
            .Select(d => d.ProjectId)
            .ToList();

        var schemaNames = new List<string>();
        var registrations = new List<GhostRegistration>(); // 借用 record 承載 (projectId, schemaName)

        await using (var connection = await TryOpenSingleDatabaseAsync(cancellationToken))
        {
            if (connection is not null)
            {
                await SqlServerControlPlaneSchema.EnsureAsync(connection, cancellationToken);
                await LoadSchemasAsync(connection, schemaNames, cancellationToken);
                await LoadRegistrationsAsync(connection, registrations, cancellationToken);
            }
        }

        var schemaSet = new HashSet<string>(schemaNames, StringComparer.Ordinal);
        var registeredSchemaSet = new HashSet<string>(
            registrations.Select(r => r.SchemaName), StringComparer.Ordinal);

        // 孤兒 schema:有 prj_% schema、registry 無對應列。
        var orphanSchemas = schemaNames
            .Where(s => !registeredSchemaSet.Contains(s))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        // 幽靈登記:registry 有列、無對應 schema。
        var ghostRegistrations = registrations
            .Where(r => !schemaSet.Contains(r.SchemaName))
            .OrderBy(r => r.ProjectId, StringComparer.Ordinal)
            .ToList();

        // 殭屍資料夾:本機 sqlServer 資料夾、衍生 schema 卻不存在。
        var zombieFolders = localSqlServerProjectIds
            .Where(id => !schemaSet.Contains(SqlServerProjectSchema.For(id)))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return new ControlPlaneReconcileReport(orphanSchemas, ghostRegistrations, zombieFolders);
    }

    /// <summary>開單庫連線;庫尚未建立（SqlException）→ 回 null（schema/registry 視為空）。不連 master。</summary>
    private async Task<SqlConnection?> TryOpenSingleDatabaseAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(BuildConnectionString(options.SingleDatabaseName));
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (SqlException)
        {
            await connection.DisposeAsync();
            return null;
        }
    }

    private static async Task LoadSchemasAsync(
        SqlConnection connection, List<string> schemaNames, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // prj_ 的底線是 LIKE 萬用字元,以 [_] 逸出為字面底線。
        command.CommandText = "SELECT name FROM sys.schemas WHERE name LIKE 'prj[_]%';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            schemaNames.Add(reader.GetString(0));
        }
    }

    private static async Task LoadRegistrationsAsync(
        SqlConnection connection, List<GhostRegistration> registrations, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_id, schema_name FROM dbo.project_registry;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            registrations.Add(new GhostRegistration(reader.GetString(0), reader.GetString(1)));
        }
    }

    private string BuildConnectionString(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(options.BaseConnectionString))
        {
            throw new JetActionException(
                JetErrorCodes.SqlServerNotConfigured,
                "未設定 SQL Server 連線。控制面對帳需要環境變數 JET_SQLSERVER_CONNECTION。");
        }

        return new SqlConnectionStringBuilder(options.BaseConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;
    }
}
