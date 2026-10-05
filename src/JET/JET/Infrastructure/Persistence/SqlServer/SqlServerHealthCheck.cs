using JET.Application;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 啟動時的 SQL Server 連線健康檢查：對外只一個 <see cref="ProbeAsync"/>，
/// 藏掉開連線、跑 <c>SELECT @@VERSION, DB_NAME(), SUSER_SNAME()</c> 與「失敗訊息去敏」的全部細節。
/// 成功 / 失敗一律以 <see cref="HealthResult.Ok"/> 顯式表達，不靠例外控制流——例外（連不上、逾時、
/// 認證失敗等）一律被收斂成 <c>Ok=false</c> 的去敏訊息，<b>絕不</b>把整段連線字串、<c>Password</c>
/// 或 <c>User ID</c> 的值放進訊息。呼叫端（啟動接點）因此可放心把 <see cref="HealthResult.Message"/>
/// 直接寫進日誌，不會洩漏密碼。
/// </summary>
public static class SqlServerHealthCheck
{
    public static async Task<HealthResult> ProbeAsync(string connectionString, CancellationToken cancellationToken)
    {
        // 先解析出可安全揭露的目標描述（Server / Database / 認證模式）；密碼與帳號值不取。
        // 解析本身也可能因連線字串格式不合法而拋例外，一併收斂成去敏訊息。
        string target;
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            var auth = b.IntegratedSecurity ? "Integrated" : "SqlLogin";
            target = $"Server={b.DataSource} Database={b.InitialCatalog} Auth={auth}";
        }
        catch (Exception ex)
        {
            // 連線字串無法解析：仍不得回放原文（可能含密碼）。只回類別名。
            return new HealthResult(false, $"SQL Server 連線字串無法解析（{ex.GetType().Name}）。");
        }

        try
        {
            // ConfigureAwait(false)：本方法不依賴任何同步情境，續行一律回執行緒池，避免被
            // sync-over-async 呼叫端（如 WinForms 主執行緒）捕捉情境而死鎖。
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT @@VERSION, DB_NAME(), SUSER_SNAME();";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var version = "";
            var db = "";
            var login = "";
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                version = reader.IsDBNull(0) ? "" : reader.GetString(0);
                db = reader.IsDBNull(1) ? "" : reader.GetString(1);
                login = reader.IsDBNull(2) ? "" : reader.GetString(2);
            }

            // @@VERSION 多行，取第一行即足以辨識版本，避免日誌噪音。
            var versionLine = FirstLine(version);
            return new HealthResult(
                true,
                $"SQL Server 連線正常：{target} 連到 DB={db} 身分={login}；版本：{versionLine}");
        }
        catch (SqlException ex)
        {
            // SqlException.Message 由 SQL Server 產生，描述伺服器/網路錯誤，不含我方連線字串密碼；
            // 但為求保險仍只取 Number 與類別名，不回放任何字串原文。
            return new HealthResult(
                false,
                $"SQL Server 連線失敗：{target}（SqlException Number={ex.Number}）。");
        }
        catch (OperationCanceledException)
        {
            return new HealthResult(false, $"SQL Server 健康檢查取消：{target}。");
        }
        catch (Exception ex)
        {
            return new HealthResult(
                false,
                $"SQL Server 連線失敗：{target}（{ex.GetType().Name}）。");
        }
    }

    /// <summary>
    /// 回報「目前設定的 SQL Server 後端身分」（供 system.databaseInfo / 前端訊息面板分辨 2022 vs Express）。
    /// 連 <b>master</b> 讀 <c>SERVERPROPERTY</c>（不連單庫，避免單庫尚未建立而誤判）。與 <see cref="ProbeAsync"/>
    /// 同一去敏紀律：結果一律以欄位顯式表達（<see cref="SqlServerBackendInfo.Reachable"/>），例外全收斂、
    /// <b>絕不</b>把整段連線字串、<c>Password</c> 或 <c>User ID</c> 的值放進任何欄位。無副作用（只讀 server 屬性）。
    /// </summary>
    /// <param name="baseConnectionString">base 連線字串（null/空白、或無 DataSource → 視為未設定 SQL Server）。</param>
    /// <param name="singleDatabaseName">schema-per-project 的單庫名（顯示用；不影響探測的 master 連線）。</param>
    public static async Task<SqlServerBackendInfo> DescribeAsync(
        string? baseConnectionString, string singleDatabaseName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return SqlServerBackendInfo.NotConfigured;
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(baseConnectionString);
        }
        catch
        {
            // 連線字串無法解析：不得回放原文（可能含密碼），視為未設定。
            return SqlServerBackendInfo.NotConfigured;
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            // 有連線字串但沒指定伺服器（SQLite-only 使用者的退化情況）→ 視為未設定 SQL Server。
            return SqlServerBackendInfo.NotConfigured;
        }

        var server = builder.DataSource;
        var database = singleDatabaseName ?? "";

        // 探測 master、設短逾時：身分查詢只是 UX 提示，伺服器慢/不可達不該久候。
        builder.InitialCatalog = "master";
        builder.ConnectTimeout = 5;

        try
        {
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var edition = "";
            var productVersion = "";
            var engineEdition = 0;
            var atVersion = "";
            // 身分讀取的 reader 收在自己的區塊：後續容量探測要在同一連線上再開 reader，
            // MARS 預設關閉，必須先關掉這個 reader（否則「連線已有開啟中的 reader」）。
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT CONVERT(nvarchar(256), SERVERPROPERTY('Edition')), " +
                    "CONVERT(nvarchar(64), SERVERPROPERTY('ProductVersion')), " +
                    "CONVERT(int, SERVERPROPERTY('EngineEdition')), @@VERSION;";
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    edition = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    productVersion = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    engineEdition = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    atVersion = reader.IsDBNull(3) ? "" : reader.GetString(3);
                }
            }

            var productName = ProductNameFrom(atVersion, productVersion);
            // EngineEdition 4＝Express（LocalDB 亦為 Express）；edition 字串含 "Express" 為保險。
            var isExpress = engineEdition == 4
                || edition.Contains("Express", StringComparison.OrdinalIgnoreCase);

            var (databaseSizeMb, schemaCount) =
                await ProbeCapacityAsync(conn, database, cancellationToken).ConfigureAwait(false);

            return new SqlServerBackendInfo(
                Configured: true, Reachable: true, Server: server, Database: database,
                Edition: edition, ProductName: productName, ProductVersion: productVersion,
                EngineEdition: engineEdition, IsExpress: isExpress, Detail: "",
                DatabaseSizeMb: databaseSizeMb, SchemaCount: schemaCount);
        }
        catch (SqlException ex)
        {
            return SqlServerBackendInfo.Unreachable(server, database, $"SqlException Number={ex.Number}");
        }
        catch (OperationCanceledException)
        {
            return SqlServerBackendInfo.Unreachable(server, database, "已取消");
        }
        catch (Exception ex)
        {
            return SqlServerBackendInfo.Unreachable(server, database, ex.GetType().Name);
        }
    }

    /// <summary>
    /// 容量探測（control-plane round 7）：在已開啟的 <b>master</b> 連線上讀單庫的整庫大小與 <c>prj_%</c> schema 數。
    /// <list type="bullet">
    /// <item><c>databaseSizeMb</c>：<c>sys.master_files</c> 該庫所有檔（含資料檔與交易記錄檔）的 8 KB 頁數總和換算 MB
    /// ——是整庫、含全部專案 schema，非單專案。</item>
    /// <item><c>schemaCount</c>：單庫內以 <c>prj_</c> 起頭的 schema 數（＝線上專案數；dbo 管理表不計）。
    /// schema 屬 DB-scoped catalog，故以三段式名 <c>[db].sys.schemas</c> 於 master 連線上查（庫名經 <c>QUOTENAME</c>
    /// 包裹，非使用者輸入）。<c>_</c> 是 LIKE 萬用字元，故 pattern 用 <c>prj[_]%</c> 精確錨定前綴。</item>
    /// </list>
    /// 純 UX 容量提示：任何失敗（權限不足、庫尚未建立、catalog 不可讀）一律吞掉退回 (0, 0)，不使 <c>reachable</c> 翻面。
    /// 無副作用（只讀 catalog）。
    /// </summary>
    private static async Task<(long DatabaseSizeMb, int SchemaCount)> ProbeCapacityAsync(
        SqlConnection masterConnection, string database, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            return (0, 0);
        }

        try
        {
            await using var cmd = masterConnection.CreateCommand();
            cmd.CommandText =
                "DECLARE @size bigint = " +
                "  (SELECT ISNULL(SUM(CAST(size AS bigint)) * 8 / 1024, 0) " +
                "   FROM sys.master_files WHERE database_id = DB_ID(@db)); " +
                "DECLARE @schemas int = 0; " +
                "IF DB_ID(@db) IS NOT NULL " +
                "BEGIN " +
                "  DECLARE @sql nvarchar(max) = " +
                "    N'SELECT @c = COUNT(*) FROM ' + QUOTENAME(@db) + N'.sys.schemas WHERE name LIKE ''prj[_]%'';'; " +
                "  EXEC sys.sp_executesql @sql, N'@c int OUTPUT', @c = @schemas OUTPUT; " +
                "END " +
                "SELECT @size, @schemas;";
            cmd.Parameters.AddWithValue("@db", database);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sizeMb = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
                var schemas = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                return (sizeMb, schemas);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消一律不吞（交由呼叫端 try 的 OperationCanceledException 分支處理原本身分探測的取消語意）。
            throw;
        }
        catch (Exception)
        {
            // 容量提示可有可無：任何（非取消）探測失敗——SqlException、catalog 不可讀或其他——一律吞掉、
            // 退回 0/0，絕不翻動已確立的 reachable（此為次要探測、不得反過來讓後端顯示為不可達）。
        }

        return (0, 0);
    }

    /// <summary>
    /// 由 <c>@@VERSION</c> 首行解析產品名（如 "Microsoft SQL Server 2022"）；解析不到時退回以主版號對照。
    /// </summary>
    private static string ProductNameFrom(string atVersion, string productVersion)
    {
        var line = FirstLine(atVersion).Trim();
        var idx = line.IndexOf(" (", StringComparison.Ordinal);
        if (idx > 0) return line[..idx].Trim();
        if (line.Length > 0) return line;

        var major = productVersion.Split('.').FirstOrDefault();
        return major switch
        {
            "17" => "Microsoft SQL Server 2025",
            "16" => "Microsoft SQL Server 2022",
            "15" => "Microsoft SQL Server 2019",
            "14" => "Microsoft SQL Server 2017",
            "13" => "Microsoft SQL Server 2016",
            _ => "Microsoft SQL Server",
        };
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var idx = text.IndexOfAny(['\r', '\n']);
        return idx < 0 ? text : text[..idx];
    }
}

/// <summary>
/// 健康檢查結果：<see cref="Ok"/> 顯式表達成功/失敗（不靠例外），<see cref="Message"/> 為可安全寫入
/// 日誌的去敏描述（永不含密碼或整段連線字串）。
/// </summary>
public readonly record struct HealthResult(bool Ok, string Message);
