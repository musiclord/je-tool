using System.Data.Common;
using DuckDB.NET.Data;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 每專案 jet.duckdb 的 DuckDB 連線工廠與 schema 初始化——本地引擎家族的第二實作
/// （<see cref="ILocalProjectDatabase"/>）。與 <see cref="SqliteProjectDatabase"/> 同屬
/// 「一資料夾一專案、檔案即 scope」模型，共用同一套 <c>Local*</c> repository 與 SQL 文本；引擎差異
/// 全部收進本類（連線工廠＋DDL）與 <see cref="DuckDbConnectionAdapter"/>（@name→$name 參數轉接）＋
/// <see cref="DuckDbDialect"/>。發出的連線一律經轉接器包裝，故共用 repo 的 <c>@p{i}</c> SQL 無需改動。
/// </summary>
public sealed class DuckDbProjectDatabase(JetProjectFolder folder) : ILocalProjectDatabase, IProjectDatabaseRetention
{
    /// <summary>DuckDB 資料庫檔名（與 sqlite 的 jet.db 並列於同一 projects/{id}/ 資料夾）。</summary>
    public const string DatabaseFileName = "jet.duckdb";

    /// <summary>
    /// 案件資料庫的現行結構，建成後標成第 15 版，表、欄位與索引名稱和 <see cref="SqliteProjectDatabase"/> 相同。
    /// 只有新資料庫會執行這段；已有資料表的資料庫只檢查版本，不是現行版本就請審計員刪除案件後重建
    /// （裁定 2026-10-30-23：案件資料庫只留現行結構，版本不符時請審計員刪除後重建）。
    /// 和 SQLite 的差異只有三類：(1) SQLite <c>INTEGER PRIMARY KEY AUTOINCREMENT</c> → DuckDB
    /// <c>BIGINT PRIMARY KEY DEFAULT nextval('seq_&lt;表&gt;')</c>＋前置 <c>CREATE SEQUENCE IF NOT EXISTS</c>
    /// （DuckDB 無 AUTOINCREMENT）；(2) 金額、計數、row_number、entry_id 等用 BIGINT（DuckDB INTEGER 為 32 位，
    /// scaled 金額會溢位），小整數（source_no、is_manual、position 等）用 INTEGER，TEXT 保留；
    /// (3) 初始資料用 ANSI <c>WHERE NOT EXISTS</c> 插入，不依賴 SQLite 專屬的 ON CONFLICT 寫法。
    /// </summary>
    private const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS schema_info (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS import_batch (
            batch_id         TEXT PRIMARY KEY,
            dataset_kind     TEXT NOT NULL CHECK (dataset_kind IN ('gl','tb','account_mapping')),
            source_file_path TEXT NOT NULL,
            source_file_name TEXT NOT NULL,
            imported_utc     TEXT NOT NULL,
            row_count        INTEGER NOT NULL DEFAULT 0,
            columns_json     TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_import_batch_kind
            ON import_batch (dataset_kind, imported_utc);

        CREATE TABLE IF NOT EXISTS import_batch_source (
            batch_id         TEXT NOT NULL,
            source_no        INTEGER NOT NULL,
            source_file_path TEXT NOT NULL,
            source_file_name TEXT NOT NULL,
            sheet_name       TEXT NULL,
            encoding         TEXT NULL,
            delimiter        TEXT NULL,
            header_row       INTEGER NULL,
            row_count        INTEGER NOT NULL DEFAULT 0,
            imported_utc     TEXT NOT NULL,
            PRIMARY KEY (batch_id, source_no)
        );

        -- 匯入檔的欄位定義（名稱、型態、長度），對應舊版 IDEA 的 TableDef，只在匯入資料時寫入。
        CREATE TABLE IF NOT EXISTS field_info (
            batch_id            TEXT NOT NULL,
            definition_scope    TEXT NOT NULL CHECK (definition_scope IN ('source','target')),
            ordinal             INTEGER NOT NULL,
            field_name          TEXT NOT NULL,
            description         TEXT NULL,
            field_kind          TEXT NOT NULL CHECK (field_kind IN ('text','number','date','time')),
            text_length         INTEGER NULL,
            decimal_places      INTEGER NULL,
            max_rendered_length INTEGER NOT NULL,
            has_observation     INTEGER NOT NULL CHECK (has_observation IN (0,1)),
            PRIMARY KEY (batch_id, definition_scope, ordinal)
        );

        CREATE TABLE IF NOT EXISTS pbc_gl (
            batch_id          TEXT NOT NULL,
            row_number        BIGINT NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        CREATE TABLE IF NOT EXISTS pbc_tb (
            batch_id          TEXT NOT NULL,
            row_number        BIGINT NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        CREATE TABLE IF NOT EXISTS field_mapping (
            dataset_kind    TEXT PRIMARY KEY CHECK (dataset_kind IN ('gl','tb')),
            mapping_json    TEXT NOT NULL,
            mode_name       TEXT NOT NULL,
            source_batch_id TEXT NOT NULL,
            committed_utc   TEXT NOT NULL,
            format_version  INTEGER NOT NULL DEFAULT 1,
            options_json    TEXT NULL
        );

        -- 重新匯入前最後一次確認的配對，只用來在重開案件時帶回草稿（LocalMappingStateStore.RetireCommittedMappingSql）。
        CREATE TABLE IF NOT EXISTS field_mapping_previous (
            dataset_kind    TEXT PRIMARY KEY CHECK (dataset_kind IN ('gl','tb')),
            mapping_json    TEXT NOT NULL,
            mode_name       TEXT NOT NULL,
            source_batch_id TEXT NOT NULL,
            committed_utc   TEXT NOT NULL,
            format_version  INTEGER NOT NULL,
            options_json    TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS calendar_day (
            day_type TEXT NOT NULL CHECK (day_type IN ('holiday','makeup')),
            date     TEXT NOT NULL,
            day_name TEXT NULL,
            PRIMARY KEY (day_type, date)
        );

        CREATE SEQUENCE IF NOT EXISTS seq_gl;
        CREATE TABLE IF NOT EXISTS gl (
            entry_id              BIGINT PRIMARY KEY DEFAULT nextval('seq_gl'),
            batch_id              TEXT NOT NULL,
            source_row_number     BIGINT NOT NULL,
            voucher_number       TEXT NULL,
            line_item             TEXT NULL,
            line_item_numeric_sort_key TEXT NULL,
            gl_date             TEXT NULL,
            approval_date         TEXT NULL,
            account_number          TEXT NULL,
            account_name          TEXT NULL,
            description           TEXT NULL,
            source_module         TEXT NULL,
            created_by            TEXT NULL,
            approved_by           TEXT NULL,
            is_manual             INTEGER NULL,
            amount_scaled         BIGINT NOT NULL,
            debit_amount_scaled   BIGINT NOT NULL,
            credit_amount_scaled  BIGINT NOT NULL,
            dr_cr                 TEXT NOT NULL CHECK (dr_cr IN ('DEBIT','CREDIT')),
            posting_status        TEXT NULL,
            is_excluded           INTEGER NULL,
            exclusion_reason      TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_gl_voucher
            ON gl (voucher_number);
        CREATE INDEX IF NOT EXISTS ix_gl_account
            ON gl (account_number);
        CREATE INDEX IF NOT EXISTS ix_gl_gl_date
            ON gl (gl_date);
        CREATE INDEX IF NOT EXISTS ix_gl_approval_date
            ON gl (approval_date);

        CREATE SEQUENCE IF NOT EXISTS seq_tb;
        CREATE TABLE IF NOT EXISTS tb (
            balance_id            BIGINT PRIMARY KEY DEFAULT nextval('seq_tb'),
            batch_id              TEXT NOT NULL,
            source_row_number     BIGINT NOT NULL,
            account_number          TEXT NULL,
            account_name          TEXT NULL,
            change_amount_scaled  BIGINT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_tb_account
            ON tb (account_number);

        CREATE TABLE IF NOT EXISTS validation_result (
            result_id     TEXT PRIMARY KEY,
            generated_utc TEXT NOT NULL,
            summary_json  TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS prescreen_result (
            result_id     TEXT PRIMARY KEY,
            generated_utc TEXT NOT NULL,
            summary_json  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS inf_sample (
            result_id       TEXT NOT NULL,
            entry_id        BIGINT NOT NULL,
            voucher_number TEXT NULL,
            line_item       TEXT NULL,
            PRIMARY KEY (result_id, entry_id)
        );

        CREATE TABLE IF NOT EXISTS filter_scenario (
            scenario_id     INTEGER PRIMARY KEY,
            position        INTEGER NOT NULL,
            name            TEXT NOT NULL,
            rationale       TEXT NOT NULL,
            definition_json TEXT NOT NULL,
            saved_utc       TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS pbc_account_mapping (
            batch_id          TEXT NOT NULL,
            row_number        BIGINT NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        CREATE SEQUENCE IF NOT EXISTS seq_account_mapping;
        CREATE TABLE IF NOT EXISTS account_mapping (
            mapping_id              BIGINT PRIMARY KEY DEFAULT nextval('seq_account_mapping'),
            batch_id                TEXT NOT NULL,
            source_row_number       INTEGER NOT NULL,
            -- account_number 的唯一性寫在欄位上，不另建唯一索引：DuckDB 對另建的唯一索引，在同一交易內刪除後
            -- 重新寫入同一個鍵會誤報重複鍵；科目配對的取代匯入正是這樣做。效果和 SQLite 的 ix_account_mapping_number 相同。
            account_number            TEXT NOT NULL UNIQUE,
            account_name            TEXT NULL,
            standardized_category   TEXT NOT NULL
                CHECK (standardized_category IN ('Revenue','Receivables','Cash','Receipt in advance','Others')),
            category_id             TEXT NULL,
            classification_explicit INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS pbc_authorized_preparer (
            batch_id          TEXT NOT NULL,
            row_number        BIGINT NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );
        CREATE TABLE IF NOT EXISTS authorized_preparer (
            name TEXT NOT NULL PRIMARY KEY
        );

        CREATE SEQUENCE IF NOT EXISTS seq_message_log;
        CREATE TABLE IF NOT EXISTS message_log (
            message_id   BIGINT PRIMARY KEY DEFAULT nextval('seq_message_log'),
            occurred_utc TEXT NOT NULL,
            level        TEXT NOT NULL CHECK (level IN ('info','warn','error')),
            text         TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS gl_control_total (
            singleton                        INTEGER PRIMARY KEY CHECK (singleton = 1),
            pbc_row_count                    BIGINT NOT NULL,
            stored_row_count                 BIGINT NOT NULL,
            stored_debit_scaled              BIGINT NOT NULL,
            stored_credit_scaled             BIGINT NOT NULL,
            gl_row_count                     BIGINT NULL,
            excluded_by_period_count         BIGINT NULL,
            excluded_by_posting_status_count BIGINT NULL,
            gl_debit_scaled                  BIGINT NULL,
            gl_credit_scaled                 BIGINT NULL
        );

        CREATE TABLE IF NOT EXISTS filter_hit (
            scenario_id INTEGER NOT NULL,
            entry_id    BIGINT NOT NULL,
            PRIMARY KEY (scenario_id, entry_id)
        );
        CREATE INDEX IF NOT EXISTS idx_filter_hit_entry
            ON filter_hit (entry_id, scenario_id);

        CREATE TABLE IF NOT EXISTS filter_hit_condition (
            scenario_id  INTEGER NOT NULL,
            entry_id     BIGINT NOT NULL,
            group_no     INTEGER NOT NULL,
            condition_no INTEGER NOT NULL,
            PRIMARY KEY (scenario_id, entry_id, group_no, condition_no)
        );

        CREATE TABLE IF NOT EXISTS gl_rde_field (
            field_id      TEXT PRIMARY KEY,
            source_column TEXT NOT NULL,
            label         TEXT NOT NULL,
            value_type    TEXT NOT NULL CHECK (value_type IN ('text','date','amount')),
            ordinal       INTEGER NOT NULL,
            is_rde        INTEGER NOT NULL CHECK (is_rde IN (0,1))
        );

        CREATE TABLE IF NOT EXISTS gl_rde_value (
            entry_id      BIGINT NOT NULL,
            field_id      TEXT NOT NULL,
            value_type    TEXT NOT NULL CHECK (value_type IN ('text','date','amount')),
            text_value    TEXT NULL,
            date_value    TEXT NULL,
            amount_scaled BIGINT NULL,
            PRIMARY KEY (entry_id, field_id),
            CHECK (
                (CASE WHEN text_value IS NULL THEN 0 ELSE 1 END
                 + CASE WHEN date_value IS NULL THEN 0 ELSE 1 END
                 + CASE WHEN amount_scaled IS NULL THEN 0 ELSE 1 END) = 1
                AND ((value_type = 'text' AND text_value IS NOT NULL)
                  OR (value_type = 'date' AND date_value IS NOT NULL)
                  OR (value_type = 'amount' AND amount_scaled IS NOT NULL))
            )
        );
        CREATE INDEX IF NOT EXISTS ix_gl_rde_value_text
            ON gl_rde_value (field_id, value_type, text_value, entry_id);
        CREATE INDEX IF NOT EXISTS ix_gl_rde_value_date
            ON gl_rde_value (field_id, value_type, date_value, entry_id);
        CREATE INDEX IF NOT EXISTS ix_gl_rde_value_amount
            ON gl_rde_value (field_id, value_type, amount_scaled, entry_id);

        CREATE TABLE IF NOT EXISTS account_category (
            category_id        TEXT PRIMARY KEY,
            label              TEXT NOT NULL,
            ordinal            INTEGER NOT NULL UNIQUE,
            semantic_role      TEXT NOT NULL,
            is_builtin         INTEGER NOT NULL CHECK (is_builtin IN (0,1)),
            revision           INTEGER NOT NULL,
            parent_category_id TEXT NULL
        );
        INSERT INTO account_category (category_id, label, ordinal, semantic_role, is_builtin, revision)
            SELECT 'builtin.revenue', 'Revenue', 0, 'revenue', 1, 1
            WHERE NOT EXISTS (SELECT 1 FROM account_category WHERE category_id = 'builtin.revenue');
        INSERT INTO account_category (category_id, label, ordinal, semantic_role, is_builtin, revision)
            SELECT 'builtin.receivables', 'Receivables', 1, 'receivables', 1, 1
            WHERE NOT EXISTS (SELECT 1 FROM account_category WHERE category_id = 'builtin.receivables');
        INSERT INTO account_category (category_id, label, ordinal, semantic_role, is_builtin, revision)
            SELECT 'builtin.cash', 'Cash', 2, 'cash', 1, 1
            WHERE NOT EXISTS (SELECT 1 FROM account_category WHERE category_id = 'builtin.cash');
        INSERT INTO account_category (category_id, label, ordinal, semantic_role, is_builtin, revision)
            SELECT 'builtin.receipt_in_advance', 'Receipt in advance', 3, 'receipt_in_advance', 1, 1
            WHERE NOT EXISTS (SELECT 1 FROM account_category WHERE category_id = 'builtin.receipt_in_advance');
        INSERT INTO account_category (category_id, label, ordinal, semantic_role, is_builtin, revision)
            SELECT 'builtin.others', 'Others', 4, 'others', 1, 1
            WHERE NOT EXISTS (SELECT 1 FROM account_category WHERE category_id = 'builtin.others');

        -- 科目分類樹的祖先與子孫對照，每個分類至少有自己對自己一列；之後由 AccountCategoryHierarchy.SavePathsAsync 整份改寫。
        CREATE TABLE IF NOT EXISTS account_category_path (
            ancestor_id   TEXT NOT NULL,
            descendant_id TEXT NOT NULL,
            PRIMARY KEY (ancestor_id, descendant_id)
        );
        INSERT INTO account_category_path (ancestor_id, descendant_id)
            SELECT t.category_id, t.category_id FROM account_category t
            WHERE NOT EXISTS (SELECT 1 FROM account_category_path p
                WHERE p.ancestor_id = t.category_id AND p.descendant_id = t.category_id);

        CREATE TABLE IF NOT EXISTS result_stale_state (
            singleton        INTEGER PRIMARY KEY CHECK (singleton = 1),
            validation_stale INTEGER NOT NULL CHECK (validation_stale IN (0,1)),
            prescreen_stale  INTEGER NOT NULL CHECK (prescreen_stale IN (0,1)),
            filter_stale     INTEGER NOT NULL CHECK (filter_stale IN (0,1))
        );
        INSERT INTO result_stale_state
            SELECT 1, 0, 0, 0
            WHERE NOT EXISTS (SELECT 1 FROM result_stale_state WHERE singleton = 1);

        -- 案件的操作留痕只能新增。DuckDB 沒有觸發器，也沒有表格層級的權限，所以這一點由程式維持：
        -- LocalProjectOperationLog 只寫 INSERT，見 docs/jet-guide.md 第 8 節「儲存與資料庫實作」。
        CREATE TABLE IF NOT EXISTS operation_log (
            event_id       TEXT PRIMARY KEY,
            occurred_utc   TEXT NOT NULL,
            operation      TEXT NOT NULL,
            target_type    TEXT NOT NULL,
            target_id      TEXT NOT NULL,
            subject_count  BIGINT NOT NULL CHECK (subject_count >= 0),
            replaced_count BIGINT NOT NULL CHECK (replaced_count >= 0)
        );
        CREATE INDEX IF NOT EXISTS ix_operation_log_occurred
            ON operation_log (occurred_utc, event_id);

        INSERT INTO schema_info (key, value)
            SELECT 'filter_data_revision', '0'
            WHERE NOT EXISTS (SELECT 1 FROM schema_info WHERE key = 'filter_data_revision');
        INSERT INTO schema_info (key, value)
            SELECT 'schema_version', '18'
            WHERE NOT EXISTS (SELECT 1 FROM schema_info WHERE key = 'schema_version');
        """;

    /// <summary>該專案 jet.duckdb 檔的實體路徑（與 sqlite 同資料夾、不同檔名）。</summary>
    public string GetDatabasePath(string projectId)
        => Path.Combine(folder.GetProjectDirectory(projectId), DatabaseFileName);

    /// <inheritdoc />
    public IProviderSqlDialect Dialect => DuckDbDialect.Instance;

    /// <summary>DuckDB：jet.duckdb 檔是否存在（同名資料夾唯一性由 IProjectStore 把關，此為防禦縱深）。
    /// databaseProvider 由介面統一傳入；本實作即 duckdb 路徑，檔案位置由 projectId 唯一決定，不需分流。</summary>
    public Task<bool> DatabaseExistsAsync(string projectId, string databaseProvider, CancellationToken cancellationToken)
        => Task.FromResult(File.Exists(GetDatabasePath(projectId)));

    public DbConnection CreateConnection(string projectId)
    {
        var databasePath = GetDatabasePath(projectId);

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 轉接器包裝：發出的每個命令在執行前把 @name→$name、參數名剝為裸名（DuckDB 綁定要求）。
        return new DuckDbConnectionAdapter(new DuckDBConnection(ConnectionString(databasePath)));
    }

    /// <summary>
    /// 一般讀寫連線的連線字串。DuckDB.NET 在同一程序內把 DataSource 相同的連線接到同一個資料庫實體，
    /// 實體只在第一次開啟時套用設定，所以持有連線與 repository 連線必須使用完全相同的字串。
    /// </summary>
    private static string ConnectionString(string databasePath) => $"DataSource={databasePath};{EngineSettings}";

    /// <summary>
    /// 每條 DuckDB 連線都套用的引擎設定。DuckDB 預設遞增與遞減都把空值排最後；改成遞增在前、遞減在後，
    /// 和 SQLite 與 SQL Server 的預設相同，沒有明確指定空值位置的排序在三種資料庫才會一樣（2026-10-19 實測）。
    /// </summary>
    internal const string EngineSettings = "default_null_order=nulls_first_on_asc_last_on_desc";

    private readonly Lock retentionGate = new();

    /// <summary>每個資料庫路徑目前有幾個持有者；路徑大小寫不敏感，與 Windows 檔案系統一致。</summary>
    private readonly Dictionary<string, int> retainers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 在一次操作期間保持 jet.duckdb 開啟：開一條連線並持有到操作結束，期間 repository 開的連線都沿用
    /// 同一個資料庫實體，不必每次重新開啟。資料庫檔不存在時不持有，避免替缺檔的案件建出新檔；
    /// SQLite 案件的資料夾沒有 jet.duckdb，因此自然不持有。開啟失敗回 null，讓 repository 照原本的方式報錯。
    /// </summary>
    public IDisposable? TryRetain(string projectId)
    {
        string databasePath;
        try
        {
            databasePath = GetDatabasePath(projectId);
        }
        catch (Exception)
        {
            return null;
        }

        // 先登記再開啟：登記與檔案檢查和 DeleteAsync 用同一把鎖，刪除不會插進「已確認檔案存在、
        // 尚未開啟」之間。
        lock (retentionGate)
        {
            if (!File.Exists(databasePath))
            {
                return null;
            }

            retainers[databasePath] = retainers.GetValueOrDefault(databasePath) + 1;
        }

        DuckDBConnection? connection = null;
        try
        {
            connection = new DuckDBConnection(ConnectionString(databasePath));
            connection.Open();
            return new DatabaseRetention(this, databasePath, connection);
        }
        catch (Exception)
        {
            try
            {
                connection?.Dispose();
            }
            catch (Exception)
            {
                // 開啟失敗後的清理失敗不影響結果：照樣不持有。
            }

            ReleaseRetainer(databasePath);
            return null;
        }
    }

    private void ReleaseRetainer(string databasePath)
    {
        lock (retentionGate)
        {
            if (!retainers.TryGetValue(databasePath, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                retainers.Remove(databasePath);
            }
            else
            {
                retainers[databasePath] = count - 1;
            }
        }
    }

    private sealed class DatabaseRetention(
        DuckDbProjectDatabase owner,
        string databasePath,
        DuckDBConnection connection) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            try
            {
                // 先關連線再撤登記：刪除檢查看到沒有持有者時，這條連線一定已經關閉。
                connection.Dispose();
            }
            catch (Exception)
            {
                // 釋放失敗不得遮蔽操作本身的結果。
            }
            finally
            {
                owner.ReleaseRetainer(databasePath);
            }
        }
    }

    /// <summary>
    /// dev 檢視專用的獨立唯讀連線：<c>ACCESS_MODE=READ_ONLY</c>（實測可與主連線並存，讀到 MVCC 快照）。
    /// DB 檔不存在時開啟即失敗（不建檔）。同樣經轉接器包裝以維持 dev 檢視查詢的參數語意一致。
    /// </summary>
    public DbConnection CreateReadOnlyConnection(string projectId)
        => new DuckDbConnectionAdapter(
            new DuckDBConnection($"DataSource={GetDatabasePath(projectId)};ACCESS_MODE=READ_ONLY;{EngineSettings}"));

    /// <summary>
    /// 匯入連線層調校：DuckDB 自管 MVCC 與寫入路徑，SQLite 的 WAL/synchronous/temp_store/cache_size
    /// pragmas 不適用——no-op。效能修法採原生 Appender（見 <see cref="DuckDbBulkRowWriter"/>）：
    /// 逐列 INSERT 的計畫成本被繞過，此為主修法。<c>SET preserve_insertion_order=false</c> 經評估未加——
    /// 它 session-scoped 只作用於匯入連線本身，而落地投影／查詢在各自的新連線（預設 preserve_insertion_order=true），
    /// 故對本連線以外的讀取序無效；要全域套用需先審計每條讀取皆具顯式 ORDER BY，超出這次修改的保守範圍。
    /// </summary>
    public Task ApplyImportSessionSettingsAsync(DbConnection connection, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>DuckDB 批量列寫入＝原生 Appender（見 <see cref="DuckDbBulkRowWriter"/>）。</summary>
    public IBulkRowWriter CreateBulkRowWriter(
        DbConnection connection, DbTransaction transaction, string table, IReadOnlyList<string> columns)
        => new DuckDbBulkRowWriter(connection, transaction, table, columns);

    private readonly LocalSchemaReadiness readiness = new();

    /// <summary>建立或載入案件時呼叫：一律重新檢查版本，新資料庫在這裡建表。</summary>
    public Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken) =>
        readiness.EnsureCreatedAsync(
            GetDatabasePath(projectId),
            ct => EnsureCreatedCoreAsync(projectId, ct),
            cancellationToken);

    /// <summary>repository 每次讀寫前呼叫：同一程序內每個資料庫檔只完整檢查一次。</summary>
    public Task EnsureReadyAsync(string projectId, CancellationToken cancellationToken) =>
        readiness.EnsureReadyAsync(
            GetDatabasePath(projectId),
            ct => EnsureCreatedCoreAsync(projectId, ct),
            cancellationToken);

    private async Task EnsureCreatedCoreAsync(string projectId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // 已經建過表的資料庫只檢查版本：不是現行版本就在任何寫入之前擋下，檔案保持原狀。
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText =
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'schema_info';";
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                await using var version = connection.CreateCommand();
                version.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version';";
                LocalSchemaReadiness.RejectUnsupportedSchemaVersion(
                    await version.ExecuteScalarAsync(cancellationToken) as string);
                return;
            }
        }

        // 新資料庫在同一個交易裡建好全部的表，中途失敗時不留下只建一半的資料庫。
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }


    /// <summary>
    /// 刪除該專案的 jet.duckdb（含 DuckDB 交易期的 .wal 邊檔若殘留）。資料夾本身由
    /// IProjectStore.DeleteAsync 刪除。DuckDB.NET 無 SQLite 的 ClearAllPools——同一路徑的最後一條連線
    /// 關閉時資料庫實體才關閉並解鎖。
    /// 實測資料庫實體開著時刪檔也會成功，之後同一路徑的新連線仍沿用舊實體、讀到舊資料；使用者刪掉案件後
    /// 再建同名案件就會讀到舊案件的資料。因此該路徑仍有持有者（<see cref="TryRetain"/>）時，
    /// 回 operation_in_progress 而且不刪任何檔案。
    /// </summary>
    public Task DeleteAsync(string projectId, CancellationToken cancellationToken)
    {
        var databasePath = GetDatabasePath(projectId);
        lock (retentionGate)
        {
            if (retainers.ContainsKey(databasePath))
            {
                throw new JetActionException(
                    JetErrorCodes.OperationInProgress,
                    "案件資料庫還有作業在使用，請等作業完成後再刪除案件。");
            }

            readiness.Forget(databasePath);
            foreach (var suffix in new[] { "", ".wal" })
            {
                TryDeleteFile(databasePath + suffix);
            }
        }

        return Task.CompletedTask;
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 資料庫實體開著時刪檔仍會成功（見 DeleteAsync），所以這裡擋不住「實體還開著」的情況，
            // 那由 DeleteAsync 的持有者檢查負責。走到這裡是其他程式占用檔案；
            // 催動 finalizer 釋放可能殘留的 handle 後重試一次，仍失敗就把例外交給呼叫端。
            GC.Collect();
            GC.WaitForPendingFinalizers();
            File.Delete(path);
        }
    }
}
