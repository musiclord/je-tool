using System.Data;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class WorkingPaperStep41PreparedExportSessionTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task PreparedSession_UsesOneConnectionTransactionAndOrderedReader(
        string provider)
    {
        using var root = new TempProjectRoot();
        var database = CreateDatabase(provider, root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await SeedAsync(database, projectId);
        var factory = Assert.IsAssignableFrom<IWorkingPaperStep41PreparedSessionFactory>(
            new LocalHighRiskSummaryRowPageRepository(database));

        var session = await factory.PrepareAsync(
            projectId,
            new GlPopulationContext(
                GlPopulationScope.AuditPeriod,
                "2025-01-01",
                "2025-12-31"),
            [1, 2],
            LegacyFieldKind.Text,
            CancellationToken.None);
        var rows = new List<WorkingPaperStep41SourceRow>();
        await using (session)
        {
            await foreach (var row in session.ReadRowsAsync(CancellationToken.None))
            {
                rows.Add(row);
            }
        }

        Assert.Equal(["DOC-1", "DOC-1", "DOC-2"], rows.Select(row => row.VoucherNumber));
        Assert.Equal([1, 2], rows[0].MatchedPositions);
        Assert.Empty(rows[1].MatchedPositions);
        Assert.Equal([2], rows[2].MatchedPositions);
        Assert.Equal(1, session.Metrics.SchemaReadinessCommands);
        Assert.Equal(1, session.Metrics.Connections);
        Assert.Equal(1, session.Metrics.Transactions);
        Assert.Equal(1, session.Metrics.TemporaryTableInitializationCommands);
        Assert.Equal(1, session.Metrics.HitVoucherMaterializationCommands);
        Assert.Equal(1, session.Metrics.RowTagMaterializationCommands);
        Assert.Equal(0, session.Metrics.WidthAggregations);
        Assert.Equal(1, session.Metrics.OrderedReaderCommands);
        Assert.Equal(1, session.Metrics.CleanupCommands);
        Assert.Equal(3, session.Metrics.RowsRead);
        Assert.True(session.Metrics.ReaderCompleted);
        Assert.True(session.Metrics.CleanupCommandSucceeded);
        Assert.True(session.Metrics.TransactionCommitted);
        Assert.False(session.Metrics.TransactionRolledBack);
        Assert.True(session.Metrics.DedicatedConnection);
        Assert.True(session.Metrics.TransactionDisposed);
        Assert.True(session.Metrics.ConnectionDisposed);
        Assert.True(session.Metrics.Disposed);
        Assert.True(session.Metrics.TemporaryObjectsCleared);
        Assert.Equal(
            [
                "schemaReadiness",
                "initializeTemporaryTables",
                "materializeHitVouchers",
                "materializeRowTags",
                "orderedRows",
                "cleanup"
            ],
            session.Metrics.Commands.Select(command => command.Operation));
        Assert.All(
            session.Metrics.Commands
                .Where(command => command.Operation is
                    "materializeHitVouchers"
                    or "materializeRowTags"
                    or "orderedRows"),
            command =>
            {
                Assert.DoesNotContain("@periodStart", command.ParameterNames);
                Assert.DoesNotContain("@periodEnd", command.ParameterNames);
                Assert.Contains("is_excluded = 0", command.CommandText, StringComparison.Ordinal);
                Assert.DoesNotContain("2025-01-01", command.CommandText);
                Assert.DoesNotContain("2025-12-31", command.CommandText);
            });
        Assert.All(
            session.Metrics.Commands
                .Where(command => command.Operation is
                    "materializeHitVouchers"
                    or "materializeRowTags"),
            command =>
            {
                Assert.Contains("@scenarioPosition0", command.ParameterNames);
                Assert.Contains("@scenarioPosition1", command.ParameterNames);
            });
        Assert.DoesNotContain(
            session.Metrics.Commands,
            command => command.CommandText.Contains(
                "LIMIT",
                StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task PreparedSession_HitVoucherScopeMarksEveryRowWithoutExpandingOtherScenarios(
        string provider)
    {
        using var root = new TempProjectRoot();
        var database = CreateDatabase(provider, root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await SeedAsync(database, projectId);
        var factory = (IWorkingPaperStep41PreparedSessionFactory)
            new LocalHighRiskSummaryRowPageRepository(database);

        var session = await factory.PrepareAsync(
            projectId,
            new GlPopulationContext(
                GlPopulationScope.AuditPeriod,
                "2025-01-01",
                "2025-12-31"),
            [1, 2],
            LegacyFieldKind.Text,
            CancellationToken.None,
            hitVoucherScenarioPositions: [1]);
        var rows = new List<WorkingPaperStep41SourceRow>();
        await using (session)
        {
            await foreach (var row in session.ReadRowsAsync(CancellationToken.None))
            {
                rows.Add(row);
            }
        }

        Assert.Equal([1, 2], rows[0].MatchedPositions);
        Assert.Equal([1], rows[1].MatchedPositions);
        Assert.Equal([2], rows[2].MatchedPositions);
        var tagCommand = Assert.Single(
            session.Metrics.Commands,
            command => command.Operation == "materializeRowTags");
        Assert.Contains("@hitVoucherScenarioPosition0", tagCommand.ParameterNames);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task PreparedSession_CancellationDuringOrderedReader_CleansSessionState(
        string provider)
    {
        using var root = new TempProjectRoot();
        var database = CreateDatabase(provider, root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await SeedAsync(database, projectId);
        var factory = (IWorkingPaperStep41PreparedSessionFactory)
            new LocalHighRiskSummaryRowPageRepository(database);
        var session = await factory.PrepareAsync(
            projectId,
            new GlPopulationContext(
                GlPopulationScope.AuditPeriod,
                "2025-01-01",
                "2025-12-31"),
            [1, 2],
            LegacyFieldKind.Text,
            CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        await using (session)
        {
            await using var rows = session
                .ReadRowsAsync(cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);
            Assert.True(await rows.MoveNextAsync());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                _ = await rows.MoveNextAsync();
            });
        }

        Assert.Equal(1, session.Metrics.OrderedReaderCommands);
        Assert.Equal(1, session.Metrics.CleanupCommands);
        Assert.False(session.Metrics.ReaderCompleted);
        Assert.True(session.Metrics.CleanupCommandSucceeded);
        Assert.False(session.Metrics.TransactionCommitted);
        Assert.True(session.Metrics.TransactionRolledBack);
        Assert.True(session.Metrics.DedicatedConnection);
        Assert.True(session.Metrics.TransactionDisposed);
        Assert.True(session.Metrics.ConnectionDisposed);
        Assert.True(session.Metrics.Disposed);
        Assert.True(session.Metrics.TemporaryObjectsCleared);
    }

    [Fact]
    public async Task PreparedSession_CancellationInsideMaterialization_DisposesDedicatedConnection()
    {
        using var root = new TempProjectRoot();
        var database = new SqliteProjectDatabase(new JetProjectFolder(root.Path));
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await SeedAsync(database, projectId);
        await using (var setup = database.CreateConnection(projectId))
        {
            await setup.OpenAsync();
            await using var command = setup.CreateCommand();
            command.CommandText =
                """
                ALTER TABLE filter_hit RENAME TO filter_hit_source;
                CREATE VIEW filter_hit AS
                SELECT scenario_id, entry_id
                FROM filter_hit_source
                WHERE cancel_step41_prepare(entry_id) = entry_id;
                """;
            await command.ExecuteNonQueryAsync();
        }

        string connectionString;
        // 2026-10-19 起 SQLite 連線是可取消的 CancellableSqliteConnection（跨資料庫一致性第二階段）；
        // 原本斷言 SqliteConnection 的確切型別，改成斷言現行的確切型別。第一次失敗收據：
        // 20261019-055334911-de734c3f428a499d8256a175e93e8bbb。
        using (var pooled = Assert.IsType<CancellableSqliteConnection>(
                   database.CreateConnection(projectId)))
        {
            connectionString = new SqliteConnectionStringBuilder(
                pooled.ConnectionString)
            {
                Pooling = false
            }.ConnectionString;
        }
        await using var connection = new SqliteConnection(connectionString);
        using var cancellation = new CancellationTokenSource();
        connection.CreateFunction<long, long>(
            "cancel_step41_prepare",
            value =>
            {
                cancellation.Cancel();
                return value;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WorkingPaperStep41PreparedSession.CreateAsync(
                connection,
                SqliteDialect.Instance,
                schemaPrefix: string.Empty,
                new GlPopulationContext(
                    GlPopulationScope.AuditPeriod,
                    "2025-01-01",
                    "2025-12-31"),
                [1, 2],
                LegacyFieldKind.Text,
                dedicatedConnection: true,
                cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.False(new SqliteConnectionStringBuilder(connectionString).Pooling);
    }

    /// <remarks>
    /// 2026-10-02 資料庫分流簡化：分流層連同它的 typed seam 轉接與 ProviderResolutions 指標一起刪除，
    /// handler 直接拿到該種資料庫組裡的 repository。這個測試原本經分流層準備 step4-1 session，
    /// 改成直接用 DuckDB 的 repository 準備，並拿掉「解析 provider 恰好一次」的指標斷言（指標隨分流層一起刪除）；
    /// 其餘連線、讀取、清理與交易斷言保留不變。第一次失敗收據（測試引用已刪除的分流類別而無法建置）：
    /// 20261002-120244042-2aa5edaa431d4f3e89e068361d5482f5。
    /// </remarks>
    [Fact]
    public async Task PreparedSession_DirectRepository_UsesOneConnectionAndOneReader()
    {
        using var root = new TempProjectRoot();
        var database = new DuckDbProjectDatabase(new JetProjectFolder(root.Path));
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await SeedAsync(database, projectId);
        var repository = new LocalHighRiskSummaryRowPageRepository(database);
        var factory = (IWorkingPaperStep41PreparedSessionFactory)repository;
        var session = await factory.PrepareAsync(
            projectId,
            new GlPopulationContext(
                GlPopulationScope.AuditPeriod,
                "2025-01-01",
                "2025-12-31"),
            [1, 2],
            LegacyFieldKind.Text,
            CancellationToken.None);

        await using (session)
        {
            await foreach (var _ in session.ReadRowsAsync(CancellationToken.None))
            {
            }
        }

        Assert.Equal("duckdb", session.Metrics.Provider);
        Assert.Equal(1, session.Metrics.Connections);
        Assert.Equal(1, session.Metrics.OrderedReaderCommands);
        Assert.True(session.Metrics.CleanupCommandSucceeded);
        Assert.True(session.Metrics.TransactionCommitted);
        Assert.True(session.Metrics.DedicatedConnection);
    }

    [SqlServerFact]
    public async Task PreparedSession_SqlServer_UsesConstantShapeAndCleansTempObjects()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());
        await SeedSqlServerAsync(project.Database, project.ProjectId);
        var factory = (IWorkingPaperStep41PreparedSessionFactory)
            new SqlServerHighRiskSummaryRowPageRepository(project.Database);
        var session = await factory.PrepareAsync(
            project.ProjectId,
            new GlPopulationContext(
                GlPopulationScope.AuditPeriod,
                "2025-01-01",
                "2025-12-31"),
            [1, 2],
            LegacyFieldKind.Text,
            CancellationToken.None);
        var rows = new List<WorkingPaperStep41SourceRow>();

        await using (session)
        {
            await foreach (var row in session.ReadRowsAsync(CancellationToken.None))
            {
                rows.Add(row);
            }
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal("sqlServer", session.Metrics.Provider);
        Assert.Equal(1, session.Metrics.Connections);
        Assert.Equal(1, session.Metrics.Transactions);
        Assert.Equal(1, session.Metrics.TemporaryTableInitializationCommands);
        Assert.Equal(1, session.Metrics.HitVoucherMaterializationCommands);
        Assert.Equal(1, session.Metrics.RowTagMaterializationCommands);
        Assert.Equal(1, session.Metrics.OrderedReaderCommands);
        Assert.Equal(1, session.Metrics.CleanupCommands);
        Assert.Equal(
            IsolationLevel.Serializable.ToString(),
            session.Metrics.TransactionIsolationLevel);
        Assert.Equal(
            "serializable",
            session.Metrics.ConsistencyMode);
        Assert.True(session.Metrics.ReaderCompleted);
        Assert.True(session.Metrics.CleanupCommandSucceeded);
        Assert.True(session.Metrics.TransactionCommitted);
        Assert.False(session.Metrics.TransactionRolledBack);
        Assert.True(session.Metrics.DedicatedConnection);
        Assert.True(session.Metrics.TransactionDisposed);
        Assert.True(session.Metrics.ConnectionDisposed);
        Assert.True(session.Metrics.TemporaryObjectsCleared);
        await AssertSqlServerNoTempObjectsAsync(
            project.Database,
            project.ProjectId);
    }

    private static ILocalProjectDatabase CreateDatabase(string provider, string root) =>
        provider switch
        {
            "sqlite" => new SqliteProjectDatabase(new JetProjectFolder(root)),
            "duckdb" => new DuckDbProjectDatabase(new JetProjectFolder(root)),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };

    private static async Task SeedAsync(ILocalProjectDatabase database, string projectId)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO pbc_gl
                (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES
                ('b', 1, 1, 1, '{"token":"first"}'),
                ('b', 2, 1, 2, '{"token":"second"}'),
                ('b', 3, 1, 3, '{"token":"third"}');

            INSERT INTO gl
                (batch_id, source_row_number, voucher_number, line_item,
                 gl_date, account_number, account_name, description,
                 is_excluded, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
            VALUES
                ('b', 1, 'DOC-1', '1', '2025-06-01', '1000', 'Cash', 'first',
                 0, 10000, 10000, 0, 'DEBIT'),
                ('b', 2, 'DOC-1', '2', '2025-06-01', '1000', 'Cash', 'second',
                 0, 20000, 20000, 0, 'DEBIT'),
                ('b', 3, 'DOC-2', '1', '2025-06-01', '1000', 'Cash', 'third',
                 0, 30000, 30000, 0, 'DEBIT');

            INSERT INTO filter_scenario (scenario_id, position, name, rationale, definition_json, saved_utc) VALUES (1, 1, 'synthetic', 'synthetic', '{}', '2026-10-31T00:00:00Z'), (2, 2, 'synthetic', 'synthetic', '{}', '2026-10-31T00:00:00Z');
            INSERT INTO filter_hit (scenario_id, entry_id)
            SELECT 1, entry_id FROM gl WHERE source_row_number = 1;
            INSERT INTO filter_hit (scenario_id, entry_id)
            SELECT 2, entry_id FROM gl WHERE source_row_number IN (1, 3);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedSqlServerAsync(
        SqlServerProjectDatabase database,
        string projectId)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = database.CreateCommand(
            connection,
            projectId,
            """
            INSERT INTO {s}.pbc_gl
                (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES
                ('b', 1, 1, 1, N'{"token":"first"}'),
                ('b', 2, 1, 2, N'{"token":"second"}'),
                ('b', 3, 1, 3, N'{"token":"third"}');

            INSERT INTO {s}.gl
                (batch_id, source_row_number, voucher_number, line_item,
                 gl_date, account_number, account_name, description,
                 is_excluded, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
            VALUES
                ('b', 1, 'DOC-1', '1', '2025-06-01', '1000', 'Cash', 'first',
                 0, 10000, 10000, 0, 'DEBIT'),
                ('b', 2, 'DOC-1', '2', '2025-06-01', '1000', 'Cash', 'second',
                 0, 20000, 20000, 0, 'DEBIT'),
                ('b', 3, 'DOC-2', '1', '2025-06-01', '1000', 'Cash', 'third',
                 0, 30000, 30000, 0, 'DEBIT');

            INSERT INTO {s}.filter_scenario (scenario_id, position, name, rationale, definition_json, saved_utc) VALUES (1, 1, 'synthetic', 'synthetic', '{}', '2026-10-31T00:00:00Z'), (2, 2, 'synthetic', 'synthetic', '{}', '2026-10-31T00:00:00Z');
            INSERT INTO {s}.filter_hit (scenario_id, entry_id)
            SELECT 1, entry_id FROM {s}.gl WHERE source_row_number = 1;
            INSERT INTO {s}.filter_hit (scenario_id, entry_id)
            SELECT 2, entry_id FROM {s}.gl WHERE source_row_number IN (1, 3);
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertSqlServerNoTempObjectsAsync(
        SqlServerProjectDatabase database,
        string projectId)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CASE
                WHEN OBJECT_ID('tempdb..#jet_step41_hit_voucher') IS NULL
                 AND OBJECT_ID('tempdb..#jet_step41_row_tag') IS NULL
                THEN 0 ELSE 1 END;
            """;
        Assert.Equal(
            0,
            Convert.ToInt32(await command.ExecuteScalarAsync()));
    }
}
