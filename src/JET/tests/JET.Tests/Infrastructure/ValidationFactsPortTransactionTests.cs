using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

// 第9批中低9：呼叫改走正式同交易summary發布；透過tests-only capture保留原facts、取消、rollback及SQL日誌的全部斷言。

/// <summary>
/// Validation facts port 的交易失敗邊界。故障只在 INF 樣本 INSERT 已完成並寫出
/// sql.executed 後注入，證明 SQLite／DuckDB／SQL Server 都不會留下部分樣本。
/// </summary>
public sealed class ValidationFactsPortTransactionTests
{
    private const string ValidationRunId = "validation-fault-run";
    private const string ExistingRunId = "existing-validation-run";

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public Task LocalFactsPort_CancellationAfterInfInsert_RollsBackEntireTransaction(string provider) =>
        VerifyLocalRollbackAsync(provider, FaultMode.Cancellation);

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public Task LocalFactsPort_ExceptionAfterInfInsert_RollsBackEntireTransaction(string provider) =>
        VerifyLocalRollbackAsync(provider, FaultMode.Exception);

    [SqlServerFact]
    public Task SqlServerFactsPort_CancellationAfterInfInsert_RollsBackEntireTransaction() =>
        VerifySqlServerRollbackAsync(FaultMode.Cancellation);

    [SqlServerFact]
    public Task SqlServerFactsPort_ExceptionAfterInfInsert_RollsBackEntireTransaction() =>
        VerifySqlServerRollbackAsync(FaultMode.Exception);

    private static async Task VerifyLocalRollbackAsync(string provider, FaultMode mode)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await SeedPopulationAndExistingSampleAsync(database.CreateConnection(projectId), schemaPrefix: "");

        using var cancellation = new CancellationTokenSource();
        var logger = new FaultAfterInfInsertLogger<LocalValidationRunRepository>(
            mode == FaultMode.Cancellation
                ? cancellation.Cancel
                : static () => throw new InjectedValidationFactsException());
        var repository = new LocalValidationRunRepository(database, logger);

        await AssertInjectedFaultAsync(
            mode,
            () => ValidationExecutionTestData.ExecuteForFactsAsync(repository, Plan(projectId), cancellation.Token));

        await AssertRolledBackAsync(database.CreateConnection(projectId), schemaPrefix: "", logger);
    }

    private static async Task VerifySqlServerRollbackAsync(FaultMode mode)
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());

        var schemaPrefix = SqlServerProjectSchema.QualifierFor(project.ProjectId);
        await SeedPopulationAndExistingSampleAsync(
            project.Database.CreateConnection(project.ProjectId),
            schemaPrefix);

        using var cancellation = new CancellationTokenSource();
        var logger = new FaultAfterInfInsertLogger<SqlServerValidationRunRepository>(
            mode == FaultMode.Cancellation
                ? cancellation.Cancel
                : static () => throw new InjectedValidationFactsException());
        var repository = new SqlServerValidationRunRepository(project.Database, logger);

        await AssertInjectedFaultAsync(
            mode,
            () => ValidationExecutionTestData.ExecuteForFactsAsync(repository, Plan(project.ProjectId), cancellation.Token));

        await AssertRolledBackAsync(
            project.Database.CreateConnection(project.ProjectId),
            schemaPrefix,
            logger);
    }

    private static ValidationPlan Plan(string projectId) =>
        JetAuditProgram.Plan(new ValidationRequest(
            ProjectId: projectId,
            HasGlMapping: true,
            HasTbMapping: false,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: ProjectDocument.DefaultMoneyScale,
            SampleSeed: 7,
            RunId: ValidationRunId,
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            SampleSize: 1));

    private static async Task AssertInjectedFaultAsync(
        FaultMode mode,
        Func<Task<ValidationFacts>> execute)
    {
        if (mode == FaultMode.Cancellation)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(execute);
            return;
        }

        await Assert.ThrowsAsync<InjectedValidationFactsException>(execute);
    }

    private static async Task SeedPopulationAndExistingSampleAsync(
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
                    (batch_id, source_row_number, document_number, line_item, post_date, account_code,
                     is_effective, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
                VALUES
                    ('seed-batch', 1, 'DOC-1', '1', '2025-06-30', '1101',
                     1, 100, 100, 0, 'DEBIT');

                INSERT INTO {schemaPrefix}result_inf_sampling_test_sample
                    (run_id, entry_id, document_number, line_item)
                VALUES
                    ('{ExistingRunId}', 999999, 'EXISTING', '1');
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task AssertRolledBackAsync<T>(
        DbConnection connection,
        string schemaPrefix,
        FaultAfterInfInsertLogger<T> logger)
    {
        Assert.True(logger.FaultInjected);
        Assert.Equal(1, logger.InsertRowsAffected);
        Assert.True(logger.HasEvent(2001)); // tx.begin
        Assert.True(logger.HasEvent(2003)); // tx.rollback
        Assert.False(logger.HasEvent(2002)); // tx.commit

        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT
                    COUNT(*),
                    COALESCE(SUM(CASE WHEN run_id = '{ExistingRunId}' AND entry_id = 999999 THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN run_id = '{ValidationRunId}' THEN 1 ELSE 0 END), 0)
                FROM {schemaPrefix}result_inf_sampling_test_sample;
                """;

            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            // DuckDB 的 COUNT/SUM reader 型別是 BigInteger；以不失真的十進位文字
            // 對照，避免為了跨 provider oracle 強制縮窄型別。
            Assert.Equal("1", reader.GetValue(0).ToString());
            Assert.Equal("1", reader.GetValue(1).ToString());
            Assert.Equal("0", reader.GetValue(2).ToString());
        }
    }

    private enum FaultMode
    {
        Cancellation,
        Exception
    }

    private sealed class InjectedValidationFactsException : Exception
    {
    }

    private sealed class FaultAfterInfInsertLogger<T>(Action injectFault) : ILogger<T>
    {
        private readonly Lock _gate = new();
        private readonly List<int> _eventIds = [];
        private int _faultInjected;

        public bool FaultInjected => Volatile.Read(ref _faultInjected) != 0;

        public int? InsertRowsAffected { get; private set; }

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
            var message = formatter(state, exception);
            lock (_gate)
            {
                _eventIds.Add(eventId.Id);
            }

            if (eventId.Id != 2000
                || !message.Contains("INSERT INTO", StringComparison.Ordinal)
                || !message.Contains("result_inf_sampling_test_sample", StringComparison.Ordinal)
                || Interlocked.Exchange(ref _faultInjected, 1) != 0)
            {
                return;
            }

            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                var rowsAffected = fields.FirstOrDefault(field =>
                    string.Equals(field.Key, "rows_affected", StringComparison.Ordinal));
                if (!string.IsNullOrEmpty(rowsAffected.Key))
                {
                    InsertRowsAffected = Convert.ToInt32(rowsAffected.Value);
                }
            }

            injectFault();
        }

        public bool HasEvent(int eventId)
        {
            lock (_gate)
            {
                return _eventIds.Contains(eventId);
            }
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
