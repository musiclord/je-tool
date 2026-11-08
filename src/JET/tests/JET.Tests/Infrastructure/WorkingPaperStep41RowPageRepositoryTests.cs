using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class WorkingPaperStep41RowPageRepositoryTests
{
    private const string MissingRawSourceMessage =
        "底稿引用的原始 GL 列已不完整，請重新匯入、配對並執行對應步驟後再產出底稿。";
    private const string MissingNumericSortKeyMessage =
        "底稿引用的 GL 傳票文件項次排序鍵已過期，請重新匯入、配對並執行對應步驟後再產出底稿。";

    [Theory]
    [InlineData("sqlite")]
    public async Task TargetAndFilterRemainButRawRowIsMissing_ThrowsStaleResult(
        string provider)
    {
        using var root = new TempProjectRoot();
        var database = CreateDatabase(provider, root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await SeedTargetAndFilterWithoutRawAsync(database, projectId);
        var repository = (IWorkingPaperStep41PreparedSessionFactory)
            new LocalHighRiskSummaryRowPageRepository(database);

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
    public async Task MigratedNumericLineWithoutSortKey_ThrowsStaleResult(
        string provider)
    {
        using var root = new TempProjectRoot();
        var database = CreateDatabase(provider, root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await SeedNumericTargetWithRawButWithoutSortKeyAsync(database, projectId);
        var repository = (IWorkingPaperStep41PreparedSessionFactory)
            new LocalHighRiskSummaryRowPageRepository(database);

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
        IWorkingPaperStep41PreparedSession session)
    {
        await foreach (var _ in session.ReadRowsAsync(CancellationToken.None))
        {
        }
    }

    private static ILocalProjectDatabase CreateDatabase(string provider, string root) =>
        provider switch
        {
            "sqlite" => new SqliteProjectDatabase(new JetProjectFolder(root)),
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
            INSERT INTO gl
                (batch_id, source_row_number, voucher_number, line_item, gl_date,
                 account_number, account_name, description, amount_scaled,
                 debit_amount_scaled, credit_amount_scaled, dr_cr, is_excluded)
            VALUES
                ('missing-raw', 1, 'DOC-STALE', '1', '2025-06-01',
                 '1000', 'Cash', 'Missing raw source', 10000, 10000, 0, 'DEBIT', 0);

            INSERT INTO filter_scenario (scenario_id, position, name, rationale, definition_json, saved_utc) VALUES (1, 1, 'synthetic', 'synthetic', '{}', '2026-10-31T00:00:00Z');
            INSERT INTO filter_hit (scenario_id, entry_id)
            SELECT 1, entry_id
            FROM gl;
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
            INSERT INTO pbc_gl
                (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES
                ('v5-projection', 1, 1, 1, '{"傳票項次":"7"}');

            INSERT INTO gl
                (batch_id, source_row_number, voucher_number, line_item,
                 line_item_numeric_sort_key, gl_date, account_number, account_name,
                 description, amount_scaled, debit_amount_scaled,
                 credit_amount_scaled, dr_cr, is_excluded)
            VALUES
                ('v5-projection', 1, 'DOC-STALE', '7', NULL, '2025-06-01',
                 '1000', 'Cash', 'Missing numeric sort key',
                 10000, 10000, 0, 'DEBIT', 0);

            INSERT INTO filter_scenario (scenario_id, position, name, rationale, definition_json, saved_utc) VALUES (1, 1, 'synthetic', 'synthetic', '{}', '2026-10-31T00:00:00Z');
            INSERT INTO filter_hit (scenario_id, entry_id)
            SELECT 1, entry_id
            FROM gl;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
