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
    /// 刻意不釘（純顯示／payload）欄不在此列：day_name、mode_name、檔名路徑、*_json、message_log.*、
    /// filter_scenario.*。line_item、account_name、description 與 source_module 會被文字條件、
    /// 排序與 MAX 用到，2026-10-19 第 14 版起固定 BIN2，讓三種資料庫一致，一併登記在下面。
    /// </summary>
    private static readonly (string Table, string Column)[] PinnedKeyColumns =
    [
        ("schema_info", "key"),
        ("import_batch", "batch_id"),
        ("import_batch", "dataset_kind"),
        ("import_batch", "imported_utc"),
        ("import_batch_source", "batch_id"),
        ("field_info", "batch_id"),
        ("field_info", "definition_scope"),
        ("field_info", "field_kind"),
        ("pbc_gl", "batch_id"),
        ("pbc_tb", "batch_id"),
        ("pbc_account_mapping", "batch_id"),
        ("pbc_authorized_preparer", "batch_id"),
        ("field_mapping", "dataset_kind"),
        ("field_mapping", "source_batch_id"),
        ("gl_rde_field", "field_id"),
        ("gl_rde_field", "value_type"),
        ("account_category", "category_id"),
        ("account_category", "semantic_role"),
        ("calendar_day", "day_type"),
        ("calendar_day", "date"),
        ("gl", "batch_id"),
        ("gl", "voucher_number"),
        ("gl", "gl_date"),
        ("gl", "approval_date"),
        // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，現行 schema 不再有 voucher_date。
        ("gl", "account_number"),
        ("gl", "created_by"),
        ("gl", "approved_by"),
        ("gl", "posting_status"),
        ("gl", "exclusion_reason"),
        ("gl", "dr_cr"),
        ("gl_rde_value", "field_id"),
        ("gl_rde_value", "value_type"),
        ("gl_rde_value", "text_value"),
        ("gl_rde_value", "date_value"),
        ("tb", "batch_id"),
        ("tb", "account_number"),
        ("validation_result", "result_id"),
        ("validation_result", "generated_utc"),
        ("prescreen_result", "result_id"),
        ("prescreen_result", "generated_utc"),
        ("inf_sample", "result_id"),
        ("inf_sample", "voucher_number"),
        ("account_mapping", "batch_id"),
        ("account_mapping", "account_number"),
        ("account_mapping", "standardized_category"),
        ("account_mapping", "category_id"),
        ("authorized_preparer", "name"),
        ("gl", "line_item"),
        ("gl", "account_name"),
        ("gl", "description"),
        ("gl", "source_module"),
        ("tb", "account_name"),
        ("account_mapping", "account_name"),
        ("inf_sample", "line_item"),
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
    public async Task VoucherNumberGrouping_CaseAndWidthVariantKeys_MatchesSqliteBinarySemantics()
    {
        // oracle：SQLite BINARY 的分組結果（值＋身分＋順序）。
        var sqliteGroups = SqliteGroupedVoucherNumbers();
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
            INSERT INTO {s}.gl
                (batch_id, source_row_number, voucher_number,
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
            "SELECT voucher_number FROM {s}.gl GROUP BY voucher_number ORDER BY voucher_number;"))
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
    private static List<string> SqliteGroupedVoucherNumbers()
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
                    INSERT INTO gl
                        (batch_id, source_row_number, voucher_number,
                         amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
                    VALUES
                    """ + string.Join(",\n", ProbeKeys.Select((key, i) => $"('b1', {i + 1}, '{key}', 100, 100, 0, 'DEBIT')")) + ";";
                insert.ExecuteNonQuery();
            }

            using var groupBy = connection.CreateCommand();
            groupBy.CommandText =
                "SELECT voucher_number FROM gl GROUP BY voucher_number ORDER BY voucher_number;";
            using var reader = groupBy.ExecuteReader();
            while (reader.Read())
            {
                groups.Add(reader.GetString(0));
            }
        }

        return groups;
    }
}
