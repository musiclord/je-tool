using System.Data.Common;
using System.Globalization;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// GL/TB 多來源匯入的 repository 最低交易 seam：第一個來源已完整送入真實 provider、
/// 第二個來源串流再故障時，整批必須回到呼叫前快照；解除故障後同一批可直接重試。
/// </summary>
public sealed class ImportBatchAtomicityProviderTests
{
    private const string ReplaceMode = "replace";
    private const string AppendMode = "append";
    private const string FailureSentinel = "sentinel";
    private const string FirstSourceName = "source-one.xlsx";
    private const string SecondSourceName = "source-two.xlsx";
    private const string FirstSheetName = "Sheet-One";
    private const string SecondSheetName = "Sheet-Two";

    private static readonly IReadOnlyList<string> Columns = ["doc", "amount"];

    [Theory]
    [InlineData("sqlite", ReplaceMode)]
    [InlineData("sqlite", AppendMode)]
    [InlineData("duckdb", ReplaceMode)]
    [InlineData("duckdb", AppendMode)]
    public async Task BatchImport_LaterSourceFailure_RollsBackWholeBatch_AndRetrySucceeds_LocalProviders(
        string provider,
        string mode)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        IImportRepository repository = new LocalImportRepository(database);

        await AssertAtomicFailureAndRetryAsync(
            repository,
            projectId,
            id => database.CreateConnection(id),
            schemaPrefix: string.Empty,
            mode: mode);
    }

    [SqlServerTheory]
    [InlineData(ReplaceMode)]
    [InlineData(AppendMode)]
    public async Task BatchImport_LaterSourceFailure_RollsBackWholeBatch_AndRetrySucceeds_SqlServer(
        string mode)
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());
        IImportRepository repository = new SqlServerImportRepository(project.Database);

        await AssertAtomicFailureAndRetryAsync(
            repository,
            project.ProjectId,
            id => project.Database.CreateConnection(id),
            SqlServerProjectSchema.QualifierFor(project.ProjectId),
            mode);
    }

    private static async Task AssertAtomicFailureAndRetryAsync(
        IImportRepository repository,
        string projectId,
        Func<string, DbConnection> createConnection,
        string schemaPrefix,
        string mode)
    {
        var baseline = await repository.ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            Source("old.csv"),
            Columns,
            Rows("old", count: 2),
            CancellationToken.None);
        await SeedDownstreamStateAsync(
            projectId,
            baseline.Batch.BatchId,
            createConnection,
            schemaPrefix);
        var before = await CaptureSnapshotAsync(projectId, createConnection, schemaPrefix);

        var failure = await Assert.ThrowsAsync<JetActionException>(() => ExecuteBatchAsync(
            repository,
            projectId,
            mode,
            FailingBatchInputs(),
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, failure.Code);
        Assert.Contains(SecondSourceName, failure.Message, StringComparison.Ordinal);
        Assert.Contains(SecondSheetName, failure.Message, StringComparison.Ordinal);
        Assert.Contains(FailureSentinel, failure.Message, StringComparison.Ordinal);

        var afterFailure = await CaptureSnapshotAsync(projectId, createConnection, schemaPrefix);
        AssertSnapshotEqual(before, afterFailure);
        Assert.DoesNotContain(
            afterFailure.Staging,
            row => row.RowJson.Contains("source-one", StringComparison.Ordinal));
        Assert.DoesNotContain(afterFailure.Sources, source => source.FileName == FirstSourceName);

        var retried = await ExecuteBatchAsync(
            repository,
            projectId,
            mode,
            SuccessfulBatchInputs(),
            CancellationToken.None);

        Assert.Equal(4, retried.AddedRowCount);
        Assert.Equal(mode == ReplaceMode ? 4 : 6, retried.Batch.RowCount);

        var expectedNames = mode == ReplaceMode
            ? new[] { FirstSourceName, SecondSourceName }
            : new[] { "old.csv", FirstSourceName, SecondSourceName };
        Assert.Equal(expectedNames, retried.Batch.Sources.Select(source => source.FileName).ToArray());
        Assert.Equal(
            Enumerable.Range(1, expectedNames.Length).ToArray(),
            retried.Batch.Sources.Select(source => source.SourceNo).ToArray());
        Assert.Equal(FirstSheetName, retried.Batch.Sources[^2].SheetName);
        Assert.Equal(SecondSheetName, retried.Batch.Sources[^1].SheetName);
        Assert.Equal(2, retried.Batch.Sources[^2].RowCount);
        Assert.Equal(2, retried.Batch.Sources[^1].RowCount);

        var afterRetry = await CaptureSnapshotAsync(projectId, createConnection, schemaPrefix);
        Assert.NotEmpty(afterRetry.FieldDefinitions);
        Assert.Empty(afterRetry.TargetRows);
        Assert.Empty(afterRetry.Mappings);
        Assert.Empty(afterRetry.RdeDefinitions);
        Assert.Empty(afterRetry.RdeValues);
        Assert.Empty(afterRetry.RuleRuns);
        Assert.Empty(afterRetry.InfSamples);
        Assert.Empty(afterRetry.FilterHits);
        Assert.Equal(before.ControlTotals.ToArray(), afterRetry.ControlTotals.ToArray());
    }

    private static Task<ImportBatchResult> ExecuteBatchAsync(
        IImportRepository repository,
        string projectId,
        string mode,
        IReadOnlyList<ImportSourceInput> sources,
        CancellationToken cancellationToken)
    {
        return mode == ReplaceMode
            ? repository.ReplaceBatchAsync(projectId, DatasetKind.Gl, sources, cancellationToken)
            : repository.AppendToBatchAsync(projectId, DatasetKind.Gl, sources, cancellationToken);
    }

    private static IReadOnlyList<ImportSourceInput> FailingBatchInputs() =>
    [
        new ImportSourceInput(
            Source(FirstSourceName, FirstSheetName),
            Columns,
            Rows("source-one", count: 2)),
        new ImportSourceInput(
            Source(SecondSourceName, SecondSheetName),
            Columns,
            FailingRows())
    ];

    private static IReadOnlyList<ImportSourceInput> SuccessfulBatchInputs() =>
    [
        new ImportSourceInput(
            Source(FirstSourceName, FirstSheetName),
            Columns,
            Rows("source-one", count: 2)),
        new ImportSourceInput(
            Source(SecondSourceName, SecondSheetName),
            Columns,
            Rows("source-two", count: 2))
    ];

    private static ImportSourceDescriptor Source(string fileName, string? sheetName = null) =>
        new($@"C:\fixtures\{fileName}", fileName, sheetName, null, null);

    private static async IAsyncEnumerable<StagingRow> Rows(string prefix, int count)
    {
        for (var index = 1; index <= count; index++)
        {
            yield return Row(index + 1, $"{prefix}-{index}", index.ToString());
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<StagingRow> FailingRows()
    {
        yield return Row(2, "source-two-partial", "1");
        await Task.Yield();
        throw new JetActionException(JetErrorCodes.FileReadError, FailureSentinel);
    }

    private static StagingRow Row(int sourceRowNumber, string document, string amount) =>
        new(
            sourceRowNumber,
            new Dictionary<string, string>
            {
                ["doc"] = document,
                ["amount"] = amount
            });

    private static async Task SeedDownstreamStateAsync(
        string projectId,
        string batchId,
        Func<string, DbConnection> createConnection,
        string schemaPrefix)
    {
        await using var connection = createConnection(projectId);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            INSERT INTO {{schemaPrefix}}target_gl_entry
                (batch_id, source_row_number, document_number, amount_scaled,
                 debit_amount_scaled, credit_amount_scaled, dr_cr)
            VALUES (@batchId, 2, 'OLD-DOC', 100, 100, 0, 'DEBIT');

            INSERT INTO {{schemaPrefix}}config_field_mapping
                (dataset_kind, mapping_json, mode_name, source_batch_id, committed_utc)
            VALUES ('gl', '{"docNum":"doc","amount":"amount"}', 'signed', @batchId,
                    '2026-07-31T00:00:00.0000000+00:00');

            INSERT INTO {{schemaPrefix}}config_gl_rde_field
                (field_id, source_column, label, value_type, ordinal, is_rde)
            VALUES ('rde.0123456789abcdef0123456789abcdef', 'risk', 'Risk', 'text', 0, 1);

            INSERT INTO {{schemaPrefix}}target_gl_rde_value
                (entry_id, field_id, value_type, text_value, date_value, amount_scaled)
            SELECT entry_id, 'rde.0123456789abcdef0123456789abcdef', 'text', 'OLD', NULL, NULL
            FROM {{schemaPrefix}}target_gl_entry
            WHERE document_number = 'OLD-DOC';

            INSERT INTO {{schemaPrefix}}result_rule_run
                (run_id, run_kind, generated_utc, summary_json)
            VALUES
                ('old-validation', 'validate', '2026-07-31T00:00:00.0000000+00:00', '{"marker":"validation"}'),
                ('old-prescreen', 'prescreen', '2026-07-31T00:00:01.0000000+00:00', '{"marker":"prescreen"}');

            INSERT INTO {{schemaPrefix}}result_inf_sampling_test_sample
                (run_id, entry_id, document_number, line_item)
            VALUES ('old-validation', 990001, 'OLD-DOC', '1');

            INSERT INTO {{schemaPrefix}}result_filter_run (scenario_position, entry_id)
            VALUES (9, 990001);

            INSERT INTO {{schemaPrefix}}gl_control_total
                (singleton, source_row_count, target_row_count,
                 target_debit_scaled, target_credit_scaled)
            VALUES (1, 2, 1, 100, 0);
            """;
        var batchParameter = command.CreateParameter();
        batchParameter.ParameterName = "@batchId";
        batchParameter.Value = batchId;
        command.Parameters.Add(batchParameter);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<ImportDatabaseSnapshot> CaptureSnapshotAsync(
        string projectId,
        Func<string, DbConnection> createConnection,
        string schemaPrefix)
    {
        await using var connection = createConnection(projectId);
        await connection.OpenAsync(CancellationToken.None);

        var batches = new List<BatchRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"SELECT batch_id, dataset_kind, source_file_name, row_count, columns_json " +
                $"FROM {schemaPrefix}import_batch ORDER BY batch_id;";
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                batches.Add(new BatchRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetString(4)));
            }
        }

        var sources = new List<SourceRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"SELECT batch_id, source_no, source_file_name, sheet_name, row_count " +
                $"FROM {schemaPrefix}import_batch_source ORDER BY batch_id, source_no;";
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                sources.Add(new SourceRecord(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4)));
            }
        }

        var staging = new List<StagingRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"SELECT batch_id, row_number, source_no, source_row_number, row_json " +
                $"FROM {schemaPrefix}staging_gl_raw_row ORDER BY batch_id, row_number;";
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                staging.Add(new StagingRecord(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetString(4)));
            }
        }

        var fieldDefinitions = await ReadRowsAsync(
            connection,
            $"""
            SELECT batch_id, definition_scope, ordinal, field_name, description,
                   field_kind, text_length, decimal_places, max_rendered_length, has_observation
            FROM {schemaPrefix}import_field_definition
            ORDER BY batch_id, definition_scope, ordinal;
            """);
        var targetRows = await ReadRowsAsync(
            connection,
            $"""
            SELECT entry_id, batch_id, source_row_number, document_number,
                   amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr
            FROM {schemaPrefix}target_gl_entry
            ORDER BY entry_id;
            """);
        var mappings = await ReadRowsAsync(
            connection,
            $"""
            SELECT dataset_kind, mapping_json, mode_name, source_batch_id, committed_utc
            FROM {schemaPrefix}config_field_mapping
            ORDER BY dataset_kind;
            """);
        var rdeDefinitions = await ReadRowsAsync(
            connection,
            $"""
            SELECT field_id, source_column, label, value_type, ordinal, is_rde
            FROM {schemaPrefix}config_gl_rde_field
            ORDER BY ordinal, field_id;
            """);
        var rdeValues = await ReadRowsAsync(
            connection,
            $"""
            SELECT entry_id, field_id, value_type, text_value, date_value, amount_scaled
            FROM {schemaPrefix}target_gl_rde_value
            ORDER BY entry_id, field_id;
            """);
        var ruleRuns = await ReadRowsAsync(
            connection,
            $"""
            SELECT run_id, run_kind, generated_utc, summary_json
            FROM {schemaPrefix}result_rule_run
            ORDER BY run_id;
            """);
        var infSamples = await ReadRowsAsync(
            connection,
            $"""
            SELECT run_id, entry_id, document_number, line_item
            FROM {schemaPrefix}result_inf_sampling_test_sample
            ORDER BY run_id, entry_id;
            """);
        var filterHits = await ReadRowsAsync(
            connection,
            $"""
            SELECT scenario_position, entry_id
            FROM {schemaPrefix}result_filter_run
            ORDER BY scenario_position, entry_id;
            """);
        var controlTotals = await ReadRowsAsync(
            connection,
            $"""
            SELECT singleton, source_row_count, target_row_count,
                   target_debit_scaled, target_credit_scaled
            FROM {schemaPrefix}gl_control_total
            ORDER BY singleton;
            """);

        return new ImportDatabaseSnapshot(
            batches,
            sources,
            staging,
            fieldDefinitions,
            targetRows,
            mappings,
            rdeDefinitions,
            rdeValues,
            ruleRuns,
            infSamples,
            filterHits,
            controlTotals);
    }

    private static async Task<IReadOnlyList<string>> ReadRowsAsync(
        DbConnection connection,
        string sql)
    {
        var rows = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            var values = new string[reader.FieldCount];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
            {
                values[ordinal] = reader.IsDBNull(ordinal)
                    ? "<null>"
                    : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)
                        ?? "<null>";
            }

            rows.Add(string.Join('\u001f', values));
        }

        return rows;
    }

    private static void AssertSnapshotEqual(ImportDatabaseSnapshot expected, ImportDatabaseSnapshot actual)
    {
        Assert.Equal(expected.Batches.ToArray(), actual.Batches.ToArray());
        Assert.Equal(expected.Sources.ToArray(), actual.Sources.ToArray());
        Assert.Equal(expected.Staging.ToArray(), actual.Staging.ToArray());
        Assert.Equal(expected.FieldDefinitions.ToArray(), actual.FieldDefinitions.ToArray());
        Assert.Equal(expected.TargetRows.ToArray(), actual.TargetRows.ToArray());
        Assert.Equal(expected.Mappings.ToArray(), actual.Mappings.ToArray());
        Assert.Equal(expected.RdeDefinitions.ToArray(), actual.RdeDefinitions.ToArray());
        Assert.Equal(expected.RdeValues.ToArray(), actual.RdeValues.ToArray());
        Assert.Equal(expected.RuleRuns.ToArray(), actual.RuleRuns.ToArray());
        Assert.Equal(expected.InfSamples.ToArray(), actual.InfSamples.ToArray());
        Assert.Equal(expected.FilterHits.ToArray(), actual.FilterHits.ToArray());
        Assert.Equal(expected.ControlTotals.ToArray(), actual.ControlTotals.ToArray());
    }

    private sealed record ImportDatabaseSnapshot(
        IReadOnlyList<BatchRecord> Batches,
        IReadOnlyList<SourceRecord> Sources,
        IReadOnlyList<StagingRecord> Staging,
        IReadOnlyList<string> FieldDefinitions,
        IReadOnlyList<string> TargetRows,
        IReadOnlyList<string> Mappings,
        IReadOnlyList<string> RdeDefinitions,
        IReadOnlyList<string> RdeValues,
        IReadOnlyList<string> RuleRuns,
        IReadOnlyList<string> InfSamples,
        IReadOnlyList<string> FilterHits,
        IReadOnlyList<string> ControlTotals);

    private sealed record BatchRecord(
        string BatchId,
        string DatasetKind,
        string SourceFileName,
        int RowCount,
        string ColumnsJson);

    private sealed record SourceRecord(
        string BatchId,
        int SourceNo,
        string FileName,
        string? SheetName,
        int RowCount);

    private sealed record StagingRecord(
        string BatchId,
        long RowNumber,
        int SourceNo,
        int SourceRowNumber,
        string RowJson);
}
