using System.Data;
using System.Text.Json;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 線上專案登記簿 <see cref="IProjectRegistry"/> 的 SQL Server 實作（<c>dbo.project_registry</c> ＋
/// 存取名單 <c>dbo.project_access</c>，兩表皆在單庫 <c>JET</c>／測試 <c>JET_Test</c> 的 <c>dbo</c>）。
/// <b>天生只屬 sqlServer</b>：不在依資料庫種類選定的資料庫組裡，直接持有 <see cref="SqlServerConnectionOptions"/>
/// 對單庫開連線（與 <see cref="SqlServerProjectDatabase"/> 平行，各自管理自己的 dbo 反查／登記表）。
/// bootstrap 冪等（IF OBJECT_ID IS NULL CREATE TABLE，並以 TRY/CATCH 吞併發建表競速），於每個公開
/// 方法開頭 ensure；鍵欄釘 <c>Latin1_General_BIN2</c>（與既有表不 join、無跨 collation 危險）。
/// 連線／DB 不存在的處理：讀方法與 Unregister/Touch 於「單庫尚未建立」時優雅回空/略過；Register 走
/// fail-loud（單庫應已由 EnsureCreated 建好，開連線失敗即讓建案整體失敗）。連線失敗一律不吞——由
/// Application 端（project.list）catch 降級。project_json 以 <see cref="JetJsonStorage"/> 存取（與
/// <c>JsonFileProjectStore</c> 同一設定，UnsafeRelaxedJsonEscaping），原樣 round-trip <see cref="ProjectDocument"/>。
/// </summary>
public sealed class SqlServerProjectRegistry(SqlServerConnectionOptions options) : IProjectRegistry
{
    // 首次觸碰時 bootstrap 兩張 dbo 表；之後同一實例跳過（表已在、單庫必存在）——參考 SqlServerProjectDatabase 的快取旗標。
    private bool _tablesEnsured;

    // dbo 共用管理表的寫入以死鎖有限次重試包裹：多 client 同時 Register/Unregister/list
    // 觸碰同兩張 dbo 表，1205 不再是理論風險；各方法自含完整交易（死鎖 rollback 後整段重跑安全）。
    public Task RegisterAsync(ProjectDocument document, string principal, CancellationToken cancellationToken) =>
        SqlServerEngineErrors.ExecuteWithDeadlockRetryAsync(
            ct => RegisterOnceAsync(document, principal, ct), cancellationToken);

    private async Task RegisterOnceAsync(ProjectDocument document, string principal, CancellationToken cancellationToken)
    {
        // fail-loud：單庫應已由 EnsureCreatedAsync 建好（建案）或 schema 已在（lazy-heal）；不存在則開連線失敗、建案整體失敗。
        await using var connection = await OpenSingleDatabaseAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // registry 一列：project_json 原樣入庫；created_by 由伺服器端 SUSER_SNAME() 取值（共用 jetapp 登入下恆為 jetapp）。
        await using (var insertRegistry = connection.CreateCommand())
        {
            insertRegistry.Transaction = transaction;
            insertRegistry.CommandText =
                """
                INSERT INTO dbo.project_registry
                    (project_id, schema_name, project_json, created_by, created_utc, last_opened_utc)
                VALUES (@id, @schema, @json, SUSER_SNAME(), @created, NULL);
                """;
            insertRegistry.Parameters.AddWithValue("@id", document.ProjectId);
            insertRegistry.Parameters.AddWithValue("@schema", SqlServerProjectSchema.For(document.ProjectId));
            insertRegistry.Parameters.AddWithValue("@json", Serialize(document));
            insertRegistry.Parameters.Add("@created", SqlDbType.DateTime2).Value = document.CreatedUtc.UtcDateTime;
            await insertRegistry.ExecuteNonQueryAsync(cancellationToken);
        }

        // access 一列：建立者自動獲授權（principal＝client 自報的 Windows 帳號名）。
        await using (var insertAccess = connection.CreateCommand())
        {
            insertAccess.Transaction = transaction;
            insertAccess.CommandText =
                "INSERT INTO dbo.project_access (project_id, principal, granted_utc) VALUES (@id, @principal, @granted);";
            insertAccess.Parameters.AddWithValue("@id", document.ProjectId);
            insertAccess.Parameters.AddWithValue("@principal", principal);
            insertAccess.Parameters.Add("@granted", SqlDbType.DateTime2).Value = DateTimeOffset.UtcNow.UtcDateTime;
            await insertAccess.ExecuteNonQueryAsync(cancellationToken);
        }

        // 同交易留痕(project.create):撞 PK 回滾時 audit 一併回滾——建案失敗必不留痕。
        await SqlServerAuditLog.WriteAsync(
            connection, transaction, document.ProjectId, "project.create", detailJson: null, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public Task UpdateDocumentAsync(ProjectDocument document, CancellationToken cancellationToken) =>
        SqlServerEngineErrors.ExecuteWithDeadlockRetryAsync(
            ct => UpdateDocumentOnceAsync(document, ct), cancellationToken);

    private async Task UpdateDocumentOnceAsync(
        ProjectDocument document,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenSingleDatabaseAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE dbo.project_registry SET project_json = @json WHERE project_id = @id;";
        command.Parameters.AddWithValue("@json", Serialize(document));
        command.Parameters.AddWithValue("@id", document.ProjectId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到線上專案 '{document.ProjectId}' 的登記資料。");
        }
    }

    public async Task<bool> ExistsAsync(string projectId, CancellationToken cancellationToken)
    {
        await using var connection = await TryOpenSingleDatabaseAsync(cancellationToken);
        if (connection is null)
        {
            return false; // 單庫尚未建立 → 不可能有登記列。
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.project_registry WHERE project_id = @id) THEN 1 ELSE 0 END;";
        command.Parameters.AddWithValue("@id", projectId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<IReadOnlyList<RegisteredProject>> ListVisibleAsync(
        string principal, CancellationToken cancellationToken)
    {
        await using var connection = await TryOpenSingleDatabaseAsync(cancellationToken);
        if (connection is null)
        {
            return []; // 單庫尚未建立 → 無可見案件（伺服器可達但登記簿尚未問世）。
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.project_json, r.created_utc, r.last_opened_utc
            FROM dbo.project_registry r
            INNER JOIN dbo.project_access a ON a.project_id = r.project_id
            WHERE a.principal = @principal;
            """;
        command.Parameters.AddWithValue("@principal", principal);

        var results = new List<RegisteredProject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadRegistered(reader));
        }

        return results;
    }

    public async Task<RegisteredProject?> FindVisibleAsync(
        string projectId, string principal, CancellationToken cancellationToken)
    {
        await using var connection = await TryOpenSingleDatabaseAsync(cancellationToken);
        if (connection is null)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.project_json, r.created_utc, r.last_opened_utc
            FROM dbo.project_registry r
            INNER JOIN dbo.project_access a ON a.project_id = r.project_id
            WHERE a.principal = @principal AND r.project_id = @id;
            """;
        command.Parameters.AddWithValue("@principal", principal);
        command.Parameters.AddWithValue("@id", projectId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRegistered(reader) : null;
    }

    public async Task TouchLastOpenedAsync(string projectId, CancellationToken cancellationToken)
    {
        await using var connection = await TryOpenSingleDatabaseAsync(cancellationToken);
        if (connection is null)
        {
            return; // 單庫尚未建立 → 無列可戳。best-effort，呼叫端已吞例外。
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.project_registry SET last_opened_utc = @now WHERE project_id = @id;";
        command.Parameters.Add("@now", SqlDbType.DateTime2).Value = DateTimeOffset.UtcNow.UtcDateTime;
        command.Parameters.AddWithValue("@id", projectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task UnregisterAsync(string projectId, CancellationToken cancellationToken) =>
        SqlServerEngineErrors.ExecuteWithDeadlockRetryAsync(
            ct => UnregisterOnceAsync(projectId, ct), cancellationToken);

    private async Task UnregisterOnceAsync(string projectId, CancellationToken cancellationToken)
    {
        await using var connection = await TryOpenSingleDatabaseAsync(cancellationToken);
        if (connection is null)
        {
            return; // 單庫尚未建立 → 無登記可清（優雅略過，對齊 SqlServerProjectDatabase.DeleteAsync）。
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var deleteAccess = connection.CreateCommand())
        {
            deleteAccess.Transaction = transaction;
            deleteAccess.CommandText = "DELETE FROM dbo.project_access WHERE project_id = @id;";
            deleteAccess.Parameters.AddWithValue("@id", projectId);
            await deleteAccess.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var deleteRegistry = connection.CreateCommand())
        {
            deleteRegistry.Transaction = transaction;
            deleteRegistry.CommandText = "DELETE FROM dbo.project_registry WHERE project_id = @id;";
            deleteRegistry.Parameters.AddWithValue("@id", projectId);
            await deleteRegistry.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    // project_json 與 JsonFileProjectStore 同一設定（UnsafeRelaxedJsonEscaping）→ 中文原樣、round-trip 一致。
    private static string Serialize(ProjectDocument document) =>
        JsonSerializer.Serialize(document, JetJsonStorage.IndentedOptions);

    private static RegisteredProject ReadRegistered(SqlDataReader reader)
    {
        ProjectDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ProjectDocument>(reader.GetString(0), JetJsonStorage.IndentedOptions)
                ?? throw new JetActionException(
                    JetErrorCodes.InvalidPayload, "registry 的 project_json 反序列化為 null（登記簿資料損壞）。");
        }
        catch (JsonException ex) when (ProjectDocumentSeedIntegrity.IsSeedJsonPath(ex.Path))
        {
            throw ProjectDocumentSeedIntegrity.Corruption(
                "SQL Server registry 的 project_json",
                "sampleSeed 或 sampleSeedVersion 的 JSON 格式不合法");
        }

        ProjectDocumentSeedIntegrity.Validate(document, "SQL Server registry 的 project_json");
        var createdUtc = new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero);
        DateTimeOffset? lastOpenedUtc =
            reader.IsDBNull(2) ? null : new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero);
        return new RegisteredProject(document, createdUtc, lastOpenedUtc);
    }

    /// <summary>開單庫連線並確保登記表就位（fail-loud：單庫不存在則 OpenAsync 拋、建案整體失敗）。</summary>
    private async Task<SqlConnection> OpenSingleDatabaseAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(BuildConnectionString(options.SingleDatabaseName));
        await connection.OpenAsync(cancellationToken);
        await EnsureTablesAsync(connection, cancellationToken);
        return connection;
    }

    /// <summary>開單庫連線；單庫尚未建立時回 null（讀方法與 Unregister/Touch 據此優雅回空/略過）。</summary>
    private async Task<SqlConnection?> TryOpenSingleDatabaseAsync(CancellationToken cancellationToken)
    {
        // 已 bootstrap 過 → 單庫必存在，免再連 master 探測。
        if (!_tablesEnsured && !await SingleDatabaseExistsAsync(cancellationToken))
        {
            return null;
        }

        return await OpenSingleDatabaseAsync(cancellationToken);
    }

    /// <summary>
    /// 單庫是否已存在。盡量不依賴 master:AssumeDatabaseExists 或 process 級就緒已確認
    /// (<see cref="SqlServerSingleDatabaseReadiness"/>)時一律當「已存在」、不連 master;否則連 master、DB_ID
    /// (不直接開單庫連線,避免「庫未建」時登入失敗),找到即標記就緒。與 <see cref="SqlServerProjectDatabase"/> 共用旗標。
    /// </summary>
    private async Task<bool> SingleDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var readinessKey = SqlServerSingleDatabaseReadiness.KeyFor(options);
        if (options.AssumeDatabaseExists || SqlServerSingleDatabaseReadiness.IsConfirmed(readinessKey))
        {
            return true;
        }

        await using var master = new SqlConnection(BuildConnectionString("master"));
        await master.OpenAsync(cancellationToken);
        await using var command = master.CreateCommand();
        command.CommandText = "SELECT DB_ID(@db);";
        command.Parameters.AddWithValue("@db", options.SingleDatabaseName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var exists = result is not null and not DBNull;
        if (exists)
        {
            SqlServerSingleDatabaseReadiness.MarkConfirmed(readinessKey);
        }

        return exists;
    }

    private async Task EnsureTablesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (_tablesEnsured)
        {
            return;
        }

        // 冪等 bootstrap:dbo 管理表五張(registry/access/app_config/audit_log/project_lock)＋移除冗餘 project_schema_map,
        // 收斂於 SqlServerControlPlaneSchema(與 database、appConfig 共用同一份 DDL)。
        await SqlServerControlPlaneSchema.EnsureAsync(connection, cancellationToken);
        _tablesEnsured = true;
    }

    private string BuildConnectionString(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(options.BaseConnectionString))
        {
            throw new JetActionException(
                JetErrorCodes.SqlServerNotConfigured,
                "未設定 SQL Server 連線。線上專案登記簿需要環境變數 JET_SQLSERVER_CONNECTION。");
        }

        return new SqlConnectionStringBuilder(options.BaseConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;
    }
}
