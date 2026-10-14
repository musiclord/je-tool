using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 文字批次 C：第五步情境的原因句（使用者 2026-10-13 兩輪勾選頁確認；裁定見計畫「收尾四項」一節）。
/// 損毀的條件開案時只寫一次「條件內容無法讀取」、儲存時標在那一條條件、原句與英文代碼只進支援日誌；
/// 已移除的欄位寫出名稱；科目分類寫第四步分類設定裡目前的名稱。SQLite 與 DuckDB 都要成立。
/// 預期句子一律寫死，不由被測程式算出。所有資料都是合成值。
/// </summary>
public sealed class ClosingTextBatchCTests
{
    private const string OlderVersion = "filter-2026-10-06-v18";
    private const string CorruptRuleSentence = "此條件內容有誤，無法儲存。請刪除此條件後重新設定。";

    // ── 開案：損毀的舊情境 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OnLoad_CorruptOldScenario_ListsUnreadableOnceWithoutCodesAndLogsTheReasons(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await CommitTwoAsync(host);
        // 第 1 組的結合方式與第 2 條的借貸限定都損毀；第 3 條是審計員看得懂的已停用條件。
        await RewriteDefinitionAsync(host, context.ProjectId, provider, 1, definition =>
        {
            definition["logicVersion"] = OlderVersion;
            definition["groups"]![0]!["join"] = "SYNTHXOR";
            var rules = definition["groups"]![0]!["rules"]!.AsArray();
            rules.Add(JsonNode.Parse("""{"join":"AND","type":"drCrOnly","drCr":"synthsideways"}"""));
            rules.Add(JsonNode.Parse("""{"join":"AND","type":"prescreen","prescreenKey":"backdatedPosting"}"""));
        });

        var loaded = await LoadAsync(host, context.ProjectId);

        var check = loaded.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Equal(1, problem.GetProperty("position").GetInt32());
        Assert.Equal(
            new[] { "條件內容無法讀取", "第 1 組第 3 條：「回溯過帳」條件已停用，請刪除此條件" },
            problem.GetProperty("messages").EnumerateArray().Select(item => item.GetString()!).ToArray());
        // 回應的原因不含英文代碼或損毀的原值。
        Assert.DoesNotMatch(new Regex("[A-Za-z]", RegexOptions.CultureInvariant),
            string.Concat(problem.GetProperty("messages").EnumerateArray().Select(item => item.GetString())));

        var lines = await ExportSupportLogAsync(host);
        var events = lines.Where(line => line.Contains("filter.scenario_unreadable", StringComparison.Ordinal)
            && line.Contains("project.load", StringComparison.Ordinal)).ToArray();
        Assert.Contains(events, line => line.Contains("group_join_invalid", StringComparison.Ordinal));
        Assert.Contains(events, line => line.Contains("drcr_only_invalid", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("synthsideways", StringComparison.Ordinal)
            || line.Contains("SYNTHXOR", StringComparison.Ordinal));
    }

    // ── 儲存：條件內容損毀 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("sqlite", "validator")]
    [InlineData("duckdb", "validator")]
    [InlineData("sqlite", "parser")]
    [InlineData("duckdb", "parser")]
    public async Task Save_CorruptRule_IsMarkedOnThatRuleWithTheGenericSentence(string provider, string where)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await LoadAsync(host, context.ProjectId);
        // validator：借貸限定的值不在允許值內；parser：文字值集合的 values 不是陣列。兩者都只有程式送錯或存檔損毀才會出現。
        var corrupt = where == "validator"
            ? """{"join":"AND","type":"drCrOnly","drCr":"synthsideways"}"""
            : """{"join":"AND","type":"textSet","field":"description","mode":"exact","values":"synth-not-an-array"}""";

        var error = await Assert.ThrowsAsync<JetActionException>(() => SaveNewAsync(host, corrupt));

        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
        Assert.Equal("第 1 組第 2 條：" + CorruptRuleSentence, error.Message);
        var detail = Assert.Single(error.Details!);
        Assert.Equal(1, detail.Group);
        Assert.Equal(2, detail.Rule);
        Assert.Equal(CorruptRuleSentence, detail.Message);
        Assert.Equal(1, detail.ScenarioPosition);

        var lines = await ExportSupportLogAsync(host);
        Assert.Contains(lines, line => line.Contains("filter.scenario_unreadable", StringComparison.Ordinal)
            && line.Contains("filter.scenario.save", StringComparison.Ordinal)
            && line.Contains(where == "validator" ? "drcr_only_invalid" : "text_set_values_not_array", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("synthsideways", StringComparison.Ordinal)
            || line.Contains("synth-not-an-array", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Save_CorruptEditorOrigins_WithoutPosition_UsesTheGenericSentenceForTheWholeScenario(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await LoadAsync(host, context.ProjectId);
        var scenario = NewScenario("""{"join":"AND","type":"drCrOnly","drCr":"credit"}""");
        scenario["editorOrigins"] = JsonNode.Parse("""{"version":1,"groups":[]}""");

        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "filter.scenario.save",
            new JsonObject { ["populationScope"] = "auditPeriod", ["position"] = null, ["scenario"] = scenario }.ToJsonString()));

        Assert.Equal(CorruptRuleSentence, error.Message);
        var detail = Assert.Single(error.Details!);
        Assert.Null(detail.Group);
        Assert.Null(detail.Rule);
        Assert.Equal(CorruptRuleSentence, detail.Message);
    }

    // ── 已移除的欄位寫出名稱 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("sqlite", """{"join":"AND","type":"dateRange","field":"voucherDate","from":"2025-01-01"}""", "第 1 組第 2 條：「傳票日期」已移除，請刪除此條件")]
    [InlineData("duckdb", """{"join":"AND","type":"dateRange","field":"voucherDate","from":"2025-01-01"}""", "第 1 組第 2 條：「傳票日期」已移除，請刪除此條件")]
    [InlineData("sqlite", """{"join":"AND","type":"fieldValue","field":"voucherDate","operator":"isBlank"}""", "第 1 組第 2 條：「傳票日期」已移除，請刪除此條件")]
    [InlineData("duckdb", """{"join":"AND","type":"fieldValue","field":"voucherDate","operator":"isBlank"}""", "第 1 組第 2 條：「傳票日期」已移除，請刪除此條件")]
    [InlineData("sqlite", """{"join":"AND","type":"dateRange","field":"synthRetiredField","from":"2025-01-01"}""", "第 1 組第 2 條：此條件使用的欄位已不存在")]
    [InlineData("duckdb", """{"join":"AND","type":"dateRange","field":"synthRetiredField","from":"2025-01-01"}""", "第 1 組第 2 條：此條件使用的欄位已不存在")]
    public async Task OnLoad_OldScenarioWithRemovedField_NamesTheFieldOrSaysItNoLongerExists(
        string provider, string rule, string expected)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await CommitTwoAsync(host);
        await RewriteDefinitionAsync(host, context.ProjectId, provider, 1, definition =>
        {
            definition["logicVersion"] = OlderVersion;
            definition["groups"]![0]!["rules"]!.AsArray().Add(JsonNode.Parse(rule));
        });

        var loaded = await LoadAsync(host, context.ProjectId);

        var problem = Assert.Single(loaded.GetProperty("filterScenarioCheck").GetProperty("problems").EnumerateArray());
        Assert.Equal(new[] { expected }, problem.GetProperty("messages").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    // ── 科目分類寫目前的名稱 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task RenamedCategories_RevenueAndCounterpartReasonsUseTheCurrentNames(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(
            host, importAccountMapping: false, runValidation: false, databaseProvider: provider);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AccountTaxonomyBuiltIns.RevenueId] = "合成營業收入",
            [AccountTaxonomyBuiltIns.ReceivablesId] = "合成應收帳款",
            [AccountTaxonomyBuiltIns.CashId] = "合成現金",
            [AccountTaxonomyBuiltIns.ReceiptInAdvanceId] = "合成預收款項"
        };
        await host.DispatchAsync("accountTaxonomy.save", JsonSerializer.Serialize(new
        {
            revision = 1,
            categories = AccountTaxonomyBuiltIns.All.Select(item => new
            {
                categoryId = item.CategoryId,
                label = renamed.GetValueOrDefault(item.CategoryId, item.Label),
                ordinal = item.Ordinal,
                semanticRole = item.SemanticRole
            })
        }));
        await host.DispatchAsync("validate.run");
        await LoadAsync(host, context.ProjectId);

        var error = await Assert.ThrowsAsync<JetActionException>(() => SaveNewAsync(
            host, """{"join":"AND","type":"revenueWithoutNormalCounterpart"}"""));

        Assert.Equal(
            new[]
            {
                "科目配對中尚無歸類為「合成營業收入」的科目，請先在第四步設定",
                "科目配對中尚無歸類為「合成應收帳款」「合成現金」或「合成預收款項」的科目，請先在第四步設定"
            },
            error.Details!.Where(detail => detail.Rule == 2).Select(detail => detail.Message).ToArray());
    }

    // ── 重新計算時單獨出現的句子 ─────────────────────────────────────────────────

    [Fact]
    public void CannotRecalculate_WritesScenarioReasonsAndNextStepInOneSentence()
    {
        Assert.Equal(
            "情境 2 目前無法重新計算：第 1 組第 1 條：「客戶名稱」尚未選擇來源欄；條件內容無法讀取。設定仍保留，請到「進階條件篩選」修改後儲存。",
            FilterScenarioRuleMessages.CannotRecalculate(2, ["第 1 組第 1 條：「客戶名稱」尚未選擇來源欄", "條件內容無法讀取"]));
    }

    // ── 佈置 ────────────────────────────────────────────────────────────────────

    private static Task<JsonElement> LoadAsync(HandlerTestHost host, string projectId) =>
        host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));

    private static Task<JsonElement> CommitTwoAsync(HandlerTestHost host) =>
        host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[]
            {
                new
                {
                    name = "情境甲",
                    rationale = "情境甲的合成動機",
                    groups = new[] { new { join = "AND", rules = new[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } } } }
                },
                new
                {
                    name = "情境乙",
                    rationale = "情境乙的合成動機",
                    groups = new[] { new { join = "AND", rules = new[] { new { join = "AND", type = "drCrOnly", drCr = "credit" } } } }
                }
            }
        }));

    /// <summary>第 1 組第 1 條是合法的借方限定，第 2 條是 <paramref name="secondRule"/>。</summary>
    private static JsonObject NewScenario(string secondRule) => new()
    {
        ["name"] = "合成情境",
        ["rationale"] = "合成動機",
        ["groups"] = new JsonArray(new JsonObject
        {
            ["join"] = "AND",
            ["rules"] = new JsonArray(
                JsonNode.Parse("""{"join":"AND","type":"drCrOnly","drCr":"debit"}"""),
                JsonNode.Parse(secondRule))
        })
    };

    private static Task<JsonElement> SaveNewAsync(HandlerTestHost host, string secondRule) =>
        host.DispatchAsync("filter.scenario.save", new JsonObject
        {
            ["populationScope"] = "auditPeriod",
            ["position"] = null,
            ["scenario"] = NewScenario(secondRule)
        }.ToJsonString());

    private static async Task<string[]> ExportSupportLogAsync(HandlerTestHost host)
    {
        var export = await host.DispatchAsync("support.log.export", "{}");
        return await File.ReadAllLinesAsync(export.GetProperty("filePath").GetString()!);
    }

    private static ILocalProjectDatabase Database(HandlerTestHost host, string provider)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        return provider == "sqlite" ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);
    }

    private static async Task RewriteDefinitionAsync(
        HandlerTestHost host, string projectId, string provider, int position, Action<JsonObject> mutate)
    {
        string definitionJson;
        await using (var connection = Database(host, provider).CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT definition_json FROM config_filter_scenario WHERE position = @position;";
            command.AddWithValue("@position", (long)position);
            definitionJson = (string)(await command.ExecuteScalarAsync())!;
        }

        var definition = JsonNode.Parse(definitionJson)!.AsObject();
        mutate(definition);
        await using (var connection = Database(host, provider).CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE config_filter_scenario SET definition_json = @definition WHERE position = @position;";
            command.AddWithValue("@definition", definition.ToJsonString());
            command.AddWithValue("@position", (long)position);
            await command.ExecuteNonQueryAsync();
        }
    }
}
