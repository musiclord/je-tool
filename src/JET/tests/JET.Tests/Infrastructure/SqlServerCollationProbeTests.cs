using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 定序契約探針（design §2.1「Collation 契約總決」）：跨 provider 等價的第三支柱是
/// 「文字鍵一律 <c>Latin1_General_BIN2</c>（＝SQLite BINARY 位元序，BMP 中文＝碼位序）」。
/// 兩層驗證：
/// 1. 結構探針——<c>sys.columns.collation_name</c> 逐欄核對 SchemaSql 釘住的文字鍵欄
///    （查欄位層、不查資料庫層：既有 JET_Test 的庫層預設定序要等環境重置才會是 BIN2，
///    但欄位層顯式 COLLATE 在新建 schema 上立即生效）。
/// 2. 行為探針——大小寫相異（a001/A001、jv-001/JV-001）與全形拉丁（ＡＤＪ/ADJ）鍵值的
///    GROUP BY 分組結果，以 SQLite BINARY 為 oracle 斷言雙 provider 等價
///    （舊預設定序 CI＋寬度不敏感會把 6 鍵折成 3 組——此即修復前的紅燈證據）。
/// </summary>
public sealed class SqlServerCollationProbeTests
{
    /// <summary>
    /// SchemaSql 顯式釘 BIN2 的文字鍵欄清單（參與 JOIN／WHERE／GROUP BY／UNIQUE／ORDER BY 的欄）。
    /// 守衛範圍誠實聲明：本探針只守「已登記的欄仍釘著 BIN2」——登記後拆釘會紅燈；
    /// 但「新增鍵欄且完全忘記登記」偵測不到（allow-list 的固有限制）。
    /// 新表入 SchemaSql 時，靠 code review 紀律把鍵欄同步登記於此。
    /// 刻意不釘（純顯示／payload）欄不在此列：line_item、account_name、document_description、
    /// source_module、day_name、mode_name、檔名路徑、*_json、app_message_log.*、config_filter_scenario.*。
    /// </summary>
    private static readonly (string Table, string Column)[] PinnedKeyColumns =
    [
        ("schema_info", "key"),
        ("import_batch", "batch_id"),
        ("import_batch", "dataset_kind"),
        ("import_batch", "imported_utc"),
        ("import_batch_source", "batch_id"),
        ("import_field_definition", "batch_id"),
        ("import_field_definition", "definition_scope"),
        ("import_field_definition", "field_kind"),
        ("staging_gl_raw_row", "batch_id"),
        ("staging_tb_raw_row", "batch_id"),
        ("staging_account_mapping_raw_row", "batch_id"),
        ("staging_authorized_preparer_raw_row", "batch_id"),
        ("config_field_mapping", "dataset_kind"),
        ("config_field_mapping", "source_batch_id"),
        ("config_gl_rde_field", "field_id"),
        ("config_gl_rde_field", "value_type"),
        ("config_account_taxonomy", "category_id"),
        ("config_account_taxonomy", "semantic_role"),
        ("staging_calendar_raw_day", "day_type"),
        ("staging_calendar_raw_day", "date"),
        ("target_gl_entry", "batch_id"),
        ("target_gl_entry", "document_number"),
        ("target_gl_entry", "post_date"),
        ("target_gl_entry", "approval_date"),
        ("target_gl_entry", "voucher_date"),
        ("target_gl_entry", "account_code"),
        ("target_gl_entry", "created_by"),
        ("target_gl_entry", "approved_by"),
        ("target_gl_entry", "posting_status"),
        ("target_gl_entry", "exclusion_reason"),
        ("target_gl_entry", "dr_cr"),
        ("target_gl_rde_value", "field_id"),
        ("target_gl_rde_value", "value_type"),
        ("target_gl_rde_value", "text_value"),
        ("target_gl_rde_value", "date_value"),
        ("target_tb_balance", "batch_id"),
        ("target_tb_balance", "account_code"),
        ("result_rule_run", "run_id"),
        ("result_rule_run", "run_kind"),
        ("result_rule_run", "generated_utc"),
        ("result_inf_sampling_test_sample", "run_id"),
        ("result_inf_sampling_test_sample", "document_number"),
        ("target_account_mapping", "batch_id"),
        ("target_account_mapping", "account_code"),
        ("target_account_mapping", "standardized_category"),
        ("target_account_mapping", "category_id"),
        ("target_authorized_preparer", "name"),
    ];

    [SqlServerFact]
    public async Task SchemaSql_TextKeyColumns_ArePinnedToLatin1GeneralBin2()
    {
        await using var sql = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(sql);

        await using var connection = sql.Database.CreateConnection(sql.ProjectId);
        await connection.OpenAsync();

        // 一次撈出本專案 schema 全部 NVARCHAR 欄的定序，逐欄核對（欄位層探針，不查資料庫層）。
        var actual = new Dictionary<(string Table, string Column), string?>();
        await using (var command = sql.Database.CreateCommand(
            connection,
            sql.ProjectId,
            """
            SELECT t.name, c.name, c.collation_name
            FROM sys.columns c
            INNER JOIN sys.tables t ON t.object_id = c.object_id
            WHERE t.schema_id = SCHEMA_ID(@schema) AND c.collation_name IS NOT NULL;
            """))
        {
            command.Parameters.AddWithValue("@schema", SqlServerProjectSchema.For(sql.ProjectId));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                actual[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
            }
        }

        Assert.NotEmpty(actual); // 空集合守門：schema 沒建起來時不得真空通過

        var offenders = PinnedKeyColumns
            .Where(k => !actual.TryGetValue(k, out var collation) || collation != "Latin1_General_BIN2")
            .Select(k => $"{k.Table}.{k.Column} = {(actual.TryGetValue(k, out var c) ? c : "<欄不存在>")}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "以下文字鍵欄未釘 Latin1_General_BIN2（欄位層定序漂移）：\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// 行為探針的鍵值組：大小寫相異兩組＋全形拉丁一組。
    /// SQLite BINARY oracle：6 個鍵 → 6 組；舊 SQL Server 預設定序（CI＋寬度不敏感）→ 3 組（紅燈證據）。
    /// </summary>
    private static readonly string[] ProbeKeys = ["A001", "ADJ", "JV-001", "a001", "jv-001", "ＡＤＪ"];

    [SqlServerFact]
    public async Task DocumentNumberGrouping_CaseAndWidthVariantKeys_MatchesSqliteBinarySemantics()
    {
        // oracle：SQLite BINARY 的分組結果（值＋身分＋順序）。
        var sqliteGroups = SqliteGroupedDocumentNumbers();
        Assert.Equal(6, sqliteGroups.Count); // oracle 自檢：6 鍵必為 6 組

        await using var sql = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(sql);

        await using var connection = sql.Database.CreateConnection(sql.ProjectId);
        await connection.OpenAsync();

        var insertValues = string.Join(",\n", ProbeKeys.Select(
            (key, i) => $"('b1', {i + 1}, N'{key}', 100, 100, 0, 'DEBIT')"));
        await using (var insert = sql.Database.CreateCommand(
            connection,
            sql.ProjectId,
            $$"""
            INSERT INTO {s}.target_gl_entry
                (batch_id, source_row_number, document_number,
                 amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
            VALUES
            {{insertValues}};
            """))
        {
            await insert.ExecuteNonQueryAsync();
        }

        var sqlServerGroups = new List<string>();
        await using (var groupBy = sql.Database.CreateCommand(
            connection,
            sql.ProjectId,
            "SELECT document_number FROM {s}.target_gl_entry GROUP BY document_number ORDER BY document_number;"))
        {
            await using var reader = await groupBy.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sqlServerGroups.Add(reader.GetString(0));
            }
        }

        // 等價斷言鎖「值＋身分＋順序」：BIN2 的碼位序 ＝ SQLite BINARY 的位元序（BMP 範圍）。
        Assert.Equal(sqliteGroups, sqlServerGroups);
    }

    /// <summary>SQLite oracle：同一組鍵值在 temp 專案 DB 的 GROUP BY／ORDER BY（BINARY 定序）結果。</summary>
    private static List<string> SqliteGroupedDocumentNumbers()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        database.EnsureCreatedAsync(projectId, CancellationToken.None).GetAwaiter().GetResult();

        var groups = new List<string>();
        using (var connection = database.CreateConnection(projectId))
        {
            connection.Open();
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    """
                    INSERT INTO target_gl_entry
                        (batch_id, source_row_number, document_number,
                         amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
                    VALUES
                    """ + string.Join(",\n", ProbeKeys.Select((key, i) => $"('b1', {i + 1}, '{key}', 100, 100, 0, 'DEBIT')")) + ";";
                insert.ExecuteNonQuery();
            }

            using var groupBy = connection.CreateCommand();
            groupBy.CommandText =
                "SELECT document_number FROM target_gl_entry GROUP BY document_number ORDER BY document_number;";
            using var reader = groupBy.ExecuteReader();
            while (reader.Read())
            {
                groups.Add(reader.GetString(0));
            }
        }

        return groups;
    }
}
