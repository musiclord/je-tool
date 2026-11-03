using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 第五步儲存情境時，只計算並存下這一個情境的結果；檢視、移除與匯出都讀存好的結果。
/// 其他情境的結果照資料庫原樣保留，不重算；上游修改照裁定 2026-10-07-03 清掉全部情境與結果。
/// SQLite 與 DuckDB 都要成立。
/// </summary>
/// <remarks>
/// 「沒有重算」用一個只有重算才會改回來的標記核對：存好結果之後，直接從資料庫刪掉某幾筆分錄的結果列，
/// 之後的動作如果重算了這個情境，那幾筆會再出現；照原樣保留時就不會。標記只用分錄編號刪除，
/// 不依賴結果表的情境欄名，所以改結構之前與之後用同一句 SQL。
/// 第一次失敗的收據 20261031-013636783-12a6daabb1d4490295d4887607c9afa9：改寫法之前執行，20 則失敗，都是因為儲存、移除與匯出整批重算、
/// 檢視即時重新判斷條件，或還沒有存下符合第幾組第幾條的結果表；和即時計算逐列比對的兩則與上游修改後過期的兩則照舊通過。
/// </remarks>
public sealed class FilterHitPerScenarioTests
{
    // ── 儲存與修改只算自己 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SavingAnotherScenario_KeepsTheEarlierScenarioResultsAsStored(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await SaveNewAsync(host, Scenario("借方分錄", DrCr("debit")));
        var debitHits = RowHitCount(await SummaryAsync(host), 1);
        var context = new ProjectContext(setup.ProjectId);
        var marked = await MarkFirstHitAsync(host, context, provider);
        Assert.Equal(1, marked);

        await SaveNewAsync(host, Scenario("貸方分錄", DrCr("credit")));

        var summary = await SummaryAsync(host);
        Assert.Equal(debitHits - 1, RowHitCount(summary, 1));
        Assert.Equal(await PreviewRowCountAsync(host, Scenario("貸方分錄", DrCr("credit"))), RowHitCount(summary, 2));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ModifyingOneScenario_ReplacesOnlyItsOwnResults(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await SaveNewAsync(host, Scenario("情境甲", DrCr("debit")));
        var saved = await SaveNewAsync(host, Scenario("情境乙", DrCr("debit")));
        var fullHits = RowHitCount(await SummaryAsync(host), 1);
        // 同一筆分錄兩個情境都符合，刪掉後兩個情境各少一筆。
        Assert.Equal(2, await MarkFirstHitAsync(host, new ProjectContext(setup.ProjectId), provider));

        var edited = Scenario("情境甲", DrCr("debit"));
        edited["rationale"] = "只改動機";
        await SaveAsync(host, 1, NameAt(saved, 1), edited);

        var summary = await SummaryAsync(host);
        Assert.Equal(fullHits, RowHitCount(summary, 1));
        Assert.Equal(fullHits - 1, RowHitCount(summary, 2));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task RemovingTheMiddleScenario_KeepsTheOtherResultsAsStored(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await SaveNewAsync(host, Scenario("情境甲", DrCr("debit")));
        await SaveNewAsync(host, Scenario("情境乙", DrCr("credit")));
        var saved = await SaveNewAsync(host, Scenario("情境丙", DrCr("debit")));
        var debitHits = RowHitCount(await SummaryAsync(host), 1);
        Assert.Equal(2, await MarkFirstHitAsync(host, new ProjectContext(setup.ProjectId), provider));

        var removed = await host.DispatchAsync("filter.scenario.remove", new JsonObject
        {
            ["populationScope"] = "auditPeriod",
            ["position"] = 2,
            ["expectedName"] = NameAt(saved, 2)
        }.ToJsonString());

        Assert.Equal(["情境甲", "情境丙"], Names(removed));
        var summary = await SummaryAsync(host);
        Assert.Equal(debitHits - 1, RowHitCount(summary, 1));
        Assert.Equal(debitHits - 1, RowHitCount(summary, 2));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task RemovingAScenario_DeletesOnlyItsOwnStoredRows(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await SaveNewAsync(host, Scenario("情境甲", DrCr("debit")));
        await SaveNewAsync(host, AndScenario("情境乙"));
        var saved = await SaveNewAsync(host, Scenario("情境丙", DrCr("credit")));
        var context = new ProjectContext(setup.ProjectId);
        var before = await StoredRowsAsync(host, context, provider);
        var secondId = before.Scenarios.Single(row => row.Position == 2).ScenarioId;
        Assert.Contains(before.Hits, row => row.ScenarioId == secondId);
        Assert.Contains(before.Conditions, row => row.ScenarioId == secondId);

        await host.DispatchAsync("filter.scenario.remove", new JsonObject
        {
            ["populationScope"] = "auditPeriod",
            ["position"] = 2,
            ["expectedName"] = NameAt(saved, 2)
        }.ToJsonString());

        var after = await StoredRowsAsync(host, context, provider);
        Assert.Equal(before.Hits.Where(row => row.ScenarioId != secondId), after.Hits);
        Assert.Equal(before.Conditions.Where(row => row.ScenarioId != secondId), after.Conditions);
        Assert.Equal(
            before.Scenarios.Where(row => row.ScenarioId != secondId).Select(row => (row.ScenarioId, row.SavedUtc)),
            after.Scenarios.Select(row => (row.ScenarioId, row.SavedUtc)));
        Assert.Equal([1, 2], after.Scenarios.Select(row => row.Position));
    }

    // ── 檢視讀存好的結果 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SavedScenarioVouchers_ReadTheStoredHits(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var definition = Scenario("借方分錄", DrCr("debit"));
        var saved = await SaveNewAsync(host, definition);
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var context = new ProjectContext(setup.ProjectId);
        var draft = await host.DispatchAsync("query.filterVoucherPage", DraftRequest(definition).ToJsonString());
        var voucher = await ScalarTextAsync(host, context, provider,
            "SELECT voucher_number FROM gl WHERE entry_id = (SELECT MIN(entry_id) FROM filter_hit);");
        // 這張傳票的結果列全部刪掉：讀存好的結果時，它不再是符合條件的傳票。
        await ExecuteAsync(host, context, provider,
            "DELETE FROM filter_hit WHERE entry_id IN (SELECT entry_id FROM gl WHERE voucher_number = @voucher);",
            ("@voucher", voucher));

        var page = await host.DispatchAsync("query.filterVoucherPage", SavedRequest(1, revision).ToJsonString());
        var searched = await host.DispatchAsync("query.filterVoucherPage",
            SavedRequest(1, revision, search: voucher).ToJsonString());

        Assert.Equal(draft.GetProperty("totalCount").GetInt64() - 1, page.GetProperty("totalCount").GetInt64());
        Assert.DoesNotContain(voucher, VoucherNumbers(searched));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SavedScenarioEntries_ReadTheStoredConditions(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var definition = AndScenario("借方現金");
        var saved = await SaveNewAsync(host, definition);
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var context = new ProjectContext(setup.ProjectId);
        var page = await host.DispatchAsync("query.filterVoucherPage", SavedRequest(1, revision).ToJsonString());
        var voucher = VoucherNumbers(page).First();
        // 這張傳票各分錄符合第幾組第幾條的紀錄全部刪掉：讀存好的結果時，說明只剩符合條件與參考分錄。
        await ExecuteAsync(host, context, provider,
            "DELETE FROM filter_hit_condition WHERE entry_id IN (SELECT entry_id FROM gl WHERE voucher_number = @voucher);",
            ("@voucher", voucher));

        var rows = await host.DispatchAsync("query.filterVoucherRowsPage",
            SavedRequest(1, revision, voucher: voucher, queryRevision: page.GetProperty("queryRevision").GetString()).ToJsonString());

        Assert.All(rows.GetProperty("rows").EnumerateArray(), row =>
        {
            Assert.Empty(row.GetProperty("primaryConditions").EnumerateArray());
            Assert.DoesNotContain("符合主要條件", row.GetProperty("matchDescription").GetString(), StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SavedScenarioVouchers_MatchTheLiveDraftRowForRow(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var scenarios = new[]
        {
            AndScenario("借方現金"),
            OrScenario("兩組任一"),
            VoucherScenario("貸方且整張傳票有借方現金"),
            VoucherOnlyScenario("只有整張傳票條件"),
            Scenario("未預期借貸組合", new JsonObject { ["type"] = "prescreen", ["prescreenKey"] = "unexpectedAccountPair" })
        };
        JsonElement saved = default;
        foreach (var scenario in scenarios)
        {
            saved = await SaveNewAsync(host, scenario.DeepClone().AsObject());
        }

        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var partialEvidenceSeen = false;
        var wholeVoucherSeen = false;
        for (var position = 1; position <= scenarios.Length; position++)
        {
            var definition = scenarios[position - 1];
            foreach (var (sort, search) in new (JsonObject? Sort, string? Search)[]
            {
                (null, null),
                (new JsonObject { ["key"] = "voucherTotal", ["direction"] = "desc" }, null),
                (new JsonObject { ["key"] = "hitRowCount", ["direction"] = "asc" }, null),
                (null, "1")
            })
            {
                var live = await ReadAllVoucherPagesAsync(host, DraftRequest(definition, sort, search));
                var stored = await ReadAllVoucherPagesAsync(host, SavedRequest(position, revision, sort: sort, search: search));
                Assert.Equal(live.TotalCount, stored.TotalCount);
                Assert.Equal(live.Rows, stored.Rows);
                Assert.Equal(live.Columns, stored.Columns);
                Assert.Equal(live.ConditionText, stored.ConditionText);
            }

            var first = await host.DispatchAsync("query.filterVoucherPage", DraftRequest(definition).ToJsonString());
            var storedFirst = await host.DispatchAsync("query.filterVoucherPage", SavedRequest(position, revision).ToJsonString());
            var vouchers = VoucherNumbers(first).Take(12).ToArray();
            Assert.NotEmpty(vouchers);
            foreach (var voucher in vouchers)
            {
                var liveRows = await ReadAllEntryPagesAsync(host,
                    DraftRequest(definition, voucher: voucher, queryRevision: first.GetProperty("queryRevision").GetString()));
                var storedRows = await ReadAllEntryPagesAsync(host,
                    SavedRequest(position, revision, voucher: voucher, queryRevision: storedFirst.GetProperty("queryRevision").GetString()));
                Assert.Equal(liveRows, storedRows);
                var parsed = liveRows.Select(row => JsonDocument.Parse(row).RootElement).ToArray();
                partialEvidenceSeen |= parsed.Any(row => !row.GetProperty("isHit").GetBoolean()
                    && row.GetProperty("primaryConditions").GetArrayLength() > 0);
                wholeVoucherSeen |= parsed.Any(row =>
                    row.GetProperty("matchDescription").GetString()!.Contains("整張傳票列入結果", StringComparison.Ordinal));
            }
        }

        // 合成情境要涵蓋未命中但符合部分條件的分錄，以及整張傳票列入結果的情形，比對才有意義。
        Assert.True(partialEvidenceSeen);
        Assert.True(wholeVoucherSeen);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SavedScenarioPage_ReportsTheStoredCounts(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var pair = Scenario("未預期借貸組合", new JsonObject { ["type"] = "prescreen", ["prescreenKey"] = "unexpectedAccountPair" });
        var debit = Scenario("借方分錄", DrCr("debit"));
        await SaveNewAsync(host, pair.DeepClone().AsObject());
        var saved = await SaveNewAsync(host, debit.DeepClone().AsObject());
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString()!;

        foreach (var (position, definition) in new[] { (1, pair), (2, debit) })
        {
            var preview = (await host.DispatchAsync("filter.preview", new JsonObject
            {
                ["populationScope"] = "auditPeriod",
                ["scenario"] = definition.DeepClone()
            }.ToJsonString())).GetProperty("scenario");
            var page = await host.DispatchAsync("query.filterVoucherPage", SavedRequest(position, revision).ToJsonString());

            Assert.Equal(preview.GetProperty("voucherCount").GetInt64(), page.GetProperty("voucherCount").GetInt64());
            Assert.Equal(preview.GetProperty("summaryRowCount").GetInt64(), page.GetProperty("summaryRowCount").GetInt64());
            Assert.Equal(preview.GetProperty("countsWholeVoucherRows").GetBoolean(), page.GetProperty("countsWholeVoucherRows").GetBoolean());
        }
    }

    // ── 過期判斷只綁這個情境 ────────────────────────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OpenScenarioPages_StayValidWhenAnotherScenarioIsSaved(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var saved = await SaveNewAsync(host, Scenario("借方分錄", DrCr("debit")));
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var first = await host.DispatchAsync("query.filterVoucherPage", SavedRequest(1, revision, pageSize: 2).ToJsonString());
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var queryRevision = first.GetProperty("queryRevision").GetString()!;
        var voucher = VoucherNumbers(first).First();

        var second = await SaveNewAsync(host, Scenario("貸方分錄", DrCr("credit")));

        var next = await host.DispatchAsync("query.filterVoucherPage",
            SavedRequest(1, revision, pageSize: 2, cursor: cursor, queryRevision: queryRevision).ToJsonString());
        Assert.Equal(2, next.GetProperty("rows").GetArrayLength());
        var rows = await host.DispatchAsync("query.filterVoucherRowsPage",
            SavedRequest(1, revision, voucher: voucher, queryRevision: queryRevision).ToJsonString());
        Assert.NotEmpty(rows.GetProperty("rows").EnumerateArray());

        // 改了這個情境，之前打開的頁面就過期。
        var edited = Scenario("借方分錄", DrCr("debit"));
        edited["rationale"] = "改了動機";
        await SaveAsync(host, 1, NameAt(second, 1), edited);
        var stale = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("query.filterVoucherPage",
            SavedRequest(1, revision, pageSize: 2, cursor: cursor, queryRevision: queryRevision).ToJsonString()));
        Assert.Equal(JetErrorCodes.StaleResult, stale.Code);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OpenScenarioPages_TurnStaleAfterAnUpstreamChange(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var saved = await SaveNewAsync(host, Scenario("借方分錄", DrCr("debit")));
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var first = await host.DispatchAsync("query.filterVoucherPage", SavedRequest(1, revision, pageSize: 2).ToJsonString());

        await host.DispatchAsync("import.holiday", JsonSerializer.Serialize(new { dates = new[] { "2025-03-03" } }));

        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("query.filterVoucherPage",
            SavedRequest(1, revision, pageSize: 2, cursor: first.GetProperty("nextCursor").GetString(),
                queryRevision: first.GetProperty("queryRevision").GetString()).ToJsonString()));
        Assert.Contains(error.Code, new[] { JetErrorCodes.StaleResult, JetErrorCodes.NoSavedScenario });
    }

    // ── 上游修改照舊清掉全部 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task UpstreamChange_ClearsEveryScenarioAndStoredResult(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await SaveNewAsync(host, AndScenario("情境甲"));
        await SaveNewAsync(host, Scenario("情境乙", DrCr("credit")));
        var context = new ProjectContext(setup.ProjectId);
        var before = await StoredRowsAsync(host, context, provider);
        Assert.NotEmpty(before.Hits);
        Assert.NotEmpty(before.Conditions);

        await host.DispatchAsync("import.holiday", JsonSerializer.Serialize(new { dates = new[] { "2025-03-03" } }));

        var after = await StoredRowsAsync(host, context, provider);
        Assert.Empty(after.Scenarios);
        Assert.Empty(after.Hits);
        Assert.Empty(after.Conditions);
    }

    // ── 匯出用存好的結果 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Exports_UseTheStoredResultsWithoutRecalculating(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var validationResultId = (await host.DispatchAsync("validate.run")).GetProperty("resultRef").GetProperty("resultId").GetString();
        var saved = await SaveNewAsync(host, Scenario("借方分錄", DrCr("debit")));
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var context = new ProjectContext(setup.ProjectId);
        Assert.Equal(1, await MarkFirstHitAsync(host, context, provider));
        var stored = await ScalarAsync(host, context, provider, "SELECT COUNT(*) FROM filter_hit;");

        await host.DispatchAsync("export.criteriaSelectionReport",
            JsonSerializer.Serialize(new { validationResultId, revision }));
        Assert.Equal(stored, await ScalarAsync(host, context, provider, "SELECT COUNT(*) FROM filter_hit;"));

        await host.DispatchAsync("export.workingPaperStream",
            JsonSerializer.Serialize(new { validationResultId, scenarioRevision = revision, scenarioPositions = new[] { 1 } }));
        Assert.Equal(stored, await ScalarAsync(host, context, provider, "SELECT COUNT(*) FROM filter_hit;"));

        // 情境清單已經改了，畫面還拿舊版本來產生，照舊擋下並寫明原因。
        await SaveNewAsync(host, Scenario("貸方分錄", DrCr("credit")));
        var stale = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("export.criteriaSelectionReport",
            JsonSerializer.Serialize(new { validationResultId, revision })));
        Assert.Equal(JetErrorCodes.StaleResult, stale.Code);
        Assert.Equal(ReportExportSupport.ScenarioListOutOfDate, stale.Message);
    }

    // ── 佈置 ────────────────────────────────────────────────────────────────────

    private sealed record ProjectContext(string ProjectId);

    private sealed record StoredScenarioRow(int ScenarioId, int Position, string SavedUtc);

    private sealed record StoredHitRow(int ScenarioId, long EntryId);

    private sealed record StoredConditionRow(int ScenarioId, long EntryId, int GroupNo, int ConditionNo);

    private sealed record StoredRows(
        IReadOnlyList<StoredScenarioRow> Scenarios,
        IReadOnlyList<StoredHitRow> Hits,
        IReadOnlyList<StoredConditionRow> Conditions);

    private sealed record VoucherPages(long TotalCount, IReadOnlyList<string> Rows, string Columns, string ConditionText);

    private static JsonObject DrCr(string drCr) => new() { ["type"] = "drCrOnly", ["drCr"] = drCr };

    private static JsonObject Scenario(string name, params JsonObject[] conditions) => new()
    {
        ["name"] = name,
        ["rationale"] = name + "的合成動機",
        ["groups"] = new JsonArray(new JsonObject
        {
            ["join"] = "AND",
            ["conditions"] = new JsonArray(conditions.Select(condition => (JsonNode)condition).ToArray())
        })
    };

    /// <summary>金額不是負數而且是薪資或租金科目：同一張傳票的對方分錄只符合第一條，用來核對參考分錄的說明。</summary>
    private static JsonObject AndScenario(string name) => Scenario(name,
        new JsonObject { ["type"] = "numRange", ["field"] = "amount", ["from"] = "0" },
        Account("6101", "6111"));

    private static JsonObject Account(params string[] accounts) => new()
    {
        ["type"] = "fieldValue",
        ["field"] = "accountNumber",
        ["operator"] = "in",
        ["values"] = new JsonArray(accounts.Select(account => (JsonNode)account).ToArray())
    };

    private static JsonObject OrScenario(string name)
    {
        var scenario = AndScenario(name);
        scenario["groups"]!.AsArray().Add(new JsonObject
        {
            ["join"] = "OR",
            ["conditions"] = new JsonArray(
                DrCr("credit"),
                Account("1131"))
        });
        return scenario;
    }

    private static JsonObject VoucherScenario(string name) => Scenario(name,
        DrCr("credit"),
        new JsonObject
        {
            ["type"] = "voucher",
            ["quantifier"] = "any",
            ["conditions"] = new JsonArray(
                DrCr("debit"),
                Account("6101"))
        });

    private static JsonObject VoucherOnlyScenario(string name) => Scenario(name,
        new JsonObject
        {
            ["type"] = "voucher",
            ["quantifier"] = "any",
            ["conditions"] = new JsonArray(
                DrCr("credit"),
                Account("4101"))
        });

    private static Task<JsonElement> SaveNewAsync(HandlerTestHost host, JsonObject scenario) =>
        host.DispatchAsync("filter.scenario.save", new JsonObject
        {
            ["populationScope"] = "auditPeriod",
            ["position"] = null,
            ["expectedName"] = null,
            ["scenario"] = scenario
        }.ToJsonString());

    private static Task<JsonElement> SaveAsync(HandlerTestHost host, int position, string expectedName, JsonObject scenario) =>
        host.DispatchAsync("filter.scenario.save", new JsonObject
        {
            ["populationScope"] = "auditPeriod",
            ["position"] = position,
            ["expectedName"] = expectedName,
            ["scenario"] = scenario
        }.ToJsonString());

    private static string NameAt(JsonElement response, int position) =>
        response.GetProperty("scenarios")[position - 1].GetProperty("name").GetString()!;

    private static string[] Names(JsonElement response) =>
        response.GetProperty("scenarios").EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToArray();

    private static Task<JsonElement> SummaryAsync(HandlerTestHost host) =>
        host.DispatchAsync("query.highRiskSummaryScenarios");

    private static long RowHitCount(JsonElement summary, int position) =>
        summary.GetProperty("scenarios").EnumerateArray()
            .Single(item => item.GetProperty("position").GetInt32() == position)
            .GetProperty("rowHitCount").GetInt64();

    private static async Task<long> PreviewRowCountAsync(HandlerTestHost host, JsonObject scenario)
    {
        var preview = await host.DispatchAsync("filter.preview", new JsonObject
        {
            ["populationScope"] = "auditPeriod",
            ["scenario"] = scenario
        }.ToJsonString());
        return preview.GetProperty("scenario").GetProperty("count").GetInt64();
    }

    private static JsonObject DraftRequest(
        JsonObject scenario, JsonObject? sort = null, string? search = null, string? voucher = null, string? queryRevision = null)
    {
        var request = new JsonObject { ["populationScope"] = "auditPeriod", ["scenario"] = scenario.DeepClone() };
        return Paging(request, sort, search, voucher, queryRevision, null, null);
    }

    private static JsonObject SavedRequest(
        int position, string revision, JsonObject? sort = null, string? search = null, string? voucher = null,
        string? queryRevision = null, int? pageSize = null, string? cursor = null)
    {
        var request = new JsonObject
        {
            ["populationScope"] = "auditPeriod",
            ["scenarioPosition"] = position,
            ["scenarioRevision"] = revision
        };
        return Paging(request, sort, search, voucher, queryRevision, pageSize, cursor);
    }

    private static JsonObject Paging(
        JsonObject request, JsonObject? sort, string? search, string? voucher, string? queryRevision, int? pageSize, string? cursor)
    {
        if (sort is not null) request["sort"] = sort.DeepClone();
        if (search is not null) request["search"] = search;
        if (voucher is not null) request["voucherNumber"] = voucher;
        if (queryRevision is not null) request["queryRevision"] = queryRevision;
        if (pageSize is not null) request["pageSize"] = pageSize;
        if (cursor is not null) request["cursor"] = cursor;
        return request;
    }

    private static IEnumerable<string> VoucherNumbers(JsonElement page) =>
        page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("voucherNumber").GetString()!);

    private static async Task<VoucherPages> ReadAllVoucherPagesAsync(HandlerTestHost host, JsonObject request)
    {
        request["pageSize"] = 37;
        var rows = new List<string>();
        var first = await host.DispatchAsync("query.filterVoucherPage", request.ToJsonString());
        var page = first;
        while (true)
        {
            rows.AddRange(page.GetProperty("rows").EnumerateArray().Select(row => row.GetRawText()));
            var next = page.GetProperty("nextCursor");
            if (next.ValueKind != JsonValueKind.String) break;
            request["cursor"] = next.GetString();
            request["queryRevision"] = page.GetProperty("queryRevision").GetString();
            page = await host.DispatchAsync("query.filterVoucherPage", request.ToJsonString());
        }

        return new VoucherPages(
            first.GetProperty("totalCount").GetInt64(),
            rows,
            first.GetProperty("columns").GetRawText(),
            first.GetProperty("conditionText").GetString()!);
    }

    private static async Task<IReadOnlyList<string>> ReadAllEntryPagesAsync(HandlerTestHost host, JsonObject request)
    {
        request["pageSize"] = 3;
        var rows = new List<string>();
        while (true)
        {
            var page = await host.DispatchAsync("query.filterVoucherRowsPage", request.ToJsonString());
            rows.AddRange(page.GetProperty("rows").EnumerateArray().Select(row => row.GetRawText()));
            var next = page.GetProperty("nextCursor");
            if (next.ValueKind != JsonValueKind.String) break;
            request["cursor"] = next.GetString();
        }

        return rows;
    }

    /// <summary>刪掉編號最小的那一筆命中分錄在每個情境的結果列，回傳刪掉幾列。只有重算會把它們加回來。</summary>
    private static Task<int> MarkFirstHitAsync(HandlerTestHost host, ProjectContext context, string provider) =>
        ExecuteAsync(host, context, provider,
            "DELETE FROM filter_hit WHERE entry_id = (SELECT MIN(entry_id) FROM filter_hit);");

    // ── 資料庫讀寫 ──────────────────────────────────────────────────────────────

    private static ILocalProjectDatabase Database(HandlerTestHost host, string provider)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        return provider == "sqlite" ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);
    }

    private static async Task<StoredRows> StoredRowsAsync(HandlerTestHost host, ProjectContext context, string provider)
    {
        await using var connection = Database(host, provider).CreateConnection(context.ProjectId);
        await connection.OpenAsync();

        async Task<List<T>> ReadAsync<T>(string sql, Func<System.Data.Common.DbDataReader, T> map)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var rows = new List<T>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(map(reader));
            return rows;
        }

        return new StoredRows(
            await ReadAsync("SELECT scenario_id, position, saved_utc FROM filter_scenario ORDER BY position;",
                reader => new StoredScenarioRow(Convert.ToInt32(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)), reader.GetString(2))),
            await ReadAsync("SELECT scenario_id, entry_id FROM filter_hit ORDER BY scenario_id, entry_id;",
                reader => new StoredHitRow(Convert.ToInt32(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)))),
            await ReadAsync(
                "SELECT scenario_id, entry_id, group_no, condition_no FROM filter_hit_condition "
                + "ORDER BY scenario_id, entry_id, group_no, condition_no;",
                reader => new StoredConditionRow(Convert.ToInt32(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)),
                    Convert.ToInt32(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3)))));
    }

    private static async Task<int> ExecuteAsync(
        HandlerTestHost host, ProjectContext context, string provider, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = Database(host, provider).CreateConnection(context.ProjectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(HandlerTestHost host, ProjectContext context, string provider, string sql)
    {
        await using var connection = Database(host, provider).CreateConnection(context.ProjectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarTextAsync(HandlerTestHost host, ProjectContext context, string provider, string sql)
    {
        await using var connection = Database(host, provider).CreateConnection(context.ProjectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }
}
