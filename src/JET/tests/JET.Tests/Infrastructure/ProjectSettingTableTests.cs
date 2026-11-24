using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 案件資料庫的 project_setting 只有一列：建案時和資料表在同一個交易裡寫入；建案後不能改的欄位與刪除整列由觸發器擋下，
/// 期末財報準備日與每週休息日可以改。沒有這一列或記的值不合法時，讀取回資料庫損壞，不從設定檔補造。
/// </summary>
public sealed class ProjectSettingTableTests
{
    private static readonly ProtectedProjectSettings Settings = new(
        "2025-01-01", "2025-12-31", ProjectDocument.DefaultMoneyScale, RocDateEnabled: true,
        SampleSeed: 48_271, SampleSeedVersion: JET.AuditCore.JetAuditProgram.CurrentInfSamplingAlgorithmVersion,
        OperatorId: "合成建案者", LastAccountingPeriodDate: "2025-09-01", NonWorkingDays: [0, 5]);

    [Fact]
    public async Task CreatedCase_RecordsTheSettingsWithTheTables()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateAsync(root, withSettings: true);

        var read = await new LocalProjectSettingStore(database).ReadAsync(projectId, CancellationToken.None);

        Assert.Equal(Settings with { NonWorkingDays = null }, read with { NonWorkingDays = null });
        Assert.Equal(Settings.NonWorkingDays, read.NonWorkingDays);
    }

    [Theory]
    [InlineData("singleton", "2")]
    [InlineData("period_start", "'2024-01-01'")]
    [InlineData("period_end", "'2024-12-31'")]
    [InlineData("money_scale", "100")]
    [InlineData("roc_date_enabled", "0")]
    [InlineData("sample_seed", "7")]
    [InlineData("sample_seed_version", "1")]
    [InlineData("operator_id", "'另一位'")]
    [InlineData("period_start", "period_start")]
    public async Task FixedColumns_CannotBeUpdatedEvenToTheSameValue(string column, string value)
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateAsync(root, withSettings: true);

        var error = await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(database, projectId, $"UPDATE project_setting SET {column} = {value};"));
        Assert.Contains("cannot change", error.Message, StringComparison.Ordinal);

        var read = await new LocalProjectSettingStore(database).ReadAsync(projectId, CancellationToken.None);
        Assert.Equal(Settings.PeriodStart, read.PeriodStart);
        Assert.Equal(Settings.SampleSeed, read.SampleSeed);
    }

    [Fact]
    public async Task PreparationDateAndNonWorkingDays_CanBeUpdated()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateAsync(root, withSettings: true);

        await ExecuteAsync(database, projectId,
            "UPDATE project_setting SET last_accounting_period_date = NULL, non_working_days = '[]';");

        var read = await new LocalProjectSettingStore(database).ReadAsync(projectId, CancellationToken.None);
        Assert.Null(read.LastAccountingPeriodDate);
        Assert.Empty(read.NonWorkingDays!);
    }

    [Fact]
    public async Task TheSettingsRow_CannotBeDeletedOrDuplicated()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateAsync(root, withSettings: true);

        var deleted = await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(database, projectId, "DELETE FROM project_setting;"));
        Assert.Contains("cannot be deleted", deleted.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(database, projectId, InsertRow("2")));
    }

    [Fact]
    public async Task MissingSettingsRow_IsReportedAsACorruptDatabase()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateAsync(root, withSettings: false);

        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            new LocalProjectSettingStore(database).ReadAsync(projectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.DatabaseCorrupt, error.Code);
    }

    [Theory]
    [InlineData("period_start", "'2025/01/01'")]
    [InlineData("period_start", "'2026-01-01'")]
    [InlineData("money_scale", "0")]
    [InlineData("money_scale", "'many'")]
    [InlineData("sample_seed", "0")]
    [InlineData("sample_seed", "2147483647")]
    [InlineData("sample_seed_version", "99")]
    [InlineData("last_accounting_period_date", "'2025-13-01'")]
    [InlineData("non_working_days", "'[7]'")]
    [InlineData("non_working_days", "'not json'")]
    [InlineData("non_working_days", "'{}'")]
    public async Task InvalidRecordedValues_AreReportedAsACorruptDatabase(string column, string value)
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateAsync(root, withSettings: false);
        await ExecuteAsync(database, projectId, InsertRow("1"));
        // 準備日與休息日可以改；建案後不能改的欄位先移除觸發器再寫入，模擬資料庫內容損壞。
        await ExecuteAsync(database, projectId,
            "DROP TRIGGER trg_project_setting_fixed;" + $"UPDATE project_setting SET {column} = {value};");

        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            new LocalProjectSettingStore(database).ReadAsync(projectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.DatabaseCorrupt, error.Code);
    }

    private static async Task<(SqliteProjectDatabase Database, string ProjectId)> CreateAsync(
        TempProjectRoot root, bool withSettings)
    {
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        const string projectId = "protected-settings";
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        if (withSettings)
        {
            await database.EnsureCreatedAsync(projectId, Settings, CancellationToken.None);
        }
        else
        {
            await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        }

        return (database, projectId);
    }

    private static string InsertRow(string singleton) =>
        "INSERT INTO project_setting (singleton, period_start, period_end, money_scale, roc_date_enabled, sample_seed, "
        + "sample_seed_version, operator_id, last_accounting_period_date, non_working_days) VALUES ("
        + $"{singleton}, '2025-01-01', '2025-12-31', 10000, 1, 48271, "
        + $"{JET.AuditCore.JetAuditProgram.CurrentInfSamplingAlgorithmVersion}, '合成建案者', NULL, NULL);";

    private static async Task ExecuteAsync(SqliteProjectDatabase database, string projectId, string sql)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
