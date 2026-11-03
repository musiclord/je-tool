using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 驗證結果與預篩選結果各存一張表（validation_result、prescreen_result）以後，存放的行為要和原本一張表、
/// 以種類欄分開時相同：各自取最新一筆、同一時間以結果編號較大者為準、兩種結果互不看見，
/// 只清除上游真的變更的那一種，失效旗標只在有結果被清掉時立起，存入新結果時放下。資料全為合成。
/// 2026-10-30 拆表時新寫；拆表前的行為由 ResultStaleStateLifecycleTests 與 ResultLatestTieBreakProviderTests 鎖住，
/// 這裡把兩種結果放在一起核對它們不會互相干擾。
/// </summary>
public sealed class ResultStoreSeparationTests
{
    private static readonly DateTimeOffset Earlier = new(2025, 12, 31, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2025, 12, 31, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task ValidationAndPrescreenResults_AreStoredReadClearedAndInvalidatedSeparately(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        IResultStore store = new LocalResultStore(database);
        var stale = new LocalResultStaleStateStore(database);

        // 還沒有任何結果時，兩種都讀不到。
        Assert.Null(await store.FindLatestAsync(projectId, ResultKind.Validation, CancellationToken.None));
        Assert.Null(await store.FindLatestAsync(projectId, ResultKind.Prescreen, CancellationToken.None));

        // 預篩選的時間比驗證晚，也不會被當成最新的驗證結果。
        await store.SaveAsync(projectId, Record("v-old", ResultKind.Validation, Earlier), CancellationToken.None);
        await store.SaveAsync(projectId, Record("v-new", ResultKind.Validation, Later), CancellationToken.None);
        await store.SaveAsync(projectId, Record("p-a", ResultKind.Prescreen, Later), CancellationToken.None);
        await store.SaveAsync(projectId, Record("p-b", ResultKind.Prescreen, Later), CancellationToken.None);
        await store.SaveAsync(projectId, Record("p-later", ResultKind.Prescreen, Later.AddHours(1)), CancellationToken.None);

        var validation = Assert.IsType<ResultRecord>(
            await store.FindLatestAsync(projectId, ResultKind.Validation, CancellationToken.None));
        Assert.Equal("v-new", validation.ResultId);
        Assert.Equal(ResultKind.Validation, validation.Kind);
        Assert.Equal(Later, validation.GeneratedUtc);
        Assert.Equal("""{"id":"v-new"}""", validation.SummaryJson);
        var prescreen = Assert.IsType<ResultRecord>(
            await store.FindLatestAsync(projectId, ResultKind.Prescreen, CancellationToken.None));
        Assert.Equal("p-later", prescreen.ResultId);
        Assert.Equal(ResultKind.Prescreen, prescreen.Kind);
        Assert.Equal(2, await ScalarAsync(database.CreateConnection(projectId), "SELECT COUNT(*) FROM validation_result;"));
        Assert.Equal(3, await ScalarAsync(database.CreateConnection(projectId), "SELECT COUNT(*) FROM prescreen_result;"));

        // 同一時間的兩筆以結果編號較大的為準。
        await store.SaveAsync(projectId, Record("p-z", ResultKind.Prescreen, Later.AddHours(2)), CancellationToken.None);
        await store.SaveAsync(projectId, Record("p-y", ResultKind.Prescreen, Later.AddHours(2)), CancellationToken.None);
        Assert.Equal("p-z", (await store.FindLatestAsync(projectId, ResultKind.Prescreen, CancellationToken.None))!.ResultId);

        // 行事曆變更只清預篩選：驗證結果保留，只有預篩選標成過期。
        await ResetAsync(database.CreateConnection(projectId), AuditMutation.Calendar);
        Assert.Equal("v-new", (await store.FindLatestAsync(projectId, ResultKind.Validation, CancellationToken.None))!.ResultId);
        Assert.Null(await store.FindLatestAsync(projectId, ResultKind.Prescreen, CancellationToken.None));
        Assert.Equal(new AuditResultStaleState(false, true, false), await stale.ReadAsync(projectId, CancellationToken.None));

        // 存入新的預篩選結果時放下預篩選的過期旗標，驗證不受影響。
        await store.SaveAsync(projectId, Record("p-fresh", ResultKind.Prescreen, Later.AddHours(3)), CancellationToken.None);
        Assert.Equal(new AuditResultStaleState(false, false, false), await stale.ReadAsync(projectId, CancellationToken.None));
        Assert.Equal("p-fresh", (await store.FindLatestAsync(projectId, ResultKind.Prescreen, CancellationToken.None))!.ResultId);

        // GL 重新投影兩種都清，抽樣也一起清，兩種都標成過期。
        await SeedSampleAsync(database.CreateConnection(projectId), "v-new");
        await ResetAsync(database.CreateConnection(projectId), AuditMutation.GlProjection);
        Assert.Null(await store.FindLatestAsync(projectId, ResultKind.Validation, CancellationToken.None));
        Assert.Null(await store.FindLatestAsync(projectId, ResultKind.Prescreen, CancellationToken.None));
        Assert.Equal(0, await ScalarAsync(database.CreateConnection(projectId), "SELECT COUNT(*) FROM inf_sample;"));
        var afterGl = await stale.ReadAsync(projectId, CancellationToken.None);
        Assert.True(afterGl.Validation);
        Assert.True(afterGl.Prescreen);

        // 沒有結果可清時，再清一次不會把「從未執行」改成過期。
        await store.SaveAsync(projectId, Record("v-again", ResultKind.Validation, Later.AddHours(4)), CancellationToken.None);
        await ResetAsync(database.CreateConnection(projectId), AuditMutation.Calendar);
        var afterCalendar = await stale.ReadAsync(projectId, CancellationToken.None);
        Assert.False(afterCalendar.Validation);
        Assert.Equal("v-again", (await store.FindLatestAsync(projectId, ResultKind.Validation, CancellationToken.None))!.ResultId);
    }

    private static ResultRecord Record(string id, ResultKind kind, DateTimeOffset generatedUtc) =>
        new(id, kind, generatedUtc, $$"""{"id":"{{id}}"}""");

    private static async Task ResetAsync(DbConnection connection, AuditMutation mutation)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await ResultReset.ClearWithinAsync(connection, transaction, CancellationToken.None, mutation, string.Empty);
            await transaction.CommitAsync();
        }
    }

    private static async Task SeedSampleAsync(DbConnection connection, string resultId)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"INSERT INTO inf_sample (result_id, entry_id, voucher_number, line_item) VALUES ('{resultId}', 1, 'S1', '1');";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> ScalarAsync(DbConnection connection, string sql)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
    }
}
