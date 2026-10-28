using System.Data.Common;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 預篩選收斂計畫階段六：匯入來源多記「欄名在第幾列」（使用者 2026-10-22 裁定開放給一般匯入）。
/// 情境：審計員開啟改版前建立的案件（本機 schema 第 13 版，來源表沒有 <c>header_row</c> 欄），
/// 開案後資料庫升到第 14 版，舊的來源紀錄原樣保留、欄名列是空值，之後新匯入的來源可以記下欄名列。
/// 第 13 版的形狀由目前的建表結果降回去：只拿掉這一欄，其他表與現行相同。
/// 第一次失敗：Focused 收據 20261023-193734239-f6249c3be1e046cfa0c55b4181d2cabd（還沒有這一欄，降版就失敗）。
/// </summary>
public sealed class ImportSourceHeaderRowMigrationTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task V13CaseWithSources_UpgradesToV14_KeepsSources_AndAcceptsHeaderRow(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await Create(provider, folder).EnsureCreatedAsync(projectId, CancellationToken.None);

        // 降回第 13 版：來源表拿掉欄名列那一欄，再照改版前的方式寫入一個批次與兩個來源。
        var opened = Create(provider, folder);
        await using (var connection = opened.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, "ALTER TABLE import_batch_source DROP COLUMN header_row");
            await ExecuteAsync(connection,
                "INSERT INTO import_batch (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json) "
                + "VALUES ('v13', 'gl', '合成總帳.xlsx', '合成總帳.xlsx', '2026-10-01T01:00:00.0000000+00:00', 3, '[\"傳票號碼\"]')");
            await ExecuteAsync(connection,
                "INSERT INTO import_batch_source (batch_id, source_no, source_file_path, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc) VALUES "
                + "('v13', 1, '合成總帳.xlsx', '合成總帳.xlsx', 'Q1', NULL, NULL, 2, '2026-10-01T01:00:00.0000000+00:00'), "
                + "('v13', 2, '合成總帳.csv', '合成總帳.csv', NULL, 'utf-8', ',', 1, '2026-10-01T01:01:00.0000000+00:00')");
            await ExecuteAsync(connection, "UPDATE schema_info SET value = '13' WHERE key = 'schema_version'");
        }

        // 審計員開啟案件。
        var database = Create(provider, folder);
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await AssertUpgradedAsync(database, projectId);

        // 再開一次不重做升版，結果相同。
        var reopened = Create(provider, folder);
        await reopened.EnsureCreatedAsync(projectId, CancellationToken.None);
        await AssertUpgradedAsync(reopened, projectId);
    }

    private static async Task AssertUpgradedAsync(ILocalProjectDatabase database, string projectId)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using (var version = connection.CreateCommand())
        {
            version.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version'";
            Assert.Equal("14", await version.ExecuteScalarAsync() as string);
        }

        var sources = new List<(int SourceNo, string? Sheet, long? HeaderRow)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT source_no, sheet_name, header_row FROM import_batch_source WHERE batch_id = 'v13' ORDER BY source_no";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sources.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
            }
        }

        Assert.Equal([(1, "Q1", (long?)null), (2, null, (long?)null)], sources);

        // 之後新匯入的來源可以記下欄名列；寫完刪掉，讓第二次開啟的檢查看到同一份資料。
        await ExecuteAsync(connection,
            "INSERT INTO import_batch_source (batch_id, source_no, source_file_path, source_file_name, sheet_name, encoding, delimiter, header_row, row_count, imported_utc) "
            + "VALUES ('v13', 3, '合成補充.xlsx', '合成補充.xlsx', 'Q2', NULL, NULL, 3, 1, '2026-10-01T01:02:00.0000000+00:00')");
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT header_row FROM import_batch_source WHERE batch_id = 'v13' AND source_no = 3";
            Assert.Equal(3L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }

        await ExecuteAsync(connection, "DELETE FROM import_batch_source WHERE batch_id = 'v13' AND source_no = 3");
    }

    private static ILocalProjectDatabase Create(string provider, JetProjectFolder folder) =>
        provider == "duckdb" ? new DuckDbProjectDatabase(folder) : new SqliteProjectDatabase(folder);

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
