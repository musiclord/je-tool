using System.Text.RegularExpressions;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SQL Server schema 的版本檢查與建表語句。要連線的測試以 [SqlServerFact] 閘控（連隔離庫 JET_Test，非 Express、≥ 2022）。
/// 既有 schema 只接受現行版本，不再升版；版本比現行舊時回舊版案件的訊息（裁定 2026-10-30-24）。
/// </summary>
public sealed class SqlServerSchemaMigrationTests
{
    private const string SingleDb = "JET_Test";

    [Fact]
    public void PreviousMappingTable_RequiresVersionTwelveForExistingV11Cases()
    {
        // Pure SQL-plan guard; no SQL Server connection is opened by this test.
        // 裁定 2026-10-13-15 把訊息分三級，訊息紀錄的等級限制在第 13 版加上 error；第 11 版案件仍會補上這張表。
        // 第一次失敗收據 20261013-095724851-a5acf04a01e14d96a9847e96470f1dde。
        // 2026-10-19 為了三種資料庫一致，把文字欄定序升到第 14 版，預期版本由 13 改成 14；
        // 第一次失敗收據 20261019-022312081-22a88a3e48db46cdbad3a14059471cd9。
        // 裁定 2026-10-21-04 把日期不符分成三種原因，第 15 版轉換舊的 period 列，預期版本由 14 改成 15；
        // 第一次失敗收據 20261021-080930829-9ee6cdc9021744a0bf40903ea77f833a。
        // 裁定 2026-10-22-15 讓每個來源可指定欄名在第幾列，import_batch_source 加 header_row 欄，預期版本由 15 改成 16；第一次失敗收據 20261023-194649480-a2d7d7e4982c4c169b10b9a8a3f12d5b。
        // 統一改名改了表名、欄名與 CHECK 值，預期版本由 16 改成 17，改名前建立的案件才會看到舊版案件的訊息；
        // 第一次失敗收據 20261030-135241888-3c45f8ad7a044419beee5d439bf6db8a。
        // 條件組結果改成依條件組編號儲存，filter_scenario 加 scenario_id、新增 filter_hit_condition，預期版本由 17 改成 18；
        // 第一次失敗收據 20261031-015438711-e019d04ff2b64ad08bc34c916334bd83。
        // 驗證摘要裡同一傳票號碼出現在不同總帳日期的鍵名改用 voucher（裁定 2026-11-01-01），預期版本由 18 改成 19；第一次失敗收據 20261101-062343383-804cee29d9d9428f9e5f502ea4e7ae94。
        // 資料缺漏輔助檢視的驗證摘要鍵名改用 blank 字根，預期版本由 19 改成 20。第一次失敗收據 20261103-025623476-a412fc781de145cdb6b2ad2aa38e67b4。
        // 欄位資訊多記每一欄的來歷與對應的 JET 欄位（裁定 2026-11-03-11），field_info 加 field_origin 與 jet_field，預期版本由 20 改成 21。第一次失敗：Focused 收據 20261103-070053462-34d240fe519d4190a85d1a4368c1a80a。
        Assert.Equal("21", SqlServerProjectDatabase.SchemaVersion);
        Assert.Contains("'schema_version', '21')", SqlServerProjectDatabase.SchemaSql);
        // 升版程式拿掉後沒有回寫版本的語句，這裡原本對它的檢查一起拿掉（裁定 2026-10-30-24）。
        Assert.Contains("field_mapping_previous", SqlServerProjectDatabase.SchemaSql);
    }

    // 為了三種資料庫一致，GL、TB 與科目配對的文字欄
    // 一律固定位元序定序，不沿用資料庫預設定序；新建 schema 直接建成。既有 schema 逐欄改定序的語句已隨升版程式拿掉。
    // 純 SQL 文字檢查，不連 SQL Server。第一次失敗收據 20261019-022312081-22a88a3e48db46cdbad3a14059471cd9。
    public static TheoryData<string, string> AuditTextColumns => new()
    {
        { "gl", "line_item" },
        { "gl", "account_name" },
        { "gl", "description" },
        { "gl", "source_module" },
        { "tb", "account_name" },
        { "account_mapping", "account_name" },
        { "inf_sample", "line_item" }
    };

    [Theory]
    [MemberData(nameof(AuditTextColumns))]
    public void TextComparisonMigration_PinsAuditTextColumnsAtVersionFourteen(string table, string column)
    {
        // 裁定 2026-10-21-04 把日期不符分成三種原因，現行版本升到 15；定序仍在第 14 版改。
        // 第一次失敗收據 20261021-080930829-9ee6cdc9021744a0bf40903ea77f833a。
        // 裁定 2026-10-22-15 讓每個來源可指定欄名在第幾列，import_batch_source 加 header_row 欄，預期版本由 15 改成 16；第一次失敗收據 20261023-194649480-a2d7d7e4982c4c169b10b9a8a3f12d5b。
        // 統一改名後預期版本由 16 改成 17；第一次失敗收據 20261030-135241888-3c45f8ad7a044419beee5d439bf6db8a。
        // 條件組結果改成依條件組編號儲存，filter_scenario 加 scenario_id、新增 filter_hit_condition，預期版本由 17 改成 18；
        // 第一次失敗收據 20261031-015438711-e019d04ff2b64ad08bc34c916334bd83。
        // 驗證摘要的鍵名改用 voucher（裁定 2026-11-01-01），預期版本由 18 改成 19；第一次失敗收據 20261101-062343383-804cee29d9d9428f9e5f502ea4e7ae94。
        // 資料缺漏輔助檢視的驗證摘要鍵名改用 blank 字根，預期版本由 19 改成 20。第一次失敗收據 20261103-025623476-a412fc781de145cdb6b2ad2aa38e67b4。
        // 欄位資訊加 field_origin 與 jet_field（裁定 2026-11-03-11），預期版本由 20 改成 21。第一次失敗：Focused 收據 20261103-070053462-34d240fe519d4190a85d1a4368c1a80a。
        Assert.Equal("21", SqlServerProjectDatabase.SchemaVersion);
        // 傳票摘要欄改名 description 後，和欄位資訊表 field_info 的 description 同名；原本在整份建表語句找欄名，
        // 會把不需要固定定序的 field_info.description 也算進來。改成只看該表的建表與補欄語句。
        // 第一次失敗：Focused 收據 20261030-120941513-6c7d1b4be48e4af4a18bea06cc5f413d。
        var tableSql = string.Concat(Regex.Matches(
                SqlServerProjectDatabase.SchemaSql,
                $@"CREATE TABLE \{{s\}}\.{table} \(.*?\n\s*\);|ALTER TABLE \{{s\}}\.{table} ADD [^\n]*",
                RegexOptions.Singleline)
            .Select(static match => match.Value + "\n"));
        Assert.NotEmpty(tableSql);
        var declarations = Regex.Matches(
            tableSql,
            $@"\b{column}\s+NVARCHAR\((\d+|MAX)\)(?<collation>\s+COLLATE Latin1_General_BIN2)?");
        Assert.NotEmpty(declarations);
        Assert.All(declarations, declaration => Assert.True(declaration.Groups["collation"].Success, declaration.Value));
        // 既有 schema 逐欄改定序的語句隨升版程式拿掉，對它的檢查一起拿掉（裁定 2026-10-30-24）；新建的 schema 仍要直接建成位元序定序。
    }

    // 漂移守衛（純單元、不需 SQL Server）：新建 schema 寫入的版本必須等於 SchemaVersion 常數。
    // 日後只改常數、忘了改建表語句的字面值，新建的 schema 下次開啟就會被當成版本不符拒絕。
    [Fact]
    public void SchemaSql_WritesTheCurrentSchemaVersionConstant()
    {
        var version = SqlServerProjectDatabase.SchemaVersion;
        Assert.Contains($"'schema_version', '{version}')", SqlServerProjectDatabase.SchemaSql);
        // 回寫版本的語句隨升版程式拿掉，對它的檢查一起拿掉（裁定 2026-10-30-24）。
    }

    [SqlServerFact]
    public async Task EnsureCreated_AtCurrentVersion_IsNoOp_DoesNotReRunGuardedDdl()
    {
        var baseConn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (baseConn is null) { return; }

        var options = new SqlServerConnectionOptions(baseConn, SingleDb);
        var database = new SqlServerProjectDatabase(options);
        var projectId = $"noop-{Guid.NewGuid():N}";
        var q = SqlServerProjectSchema.QualifierFor(projectId);

        try
        {
            await database.EnsureCreatedAsync(projectId, CancellationToken.None);

            // 卸一欄但「不」改版本（維持現行 7）：證等於現行時走 no-op、不重跑守欄 DDL。
            await ExecuteAsync(
                baseConn,
                $"ALTER TABLE {q}gl DROP COLUMN line_item_numeric_sort_key;");
            Assert.Equal(SqlServerProjectDatabase.SchemaVersion, await VersionAsync(baseConn, q));
            Assert.Equal(
                0,
                await ColumnPresentAsync(
                    baseConn,
                    q,
                    "gl",
                    "line_item_numeric_sort_key"));

            await database.EnsureCreatedAsync(projectId, CancellationToken.None);

            // 版本＝現行 → 讀 schema_info、比對 SchemaVersion 常數後 no-op：欄仍缺（守欄 DDL 未重跑）＝冪等不重複 ALTER。
            // 這同時證 SchemaVersion 常數確有真實讀者（非死碼）——否則此路不會停在 no-op。
            Assert.Equal(
                0,
                await ColumnPresentAsync(
                    baseConn,
                    q,
                    "gl",
                    "line_item_numeric_sort_key"));
            Assert.Equal(SqlServerProjectDatabase.SchemaVersion, await VersionAsync(baseConn, q));
        }
        finally
        {
            await database.DeleteAsync(projectId, CancellationToken.None);
        }
    }

    // 案件資料庫只留現行結構（裁定 2026-10-30-24：SQL Server 的程式跟著改，只經編譯）：既有 schema 版本比現行舊時，
    // 開案回舊版案件的訊息，不補欄位、不改版本，也不動既有資料。SQL Server 開發暫緩，這則測試沒有在 SQL Server 上執行過。
    [SqlServerFact]
    public async Task EnsureCreated_OnOlderSchemaVersion_RejectsAsLegacyProjectWithoutChangingData()
    {
        var baseConn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (baseConn is null) { return; }

        var database = new SqlServerProjectDatabase(new SqlServerConnectionOptions(baseConn, SingleDb));
        var projectId = $"legacy-{Guid.NewGuid():N}";
        var q = SqlServerProjectSchema.QualifierFor(projectId);

        try
        {
            await database.EnsureCreatedAsync(projectId, CancellationToken.None);
            await ExecuteAsync(baseConn, $"ALTER TABLE {q}gl DROP COLUMN line_item_numeric_sort_key;");
            await ExecuteAsync(baseConn, $"UPDATE {q}schema_info SET [value] = '15' WHERE [key] = 'schema_version';");

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => database.EnsureCreatedAsync(projectId, CancellationToken.None));

            Assert.Equal(JetErrorCodes.InvalidProjectSchema, ex.Code);
            Assert.Equal(ProjectFolderMessages.OldVersionProject(), ex.Message);
            Assert.Equal("15", await VersionAsync(baseConn, q));
            Assert.Equal(0, await ColumnPresentAsync(baseConn, q, "gl", "line_item_numeric_sort_key"));
        }
        finally
        {
            await database.DeleteAsync(projectId, CancellationToken.None);
        }
    }

    // ---- 對 JET_Test 的最小 raw helper（直接下 schema-qualified SQL 驗形狀/資料/版本） ----

    private static string SingleDbConnectionString(string baseConnectionString) =>
        new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = SingleDb }.ConnectionString;

    private static async Task ExecuteAsync(string baseConnectionString, string sql)
    {
        await using var connection = new SqlConnection(SingleDbConnectionString(baseConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(string baseConnectionString, string sql)
    {
        await using var connection = new SqlConnection(SingleDbConnectionString(baseConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private static async Task<string> VersionAsync(string baseConnectionString, string qualifier)
    {
        await using var connection = new SqlConnection(SingleDbConnectionString(baseConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT [value] FROM {qualifier}schema_info WHERE [key] = 'schema_version';";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    // COL_LENGTH 回欄長度或 NULL：欄存在回 1、不存在回 0（qualifier 內含中括號，COL_LENGTH 接受）。
    private static Task<long> ColumnPresentAsync(string baseConnectionString, string qualifier, string table, string column) =>
        ScalarLongAsync(baseConnectionString,
            $"SELECT CASE WHEN COL_LENGTH('{qualifier}{table}', '{column}') IS NULL THEN 0 ELSE 1 END;");

    private static Task<long> TablePresentAsync(string baseConnectionString, string qualifier, string table) =>
        ScalarLongAsync(baseConnectionString,
            $"SELECT CASE WHEN OBJECT_ID(N'{qualifier}{table}', N'U') IS NULL THEN 0 ELSE 1 END;");
}
