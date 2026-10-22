using System.Data.Common;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 使用者 2026-10-21 裁定 GL(未納入測試) 的「日期不符」分成總帳日期空白、早於查核期間、晚於查核期間三種原因。
/// 情境：審計員開啟改版前確認過 GL 欄位配對的案件（本機 schema 第 12 版，日期不符的分錄都記成 <c>period</c>），
/// 開案後資料庫升到第 13 版，日期不符的每一列換成各自的原因；GL 的分錄與過帳狀態不符的分錄都不動。
/// 資料庫不存查核期間，升版用 GL 的總帳日期判斷：比最早的 GL 分錄早就是早於查核期間，其餘是晚於查核期間。
/// 第 12 版的形狀由目前的建表結果降回去：兩版的資料表結構相同，只差日期不符的記法。
/// </summary>
public sealed class GlExclusionReasonMigrationTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task V12CaseWithPeriodRows_UpgradesToV13_SplitsDateMismatchIntoThreeReasons(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await Create(provider, folder).EnsureCreatedAsync(projectId, CancellationToken.None);

        // 降回第 12 版：照改版前的記法寫入一批確認過的 GL，查核期間是 2025 年，日期不符的列一律記成 period。
        var opened = Create(provider, folder);
        await using (var connection = opened.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection,
                "INSERT INTO target_gl_entry (batch_id, source_row_number, document_number, line_item, post_date, "
                + "amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, posting_status, is_effective, exclusion_reason) VALUES "
                + "('v12', 1, 'JV-01', '1', '2025-01-01', 100, 100, 0, 'DEBIT', 'POSTED', 1, NULL), "
                + "('v12', 2, 'JV-02', '1', '2025-12-31', -100, 0, 100, 'CREDIT', 'POSTED', 1, NULL), "
                + "('v12', 3, 'JV-03', '1', NULL, 100, 100, 0, 'DEBIT', 'DRAFT', 0, 'period'), "
                + "('v12', 4, 'JV-04', '1', '2024-12-31', 100, 100, 0, 'DEBIT', 'POSTED', 0, 'period'), "
                + "('v12', 5, 'JV-05', '1', '2026-01-01', 100, 100, 0, 'DEBIT', 'POSTED', 0, 'period'), "
                + "('v12', 6, 'JV-06', '1', '2025-06-30', 100, 100, 0, 'DEBIT', 'DRAFT', 0, 'posting_status'), "
                + "('v12', 7, 'JV-07', '1', '2024-06-30', 100, 100, 0, 'DEBIT', 'DRAFT', 0, 'period'), "
                + "('v12', 8, 'JV-08', '1', '2027-03-15', 100, 100, 0, 'DEBIT', 'DRAFT', 0, 'period')");
            await ExecuteAsync(connection, "UPDATE schema_info SET value = '12' WHERE key = 'schema_version'");
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
            Assert.Equal("13", await version.ExecuteScalarAsync() as string);
        }

        var rows = new List<(string?, long, string?)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT document_number, is_effective, exclusion_reason FROM target_gl_entry ORDER BY source_row_number";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add((
                    reader.GetString(0),
                    Convert.ToInt64(reader.GetValue(1)),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        Assert.Equal(
            new (string?, long, string?)[]
            {
                ("JV-01", 1, null), ("JV-02", 1, null),
                ("JV-03", 0, "blank_post_date"), ("JV-04", 0, "before_period"), ("JV-05", 0, "after_period"),
                ("JV-06", 0, "posting_status"), ("JV-07", 0, "before_period"), ("JV-08", 0, "after_period")
            },
            rows);
    }

    private static ILocalProjectDatabase Create(string provider, JetProjectFolder folder) =>
        provider == "sqlite" ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
