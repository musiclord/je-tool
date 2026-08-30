using System.Data.Common;
using System.Globalization;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// filter.commit 的 provider transaction 邊界：definitions 與 materialized hits 必須在同一
/// transaction 發布。故障於第一筆新 hit INSERT 已執行、transaction 尚未 commit 時注入，
/// 證明 SQLite、DuckDB、SQL Server 都會精確保留前一 revision，且同 payload 可安全重試。
/// </summary>
public sealed class FilterCommitRepositoryTransactionTests
{
    private static readonly DateTimeOffset PreviousSavedUtc =
        new(2026, 7, 31, 1, 2, 3, TimeSpan.Zero);

    private static readonly DateTimeOffset FailedSavedUtc =
        new(2026, 8, 1, 4, 5, 6, TimeSpan.Zero);

    private static readonly DateTimeOffset RetrySavedUtc =
        new(2026, 8, 1, 7, 8, 9, TimeSpan.Zero);

    private static readonly IReadOnlyList<HitPair> PreviousHits =
    [
        new(7, 700001),
        new(8, 800001),
        new(8, 800002)
    ];

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task Commit_LocalProvider_FaultAfterFirstNewHitInsert_RestoresPreviousRevisionAndRetrySucceeds(
        string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        var injected = new InjectedFilterCommitException("injected after first new filter hit insert");
        var fault = new FaultAfterFirstHitInsertLogger<LocalFilterCommitRepository>(injected);
        var repository = new LocalFilterCommitRepository(database, fault);
        var scenarioStore = new LocalFilterScenarioStore(database);

        await AssertRollbackAndRetryAsync(
            projectId,
            repository,
            scenarioStore,
            database.CreateConnection,
            schemaPrefix: string.Empty,
            fault: fault);
    }

    [SqlServerFact]
    public async Task Commit_SqlServer_FaultAfterFirstNewHitInsert_RestoresPreviousRevisionAndRetrySucceeds()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());
        var injected = new InjectedFilterCommitException("injected after first new filter hit insert");
        var fault = new FaultAfterFirstHitInsertLogger<SqlServerFilterCommitRepository>(injected);
        var repository = new SqlServerFilterCommitRepository(project.Database, fault);
        var scenarioStore = new SqlServerFilterScenarioStore(project.Database);

        await AssertRollbackAndRetryAsync(
            project.ProjectId,
            repository,
            scenarioStore,
            project.Database.CreateConnection,
            SqlServerProjectSchema.QualifierFor(project.ProjectId),
            fault);
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task Commit_LocalProvider_CancelAfterFirstNewHitInsert_RestoresPreviousRevisionAndRetrySucceeds(
        string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        var fault = new CancelAfterFirstHitInsertLogger<LocalFilterCommitRepository>(cancellation);
        var repository = new LocalFilterCommitRepository(database, fault);
        await AssertCancellationRollbackAndRetryAsync(
            projectId,
            repository,
            new LocalFilterScenarioStore(database),
            database.CreateConnection,
            schemaPrefix: string.Empty,
            cancellation,
            fault);
    }

    [SqlServerFact]
    public async Task Commit_SqlServer_CancelAfterFirstNewHitInsert_RestoresPreviousRevisionAndRetrySucceeds()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());
        using var cancellation = new CancellationTokenSource();
        var fault = new CancelAfterFirstHitInsertLogger<SqlServerFilterCommitRepository>(cancellation);
        var repository = new SqlServerFilterCommitRepository(project.Database, fault);
        await AssertCancellationRollbackAndRetryAsync(
            project.ProjectId,
            repository,
            new SqlServerFilterScenarioStore(project.Database),
            project.Database.CreateConnection,
            SqlServerProjectSchema.QualifierFor(project.ProjectId),
            cancellation,
            fault);
    }

    private static async Task AssertRollbackAndRetryAsync(
        string projectId,
        IFilterCommitRepository repository,
        IFilterScenarioStore scenarioStore,
        Func<string, DbConnection> connectionFactory,
        string schemaPrefix,
        IOneShotFilterCommitFault fault)
    {
        var previousDefinitions = Definitions(
            PreviousSavedUtc,
            firstPosition: 7,
            namePrefix: "previous");
        await scenarioStore.ReplaceAllAsync(
            projectId,
            previousDefinitions,
            CancellationToken.None);
        await SeedPopulationAndPreviousHitsAsync(
            connectionFactory(projectId),
            schemaPrefix);
        await SetFilterStaleAsync(connectionFactory(projectId), schemaPrefix);
        var expectedNewHits = await ReadExpectedNewHitsAsync(
            connectionFactory(projectId),
            schemaPrefix);

        var failedDefinitions = Definitions(
            FailedSavedUtc,
            firstPosition: 1,
            namePrefix: "failed");
        var exception = await Assert.ThrowsAsync<InjectedFilterCommitException>(() =>
            repository.CommitAsync(
                projectId,
                CommitItems(failedDefinitions),
                Context(),
                CancellationToken.None));

        Assert.Same(fault.InjectedException, exception);
        Assert.True(fault.FaultInjected);
        Assert.Equal(
            previousDefinitions.ToArray(),
            (await scenarioStore.ListAsync(projectId, CancellationToken.None)).ToArray());
        Assert.Equal(
            PreviousHits.ToArray(),
            (await ReadHitsAsync(connectionFactory(projectId), schemaPrefix)).ToArray());
        Assert.True(await ReadFilterStaleAsync(connectionFactory(projectId), schemaPrefix));

        fault.Disable();
        var retryDefinitions = Definitions(
            RetrySavedUtc,
            firstPosition: 1,
            namePrefix: "retry");
        await repository.CommitAsync(
            projectId,
            CommitItems(retryDefinitions),
            Context(),
            CancellationToken.None);

        Assert.Equal(
            retryDefinitions.ToArray(),
            (await scenarioStore.ListAsync(projectId, CancellationToken.None)).ToArray());
        Assert.Equal(
            expectedNewHits.ToArray(),
            (await ReadHitsAsync(connectionFactory(projectId), schemaPrefix)).ToArray());
        Assert.False(await ReadFilterStaleAsync(connectionFactory(projectId), schemaPrefix));

        await repository.CommitAsync(
            projectId,
            [],
            Context(),
            CancellationToken.None);
        Assert.Empty(await scenarioStore.ListAsync(projectId, CancellationToken.None));
        Assert.Empty(await ReadHitsAsync(connectionFactory(projectId), schemaPrefix));
    }

    private static async Task AssertCancellationRollbackAndRetryAsync(
        string projectId,
        IFilterCommitRepository repository,
        IFilterScenarioStore scenarioStore,
        Func<string, DbConnection> connectionFactory,
        string schemaPrefix,
        CancellationTokenSource cancellation,
        IOneShotCancellationFault fault)
    {
        var previousDefinitions = Definitions(
            PreviousSavedUtc,
            firstPosition: 7,
            namePrefix: "previous-cancel");
        await scenarioStore.ReplaceAllAsync(
            projectId,
            previousDefinitions,
            CancellationToken.None);
        await SeedPopulationAndPreviousHitsAsync(
            connectionFactory(projectId),
            schemaPrefix);
        await SetFilterStaleAsync(connectionFactory(projectId), schemaPrefix);
        var expectedNewHits = await ReadExpectedNewHitsAsync(
            connectionFactory(projectId),
            schemaPrefix);

        var cancelledDefinitions = Definitions(
            FailedSavedUtc,
            firstPosition: 1,
            namePrefix: "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.CommitAsync(
                projectId,
                CommitItems(cancelledDefinitions),
                Context(),
                cancellation.Token));

        Assert.True(fault.FaultInjected);
        Assert.Equal(
            previousDefinitions.ToArray(),
            (await scenarioStore.ListAsync(projectId, CancellationToken.None)).ToArray());
        Assert.Equal(
            PreviousHits.ToArray(),
            (await ReadHitsAsync(connectionFactory(projectId), schemaPrefix)).ToArray());
        Assert.True(await ReadFilterStaleAsync(connectionFactory(projectId), schemaPrefix));

        var retryDefinitions = Definitions(
            RetrySavedUtc,
            firstPosition: 1,
            namePrefix: "cancel-retry");
        await repository.CommitAsync(
            projectId,
            CommitItems(retryDefinitions),
            Context(),
            CancellationToken.None);

        Assert.Equal(
            retryDefinitions.ToArray(),
            (await scenarioStore.ListAsync(projectId, CancellationToken.None)).ToArray());
        Assert.Equal(
            expectedNewHits.ToArray(),
            (await ReadHitsAsync(connectionFactory(projectId), schemaPrefix)).ToArray());
        Assert.False(await ReadFilterStaleAsync(connectionFactory(projectId), schemaPrefix));
    }

    private static IReadOnlyList<SavedFilterScenario> Definitions(
        DateTimeOffset savedUtc,
        int firstPosition,
        string namePrefix)
    {
        var debitName = $"{namePrefix}-debit";
        var debitRationale = $"{namePrefix}-debit-rationale";
        var creditName = $"{namePrefix}-credit";
        var creditRationale = $"{namePrefix}-credit-rationale";
        return
        [
            new(
                firstPosition,
                debitName,
                debitRationale,
                DefinitionJson(debitName, debitRationale, "debit"),
                savedUtc),
            new(
                firstPosition + 1,
                creditName,
                creditRationale,
                DefinitionJson(creditName, creditRationale, "credit"),
                savedUtc)
        ];
    }

    private static IReadOnlyList<FilterCommitItem> CommitItems(
        IReadOnlyList<SavedFilterScenario> definitions) =>
    [
        new(
            definitions[0],
            DrCrScenario("debit", definitions[0].Name, definitions[0].Rationale)),
        new(
            definitions[1],
            DrCrScenario("credit", definitions[1].Name, definitions[1].Rationale))
    ];

    private static string DefinitionJson(string name, string rationale, string drCr) =>
        $$"""
        {"name":"{{name}}","rationale":"{{rationale}}","groups":[{"join":"AND","rules":[{"join":"AND","type":"drCrOnly","drCr":"{{drCr}}"}]}]}
        """;

    private static FilterScenarioSpec DrCrScenario(
        string drCr,
        string name,
        string rationale) => new(
        name,
        rationale,
        [
            new FilterGroupSpec(
                FilterJoin.And,
                [
                    new FilterRuleSpec(
                        FilterJoin.And,
                        FilterRuleType.DrCrOnly,
                        PrescreenKey: null,
                        Field: null,
                        Keywords: [],
                        Mode: TextMatchMode.Contains,
                        FromDate: null,
                        ToDate: null,
                        FromAmountScaled: null,
                        ToAmountScaled: null,
                        DrCr: drCr,
                        IsManual: null)
                ])
        ]);

    private static FilterRuleContext Context() => new(
        ProjectDocument.DefaultMoneyScale,
        LastPeriodStart: null,
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        PopulationScope: GlPopulationScope.AuditPeriod);

    private static async Task SeedPopulationAndPreviousHitsAsync(
        DbConnection connection,
        string schemaPrefix)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                INSERT INTO {schemaPrefix}target_gl_entry
                    (batch_id, source_row_number, document_number, line_item, post_date,
                     account_code, account_name, amount_scaled, debit_amount_scaled,
                     credit_amount_scaled, dr_cr)
                VALUES
                    ('atomic-filter', 1, 'DOC-DEBIT', '1', '2025-03-01',
                     '1101', 'Cash', 10000, 10000, 0, 'DEBIT'),
                    ('atomic-filter', 2, 'DOC-CREDIT', '1', '2025-03-02',
                     '4101', 'Revenue', -10000, 0, 10000, 'CREDIT');

                UPDATE {schemaPrefix}target_gl_entry SET is_effective = 1;

                INSERT INTO {schemaPrefix}result_filter_run (scenario_position, entry_id)
                VALUES
                    (7, 700001),
                    (8, 800001),
                    (8, 800002);
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<IReadOnlyList<HitPair>> ReadExpectedNewHitsAsync(
        DbConnection connection,
        string schemaPrefix)
    {
        var rows = new List<HitPair>();
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT entry_id, dr_cr FROM {schemaPrefix}target_gl_entry ORDER BY entry_id;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var position = string.Equals(reader.GetString(1), "DEBIT", StringComparison.Ordinal)
                    ? 1
                    : 2;
                rows.Add(new HitPair(position, ReadInt64(reader, 0)));
            }
        }

        return rows.OrderBy(row => row.Position).ThenBy(row => row.EntryId).ToArray();
    }

    private static async Task<IReadOnlyList<HitPair>> ReadHitsAsync(
        DbConnection connection,
        string schemaPrefix)
    {
        var rows = new List<HitPair>();
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT scenario_position, entry_id
                FROM {schemaPrefix}result_filter_run
                ORDER BY scenario_position, entry_id;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(new HitPair(
                    checked((int)ReadInt64(reader, 0)),
                    ReadInt64(reader, 1)));
            }
        }

        return rows;
    }

    private static async Task SetFilterStaleAsync(
        DbConnection connection,
        string schemaPrefix)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE {schemaPrefix}config_result_stale_state SET filter_stale = 1 WHERE singleton = 1;";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
    }

    private static async Task<bool> ReadFilterStaleAsync(
        DbConnection connection,
        string schemaPrefix)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT filter_stale FROM {schemaPrefix}config_result_stale_state WHERE singleton = 1;";
            return Convert.ToBoolean(
                await command.ExecuteScalarAsync(),
                CultureInfo.InvariantCulture);
        }
    }

    private static long ReadInt64(DbDataReader reader, int ordinal) =>
        long.Parse(
            reader.GetValue(ordinal).ToString()
                ?? throw new InvalidOperationException("Expected an integer database value."),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture);

    private sealed record HitPair(int Position, long EntryId);

    private sealed class InjectedFilterCommitException(string message) : Exception(message);

    private interface IOneShotFilterCommitFault
    {
        InjectedFilterCommitException InjectedException { get; }

        bool FaultInjected { get; }

        void Disable();
    }

    private interface IOneShotCancellationFault
    {
        bool FaultInjected { get; }
    }

    private sealed class FaultAfterFirstHitInsertLogger<T>(
        InjectedFilterCommitException injectedException) : ILogger<T>, IOneShotFilterCommitFault
    {
        private int _enabled = 1;
        private int _faultInjected;

        public InjectedFilterCommitException InjectedException { get; } = injectedException;

        public bool FaultInjected => Volatile.Read(ref _faultInjected) != 0;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Volatile.Read(ref _enabled) == 0 || eventId.Id != 2000)
            {
                return;
            }

            var message = formatter(state, exception);
            if (!message.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase)
                || !message.Contains("result_filter_run", StringComparison.OrdinalIgnoreCase)
                || Interlocked.Exchange(ref _faultInjected, 1) != 0)
            {
                return;
            }

            throw InjectedException;
        }

        public void Disable() => Volatile.Write(ref _enabled, 0);
    }

    private sealed class CancelAfterFirstHitInsertLogger<T>(
        CancellationTokenSource cancellation) : ILogger<T>, IOneShotCancellationFault
    {
        private int _faultInjected;

        public bool FaultInjected => Volatile.Read(ref _faultInjected) != 0;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 2000)
            {
                return;
            }

            var message = formatter(state, exception);
            if (!message.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase)
                || !message.Contains("result_filter_run", StringComparison.OrdinalIgnoreCase)
                || Interlocked.Exchange(ref _faultInjected, 1) != 0)
            {
                return;
            }

            cancellation.Cancel();
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
