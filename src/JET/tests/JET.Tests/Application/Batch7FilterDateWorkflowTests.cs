using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch7FilterDateWorkflowTests
{
    private const string RdeDateId = "rde.b7000000000000000000000000000003";

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ProjectDateOptions_FlowThroughPreviewCommitReopenVoucherLazyHitsAndExport(string provider)
    {
        using var host = new HandlerTestHost();
        var id = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("D1", "2025-06-11", "1000", "合成", "first", 10, 1)
            .AddRow("D2", "2025-06-12", "1000", "合成", "next", 10, 1)
            .AddRow("SERIAL", "2024-01-01", "1000", "合成", "serial", 10, 1),
            databaseProvider: provider, periodStart: "2024-01-01", validateForDownstream: true);
        foreach (var raw in new[] { "2025/6/11", "2025.6.11", "20250611", "114/6/11", "1140611" })
            await AssertPreviewAsync(host, DateScenario(raw), ["D1"]);
        // 預篩選收斂計畫階段七（使用者 2026-10-22 裁定）：第五步的日期也不再把純數字當成 Excel 序列值。
        // 「45292」原本期待命中 SERIAL 那一列（2024-01-01），現在和 2025、2025.12 一樣是無法辨識的日期，情境無效並寫出那個值。
        // 第一次失敗：收據 20261024-044716893-6a6900c538364667949441681dcd3b6d。
        foreach (var plain in new[] { "45292", "2025", "2025.12" })
        {
            var invalid = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.preview",
                JsonSerializer.Serialize(new { scenario = DateScenario(plain) })));
            Assert.Equal(JetErrorCodes.InvalidScenario, invalid.Code);
            Assert.Contains($"日期「{plain}」無法辨識", invalid.Message, StringComparison.Ordinal);
        }
        var list = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.preview",
            JsonSerializer.Serialize(new { scenario = DateListScenario("2025", "2025.12", "45292") })));
        Assert.Equal(JetErrorCodes.InvalidScenario, list.Code);
        // 清單逐值檢查，三個值都要寫出來。
        Assert.Contains("清單第 1 個值：日期「2025」無法辨識", list.Message, StringComparison.Ordinal);
        Assert.Contains("清單第 2 個值：日期「2025.12」無法辨識", list.Message, StringComparison.Ordinal);
        Assert.Contains("清單第 3 個值：日期「45292」無法辨識", list.Message, StringComparison.Ordinal);

        var scenario = DateScenario("114/6/11");
        var committed = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString();
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.Equal("114/6/11", loaded.GetProperty("filterScenarios")[0].GetProperty("groups")[0].GetProperty("rules")[0].GetProperty("value").GetString());
        var vouchers = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenarioPosition = 1, scenarioRevision = revision }));
        Assert.Equal(["D1"], DocumentNumbers(vouchers.GetProperty("rows")));

        var folder = new JetProjectFolder(host.ProjectsRoot);
        ILocalProjectDatabase database = provider == "duckdb" ? new DuckDbProjectDatabase(folder) : new SqliteProjectDatabase(folder);
        await using (var connection = database.CreateConnection(id))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE config_result_stale_state SET filter_stale = 1 WHERE singleton = 1;";
            await command.ExecuteNonQueryAsync();
        }
        var hits = await host.DispatchAsync("query.filterHitsPage", JsonSerializer.Serialize(new { scenarioPosition = 1, scenarioRevision = revision }));
        Assert.Equal(["D1"], DocumentNumbers(hits.GetProperty("rows")));
        var report = await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new
        {
            validationRunId = loaded.GetProperty("latestRuns").GetProperty("validate").GetProperty("resultRef").GetProperty("runId").GetString(), revision
        }));
        Assert.True(File.Exists(report.GetProperty("artifact").GetProperty("fullPath").GetString()));

        // Synthetic project metadata selects the same date option used by GL import; it does not change source rows.
        var store = new JsonFileProjectStore(folder);
        var project = (await store.FindAsync(id, CancellationToken.None))!;
        await store.SaveAsync(project with { RocDateEnabled = false }, CancellationToken.None);
        var disabled = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario })));
        Assert.Equal(JetErrorCodes.InvalidScenario, disabled.Code);
        await AssertPreviewAsync(host, DateScenario("2025/6/11"), ["D1"]);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task RdeAndCoreDateSql_UseFixedNormalizedDateAnswers(string provider)
    {
        var sql = $$"""
            INSERT INTO target_gl_entry
              (batch_id, source_row_number, document_number, post_date, account_code, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_effective)
            VALUES ('batch7', 1, 'D1', '2025-06-11', '1000', 10, 10, 0, 'DEBIT', 1),
                   ('batch7', 2, 'D2', '2025-06-12', '1000', 10, 10, 0, 'DEBIT', 1),
                   ('batch7', 3, 'D3', '2025-07-01', '1000', 10, 10, 0, 'DEBIT', 1);
            INSERT INTO target_gl_rde_value (entry_id, field_id, value_type, date_value)
            SELECT entry_id, '{{RdeDateId}}', 'date', post_date FROM target_gl_entry;
            """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, sql);
        var context = new FilterRuleContext(1, null, "2025-01-01", "2025-12-31")
        {
            RdeFields = [new GlRdeFieldMetadata(RdeDateId, "SyntheticDate", "合成日期", "date")]
        };
        var cases = new (string Rule, string[] Expected)[]
        {
            ($$"""{"type":"typed","fieldId":"{{RdeDateId}}","operator":"on","value":"114/6/11"}""", ["D1"]),
            ($$"""{"type":"typed","fieldId":"{{RdeDateId}}","operator":"between","from":"2025/6/11","to":"114/6/12"}""", ["D1", "D2"]),
            ($$"""{"type":"fieldValue","fieldId":"{{RdeDateId}}","operator":"in","values":["114/6/11","20250611","2025.6.12"]}""", ["D1", "D2"]),
            ("""{"type":"fieldValue","field":"postDate","operator":"between","from":"114/6/11","to":"2025.6.12"}""", ["D1", "D2"])
        };
        foreach (var (rule, expected) in cases)
        {
            using var json = JsonDocument.Parse($$"""{"name":"合成日期","rationale":"固定答案","groups":[{"rules":[{{rule}}]}]}""");
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, FilterScenarioPayloadParser.Parse(json.RootElement, 1), context, CancellationToken.None);
            Assert.Equal(expected, result.PreviewRows.Select(row => row.DocumentNumber).Order(StringComparer.Ordinal));
            Assert.Equal(expected.Length, result.Count);
        }
    }

    private static JsonElement DateScenario(string value) => JsonSerializer.SerializeToElement(new
    {
        name = "合成日期", rationale = "固定答案", groups = new[] { new { rules = new[] { new { type = "fieldValue", field = "postDate", @operator = "on", value } } } }
    });

    private static JsonElement DateListScenario(params string[] values) => JsonSerializer.SerializeToElement(new
    {
        name = "合成日期", rationale = "固定答案", groups = new[] { new { rules = new[] { new { type = "fieldValue", field = "postDate", @operator = "in", values } } } }
    });

    private static async Task AssertPreviewAsync(HandlerTestHost host, JsonElement scenario, string[] expected)
    {
        var preview = (await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }))).GetProperty("scenario");
        Assert.Equal(expected.Length, preview.GetProperty("count").GetInt64());
        Assert.Equal(expected, DocumentNumbers(preview.GetProperty("previewRows")));
    }

    private static string?[] DocumentNumbers(JsonElement rows) => rows.EnumerateArray()
        .Select(row => row.GetProperty("documentNumber").GetString()).Order(StringComparer.Ordinal).ToArray();
}
