using System.Globalization;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9TransactionBoundaryTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ValidationSummaryInsertFailure_RollsBackNewSamplesAndPreservesPriorRunAndStaleState(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        await KeepOnlyCurrentValidationAsync(host, prepared, provider);
        await Batch9ArtifactStateTests.ExecuteAsync(host, prepared.Id, provider, $$"""
            DROP INDEX IF EXISTS ix_result_rule_run_kind;
            ALTER TABLE result_rule_run RENAME TO saved_rule_run;
            CREATE TABLE result_rule_run (run_id TEXT PRIMARY KEY, run_kind TEXT NOT NULL, generated_utc TEXT NOT NULL, summary_json TEXT NOT NULL,
                CHECK (run_kind <> 'validate' OR run_id = '{{prepared.ValidationRunId}}'));
            INSERT INTO result_rule_run SELECT run_id, run_kind, generated_utc, summary_json FROM saved_rule_run;
            CREATE INDEX ix_result_rule_run_kind ON result_rule_run (run_kind, generated_utc);
            UPDATE config_result_stale_state SET validation_stale=1 WHERE singleton=1;
            """);
        var error = await Record.ExceptionAsync(() => host.DispatchAsync("validate.run"));
        Assert.NotNull(error);
        Assert.Equal(2, await ScalarAsync(host, prepared.Id, provider, "SELECT COUNT(*) FROM result_inf_sampling_test_sample;"));
        Assert.Equal(1, await ScalarAsync(host, prepared.Id, provider, "SELECT COUNT(*) FROM result_rule_run WHERE run_kind='validate';"));
        Assert.Equal(1, await ScalarAsync(host, prepared.Id, provider, "SELECT validation_stale FROM config_result_stale_state WHERE singleton=1;"));
        Assert.Equal(0, await ScalarAsync(host, prepared.Id, provider,
            "SELECT COUNT(*) FROM result_inf_sampling_test_sample s WHERE NOT EXISTS (SELECT 1 FROM result_rule_run r WHERE r.run_id=s.run_id AND r.run_kind='validate');"));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TbMappingWriteFailure_RollsBackProjectionDefinitionAndResultInvalidation(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        await KeepOnlyCurrentValidationAsync(host, prepared, provider);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        var mapping = loaded.GetProperty("mapping").GetProperty("tb").GetRawText();
        var changedRow = JsonSerializer.Serialize(new Dictionary<string, string>
        { ["科目代號"] = "1000", ["科目名稱"] = "合成資產", ["變動金額"] = "77" });
        await Batch9ArtifactStateTests.ExecuteAsync(host, prepared.Id, provider, "UPDATE staging_tb_raw_row SET row_json=@row WHERE row_number=1;", ("@row", changedRow));
        await Batch9ArtifactStateTests.ExecuteAsync(host, prepared.Id, provider, """
            UPDATE config_field_mapping SET committed_utc='2000-01-01T00:00:00.0000000+00:00' WHERE dataset_kind='tb';
            ALTER TABLE config_field_mapping RENAME TO saved_field_mapping;
            CREATE TABLE config_field_mapping (dataset_kind TEXT PRIMARY KEY, mapping_json TEXT NOT NULL, mode_name TEXT NOT NULL,
                source_batch_id TEXT NOT NULL, committed_utc TEXT NOT NULL, format_version INTEGER NOT NULL, options_json TEXT,
                CHECK (dataset_kind <> 'tb' OR committed_utc='2000-01-01T00:00:00.0000000+00:00'));
            INSERT INTO config_field_mapping SELECT dataset_kind, mapping_json, mode_name, source_batch_id, committed_utc, format_version, options_json FROM saved_field_mapping;
            CREATE TABLE saved_field_definition AS SELECT * FROM import_field_definition;
            """);
        var error = await Record.ExceptionAsync(() => host.DispatchAsync("mapping.commit.tb", mapping));
        Assert.NotNull(error);
        Assert.Equal(100000, await ScalarAsync(host, prepared.Id, provider, "SELECT change_amount_scaled FROM target_tb_balance WHERE account_code='1000';"));
        Assert.Equal(2, await ScalarAsync(host, prepared.Id, provider, "SELECT COUNT(*) FROM target_tb_balance;"));
        Assert.Equal(1, await ScalarAsync(host, prepared.Id, provider, "SELECT COUNT(*) FROM result_rule_run WHERE run_kind='validate';"));
        Assert.Equal(2, await ScalarAsync(host, prepared.Id, provider, "SELECT COUNT(*) FROM result_inf_sampling_test_sample;"));
        Assert.Equal(0, await ScalarAsync(host, prepared.Id, provider, "SELECT validation_stale FROM config_result_stale_state WHERE singleton=1;"));
        Assert.Equal(1, await ScalarAsync(host, prepared.Id, provider,
            "SELECT COUNT(*) FROM config_field_mapping c JOIN saved_field_mapping s ON c.dataset_kind=s.dataset_kind AND c.mapping_json=s.mapping_json AND c.committed_utc=s.committed_utc WHERE c.dataset_kind='tb';"));
        Assert.Equal(0, await ScalarAsync(host, prepared.Id, provider,
            "SELECT COUNT(*) FROM (SELECT * FROM import_field_definition EXCEPT SELECT * FROM saved_field_definition) AS changed;"));
        Assert.Equal(0, await ScalarAsync(host, prepared.Id, provider,
            "SELECT COUNT(*) FROM (SELECT * FROM saved_field_definition EXCEPT SELECT * FROM import_field_definition) AS removed;"));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OrphanInfSamples_AreNotEvidenceThatValidationCompleted(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        await Batch9ArtifactStateTests.ExecuteAsync(host, prepared.Id, provider,
            "DELETE FROM result_rule_run WHERE run_kind='validate'; UPDATE config_result_stale_state SET validation_stale=0 WHERE singleton=1;");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("latestRuns").GetProperty("validate").ValueKind);
        await host.DispatchAsync("mapping.commit.tb", loaded.GetProperty("mapping").GetProperty("tb").GetRawText());
        Assert.Equal(0, await ScalarAsync(host, prepared.Id, provider, "SELECT validation_stale FROM config_result_stale_state WHERE singleton=1;"));
        Assert.Equal(0, await ScalarAsync(host, prepared.Id, provider, "SELECT COUNT(*) FROM result_inf_sampling_test_sample;"));
    }

    private static Task KeepOnlyCurrentValidationAsync(HandlerTestHost host, Batch9ArtifactStateTests.Prepared prepared, string provider) =>
        Batch9ArtifactStateTests.ExecuteAsync(host, prepared.Id, provider,
            "DELETE FROM result_rule_run WHERE run_kind='validate' AND run_id<>@run; DELETE FROM result_inf_sampling_test_sample WHERE run_id<>@run;",
            ("@run", prepared.ValidationRunId));

    internal static async Task<long> ScalarAsync(HandlerTestHost host, string id, string provider, string sql)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        ILocalProjectDatabase database = provider == "sqlite" ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);
        await using var connection = database.CreateConnection(id);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return long.Parse((await command.ExecuteScalarAsync())!.ToString()!, CultureInfo.InvariantCulture);
    }
}
