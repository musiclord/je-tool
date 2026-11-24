using System.Data.Common;
using JET.Domain;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace JET.Infrastructure;

/// <summary>
/// <see cref="ILocalProjectDatabase"/> 的 SQLite 實作：建立每個案件 jet.db 的連線，並負責建表與檢查結構版本。
/// 每個案件一個資料庫檔，一個檔案裡只有一個案件的資料，所以資料表沒有 project_id 欄。
/// 傳入監看模組時，這裡建立的每條連線都把語句的執行通知它；監看模組只記錄，不改變語句的執行與結果。
/// </summary>
public sealed class SqliteProjectDatabase(JetProjectFolder folder, ISqlStatementObserver? statementObserver = null)
    : ILocalProjectDatabase
{
    /// <summary>
    /// 案件資料庫的現行結構，建成後標成第 21 版。只有新資料庫會執行這段；已有資料表的資料庫只檢查版本，
    /// 不是現行版本就請審計員刪除案件後重建（案件資料庫只留現行結構）。
    /// 匯入暫存表的 row_number 是同一個批次裡只增不減的排序鍵，INF 抽樣以它為準；
    /// source_row_number 是來源檔裡實際的列號，錯誤訊息用它指出是哪一列；source_no 對應 import_batch_source。
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

        -- 以案件資料庫為準的案件設定，只有一列，建案時和資料表在同一個交易裡寫入。
        -- 建案後不能改的欄位由觸發器擋下修改，整列也不能刪除；準備日與每週休息日只在 JET 修改時更新。
        CREATE TABLE IF NOT EXISTS project_setting (
            singleton                   INTEGER PRIMARY KEY CHECK (singleton = 1),
            period_start                TEXT NOT NULL,
            period_end                  TEXT NOT NULL,
            money_scale                 INTEGER NOT NULL,
            roc_date_enabled            INTEGER NOT NULL CHECK (roc_date_enabled IN (0,1)),
            sample_seed                 INTEGER NOT NULL,
            sample_seed_version         INTEGER NOT NULL,
            operator_id                 TEXT NOT NULL,
            last_accounting_period_date TEXT,
            non_working_days            TEXT
        );
        CREATE TRIGGER IF NOT EXISTS trg_project_setting_fixed
        BEFORE UPDATE OF singleton, period_start, period_end, money_scale, roc_date_enabled,
            sample_seed, sample_seed_version, operator_id ON project_setting
        BEGIN
            SELECT RAISE(ABORT, 'project settings fixed at creation cannot change');
        END;
        CREATE TRIGGER IF NOT EXISTS trg_project_setting_no_delete
        BEFORE DELETE ON project_setting
        BEGIN
            SELECT RAISE(ABORT, 'project settings cannot be deleted');
        END;

        INSERT INTO schema_info (key, value) VALUES ('filter_data_revision', '0')
        ON CONFLICT(key) DO NOTHING;
        INSERT INTO schema_info (key, value) VALUES ('schema_version', '21')
        ON CONFLICT(key) DO NOTHING;
        """;

    public string GetDatabasePath(string projectId) => folder.GetDatabasePath(projectId);

    /// <inheritdoc />
    public IProviderSqlDialect Dialect => SqliteDialect.Instance;

    public DbConnection CreateConnection(string projectId) =>
        CreateConfiguredConnection(PooledConnectionString(PrepareDatabasePath(projectId)), CaseConnectionSettings);

    /// <summary>
    /// 一般案件連線每次開啟時的設定：頁面快取 256 MiB。驗證、預篩選、報告與底稿會反覆讀 gl 與它的索引，
    /// SQLite 給新連線的快取約 2 MiB，GL 大時裝不下常用的頁面，同一批頁面要一再從資料庫檔讀回來。
    /// 快取讀到資料才配置，小案件用不到這麼多。快取跟著連線走，連線池裡每條連線各有一份，
    /// 同時開著幾條時，合計由 <see cref="SqliteMemoryLimitBytes"/> 限制。
    /// 匯入與確認 GL 配對的專用連線不進連線池，各用自己的設定。
    /// </summary>
    private const string CaseConnectionSettings = "PRAGMA cache_size=-262144;";

    /// <summary>
    /// 整個程序裡 SQLite 記憶體的合計上限：1 GiB。這個上限是程序共用的一個值，不分連線；
    /// 用量接近時 SQLite 先釋放各連線快取裡的頁面，盡量不超過，但寧可略為超過，也不讓配置失敗，
    /// 所以操作不會因為這個上限出錯。會讓配置直接失敗的硬上限不設。
    /// </summary>
    private const long SqliteMemoryLimitBytes = 1L << 30;

    private static int memoryLimitApplied;

    /// <summary>
    /// 所有案件連線建立時都經過 <see cref="CreateConfiguredConnection"/>，第一次建立時設一次上限，之後的連線共用同一個值。
    /// 建立連線物件時 Microsoft.Data.Sqlite 已經載入 SQLite，這時才能呼叫。
    /// </summary>
    private static void ApplySqliteMemoryLimitOnce()
    {
        if (Interlocked.Exchange(ref memoryLimitApplied, 1) == 0)
        {
            raw.sqlite3_soft_heap_limit64(SqliteMemoryLimitBytes);
        }
    }

    /// <summary>這個案件的資料庫檔路徑；案件資料夾不存在時先建立，開啟讀寫連線時才能建檔。</summary>
    private string PrepareDatabasePath(string projectId)
    {
        var databasePath = folder.GetDatabasePath(projectId);

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return databasePath;
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
    /// 開發用的唯讀檢視工具專用的連線：Mode=ReadOnly、私有快取、不進連線池。
    /// 每次查詢都重新開磁碟上的檔案，看到的一定是已經寫進檔案的資料，也不會改到任何東西。
    /// 資料庫檔不存在時開啟就失敗，不會建檔。
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

    private SqliteConnection CreateConfiguredConnection(string connectionString, string? openSettings = null)
    {
        // 取消會中斷執行中的語句，見 CancellableSqliteConnection。
        var connection = new CancellableSqliteConnection(connectionString, statementObserver, openSettings);
        ApplySqliteMemoryLimitOnce();
        // SQLite 內建的 UPPER 只轉 ASCII 字母。篩選條件與預篩選規則的比對值是用 .NET 的 ToUpperInvariant() 轉大寫，
        // 這裡把 upper 換成同一個方法，兩邊才一致。連線池裡的連線、唯讀連線與匯入、投影的專用連線都經過這裡。
        connection.CreateFunction<string?, string?>("upper", static value => value?.ToUpperInvariant(), isDeterministic: true);
        return connection;
    }

    /// <summary>
    /// 匯入用的連線。資料庫用 WAL 時，synchronous=NORMAL 仍然保證一致性；
    /// temp_store 與 cache_size 減少大量寫入時的磁碟讀寫。這類跟著資料庫種類走的設定只放在 Infrastructure。
    /// </summary>
    public Task<DbConnection> OpenImportConnectionAsync(string projectId, CancellationToken cancellationToken) =>
        OpenDedicatedConnectionAsync(
            projectId,
            "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536;",
            cancellationToken);

    /// <summary>
    /// 確認 GL 配對時投影用的連線：暫存寫進暫存檔，頁面快取 16 MiB。
    /// 投影在同一個交易裡逐列寫入、建索引，再用一句 UPDATE 補分錄序號；SQLite 為了能單獨撤銷這句 UPDATE，
    /// 會記下它改過的頁面。暫存放在記憶體時，這份紀錄與建索引的排序都整份留在記憶體，GL 越大用得越多；
    /// 寫進暫存檔時，超過一定大小的部分寫到磁碟。16 MiB 的快取讓逐列寫入與建索引少讀幾次磁碟。
    /// </summary>
    public Task<DbConnection> OpenGlProjectionConnectionAsync(string projectId, CancellationToken cancellationToken) =>
        OpenDedicatedConnectionAsync(projectId, "PRAGMA temp_store=FILE; PRAGMA cache_size=-16384;", cancellationToken);

    /// <summary>
    /// 開一條不進連線池的讀寫連線，開啟後立刻套用 <paramref name="settings"/>。
    /// SQLite 的這類設定跟著連線走，連線池放回連線時不會重設；這條連線用完就關，
    /// 設定不會留給之後剛好拿到同一條連線的動作。一般案件連線開啟時的快取設定不套用在這裡，
    /// 其餘和 <see cref="CreateConnection"/> 相同，SQL 函式照樣註冊。
    /// </summary>
    private async Task<DbConnection> OpenDedicatedConnectionAsync(
        string projectId, string settings, CancellationToken cancellationToken)
    {
        var connection = CreateConfiguredConnection(
            new SqliteConnectionStringBuilder(PooledConnectionString(PrepareDatabasePath(projectId)))
            {
                Pooling = false
            }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = settings;
            await pragma.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>SQLite 依資料庫結構表記下的定義刪掉與建回索引，見 <see cref="SqliteDeferredIndexes"/>。</summary>
    public async Task<IDeferredIndexes> DeferIndexesAsync(
        DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken) =>
        await SqliteDeferredIndexes.DropAsync(connection, transaction, table, cancellationToken);

    /// <summary>SQLite 的批量寫入是把一句參數化 INSERT 包起來重複執行，見 <see cref="SqliteBulkRowWriter"/>。</summary>
    public IBulkRowWriter CreateBulkRowWriter(
        DbConnection connection, DbTransaction transaction, string table, IReadOnlyList<string> columns)
        => new SqliteBulkRowWriter(connection, transaction, table, columns);

    private readonly LocalSchemaReadiness readiness = new();

    /// <summary>一律重新檢查版本，新資料庫在這裡建表，不寫入案件設定。只有建案與直接讀寫資料表的程式會建立新的資料庫。</summary>
    public Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken) =>
        readiness.EnsureCreatedAsync(
            GetDatabasePath(projectId),
            ct => EnsureCreatedCoreAsync(projectId, allowCreate: true, settings: null, ct),
            cancellationToken);

    /// <summary>建案時呼叫：一律重新檢查版本，新資料庫在這裡建表，同一個交易裡寫入案件設定。</summary>
    public Task EnsureCreatedAsync(
        string projectId, ProtectedProjectSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return readiness.EnsureCreatedAsync(
            GetDatabasePath(projectId),
            ct => EnsureCreatedCoreAsync(projectId, allowCreate: true, _ => Task.FromResult(settings), ct),
            cancellationToken);
    }

    /// <summary>開案時呼叫：一律重新檢查版本，不建立新的資料庫；建案中斷留下的案件在這裡把初始化做完。</summary>
    public Task EnsureExistingAsync(string projectId, CancellationToken cancellationToken) =>
        readiness.EnsureExistingAsync(
            GetDatabasePath(projectId),
            folder.GetCreationMarkerPath(projectId),
            (allowCreate, ct) => EnsureCreatedCoreAsync(projectId, allowCreate, settings: null, ct),
            cancellationToken);

    /// <summary>
    /// 開案時呼叫，和不傳設定的多載相同；建案中斷留下的案件在這裡把初始化做完時，才向呼叫端取得案件設定並寫入。
    /// </summary>
    public Task EnsureExistingAsync(
        string projectId,
        Func<CancellationToken, Task<ProtectedProjectSettings>> settingsIfCreating,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settingsIfCreating);
        return readiness.EnsureExistingAsync(
            GetDatabasePath(projectId),
            folder.GetCreationMarkerPath(projectId),
            (allowCreate, ct) => EnsureCreatedCoreAsync(projectId, allowCreate, settingsIfCreating, ct),
            cancellationToken);
    }

    /// <summary>repository 每次讀寫前呼叫：同一程序內每個資料庫檔只完整檢查一次，不建立新的資料庫。</summary>
    public Task EnsureReadyAsync(string projectId, CancellationToken cancellationToken) =>
        readiness.EnsureReadyAsync(
            GetDatabasePath(projectId),
            ct => EnsureCreatedCoreAsync(projectId, allowCreate: false, settings: null, ct),
            cancellationToken);

    private async Task EnsureCreatedCoreAsync(
        string projectId,
        bool allowCreate,
        Func<CancellationToken, Task<ProtectedProjectSettings>>? settings,
        CancellationToken cancellationToken)
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

        // WAL 是存在資料庫檔裡的設定，重複設定沒有影響。用 WAL 以後，一個交易寫入上百萬列時
        // 不必像 rollback journal 那樣把資料寫兩次，取代匯入時的大量 DELETE 也比較快。
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        if (existing)
        {
            return;
        }

        // 案件設定在開交易之前取得，取得時可能要等使用者全名查好。
        var recordedSettings = settings is null ? null : await settings(cancellationToken);

        // 新資料庫在同一個交易裡建好全部的表，中途失敗時不留下只建一半的資料庫。
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (recordedSettings is not null)
        {
            await LocalProjectSettingSql.InsertAsync(connection, transaction, recordedSettings, cancellationToken);
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
