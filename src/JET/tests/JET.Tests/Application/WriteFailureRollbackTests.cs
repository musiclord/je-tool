using System.Globalization;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class WriteFailureRollbackTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ValidationSummaryInsertFailure_RollsBackNewSamplesAndPreservesPriorRunAndStaleState(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await ReportRevisionStateTests.PrepareAsync(host, provider);
        await KeepOnlyCurrentValidationAsync(host, prepared, provider);
        await ReportRevisionStateTests.ExecuteAsync(host, prepared.Id, provider, $$"""
            ALTER TABLE validation_result RENAME TO saved_result;
            CREATE TABLE validation_result (result_id TEXT PRIMARY KEY, generated_utc TEXT NOT NULL, summary_json TEXT NOT NULL,
                CHECK (result_id = '{{prepared.ValidationResultId}}'));
            INSERT INTO validation_result SELECT result_id, generated_utc, summary_json FROM saved_result;
            UPDATE result_stale_state SET validation_stale=1 WHERE singleton=1;
            """);
        var error = await Record.ExceptionAsync(() => host.DispatchAsync("validate.run"));
        Assert.NotNull(error);
        Assert.Equal(2, await ScalarAsync(host, prepared.Id, provider, "SELECT COUNT(*) FROM inf_sample;"));
        Assert.Equal(1, await ScalarAsync(host, prepared.Id, provider, "SELECT COUNT(*) FROM validation_result;"));
        Assert.Equal(1, await ScalarAsync(host, prepared.Id, provider, "SELECT validation_stale FROM result_stale_state WHERE singleton=1;"));
        Assert.Equal(0, await ScalarAsync(host, prepared.Id, provider,
            "SELECT COUNT(*) FROM inf_sample s WHERE NOT EXISTS (SELECT 1 FROM validation_result r WHERE r.result_id=s.result_id);"));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TbMappingWriteFailure_RollsBackProjectionDefinitionAndResultInvalidation(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await ReportRevisionStateTests.PrepareAsync(host, provider);
        await KeepOnlyCurrentValidationAsync(host, prepared, provider);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        var mapping = loaded.GetProperty("mapping").GetProperty("tb").GetRawText();
        var changedRow = JsonSerializer.Serialize(new Dictionary<string, string>
        { ["科目代號"] = "1000", ["科目名稱"] = "合成資產", ["變動金額"] = "77" });
        await ReportRevisionStateTests.ExecuteAsync(host, prepared.Id, provider, "UPDATE pbc_tb SET row_json=@row WHERE row_number=1;", ("@row", changedRow));
        await ReportRevisionStateTests.ExecuteAsync(host, prepared.Id, provider, """
            UPDATE field_mapping SET committed_utc='2000-01-01T00:00:00.0000000+00:00' WHERE dataset_kind='tb';
            ALTER TABLE field_mapping RENAME TO saved_field_mapping;
            CREATE TABLE field_mapping (dataset_kind TEXT PRIMARY KEY, mapping_json TEXT NOT NULL, mode_name TEXT NOT NULL,
                source_batch_id TEXT NOT NULL, committed_utc TEXT NOT NULL, format_version INTEGER NOT NULL, options_json TEXT,
                CHECK (dataset_kind <> 'tb' OR committed_utc='2000-01-01T00:00:00.0000000+00:00'));
            INSERT INTO field_mapping SELECT dataset_kind, mapping_json, mode_name, source_batch_id, committed_utc, format_version, options_json FROM saved_field_mapping;
            CREATE TABLE saved_field_info AS SELECT * FROM field_info;
            """);
        var error = await Record.ExceptionAsync(() => host.DispatchAsync("mapping.commit.tb", mapping));
        Assert.NotNull(error);
        Assert.Equal(100000, await ScalarAsync(host, prepared.Id, provider, "SELECT change_amount_scaled FROM tb WHERE account_number='1000';"));
        Assert.Equal(2, await ScalarAsync(host, prepared.Id, provider, "SELECT COUNT(*) FROM tb;"));
        Assert.Equal(1, await ScalarAsync(host, prepared.Id, provider, "SELECT COUNT(*) FROM validation_result;"));
        Assert.Equal(2, await ScalarAsync(host, prepared.Id, provider, "SELECT COUNT(*) FROM inf_sample;"));
        Assert.Equal(0, await ScalarAsync(host, prepared.Id, provider, "SELECT validation_stale FROM result_stale_state WHERE singleton=1;"));
        Assert.Equal(1, await ScalarAsync(host, prepared.Id, provider,
            "SELECT COUNT(*) FROM field_mapping c JOIN saved_field_mapping s ON c.dataset_kind=s.dataset_kind AND c.mapping_json=s.mapping_json AND c.committed_utc=s.committed_utc WHERE c.dataset_kind='tb';"));
        Assert.Equal(0, await ScalarAsync(host, prepared.Id, provider,
            "SELECT COUNT(*) FROM (SELECT * FROM field_info EXCEPT SELECT * FROM saved_field_info) AS changed;"));
        Assert.Equal(0, await ScalarAsync(host, prepared.Id, provider,
            "SELECT COUNT(*) FROM (SELECT * FROM saved_field_info EXCEPT SELECT * FROM field_info) AS removed;"));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OrphanInfSamples_AreNotEvidenceThatValidationCompleted(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await ReportRevisionStateTests.PrepareAsync(host, provider);
        await ReportRevisionStateTests.ExecuteAsync(host, prepared.Id, provider,
            "DELETE FROM validation_result; UPDATE result_stale_state SET validation_stale=0 WHERE singleton=1;");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("validationResult").ValueKind);
        await host.DispatchAsync("mapping.commit.tb", loaded.GetProperty("mapping").GetProperty("tb").GetRawText());
        Assert.Equal(0, await ScalarAsync(host, prepared.Id, provider, "SELECT validation_stale FROM result_stale_state WHERE singleton=1;"));
        Assert.Equal(0, await ScalarAsync(host, prepared.Id, provider, "SELECT COUNT(*) FROM inf_sample;"));
    }

    private static Task KeepOnlyCurrentValidationAsync(HandlerTestHost host, ReportRevisionStateTests.Prepared prepared, string provider) =>
        ReportRevisionStateTests.ExecuteAsync(host, prepared.Id, provider,
            "DELETE FROM validation_result WHERE result_id<>@run; DELETE FROM inf_sample WHERE result_id<>@run;",
            ("@run", prepared.ValidationResultId));

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
