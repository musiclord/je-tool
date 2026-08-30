using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class WorkpaperStep41RowPageRepositoryTests
{
    private const string MissingRawSourceMessage =
        "底稿引用的原始 GL 列已不完整，請重新匯入、配對並執行對應步驟後再產出底稿。";
    private const string MissingNumericSortKeyMessage =
        "底稿引用的 GL 傳票文件項次排序鍵已過期，請重新匯入、配對並執行對應步驟後再產出底稿。";

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TargetAndFilterRemainButRawRowIsMissing_ThrowsStaleResult(
        string provider)
    {
        using var root = new TempProjectRoot();
        var database = CreateDatabase(provider, root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await SeedTargetAndFilterWithoutRawAsync(database, projectId);
        var repository = (IWorkpaperStep41PreparedSessionFactory)
            new LocalTagMatrixRowPageRepository(database);

        var session = await repository.PrepareAsync(
                projectId,
                new GlPopulationContext(
                    GlPopulationScope.AuditPeriod,
                    "2025-01-01",
                    "2025-12-31"),
                [1],
                LegacyFieldKind.Text,
                CancellationToken.None);
        JetActionException exception;
        await using (session)
        {
            exception = await Assert.ThrowsAsync<JetActionException>(() =>
                ConsumeAsync(session));
        }

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
        Assert.Equal(MissingRawSourceMessage, exception.Message);
        Assert.DoesNotContain("範本", exception.Message);
        Assert.Equal(1, session.Metrics.CleanupCommands);
        Assert.True(session.Metrics.TemporaryObjectsCleared);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MigratedNumericLineWithoutSortKey_ThrowsStaleResult(
        string provider)
    {
        using var root = new TempProjectRoot();
        var database = CreateDatabase(provider, root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await SeedNumericTargetWithRawButWithoutSortKeyAsync(database, projectId);
        var repository = (IWorkpaperStep41PreparedSessionFactory)
            new LocalTagMatrixRowPageRepository(database);

        var session = await repository.PrepareAsync(
                projectId,
                new GlPopulationContext(
                    GlPopulationScope.AuditPeriod,
                    "2025-01-01",
                    "2025-12-31"),
                [1],
                LegacyFieldKind.Number,
                CancellationToken.None);
        JetActionException exception;
        await using (session)
        {
            exception = await Assert.ThrowsAsync<JetActionException>(() =>
                ConsumeAsync(session));
        }

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
        Assert.Equal(MissingNumericSortKeyMessage, exception.Message);
        Assert.DoesNotContain("範本", exception.Message);
        Assert.Equal(1, session.Metrics.CleanupCommands);
        Assert.True(session.Metrics.TemporaryObjectsCleared);
    }

    private static async Task ConsumeAsync(
        IWorkpaperStep41PreparedSession session)
    {
        await foreach (var _ in session.ReadRowsAsync(CancellationToken.None))
        {
        }
    }

    private static ILocalProjectDatabase CreateDatabase(string provider, string root) =>
        provider switch
        {
            "sqlite" => new SqliteProjectDatabase(new JetProjectFolder(root)),
            "duckdb" => new DuckDbProjectDatabase(new JetProjectFolder(root)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unknown local provider.")
        };

    private static async Task SeedTargetAndFilterWithoutRawAsync(
        ILocalProjectDatabase database,
        string projectId)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO target_gl_entry
                (batch_id, source_row_number, document_number, line_item, post_date,
                 account_code, account_name, document_description, amount_scaled,
                 debit_amount_scaled, credit_amount_scaled, dr_cr, is_effective)
            VALUES
                ('missing-raw', 1, 'DOC-STALE', '1', '2025-06-01',
                 '1000', 'Cash', 'Missing raw source', 10000, 10000, 0, 'DEBIT', 1);

            INSERT INTO result_filter_run (scenario_position, entry_id)
            SELECT 1, entry_id
            FROM target_gl_entry;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedNumericTargetWithRawButWithoutSortKeyAsync(
        ILocalProjectDatabase database,
        string projectId)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO staging_gl_raw_row
                (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES
                ('v5-projection', 1, 1, 1, '{"傳票項次":"7"}');

            INSERT INTO target_gl_entry
                (batch_id, source_row_number, document_number, line_item,
                 line_item_numeric_sort_key, post_date, account_code, account_name,
                 document_description, amount_scaled, debit_amount_scaled,
                 credit_amount_scaled, dr_cr, is_effective)
            VALUES
                ('v5-projection', 1, 'DOC-STALE', '7', NULL, '2025-06-01',
                 '1000', 'Cash', 'Missing numeric sort key',
                 10000, 10000, 0, 'DEBIT', 1);

            INSERT INTO result_filter_run (scenario_position, entry_id)
            SELECT 1, entry_id
            FROM target_gl_entry;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
