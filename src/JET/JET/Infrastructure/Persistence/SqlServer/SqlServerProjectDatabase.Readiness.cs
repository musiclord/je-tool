using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

public sealed partial class SqlServerProjectDatabase
{
    // Express 淘汰守衛：首次連上單庫時檢查引擎版別一次,非 Express 即快取放行(避免每次操作重查)。
    private bool _engineVerified;

    public Task EnsureDatabaseReadyAsync(CancellationToken cancellationToken) =>
        EnsureSingleDatabaseAndControlPlaneAsync(cancellationToken);

    /// <summary>
    /// 確保單庫存在(連 master、DB_ID 守冪等)並確保 dbo 控制面表就位(<see cref="SqlServerControlPlaneSchema"/>:
    /// registry/access/app_config/audit_log,並移除冗餘的 project_schema_map)。
    /// 單庫名取自 <see cref="SqlServerConnectionOptions.SingleDatabaseName"/>(顯式設定值、非使用者輸入、
    /// 不從連線字串 InitialCatalog 猜測)。空白即視為未設定 → 明確錯誤,杜絕 <c>CREATE DATABASE []</c>。
    /// <para>master 依賴最小化(§4):就緒(存在＋非 Express)由 process 級旗標
    /// <see cref="SqlServerSingleDatabaseReadiness"/> 快取,確認後全 app 生命週期跳過 master;
    /// <see cref="SqlServerConnectionOptions.AssumeDatabaseExists"/>=true 時完全不連 master(庫由 DBA 預建)。</para>
    /// </summary>
    private async Task EnsureSingleDatabaseAndControlPlaneAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SingleDatabaseName))
        {
            throw new JetActionException(
                JetErrorCodes.SqlServerNotConfigured,
                "SQL Server 單一資料庫名未設定(SqlServerConnectionOptions.SingleDatabaseName)。選用 sqlServer provider 的專案需要它。");
        }

        // master 探測至多一次/process/目標:AssumeDatabaseExists=true 或此目標已確認就緒 → 跳過整個 master 區塊
        // (不建庫、不驗引擎),庫由 DBA 預建、責任外移。首次(未確認且不假設)才連 master 建庫＋驗引擎。
        var readinessKey = SqlServerSingleDatabaseReadiness.KeyFor(options);
        if (!options.AssumeDatabaseExists && !SqlServerSingleDatabaseReadiness.IsConfirmed(readinessKey))
        {
            var dbName = options.SingleDatabaseName;
            await using var master = new SqlConnection(BuildConnectionString("master"));
            await master.OpenAsync(cancellationToken);

            // Express 已淘汰(EngineEdition=4,含 LocalDB):單庫模型下所有專案共用一個資料庫,會撞 Express 的
            // 10 GB 上限。偵測到即擋下、不建庫,回明確錯誤碼引導改用 SQL Server 2022。首次檢查後快取放行。
            if (!_engineVerified)
            {
                await using var caps = master.CreateCommand();
                caps.CommandText = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);";
                const int expressEngineEdition = 4;
                if (Convert.ToInt32(await caps.ExecuteScalarAsync(cancellationToken)) == expressEngineEdition)
                {
                    throw new JetActionException(
                        JetErrorCodes.SqlServerExpressUnsupported,
                        "偵測到 SQL Server Express：單庫模型下所有專案共用一個資料庫，會撞 Express 的 10 GB 上限。" +
                        "Express 已淘汰，請改用 SQL Server 2022（Developer／Standard）。");
                }

                _engineVerified = true;
            }

            await using var create = master.CreateCommand();
            // dbName 來自設定、非使用者輸入;以括號內嵌(CREATE DATABASE 不接受參數化庫名)。
            // 庫層預設定序釘 Latin1_General_BIN2(design §2.1 雙保險之一):消滅「結果隨部署伺服器預設
            // 定序漂移」這條軸。只影響未來新建庫;既有庫不自動改(改庫 collation 是重建級操作,環境重置
            // 由使用者裁決執行)。欄位層另有顯式 COLLATE(見 SchemaSql),兩層缺一不可。
            create.CommandText =
                $"IF DB_ID(N'{dbName}') IS NULL EXEC('CREATE DATABASE [{dbName}] COLLATE Latin1_General_BIN2');";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var conn = CreateSingleDbConnection();
        await conn.OpenAsync(cancellationToken);
        await SqlServerControlPlaneSchema.EnsureAsync(conn, cancellationToken);

        // 走到這裡＝單庫可連且控制面表就位 → 標記此目標 process 級就緒,後續元件跳過 master。
        SqlServerSingleDatabaseReadiness.MarkConfirmed(readinessKey);
    }

    /// <summary>
    /// 單一資料庫(<see cref="SqlServerConnectionOptions.SingleDatabaseName"/>)目前是否已存在。
    /// master 依賴最小化(§4):<see cref="SqlServerConnectionOptions.AssumeDatabaseExists"/>=true 或
    /// process 級就緒已確認(<see cref="SqlServerSingleDatabaseReadiness"/>)時,一律當「已存在」、<b>完全不連 master</b>;
    /// 否則連 master 以 <c>DB_ID</c> 判定(<b>不</b>直接開 <c>InitialCatalog=單庫</c> 的連線——後者在「庫尚未建立」
    /// 時會以「無法開啟登入所要求的資料庫」失敗),找到即標記就緒。供 <see cref="DeleteAsync"/>/<see cref="DatabaseExistsAsync"/>
    /// 在切換伺服器或全新環境(單庫還沒建)時先行守門,避免誤把「庫不存在」當成錯誤。
    /// internal:供 AssumeDatabaseExists 測試斷言「就緒檢查跳過 master」(以無效 master 連線字串仍回 true)。
    /// </summary>
    internal async Task<bool> SingleDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var readinessKey = SqlServerSingleDatabaseReadiness.KeyFor(options);
        if (options.AssumeDatabaseExists || SqlServerSingleDatabaseReadiness.IsConfirmed(readinessKey))
        {
            return true;
        }

        await using var master = new SqlConnection(BuildConnectionString("master"));
        await master.OpenAsync(cancellationToken);
        await using var cmd = master.CreateCommand();
        cmd.CommandText = "SELECT DB_ID(@db);";
        cmd.Parameters.AddWithValue("@db", options.SingleDatabaseName);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        var exists = result is not null and not DBNull;
        if (exists)
        {
            SqlServerSingleDatabaseReadiness.MarkConfirmed(readinessKey);
        }

        return exists;
    }

    /// <summary>
    /// 該專案衍生的 schema 是否已存在(SCHEMA_ID)。供 project.create 在寫入本機檔案之前攔截
    /// 「不同案件名稱衍生後撞同 schema」與「本機登記遺失後的孤兒 schema」。單庫模型下語意為
    /// 「專案 schema 是否存在」(方法名沿用介面的 database 措辭)。databaseProvider 由介面統一傳入;
    /// 本實作即 sqlServer 路徑,不需分流。
    /// </summary>
    public async Task<bool> DatabaseExistsAsync(string projectId, string databaseProvider, CancellationToken cancellationToken)
    {
        var schema = SqlServerProjectSchema.For(projectId);

        // 單庫尚未建立 → schema 必然不存在。先守門,避免在全新伺服器(單庫還沒建)上開單庫連線
        // 而登入失敗(此方法在 project.create 的碰撞攔截被呼叫、早於 EnsureCreatedAsync 建庫)。
        if (!await SingleDatabaseExistsAsync(cancellationToken)) return false;

        await using var connection = CreateSingleDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN SCHEMA_ID(@s) IS NULL THEN 0 ELSE 1 END;";
        cmd.Parameters.AddWithValue("@s", schema);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}
