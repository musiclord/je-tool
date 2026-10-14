using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 使用者 2026-10-08 裁定移除傳票日期（voucherDate）與回溯過帳（backdatedPosting）。
/// 專案尚未正式發布，舊案件可以重建；這裡只確認移除前建立的案件重開時有清楚、不崩潰的結果：
/// 舊資料表多出的 voucher_date 欄不妨礙重新投影，舊配對的 voucherDate 鍵讀出時略過，
/// 舊預篩選摘要視為過期，含回溯過帳條件的舊情境列在待修正清單。
/// </summary>
public sealed class VoucherDateRemovalOldCaseTests
{
    private const string OldFilterVersion = "filter-2026-10-06-v18";
    private const string OldPrescreenVersion = "prescreen-2026-10-04-v9";

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OldCaseWithVoucherDateAndBackdatedPosting_OpensWithClearResults(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("prescreen.run");
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new
            {
                name = "舊回溯情境",
                rationale = "合成舊案件",
                groups = new[] { new { join = "AND", rules = new[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } } } }
            } }
        }));

        // 模擬移除前的案件：資料表有 voucher_date 欄、配對有 voucherDate 鍵、
        // 預篩選摘要帶 backdatedPosting 且是舊版本、情境含回溯過帳條件且是舊版本。
        await ExecuteAsync(host, context.ProjectId, provider, "ALTER TABLE target_gl_entry ADD COLUMN voucher_date TEXT NULL;");
        var mappingJson = await TextAsync(host, context.ProjectId, provider,
            "SELECT mapping_json FROM config_field_mapping WHERE dataset_kind = 'gl';");
        var oldMapping = JsonNode.Parse(mappingJson)!.AsObject();
        oldMapping["voucherDate"] = "舊傳票日期欄";
        await ExecuteAsync(host, context.ProjectId, provider,
            "UPDATE config_field_mapping SET mapping_json = @json WHERE dataset_kind = 'gl';",
            ("@json", oldMapping.ToJsonString()));
        var summaryJson = await TextAsync(host, context.ProjectId, provider,
            "SELECT summary_json FROM result_rule_run WHERE run_kind = 'prescreen';");
        var oldSummary = JsonNode.Parse(summaryJson)!.AsObject();
        oldSummary["backdatedPosting"] = JsonNode.Parse("""{"status":"V","naReason":null,"count":44}""");
        oldSummary["resultRef"]!["logicVersion"] = OldPrescreenVersion;
        await ExecuteAsync(host, context.ProjectId, provider,
            "UPDATE result_rule_run SET summary_json = @json WHERE run_kind = 'prescreen';",
            ("@json", oldSummary.ToJsonString()));
        var definitionJson = await TextAsync(host, context.ProjectId, provider,
            "SELECT definition_json FROM config_filter_scenario WHERE position = 1;");
        var oldDefinition = JsonNode.Parse(definitionJson)!.AsObject();
        oldDefinition["logicVersion"] = OldFilterVersion;
        oldDefinition["groups"]![0]!["rules"]!.AsArray().Add(JsonNode.Parse(
            """{"join":"AND","type":"prescreen","prescreenKey":"backdatedPosting"}"""));
        await ExecuteAsync(host, context.ProjectId, provider,
            "UPDATE config_filter_scenario SET definition_json = @json WHERE position = 1;",
            ("@json", oldDefinition.ToJsonString()));

        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        // 舊情境不自動改版，列出停用的條件，讓審計員刪除後再儲存。
        var check = loaded.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Equal(1, problem.GetProperty("position").GetInt32());
        Assert.Contains(
            problem.GetProperty("messages").EnumerateArray().Select(message => message.GetString()),
            message => message!.Contains("「回溯過帳」條件已停用", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("filterResultRef").ValueKind);

        // 舊預篩選摘要是舊版本，不當成目前結果。
        Assert.True(loaded.GetProperty("staleState").GetProperty("prescreen").GetBoolean());
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("latestRuns").GetProperty("prescreen").ValueKind);

        // 舊配對的 voucherDate 鍵讀出時略過，可以原樣重新確認。
        var gl = loaded.GetProperty("mapping").GetProperty("gl");
        Assert.False(gl.GetProperty("mapping").TryGetProperty("voucherDate", out _));
        var recommit = new JsonObject();
        foreach (var key in new[] { "mapping", "amountMode", "approvalDateMode", "postingStatusPolicy", "manualAutoPolicy", "rdeFields" })
        {
            if (gl.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null)
            {
                recommit[key] = JsonNode.Parse(value.GetRawText());
            }
        }

        await host.DispatchAsync("mapping.commit.gl", recommit.ToJsonString());

        // 舊資料表多出的 voucher_date 欄不妨礙重新投影，新投影不寫這個欄位。
        Assert.Equal(
            (long)DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher,
            await ScalarAsync(host, context.ProjectId, provider, "SELECT COUNT(*) FROM target_gl_entry;"));
        Assert.Equal(0, await ScalarAsync(host, context.ProjectId, provider,
            "SELECT COUNT(*) FROM target_gl_entry WHERE voucher_date IS NOT NULL;"));

        await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        Assert.False(prescreen.TryGetProperty("backdatedPosting", out _));
        Assert.Equal(RuleLogicVersions.Prescreen,
            prescreen.GetProperty("resultRef").GetProperty("logicVersion").GetString());
    }

    private static ILocalProjectDatabase Database(HandlerTestHost host, string provider)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        return provider == "sqlite" ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);
    }

    private static async Task ExecuteAsync(
        HandlerTestHost host, string projectId, string provider, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = Database(host, provider).CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> TextAsync(HandlerTestHost host, string projectId, string provider, string sql)
    {
        await using var connection = Database(host, provider).CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> ScalarAsync(HandlerTestHost host, string projectId, string provider, string sql)
    {
        await using var connection = Database(host, provider).CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
