using System.Data.Common;
using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

/// <summary>
/// 每專案 jet.db 的 SQLite 連線工廠與 schema 初始化——本地引擎家族的 SQLite 實作
/// （<see cref="ILocalProjectDatabase"/>）。
/// 每專案一個 DB 檔，資料表不帶 project_id 欄（檔案即 scope）。
/// </summary>
public sealed class SqliteProjectDatabase(JetProjectFolder folder) : ILocalProjectDatabase
{
    /// <summary>
    /// 案件資料庫的現行結構，建成後標成第 20 版。只有新資料庫會執行這段；已有資料表的資料庫只檢查版本，
    /// 不是現行版本就請審計員刪除案件後重建（案件資料庫只留現行結構）。
    /// staging 的 row_number = 批次內單調遞增排序鍵（INF 抽樣基礎），
    /// source_row_number = 來源檔內實際列號（錯誤定位），source_no 對應 import_batch_source。
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
            -- 每一欄的來歷與對應的 JET 欄位；底稿與報告看這兩欄，不看欄名結尾。
            field_origin        TEXT NOT NULL CHECK (field_origin IN ('source','renamed','generated','temp')),
            jet_field           TEXT NULL,
            PRIMARY KEY (batch_id, definition_scope, ordinal)
        );

        CREATE TABLE IF NOT EXISTS pbc_gl (
            batch_id          TEXT NOT NULL,
            row_number        INTEGER NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        CREATE TABLE IF NOT EXISTS pbc_tb (
            batch_id          TEXT NOT NULL,
            row_number        INTEGER NOT NULL,
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
            options_json    TEXT
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

        CREATE TABLE IF NOT EXISTS gl (
            entry_id              INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id              TEXT NOT NULL,
            source_row_number     INTEGER NOT NULL,
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
            amount_scaled         INTEGER NOT NULL,
            debit_amount_scaled   INTEGER NOT NULL,
            credit_amount_scaled  INTEGER NOT NULL,
            dr_cr                 TEXT NOT NULL CHECK (dr_cr IN ('DEBIT','CREDIT')),
            posting_status        TEXT,
            is_excluded           INTEGER,
            exclusion_reason      TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_gl_voucher
            ON gl (voucher_number);
        CREATE INDEX IF NOT EXISTS ix_gl_account
            ON gl (account_number);
        CREATE INDEX IF NOT EXISTS ix_gl_gl_date
            ON gl (gl_date);
        CREATE INDEX IF NOT EXISTS ix_gl_approval_date
            ON gl (approval_date);

        CREATE TABLE IF NOT EXISTS tb (
            balance_id            INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id              TEXT NOT NULL,
            source_row_number     INTEGER NOT NULL,
            account_number          TEXT NULL,
            account_name          TEXT NULL,
            change_amount_scaled  INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_tb_account
            ON tb (account_number);

        -- 驗證結果與預篩選結果各一張表，每次執行新增一列，讀取時取 generated_utc 最新的一列。
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
            entry_id        INTEGER NOT NULL,
            voucher_number TEXT NULL,
            line_item       TEXT NULL,
            PRIMARY KEY (result_id, entry_id)
        );

        -- scenario_id 是情境不變的編號，結果依它存放；position 是畫面上的順序，移除情境後由後面的往前補。
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
            row_number        INTEGER NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        CREATE TABLE IF NOT EXISTS account_mapping (
            mapping_id              INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id                TEXT NOT NULL,
            source_row_number       INTEGER NOT NULL,
            account_number            TEXT NOT NULL,
            account_name            TEXT NULL,
            standardized_category   TEXT NOT NULL
                CHECK (standardized_category IN ('Revenue','Receivables','Cash','Receipt in advance','Others')),
            category_id             TEXT,
            classification_explicit INTEGER NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_account_mapping_number
            ON account_mapping (account_number);

        -- 授權編製人員清單：只有姓名一欄（name 為主鍵），另有原始列暫存表；不登記在 import_batch。
        CREATE TABLE IF NOT EXISTS pbc_authorized_preparer (
            batch_id          TEXT NOT NULL,
            row_number        INTEGER NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );
        CREATE TABLE IF NOT EXISTS authorized_preparer (
            name TEXT NOT NULL PRIMARY KEY
        );

        -- 前端「狀態與訊息」的保存處，由 log.append 寫入、log.recent 讀取；只是操作輔助紀錄，不是審計留痕。
        CREATE TABLE IF NOT EXISTS message_log (
            message_id   INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_utc TEXT NOT NULL,
            level        TEXT NOT NULL CHECK (level IN ('info','warn','error')),
            text         TEXT NOT NULL
        );

        -- 存入筆數與合計核對的控制總數，只有一列，每次把匯入資料寫進正式表時整列覆寫。
        -- 記錄當時的來源列數、母體列數與借貸總額，供 validate.run 和正式表目前的數字比對。
        CREATE TABLE IF NOT EXISTS gl_control_total (
            singleton                        INTEGER PRIMARY KEY CHECK (singleton = 1),
            pbc_row_count                    INTEGER NOT NULL,
            stored_row_count                 INTEGER NOT NULL,
            stored_debit_scaled              INTEGER NOT NULL,
            stored_credit_scaled             INTEGER NOT NULL,
            gl_row_count                     INTEGER,
            excluded_by_period_count         INTEGER,
            excluded_by_posting_status_count INTEGER,
            gl_debit_scaled                  INTEGER,
            gl_credit_scaled                 INTEGER
        );

        -- 進階篩選的命中結果：儲存情境時把這個情境命中的 entry_id 依情境編號存在這裡，
        -- 檢視、高風險條件彙總與匯出都讀這裡。這是可重算的衍生資料，由 ResultReset 隨其他結果一起清除。
        CREATE TABLE IF NOT EXISTS filter_hit (
            scenario_id INTEGER NOT NULL,
            entry_id    INTEGER NOT NULL,
            PRIMARY KEY (scenario_id, entry_id)
        );
        -- 高風險條件彙總查詢（query.highRiskSummaryVoucherPage、query.highRiskSummaryRowPage）的輔助索引：以 entry_id 開頭，讓「這筆分錄有沒有命中」
        -- 與「一段 entry_id 範圍命中了哪些情境」兩種查詢都能走索引；主鍵以 scenario_id 開頭，不適合這兩種查詢。
        CREATE INDEX IF NOT EXISTS idx_filter_hit_entry ON filter_hit (entry_id, scenario_id);

        -- 命中傳票裡每筆分錄符合第幾組第幾條，和 filter_hit 同時寫入、同時清除；檢視傳票分錄的說明讀這裡，不再重新判斷條件。
        -- 只存命中傳票裡的分錄，沒命中的分錄也可能符合其中幾條，畫面照樣寫出。
        CREATE TABLE IF NOT EXISTS filter_hit_condition (
            scenario_id  INTEGER NOT NULL,
            entry_id     INTEGER NOT NULL,
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
            entry_id      INTEGER NOT NULL,
            field_id      TEXT NOT NULL,
            value_type    TEXT NOT NULL CHECK (value_type IN ('text','date','amount')),
            text_value    TEXT NULL,
            date_value    TEXT NULL,
            amount_scaled INTEGER NULL,
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
        INSERT INTO account_category
            (category_id, label, ordinal, semantic_role, is_builtin, revision)
        VALUES
            ('builtin.revenue', 'Revenue', 0, 'revenue', 1, 1),
            ('builtin.receivables', 'Receivables', 1, 'receivables', 1, 1),
            ('builtin.cash', 'Cash', 2, 'cash', 1, 1),
            ('builtin.receipt_in_advance', 'Receipt in advance', 3, 'receipt_in_advance', 1, 1),
            ('builtin.others', 'Others', 4, 'others', 1, 1)
        ON CONFLICT(category_id) DO NOTHING;

        -- 科目分類樹的祖先與子孫對照，每個分類至少有自己對自己一列；之後由 AccountCategoryHierarchy.SavePathsAsync 整份改寫。
        CREATE TABLE IF NOT EXISTS account_category_path (
            ancestor_id   TEXT NOT NULL,
            descendant_id TEXT NOT NULL,
            PRIMARY KEY (ancestor_id, descendant_id)
        );
        INSERT INTO account_category_path (ancestor_id, descendant_id)
            SELECT category_id, category_id FROM account_category WHERE true
        ON CONFLICT(ancestor_id, descendant_id) DO NOTHING;

        CREATE TABLE IF NOT EXISTS result_stale_state (
            singleton        INTEGER PRIMARY KEY CHECK (singleton = 1),
            validation_stale INTEGER NOT NULL CHECK (validation_stale IN (0,1)),
            prescreen_stale  INTEGER NOT NULL CHECK (prescreen_stale IN (0,1)),
            filter_stale     INTEGER NOT NULL CHECK (filter_stale IN (0,1))
        );
        INSERT INTO result_stale_state
            (singleton, validation_stale, prescreen_stale, filter_stale)
        VALUES (1, 0, 0, 0)
        ON CONFLICT(singleton) DO NOTHING;

        -- 案件的操作留痕只能新增：兩個觸發器擋下修改與刪除。
        CREATE TABLE IF NOT EXISTS operation_log (
            event_id       TEXT PRIMARY KEY,
            occurred_utc   TEXT NOT NULL,
            operation      TEXT NOT NULL,
            target_type    TEXT NOT NULL,
            target_id      TEXT NOT NULL,
            subject_count  INTEGER NOT NULL CHECK (subject_count >= 0),
            replaced_count INTEGER NOT NULL CHECK (replaced_count >= 0)
        );
        CREATE INDEX IF NOT EXISTS ix_operation_log_occurred
            ON operation_log (occurred_utc, event_id);
        CREATE TRIGGER IF NOT EXISTS trg_operation_log_no_update
        BEFORE UPDATE ON operation_log
        BEGIN
            SELECT RAISE(ABORT, 'project audit log is append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS trg_operation_log_no_delete
        BEFORE DELETE ON operation_log
        BEGIN
            SELECT RAISE(ABORT, 'project audit log is append-only');
        END;

        INSERT INTO schema_info (key, value) VALUES ('filter_data_revision', '0')
        ON CONFLICT(key) DO NOTHING;
        INSERT INTO schema_info (key, value) VALUES ('schema_version', '20')
        ON CONFLICT(key) DO NOTHING;
        """;

    public string GetDatabasePath(string projectId) => folder.GetDatabasePath(projectId);

    /// <inheritdoc />
    public IProviderSqlDialect Dialect => SqliteDialect.Instance;

    public DbConnection CreateConnection(string projectId)
    {
        var databasePath = folder.GetDatabasePath(projectId);

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return CreateConfiguredConnection(PooledConnectionString(databasePath));
    }

    /// <summary>
    /// 案件讀寫連線的連線字串。連線池依連線字串分組，清掉這個案件的連線池時用同一個字串。
    /// </summary>
    private static string PooledConnectionString(string databasePath) =>
        // 私有快取：案件資料庫用 WAL，讀取讀上一次提交的資料，不等別條連線還沒提交的寫入。
        // 共用快取會改用表格鎖，長作業寫入期間其他要求讀同一張表要等到逾時。
        // 寫入仍然一次一條，由 Microsoft.Data.Sqlite 在逾時內重試。
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();

    /// <summary>
    /// dev 檢視專用的獨立唯讀連線：Mode=ReadOnly、私有快取、不進連線池——
    /// 每次查詢都真實開磁碟檔，保證看到的是已持久化資料且零副作用。
    /// DB 檔不存在時開啟即失敗（不會建檔）。
    /// </summary>
    public DbConnection CreateReadOnlyConnection(string projectId)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = folder.GetDatabasePath(projectId),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };

        return CreateConfiguredConnection(builder.ToString());
    }

    private static SqliteConnection CreateConfiguredConnection(string connectionString)
    {
        // 取消會中斷執行中的語句，見 CancellableSqliteConnection。
        var connection = new CancellableSqliteConnection(connectionString);
        // SQLite's built-in UPPER is ASCII-only. Match the existing invariant-uppercase
        // operands for filters and prescreen rules, including pooled and read-only connections.
        connection.CreateFunction<string?, string?>("upper", static value => value?.ToUpperInvariant(), isDeterministic: true);
        return connection;
    }

    /// <summary>
    /// 匯入連線層調校（只作用於匯入連線）：WAL 下 synchronous=NORMAL
    /// 仍保證一致性；temp_store/cache_size 降低大批寫入的 I/O。由本地引擎家族的匯入 repository 於
    /// 連線開啟後、交易開始前呼叫（provider 分支留在 Infrastructure）。
    /// </summary>
    public async Task ApplyImportSessionSettingsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>SQLite 批量列寫入＝現行參數化 INSERT 的封裝（見 <see cref="SqliteBulkRowWriter"/>）。</summary>
    public IBulkRowWriter CreateBulkRowWriter(
        DbConnection connection, DbTransaction transaction, string table, IReadOnlyList<string> columns)
        => new SqliteBulkRowWriter(connection, transaction, table, columns);

    private readonly LocalSchemaReadiness readiness = new();

    /// <summary>建案時呼叫：一律重新檢查版本，新資料庫在這裡建表。只有建案會建立新的資料庫。</summary>
    public Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken) =>
        readiness.EnsureCreatedAsync(
            GetDatabasePath(projectId),
            ct => EnsureCreatedCoreAsync(projectId, allowCreate: true, ct),
            cancellationToken);

    /// <summary>開案時呼叫：一律重新檢查版本，不建立新的資料庫；建案中斷留下的案件在這裡把初始化做完。</summary>
    public Task EnsureExistingAsync(string projectId, CancellationToken cancellationToken) =>
        readiness.EnsureExistingAsync(
            GetDatabasePath(projectId),
            folder.GetCreationMarkerPath(projectId),
            (allowCreate, ct) => EnsureCreatedCoreAsync(projectId, allowCreate, ct),
            cancellationToken);

    /// <summary>repository 每次讀寫前呼叫：同一程序內每個資料庫檔只完整檢查一次，不建立新的資料庫。</summary>
    public Task EnsureReadyAsync(string projectId, CancellationToken cancellationToken) =>
        readiness.EnsureReadyAsync(
            GetDatabasePath(projectId),
            ct => EnsureCreatedCoreAsync(projectId, allowCreate: false, ct),
            cancellationToken);

    private async Task EnsureCreatedCoreAsync(string projectId, bool allowCreate, CancellationToken cancellationToken)
    {
        var databasePath = GetDatabasePath(projectId);
        if (!allowCreate)
        {
            // 開案與讀寫只用已經建好的資料庫：檔案不見或是 0 位元組時比照無法開啟的案件回報，
            // 不默默建出空的資料庫。
            LocalSchemaReadiness.RequireDatabaseFile(projectId, databasePath);
        }
        else if (LocalSchemaReadiness.IsEmptyFile(databasePath))
        {
            // 建立時遇到 0 位元組的檔案，只會是建案中斷留下的；連同記錄檔刪掉再建。
            DeleteDatabaseFiles(projectId);
        }

        await using var connection = CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // 已經建過表的資料庫只檢查版本：不是現行版本就在任何寫入之前擋下，檔案保持原狀。
        var existing = await HasSchemaInfoAsync(connection, cancellationToken);
        if (existing)
        {
            LocalSchemaReadiness.RejectUnsupportedSchemaVersion(await ReadVersionAsync(connection, cancellationToken));
        }
        else if (!allowCreate)
        {
            // 有檔案但沒有任何資料表，同樣是沒有內容；在任何寫入之前回報，檔案保持原狀。
            throw LocalSchemaReadiness.EmptyDatabase(projectId);
        }

        // WAL 是資料庫檔案層的持久設定（冪等）：百萬列單交易寫入避免 rollback journal
        // 的雙倍寫放大，replace 模式的大量 DELETE 也因此變廉價
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        if (existing)
        {
            return;
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
    /// 刪除該專案的 jet.db（含 WAL/SHM 邊檔）。只清除此專案的連線池以釋放檔案鎖，
    /// 避免刪除一案時中斷其他專案正在執行的 SQLite 工作。
    /// 資料夾本身由 IProjectStore.DeleteAsync 刪除。
    /// </summary>
    public Task DeleteAsync(string projectId, CancellationToken cancellationToken)
    {
        readiness.Forget(folder.GetDatabasePath(projectId));
        DeleteDatabaseFiles(projectId);
        return Task.CompletedTask;
    }

    /// <summary>清掉這個案件的連線池釋放檔案鎖，再刪 jet.db 與 WAL/SHM 邊檔。</summary>
    private void DeleteDatabaseFiles(string projectId)
    {
        var databasePath = folder.GetDatabasePath(projectId);
        ClearPool(databasePath);

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// 一項改資料的作業結束後呼叫：把記錄檔裡已提交的資料併回 jet.db，再把記錄檔清成 0 位元組。
    /// 用一條不進連線池、不建立新檔的連線執行，jet.db 不存在時什麼都不做。有其他連線正在讀寫時，
    /// SQLite 只併回能併的部分就回來，不等待；記錄檔留到下一次併回或關閉時再清。
    /// </summary>
    public async Task CheckpointAsync(string projectId, CancellationToken cancellationToken)
    {
        var databasePath = folder.GetDatabasePath(projectId);
        if (!File.Exists(databasePath))
        {
            return;
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 離開案件、開案失敗與結束 JET 時呼叫：先清掉這個案件的連線池，再併回並清空記錄檔。併回用的連線是這個程序
    /// 最後一條連線，關閉時 SQLite 會刪掉記錄檔與共用記憶體檔，JET 不再開著 jet.db。正在執行的查詢用完連線後才關閉。
    /// </summary>
    public Task CloseAsync(string projectId, CancellationToken cancellationToken)
    {
        ClearPool(folder.GetDatabasePath(projectId));
        return CheckpointAsync(projectId, cancellationToken);
    }

    /// <summary>
    /// 清掉這個案件的連線池：閒置的連線立刻關閉，使用中的連線用完後關閉。只清這個案件，
    /// 不中斷其他案件的 SQLite 工作；不開啟連線，也不建立資料夾或檔案。
    /// </summary>
    private static void ClearPool(string databasePath)
    {
        using var connection = new SqliteConnection(PooledConnectionString(databasePath));
        SqliteConnection.ClearPool(connection);
    }

    private static async Task<string?> ReadVersionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var versionQuery = connection.CreateCommand();
        versionQuery.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version';";
        // 存的不是文字（例如二進位）時回 null，由 RejectUnsupportedSchemaVersion 當成版本資訊無法辨識；直接轉型會丟 InvalidCastException。
        return await versionQuery.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>資料庫裡是否已經有 schema_info；只讀取，不建立任何資料表。</summary>
    private static async Task<bool> HasSchemaInfoAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_info';";
        return Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) > 0;
    }
}
