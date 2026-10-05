using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

// 第 9 批中低 14：改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Infrastructure;

/// <summary>
/// 三 provider 共用的有效母體 projection oracle。全部資料為程式內合成，驗證 raw 1,000 列
/// 的互斥 900／40／60 分割、period-first、trim／case／multi-value／blank 及控制總數。
/// </summary>
public sealed class GlEffectivePopulationProjectionProviderTests
{
    private static readonly DateOnly PeriodStart = new(2025, 1, 1);
    private static readonly DateOnly PeriodEnd = new(2025, 12, 31);
    private static readonly GlPostingStatusPolicy Policy = new(
        ["posted", "approved"],
        IncludeBlank: true);

    [Fact]
    public async Task Sqlite_Projection_MatchesEffectivePopulationOracle()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);

        await AssertLocalOracleAsync(database, folder);
    }

    [Fact]
    public async Task DuckDb_Projection_MatchesEffectivePopulationOracle()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new DuckDbProjectDatabase(folder);

        await AssertLocalOracleAsync(database, folder);
    }

    [SqlServerFact]
    public async Task SqlServer_Projection_MatchesEffectivePopulationOracle()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());
        var batch = (await new SqlServerImportRepository(project.Database).ReplaceBatchAsync(
            project.ProjectId,
            DatasetKind.Gl,
            [new ImportSourceInput(Source(),
            Columns(),
            Rows())],
            CancellationToken.None)).Batch;

        var repository = new SqlServerGlRepository(project.Database);
        var result = await repository.ProjectStagingToTargetAsync(
            project.ProjectId,
            batch.BatchId,
            Spec(),
            ProjectDocument.DefaultMoneyScale,
            DateParseOptions.Default,
            PeriodStart,
            PeriodEnd,
            postingStatusMapped: true,
            Policy,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        await AssertResultAsync(result, project.Database.CreateConnection(project.ProjectId),
            SqlServerProjectSchema.QualifierFor(project.ProjectId));
        await AssertZeroEffectiveRollbackAsync(
            repository,
            new SqlServerMappingStateStore(project.Database),
            project.ProjectId,
            batch.BatchId,
            () => project.Database.CreateConnection(project.ProjectId),
            SqlServerProjectSchema.QualifierFor(project.ProjectId));
    }

    [Fact]
    public async Task Sqlite_ControlTotalOverflow_RollsBackProjection()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        await AssertLocalControlTotalOverflowAsync(new SqliteProjectDatabase(folder), folder);
    }

    [Fact]
    public async Task DuckDb_ControlTotalOverflow_RollsBackProjection()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        await AssertLocalControlTotalOverflowAsync(new DuckDbProjectDatabase(folder), folder);
    }

    [SqlServerFact]
    public async Task SqlServer_ControlTotalOverflow_RollsBackProjection()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());
        var batch = (await new SqlServerImportRepository(project.Database).ReplaceBatchAsync(
            project.ProjectId,
            DatasetKind.Gl,
            [new ImportSourceInput(Source(),
            Columns(),
            OverflowRows())],
            CancellationToken.None)).Batch;

        var result = await new SqlServerGlRepository(project.Database).ProjectStagingToTargetAsync(
            project.ProjectId,
            batch.BatchId,
            Spec(),
            ProjectDocument.DefaultMoneyScale,
            DateParseOptions.Default,
            PeriodStart,
            PeriodEnd,
            postingStatusMapped: true,
            Policy,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        await AssertControlTotalOverflowResultAsync(
            result,
            project.Database.CreateConnection(project.ProjectId),
            SqlServerProjectSchema.QualifierFor(project.ProjectId));
        Assert.Null(await new SqlServerMappingStateStore(project.Database).FindAsync(
            project.ProjectId,
            DatasetKind.Gl,
            CancellationToken.None));
    }

    private static async Task AssertLocalOracleAsync(
        ILocalProjectDatabase database,
        JetProjectFolder folder)
    {
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var batch = (await new LocalImportRepository(database).ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            [new ImportSourceInput(Source(),
            Columns(),
            Rows())],
            CancellationToken.None)).Batch;

        var repository = new LocalGlRepository(database);
        var result = await repository.ProjectStagingToTargetAsync(
            projectId,
            batch.BatchId,
            Spec(),
            ProjectDocument.DefaultMoneyScale,
            DateParseOptions.Default,
            PeriodStart,
            PeriodEnd,
            postingStatusMapped: true,
            Policy,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        await AssertResultAsync(result, database.CreateConnection(projectId), string.Empty);
        await AssertZeroEffectiveRollbackAsync(
            repository,
            new LocalMappingStateStore(database),
            projectId,
            batch.BatchId,
            () => database.CreateConnection(projectId),
            string.Empty);
    }

    private static async Task AssertLocalControlTotalOverflowAsync(
        ILocalProjectDatabase database,
        JetProjectFolder folder)
    {
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var batch = (await new LocalImportRepository(database).ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            [new ImportSourceInput(Source(),
            Columns(),
            OverflowRows())],
            CancellationToken.None)).Batch;

        var result = await new LocalGlRepository(database).ProjectStagingToTargetAsync(
            projectId,
            batch.BatchId,
            Spec(),
            ProjectDocument.DefaultMoneyScale,
            DateParseOptions.Default,
            PeriodStart,
            PeriodEnd,
            postingStatusMapped: true,
            Policy,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        await AssertControlTotalOverflowResultAsync(result, database.CreateConnection(projectId), string.Empty);
        Assert.Null(await new LocalMappingStateStore(database).FindAsync(
            projectId,
            DatasetKind.Gl,
            CancellationToken.None));
    }

    private static async Task AssertControlTotalOverflowResultAsync(
        ProjectionResult result,
        DbConnection connection,
        string qualifier)
    {
        Assert.Equal(0, result.ProjectedRowCount);
        var error = Assert.Single(result.Errors);
        Assert.Equal(3, error.SourceRowNumber);
        Assert.Equal("amount", error.Field);
        Assert.Equal("金額加總後超過系統可保存的範圍。請確認金額是否正確，或調整案件的金額小數位數", error.Reason);
        Assert.Null(result.EffectivePopulation);

        await using (connection)
        {
            await connection.OpenAsync();
            var target = string.IsNullOrEmpty(qualifier)
                ? "target_gl_entry"
                : $"{qualifier}target_gl_entry";
            var control = string.IsNullOrEmpty(qualifier)
                ? "gl_control_total"
                : $"{qualifier}gl_control_total";
            Assert.Equal(0, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {target};"));
            Assert.Equal(0, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {control};"));
        }
    }

    private static async Task AssertZeroEffectiveRollbackAsync(
        IGlRepository repository,
        IMappingStateStore mappingStore,
        string projectId,
        string batchId,
        Func<DbConnection> connectionFactory,
        string qualifier)
    {
        await AssertCommittedPolicyAsync(mappingStore, projectId, batchId, Policy);
        await SeedRuleRunSentinelAsync(connectionFactory(), qualifier);

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            repository.ProjectStagingToTargetAsync(
                projectId,
                batchId,
                Spec(),
                ProjectDocument.DefaultMoneyScale,
                DateParseOptions.Default,
                PeriodStart,
                PeriodEnd,
                postingStatusMapped: true,
                new GlPostingStatusPolicy(["never-accepted"], IncludeBlank: false),
                DateTimeOffset.UnixEpoch,
                CancellationToken.None));
        Assert.Equal(JetErrorCodes.EmptyEffectivePopulation, exception.Code);

        await using (var connection = connectionFactory())
        {
            await connection.OpenAsync();
            var target = string.IsNullOrEmpty(qualifier)
                ? "target_gl_entry"
                : $"{qualifier}target_gl_entry";
            var control = string.IsNullOrEmpty(qualifier)
                ? "gl_control_total"
                : $"{qualifier}gl_control_total";
            Assert.Equal(1_000, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {target};"));
            Assert.Equal(900, await ScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM {target} WHERE is_effective = 1;"));
            Assert.Equal(40, await ScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM {target} WHERE exclusion_reason = 'period';"));
            Assert.Equal(60, await ScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM {target} WHERE exclusion_reason = 'posting_status';"));
            Assert.Equal(0, await ScalarAsync(
                connection,
                $"""
                SELECT COUNT(*) FROM {target}
                WHERE (is_effective = 1 AND exclusion_reason IS NOT NULL)
                   OR (is_effective = 0 AND exclusion_reason IS NULL)
                   OR (is_effective = 0 AND exclusion_reason NOT IN ('period', 'posting_status'));
                """));
            Assert.Equal(1_000, await ScalarAsync(
                connection,
                $"SELECT raw_row_count FROM {control} WHERE singleton = 1;"));
            Assert.Equal(900, await ScalarAsync(
                connection,
                $"SELECT effective_row_count FROM {control} WHERE singleton = 1;"));
            Assert.Equal(40, await ScalarAsync(
                connection,
                $"SELECT excluded_by_period_count FROM {control} WHERE singleton = 1;"));
            Assert.Equal(60, await ScalarAsync(
                connection,
                $"SELECT excluded_by_posting_status_count FROM {control} WHERE singleton = 1;"));
            Assert.Equal(450_000_000, await ScalarAsync(
                connection,
                $"SELECT effective_debit_scaled FROM {control} WHERE singleton = 1;"));
            Assert.Equal(450_000_000, await ScalarAsync(
                connection,
                $"SELECT effective_credit_scaled FROM {control} WHERE singleton = 1;"));

            var resultRuleRun = string.IsNullOrEmpty(qualifier)
                ? "result_rule_run"
                : $"{qualifier}result_rule_run";
            Assert.Equal(1, await ScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM {resultRuleRun} WHERE run_id = 'rollback-sentinel';"));
        }

        await AssertCommittedPolicyAsync(mappingStore, projectId, batchId, Policy);

        var retry = await repository.ProjectStagingToTargetAsync(
            projectId,
            batchId,
            Spec(),
            ProjectDocument.DefaultMoneyScale,
            DateParseOptions.Default,
            PeriodStart,
            PeriodEnd,
            postingStatusMapped: true,
            Policy,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);
        await AssertResultAsync(retry, connectionFactory(), qualifier);
        await AssertCommittedPolicyAsync(mappingStore, projectId, batchId, Policy);
    }

    private static async Task SeedRuleRunSentinelAsync(DbConnection connection, string qualifier)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            var resultRuleRun = string.IsNullOrEmpty(qualifier)
                ? "result_rule_run"
                : $"{qualifier}result_rule_run";
            await using var command = connection.CreateCommand();
            command.CommandText =
                $$"""
                INSERT INTO {{resultRuleRun}} (run_id, run_kind, generated_utc, summary_json)
                VALUES ('rollback-sentinel', 'validate', '1970-01-01T00:00:00.0000000+00:00', '{}');
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task AssertCommittedPolicyAsync(
        IMappingStateStore mappingStore,
        string projectId,
        string batchId,
        GlPostingStatusPolicy expected)
    {
        var committed = Assert.IsType<CommittedMapping>(
            await mappingStore.FindAsync(projectId, DatasetKind.Gl, CancellationToken.None));
        Assert.Equal(batchId, committed.SourceBatchId);
        Assert.Equal(DateTimeOffset.UnixEpoch, committed.CommittedUtc);
        var policy = Assert.IsType<GlPostingStatusPolicy>(committed.GlOptions?.PostingStatusPolicy);
        Assert.Equal(expected.AcceptedValues, policy.AcceptedValues);
        Assert.Equal(expected.IncludeBlank, policy.IncludeBlank);
    }

    private static async Task AssertResultAsync(
        ProjectionResult result,
        DbConnection connection,
        string qualifier)
    {
        Assert.Equal(1_000, result.ProjectedRowCount);
        Assert.Equal(
            new GlEffectivePopulationTotals(
                RawRowCount: 1_000,
                EffectiveRowCount: 900,
                ExcludedByPeriodCount: 40,
                ExcludedByPostingStatusCount: 60,
                EffectiveDebitScaled: 450_000_000,
                EffectiveCreditScaled: 450_000_000),
            result.EffectivePopulation);

        await using (connection)
        {
            await connection.OpenAsync();
            var target = string.IsNullOrEmpty(qualifier)
                ? "target_gl_entry"
                : $"{qualifier}target_gl_entry";
            var control = string.IsNullOrEmpty(qualifier)
                ? "gl_control_total"
                : $"{qualifier}gl_control_total";

            Assert.Equal(1_000, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {target};"));
            Assert.Equal(900, await ScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM {target} WHERE is_effective = 1;"));
            Assert.Equal(40, await ScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM {target} WHERE exclusion_reason = 'period';"));
            Assert.Equal(60, await ScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM {target} WHERE exclusion_reason = 'posting_status';"));
            Assert.Equal(20, await ScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM {target} WHERE post_date IS NULL AND exclusion_reason = 'period';"));
            Assert.Equal(20, await ScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM {target} WHERE post_date = '2026-01-01' AND exclusion_reason = 'period';"));

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT raw_row_count, effective_row_count, excluded_by_period_count,
                       excluded_by_posting_status_count, effective_debit_scaled,
                       effective_credit_scaled
                FROM {control}
                WHERE singleton = 1;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1_000, reader.GetInt64(0));
            Assert.Equal(900, reader.GetInt64(1));
            Assert.Equal(40, reader.GetInt64(2));
            Assert.Equal(60, reader.GetInt64(3));
            Assert.Equal(450_000_000, reader.GetInt64(4));
            Assert.Equal(450_000_000, reader.GetInt64(5));
            Assert.Equal(reader.GetInt64(0), reader.GetInt64(1) + reader.GetInt64(2) + reader.GetInt64(3));
        }
    }

    private static async Task<long> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static IReadOnlyList<string> Columns() =>
        ["doc", "post", "account", "accountName", "description", "amount", "status"];

    private static GlMappingSpec Spec() => new(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.DocNum] = "doc",
            [GlMappingKeys.PostDate] = "post",
            [GlMappingKeys.AccNum] = "account",
            [GlMappingKeys.AccName] = "accountName",
            [GlMappingKeys.Description] = "description",
            [GlMappingKeys.Amount] = "amount",
            [GlMappingKeys.PostingStatus] = "status"
        },
        GlAmountMode.SignedAmount);

    private static ImportSourceDescriptor Source() => new(
        @"C:\synthetic-effective-population.xlsx",
        "synthetic-effective-population.xlsx",
        null,
        null,
        null);

    private static async IAsyncEnumerable<StagingRow> Rows()
    {
        for (var index = 0; index < 1_000; index++)
        {
            var postDate = index switch
            {
                >= 980 => string.Empty,
                >= 960 => "2026-01-01",
                _ => "2025-06-30"
            };
            var status = index switch
            {
                < 850 when index % 2 == 0 => " POSTED ",
                < 850 => "aPpRoVeD",
                < 900 => " \t ",
                _ => "VOID"
            };
            var amount = index % 2 == 0 ? "100" : "-100";
            yield return new StagingRow(
                index + 2,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["doc"] = $"JE-{index:0000}",
                    ["post"] = postDate,
                    ["account"] = index % 2 == 0 ? "1000" : "2000",
                    ["accountName"] = index % 2 == 0 ? "Debit" : "Credit",
                    ["description"] = "synthetic",
                    ["amount"] = amount,
                    ["status"] = status
                });
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<StagingRow> OverflowRows()
    {
        for (var index = 0; index < 2; index++)
        {
            yield return new StagingRow(
                index + 2,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["doc"] = $"OVERFLOW-{index + 1}",
                    ["post"] = "2025-06-30",
                    ["account"] = "1000",
                    ["accountName"] = "Debit",
                    ["description"] = "synthetic overflow guard",
                    ["amount"] = "922337203685477.5807",
                    ["status"] = "POSTED"
                });
        }

        await Task.CompletedTask;
    }
}
