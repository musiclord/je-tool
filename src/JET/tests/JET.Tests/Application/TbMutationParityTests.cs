using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 預期值來自 KCT 計畫第八節「TB 修改改成比照 GL（2026-10-07）」：
/// TB 匯入與配對確認使驗證、預篩選、命中及全部情境一起失效；失敗則整筆回復。
/// 舊報告標成過期，已匯出的檔案不改寫。不從被測政策讀取預期值。
/// </summary>
public sealed class TbMutationParityTests
{
    private const string Scenarios = """{"scenarios":[{"name":"合成借方","rationale":"固定答案","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]},{"name":"合成貸方","rationale":"固定答案","groups":[{"rules":[{"type":"drCrOnly","drCr":"credit"}]}]}]}""";

    public static IEnumerable<object[]> SuccessfulCases() =>
        from provider in new[] { "sqlite", "duckdb" }
        from change in new[] { "replace", "append", "mapping" }
        select new object[] { provider, change };

    public static IEnumerable<object[]> FailureCases() =>
        from provider in new[] { "sqlite", "duckdb" }
        from change in new[] { "read", "mapping" }
        select new object[] { provider, change };

    [Theory]
    [InlineData(AuditMutation.TbImport)]
    [InlineData(AuditMutation.TbProjection)]
    internal void Policy_TbChangesInvalidateAllDerivedResultsAndDefinitions(AuditMutation mutation)
    {
        var impact = AuditDependencyPolicy.For(mutation);
        Assert.True(impact.InvalidateValidation);
        Assert.True(impact.InvalidatePrescreen);
        Assert.True(impact.InvalidateFilterHits);
        Assert.True(impact.InvalidateFilterScenarioDefinitions);
        Assert.False(impact.ClearGlControlTotal);
    }

    [Theory]
    [MemberData(nameof(SuccessfulCases))]
    public async Task SuccessfulTbChange_ClearsEveryScenarioAndResult_AndStalesReportsWithoutRewritingFiles(
        string provider, string change)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        var saved = await host.DispatchAsync("filter.commit", Scenarios);
        prepared = prepared with { Revision = saved.GetProperty("resultRef").GetProperty("revision").GetString()! };
        var before = await LoadAsync(host, prepared.Id);
        Assert.Equal(2, before.GetProperty("filterScenarios").GetArrayLength());
        Assert.Equal(2, await CountAsync(host, prepared.Id, provider, "result_filter_run"));
        var reports = await ExportBothAsync(host, prepared);
        var controlTotal = await SnapshotRowsAsync(host, prepared.Id, provider, "gl_control_total");

        JsonElement response;
        if (change == "mapping")
        {
            response = await host.DispatchAsync("mapping.commit.tb", before.GetProperty("mapping").GetProperty("tb").GetRawText());
        }
        else
        {
            var path = new InlineTbWorkbookBuilder().AddRow("1000", "合成資產", 10m).AddRow("4000", "合成收入", -10m).WriteWorkbook();
            try
            {
                response = await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = path, mode = change }));
            }
            finally { TestWorkbookBuilder.Delete(path); }
        }

        var invalidated = response.GetProperty("invalidatedResults");
        Assert.Equal(new[] { "filter", "filterScenarios", "prescreen", "validation" },
            invalidated.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.All(invalidated.EnumerateObject(), item => Assert.True(item.Value.GetBoolean(), item.Name));
        AssertClearedState(response);
        AssertReports(reports, response, stale: true);
        foreach (var table in new[] { "result_rule_run", "result_inf_sampling_test_sample", "result_filter_run", "config_filter_scenario" })
            Assert.Equal(0, await CountAsync(host, prepared.Id, provider, table));
        Assert.Equal(controlTotal, await SnapshotRowsAsync(host, prepared.Id, provider, "gl_control_total"));

        var reopened = await LoadAsync(host, prepared.Id);
        AssertClearedState(reopened);
        Assert.Empty(reopened.GetProperty("filterScenarios").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("latestRuns").GetProperty("validate").ValueKind);
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("latestRuns").GetProperty("prescreen").ValueKind);
        AssertReports(reports, reopened, stale: true);
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task FailedTbChange_RollsBackSourceMappingResultsAndScenarios(string provider, string change)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        var saved = await host.DispatchAsync("filter.commit", Scenarios);
        prepared = prepared with { Revision = saved.GetProperty("resultRef").GetProperty("revision").GetString()! };
        var reports = await ExportBothAsync(host, prepared);
        var before = await LoadAsync(host, prepared.Id);
        var tables = new[] { "staging_tb_raw_row", "target_tb_balance", "result_rule_run", "result_inf_sampling_test_sample", "result_filter_run", "config_filter_scenario", "gl_control_total" };
        var rows = new Dictionary<string, string[]>();
        foreach (var table in tables) rows[table] = await SnapshotRowsAsync(host, prepared.Id, provider, table);
        Assert.Equal(2, rows["config_filter_scenario"].Length);
        Assert.Equal(2, rows["result_filter_run"].Length);

        if (change == "mapping")
        {
            var mapping = JsonNode.Parse(before.GetProperty("mapping").GetProperty("tb").GetRawText())!;
            // 已存在的文字來源欄讓投影進入交易後失敗，而不是在 payload 檢查時被拒絕。
            mapping["mapping"]!["amount"] = "科目名稱";
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("mapping.commit.tb", mapping.ToJsonString()));
            Assert.Equal("projection_failed", error.Code);
        }
        else
        {
            var path = Path.Combine(host.ProjectsRoot, "synthetic-broken-tb.csv");
            var validPrefix = Encoding.UTF8.GetBytes("科目代號,科目名稱,變動金額\n" + string.Concat(Enumerable.Repeat("1000,合成資產,10\n", 21000)));
            await File.WriteAllBytesAsync(path, validPrefix.Concat(new byte[] { 0xff, 0xff }).ToArray());
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("import.tb.fromFile",
                JsonSerializer.Serialize(new { filePath = path, mode = "replace", encoding = "utf-8", delimiter = "," })));
            Assert.Equal("file_read_error", error.Code);
        }

        var after = await LoadAsync(host, prepared.Id);
        foreach (var property in new[] { "filterScenarios", "mapping", "latestRuns", "staleState", "importState" })
            Assert.Equal(before.GetProperty(property).GetRawText(), after.GetProperty(property).GetRawText());
        foreach (var table in tables)
            Assert.Equal(rows[table], await SnapshotRowsAsync(host, prepared.Id, provider, table));
        AssertReports(reports, after, stale: false);
    }

    private static void AssertClearedState(JsonElement response)
    {
        var state = response.GetProperty("staleState");
        Assert.True(state.GetProperty("validation").GetBoolean());
        Assert.True(state.GetProperty("prescreen").GetBoolean());
        Assert.False(state.GetProperty("filter").GetBoolean());
    }

    private static Task<JsonElement> LoadAsync(HandlerTestHost host, string id) =>
        host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));

    private sealed record Exported(string Id, string Path, byte[] Bytes);

    private static async Task<Exported[]> ExportBothAsync(HandlerTestHost host, Batch9ArtifactStateTests.Prepared prepared)
    {
        var criteria = await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new
        { validationRunId = prepared.ValidationRunId, revision = prepared.Revision }));
        var paper = await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        { validationRunId = prepared.ValidationRunId, scenarioRevision = prepared.Revision, scenarioPositions = new[] { 1, 2 } }));
        return new[] { criteria, paper }.Select(response =>
        {
            var artifact = response.GetProperty("artifact");
            Assert.False(artifact.GetProperty("stale").GetBoolean());
            var path = artifact.GetProperty("fullPath").GetString()!;
            Assert.InRange(new FileInfo(path).Length, 1, 10_000_000);
            return new Exported(artifact.GetProperty("artifactId").GetString()!, path, File.ReadAllBytes(path));
        }).ToArray();
    }

    private static void AssertReports(Exported[] reports, JsonElement response, bool stale)
    {
        foreach (var report in reports)
        {
            var entry = response.GetProperty("reportArtifacts").EnumerateArray()
                .Single(item => item.GetProperty("artifactId").GetString() == report.Id);
            Assert.Equal(stale, entry.GetProperty("stale").GetBoolean());
            Assert.Equal(report.Bytes, File.ReadAllBytes(report.Path));
        }
    }

    private static ILocalProjectDatabase Database(HandlerTestHost host, string provider)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        return provider == "duckdb" ? new DuckDbProjectDatabase(folder) : new SqliteProjectDatabase(folder);
    }

    private static async Task<long> CountAsync(HandlerTestHost host, string id, string provider, string table)
    {
        await using var connection = Database(host, provider).CreateConnection(id);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string[]> SnapshotRowsAsync(HandlerTestHost host, string id, string provider, string table)
    {
        await using var connection = Database(host, provider).CreateConnection(id);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table};";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var values = Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))
                .ToArray();
            rows.Add(JsonSerializer.Serialize(values));
        }
        return rows.Order(StringComparer.Ordinal).ToArray();
    }
}
