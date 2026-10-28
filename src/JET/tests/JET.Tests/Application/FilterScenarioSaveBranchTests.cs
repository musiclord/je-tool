using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 篩選情境儲存（filter.scenario.save）的業務分支，各給一個固定輸入與寫死的預期結果（預篩選收斂計畫階段十一，
/// 使用者 2026-10-25 裁定 B1 到 B7 全部要補）。分支依架構圖「篩選情境儲存」的循序圖與流程圖列出：前提不符、本次不合格、
/// 位置與名稱、整批合格、仍有不合格、推進到第五步、取消與例外。合成母體固定四列：兩列借方、兩列貸方，所以借方情境的命中
/// 一定是 2 筆。SQLite 與 DuckDB 各跑一次。這些測試鎖的是現行行為，預期一次通過；有測試失敗就是發現，先回報再決定改什麼。
/// </summary>
public sealed class FilterScenarioSaveBranchTests
{
    private const string UnknownRdeFieldId = "rde.aaaa0000aaaa0000aaaa0000aaaa0000";
    private const string ListOutOfDateMessage = "畫面上的情境清單與已儲存的內容不一致，本次未儲存。請重新開啟案件後再操作。";
    private static readonly string[] WatchedTables = ["config_filter_scenario", "result_filter_run", "config_result_stale_state"];

    private sealed record StoredScenario(int Position, string Name, string DefinitionJson, string SavedUtc);

    // ── B1 前提不符就回錯誤、不寫入 ─────────────────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MissingValidation_IsRejectedWithTheFixedReason_AndWritesNothing(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider, validated: false);

        var error = await Assert.ThrowsAsync<JetActionException>(() => SaveAsync(host, null, null, Scenario("情境甲", "debit")));

        Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, error.Code);
        Assert.Equal("目前資料尚無可用的資料驗證結果，請重新執行資料驗證。", error.Message);
        Assert.Empty(await ReadScenariosAsync(host, projectId, provider));
        Assert.Equal(0, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM result_filter_run;"));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ProjectJsonMissing_IsRejected_AndWritesNothing(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var before = await SnapshotAsync(host, projectId, provider);
        File.Delete(ProjectJsonPath(host, projectId));

        var error = await Assert.ThrowsAsync<JetActionException>(() => SaveAsync(host, null, null, Scenario("情境甲", "debit")));

        Assert.Equal(JetErrorCodes.ProjectNotFound, error.Code);
        Assert.Equal(ProjectFolderMessages.ActiveProjectMissing, error.Message);
        await AssertUnchangedAsync(host, projectId, provider, before);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task GlMappingNotConfirmed_IsRejected_AndWritesNothing(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        // 驗證結果仍是目前版本，只有 GL 配對不在了：合成「配對被清掉」的狀態，走到第三個前提。
        await ExecuteAsync(host, projectId, provider, "DELETE FROM config_field_mapping WHERE dataset_kind = 'gl';");
        var before = await SnapshotAsync(host, projectId, provider);

        var error = await Assert.ThrowsAsync<JetActionException>(() => SaveAsync(host, null, null, Scenario("情境甲", "debit")));

        Assert.Equal(JetErrorCodes.NoTargetData, error.Code);
        Assert.Equal("尚未確認 GL 欄位配對，請先到第三步按「確認配對」。", error.Message);
        await AssertUnchangedAsync(host, projectId, provider, before);
    }

    // ── B2 本次情境不合格就回錯誤、不寫入 ───────────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task DuplicateName_IsRejected_AndWritesNothing(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        await SaveAsync(host, null, null, Scenario("情境甲", "debit"));
        var before = await SnapshotAsync(host, projectId, provider);
        var projectBytes = await File.ReadAllBytesAsync(ProjectJsonPath(host, projectId));

        var error = await Assert.ThrowsAsync<JetActionException>(() => SaveAsync(host, null, null, Scenario("情境甲", "credit")));

        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
        Assert.Equal("情境名稱重複：「情境甲」。", error.Message);
        await AssertUnchangedAsync(host, projectId, provider, before);
        Assert.Equal(projectBytes, await File.ReadAllBytesAsync(ProjectJsonPath(host, projectId)));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task RemovedExclusionArea_IsRejectedForThisScenario_AndWritesNothing(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        await SaveAsync(host, null, null, Scenario("情境甲", "debit"));
        var before = await SnapshotAsync(host, projectId, provider);
        var scenario = Scenario("情境乙", "credit");
        scenario["exclusions"] = JsonNode.Parse(
            """[{"scope":"voucher","rule":{"type":"fieldValue","field":"postDate","operator":"in","values":["2025-01-15"]}}]""");

        var error = await Assert.ThrowsAsync<JetActionException>(() => SaveAsync(host, null, null, scenario));

        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
        Assert.Contains("使用了已移除的「排除區域」，請改用條件中的「不屬於」或「不在區間」", error.Message, StringComparison.Ordinal);
        // 錯誤屬於正在儲存的這一個：它會是清單上的第 2 個。
        var detail = Assert.Single(error.Details!);
        Assert.Equal(2, detail.ScenarioPosition);
        Assert.Equal("情境乙", detail.ScenarioName);
        await AssertUnchangedAsync(host, projectId, provider, before);
    }

    // ── B3 位置留空是新增或另存，指定位置是取代 ─────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task NullPosition_AppendsAtTheEnd_GivenPosition_ReplacesInPlace(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);

        var first = await SaveAsync(host, null, null, Scenario("情境甲", "debit"));
        var second = await SaveAsync(host, null, null, Scenario("情境乙", "credit"));
        var stored = await ReadScenariosAsync(host, projectId, provider);
        var replaced = await SaveAsync(host, 1, "情境甲", Scenario("情境甲改", "credit"));

        Assert.Equal(1, first.GetProperty("position").GetInt32());
        Assert.Equal(1, first.GetProperty("savedCount").GetInt32());
        Assert.Equal(2, second.GetProperty("position").GetInt32());
        Assert.Equal(2, second.GetProperty("savedCount").GetInt32());
        Assert.Equal(["情境甲", "情境乙"], stored.Select(row => row.Name));
        Assert.Equal(1, replaced.GetProperty("position").GetInt32());
        Assert.Equal(2, replaced.GetProperty("savedCount").GetInt32());
        var after = await ReadScenariosAsync(host, projectId, provider);
        Assert.Equal([1, 2], after.Select(row => row.Position));
        Assert.Equal(["情境甲改", "情境乙"], after.Select(row => row.Name));
        // 沒被編輯的那一個照原樣保留：定義一字不改。
        Assert.Equal(stored[1].DefinitionJson, after[1].DefinitionJson);
        Assert.Contains("\"drCr\":\"credit\"", after[0].DefinitionJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ExpectedNameMismatch_IsRejected_AndWritesNothing(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        await SaveAsync(host, null, null, Scenario("情境甲", "debit"));
        var before = await SnapshotAsync(host, projectId, provider);

        var renamed = await Assert.ThrowsAsync<JetActionException>(() => SaveAsync(host, 1, "不是這個名稱", Scenario("情境甲", "credit")));
        var beyond = await Assert.ThrowsAsync<JetActionException>(() => SaveAsync(host, 2, "情境甲", Scenario("情境甲", "credit")));

        Assert.Equal(JetErrorCodes.StaleResult, renamed.Code);
        Assert.Equal(ListOutOfDateMessage, renamed.Message);
        Assert.Equal(JetErrorCodes.StaleResult, beyond.Code);
        Assert.Equal(ListOutOfDateMessage, beyond.Message);
        await AssertUnchangedAsync(host, projectId, provider, before);
    }

    // ── B4 整批都合格：同一筆交易取代定義並算出命中 ─────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AllValid_WritesDefinitionsAndHitsAndClearsTheStaleFlag(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);

        var first = await SaveAsync(host, null, null, Scenario("情境甲", "debit"));
        var second = await SaveAsync(host, null, null, Scenario("情境乙", "credit"));

        Assert.Equal("current", first.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        Assert.Equal("current", second.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        Assert.Equal(2, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM result_filter_run WHERE scenario_position = 1;"));
        Assert.Equal(2, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM result_filter_run WHERE scenario_position = 2;"));
        Assert.Equal(4, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM result_filter_run;"));
        Assert.Equal(0, await StaleFlagAsync(host, projectId, provider));
        Assert.False(second.GetProperty("staleState").GetProperty("filter").GetBoolean());
        var stored = await ReadScenariosAsync(host, projectId, provider);
        Assert.Equal(stored[0].SavedUtc, second.GetProperty("resultRef").GetProperty("revision").GetString());
        Assert.Single(stored.Select(row => row.SavedUtc).Distinct());
    }

    /// <summary>
    /// 同一筆交易：清掉命中與定義之後，寫新定義時失敗，命中與定義都要回到原樣。失敗由測試在情境定義表加上的 CHECK 製造，
    /// 只允許原本那個儲存時間，所以任何新寫入都會被擋下；兩種資料庫同一招。這也是取消與例外那一組裡「例外」的回應：
    /// 不是業務錯誤也不是取消，畫面會看到一般錯誤。
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task DefinitionWriteFailure_RollsBackTheWholeTransaction(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        await SaveAsync(host, null, null, Scenario("情境甲", "debit"));
        var before = await SnapshotAsync(host, projectId, provider);
        var projectBytes = await File.ReadAllBytesAsync(ProjectJsonPath(host, projectId));
        var savedUtc = (await ReadScenariosAsync(host, projectId, provider)).Single().SavedUtc;
        await ExecuteAsync(host, projectId, provider, $$"""
            ALTER TABLE config_filter_scenario RENAME TO synthetic_saved_scenarios;
            CREATE TABLE config_filter_scenario (
                position        INTEGER PRIMARY KEY,
                name            TEXT NOT NULL,
                rationale       TEXT NOT NULL,
                definition_json TEXT NOT NULL,
                saved_utc       TEXT NOT NULL,
                CONSTRAINT synthetic_definition_failure CHECK (saved_utc = '{{savedUtc}}'));
            INSERT INTO config_filter_scenario SELECT * FROM synthetic_saved_scenarios;
            """);

        var error = await Record.ExceptionAsync(() => SaveAsync(host, null, null, Scenario("情境乙", "credit")));

        Assert.NotNull(error);
        Assert.IsNotType<JetActionException>(error);
        Assert.False(error is OperationCanceledException);
        Assert.Contains("CHECK", error.ToString(), StringComparison.OrdinalIgnoreCase);
        await ExecuteAsync(host, projectId, provider, """
            DROP TABLE config_filter_scenario;
            ALTER TABLE synthetic_saved_scenarios RENAME TO config_filter_scenario;
            """);
        await AssertUnchangedAsync(host, projectId, provider, before);
        Assert.Equal(2, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM result_filter_run;"));
        Assert.Equal(projectBytes, await File.ReadAllBytesAsync(ProjectJsonPath(host, projectId)));
    }

    // ── B5 仍有情境不合格：只寫定義、清除命中並標成失效 ─────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AnotherSavedScenarioInvalid_WritesDefinitionsOnly_ClearsHits_MarksStale(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        await SaveAsync(host, null, null, Scenario("情境甲", "debit"));
        await SaveAsync(host, null, null, Scenario("情境乙", "credit"));
        // 情境乙仍是目前版本，只是條件用的欄位已不在配對裡：開案與儲存都會把它列為待修。
        var invalid = JsonNode.Parse((await ReadScenariosAsync(host, projectId, provider))[1].DefinitionJson)!.AsObject();
        invalid["groups"]![0]!["rules"]!.AsArray().Insert(0, JsonNode.Parse(
            "{\"join\":\"AND\",\"type\":\"typed\",\"fieldId\":\"" + UnknownRdeFieldId + "\",\"operator\":\"isBlank\"}"));
        await ExecuteAsync(host, projectId, provider,
            "UPDATE config_filter_scenario SET definition_json = @definition WHERE position = 2;", ("@definition", invalid.ToJsonString()));
        Assert.Equal(4, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM result_filter_run;"));

        var response = await SaveAsync(host, null, null, Scenario("情境丙", "debit"));

        var check = response.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Equal(2, problem.GetProperty("position").GetInt32());
        Assert.Equal("情境乙", problem.GetProperty("name").GetString());
        Assert.Equal(3, response.GetProperty("position").GetInt32());
        Assert.Equal(3, response.GetProperty("savedCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("resultRef").ValueKind);
        Assert.True(response.GetProperty("staleState").GetProperty("filter").GetBoolean());
        var stored = await ReadScenariosAsync(host, projectId, provider);
        Assert.Equal(["情境甲", "情境乙", "情境丙"], stored.Select(row => row.Name));
        Assert.Equal(invalid.ToJsonString(), stored[1].DefinitionJson);
        Assert.Equal(0, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM result_filter_run;"));
        Assert.Equal(1, await StaleFlagAsync(host, projectId, provider));
    }

    // ── B6 發布後案件步驟落後才推進到第五步 ─────────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AdvancesToStepFive_OnlyWhenBehind(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var projectPath = ProjectJsonPath(host, projectId);

        await SetStepAsync(projectPath, 3);
        await SaveAsync(host, null, null, Scenario("情境甲", "debit"));
        Assert.Equal(5, await StepAsync(projectPath));

        // 已經是第五步：不再寫 project.json。
        var atFive = await File.ReadAllBytesAsync(projectPath);
        await SaveAsync(host, null, null, Scenario("情境乙", "credit"));
        Assert.Equal(atFive, await File.ReadAllBytesAsync(projectPath));

        // 已經走到更後面：步驟只增不減。
        await SetStepAsync(projectPath, 6);
        var atSix = await File.ReadAllBytesAsync(projectPath);
        await SaveAsync(host, 1, "情境甲", Scenario("情境甲改", "debit"));
        Assert.Equal(6, await StepAsync(projectPath));
        Assert.Equal(atSix, await File.ReadAllBytesAsync(projectPath));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ProjectJsonLocked_StillAnswersSuccess_AndLogsOneWarning(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var projectPath = ProjectJsonPath(host, projectId);
        await SetStepAsync(projectPath, 4);
        var logCount = host.DiagnosticLog!.Snapshot().Count;

        JsonElement response;
        // 只擋住換檔，不擋讀取：前提檢查讀得到 project.json，提交後的步驟推進換不了檔。
        using (new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            response = await SaveAsync(host, null, null, Scenario("情境甲", "debit"));
        }

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("current", response.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        Assert.Equal(2, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM result_filter_run;"));
        Assert.Equal(4, await StepAsync(projectPath));
        var warning = Assert.Single(host.DiagnosticLog.Snapshot().Skip(logCount), entry =>
            entry.Level == "Warning" && entry.Message.Contains("workflow.milestone", StringComparison.Ordinal));
        Assert.DoesNotContain(projectPath, warning.Message);
    }

    // ── B7 取消與例外的回應 ─────────────────────────────────────────────────────
    // 例外的回應見上面「寫新定義時失敗」那一則：不是業務錯誤也不是取消，整筆交易回到原樣。

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CancelledBeforeDispatch_ReportsCancellation_AndWritesNothing(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var before = await SnapshotAsync(host, projectId, provider);
        var projectBytes = await File.ReadAllBytesAsync(ProjectJsonPath(host, projectId));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SaveAsync(host, null, null, Scenario("情境甲", "debit"), cancellation.Token));

        await AssertUnchangedAsync(host, projectId, provider, before);
        Assert.Equal(projectBytes, await File.ReadAllBytesAsync(ProjectJsonPath(host, projectId)));
    }

    /// <summary>
    /// 提交之後才收到取消：真的資料庫實作已經提交，handler 仍要推進步驟、讀回最新狀態並回應成功。
    /// 取消由包在真實篩選執行外面的一層在提交完成後發出；handler 直接建立，資料庫實作和正式組裝相同。
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CancelledAfterCommit_StillAdvancesTheStep_AndAnswersSuccess(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var projectPath = ProjectJsonPath(host, projectId);
        await SetStepAsync(projectPath, 4);
        using var cancellation = new CancellationTokenSource();
        var database = Database(host, provider);
        var realFacts = new FilterFactsPort(new LocalFilterRunRepository(database), new LocalFilterCommitRepository(database));
        var session = new ProjectSession();
        session.Enter(projectId, TestProjectRepositories.Unconfigured(provider) with
        {
            FilterScenarios = new LocalFilterScenarioStore(database),
            FilterFacts = new CancelAfterCommit(realFacts, cancellation),
            RuleRuns = new LocalRuleRunStore(database),
            ResultStaleStates = new LocalResultStaleStateStore(database),
            MappingStates = new LocalMappingStateStore(database),
            AccountMappings = new LocalAccountMappingRepository(database),
            AuthorizedPreparers = new LocalAuthorizedPreparerRepository(database),
            AccountTaxonomy = new LocalAccountTaxonomyStore(database)
        });
        var handler = new FilterScenarioSaveHandler(new JsonFileProjectStore(new JetProjectFolder(host.ProjectsRoot)), session);
        using var payload = JsonDocument.Parse(SavePayload(null, null, Scenario("情境甲", "debit")));

        var raw = await handler.HandleAsync(payload.RootElement, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        var response = JsonSerializer.SerializeToElement(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("current", response.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        Assert.Equal(1, response.GetProperty("position").GetInt32());
        Assert.Equal(["情境甲"], (await ReadScenariosAsync(host, projectId, provider)).Select(row => row.Name));
        Assert.Equal(2, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM result_filter_run;"));
        Assert.Equal(0, await StaleFlagAsync(host, projectId, provider));
        Assert.Equal(5, await StepAsync(projectPath));
    }

    /// <summary>真實篩選執行跑完、交易已提交之後才要求取消。</summary>
    private sealed class CancelAfterCommit(IFilterFactsPort inner, CancellationTokenSource cancellation) : IFilterFactsPort
    {
        public async Task<FilterFacts> ExecuteAsync(FilterPlan plan, CancellationToken cancellationToken)
        {
            var facts = await inner.ExecuteAsync(plan, cancellationToken);
            cancellation.Cancel();
            return facts;
        }
    }

    // ── 佈置 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 合成母體：兩張傳票各一借一貸，共四列；借方情境命中 2 筆，貸方情境命中 2 筆。validated 為 true 時連 TB、配對與驗證一起做完。
    /// 同一種資料庫只建一次範本，每個測試複製一份再開啟。
    /// </summary>
    private static async Task<string> PrepareAsync(HandlerTestHost host, string provider, bool validated = true)
    {
        var copied = await CaseTemplates.CopyIntoAsync(host, $"phase11-filter-save:{validated}:{provider}", async templateHost =>
        {
            var id = await InlineWorkbookProject.SetupAsync(templateHost, builder => builder
                .WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("V1", "2025-06-01", "2025-06-02", "1000", "合成資產", "synthetic", 10, 1)
                .AddRow("V1", "2025-06-01", "2025-06-02", "4000", "合成收入", "synthetic", 10, 0)
                .AddRow("V2", "2025-07-01", "2025-07-02", "1100", "合成現金", "synthetic", 25, 1)
                .AddRow("V2", "2025-07-01", "2025-07-02", "4100", "合成其他收入", "synthetic", 25, 0),
                databaseProvider: provider, validateForDownstream: validated);
            return (id, (object?)null);
        });
        return copied.ProjectId;
    }

    private static JsonObject Scenario(string name, string drCr) => new()
    {
        ["name"] = name,
        ["rationale"] = name + "的合成動機",
        ["groups"] = new JsonArray(new JsonObject
        {
            ["join"] = "AND",
            ["rules"] = new JsonArray(new JsonObject { ["join"] = "AND", ["type"] = "drCrOnly", ["drCr"] = drCr })
        })
    };

    private static string SavePayload(int? position, string? expectedName, JsonNode scenario) => new JsonObject
    {
        ["populationScope"] = "auditPeriod",
        ["position"] = position,
        ["expectedName"] = expectedName,
        ["scenario"] = scenario
    }.ToJsonString();

    private static Task<JsonElement> SaveAsync(
        HandlerTestHost host, int? position, string? expectedName, JsonNode scenario, CancellationToken cancellationToken = default) =>
        host.DispatchAsync("filter.scenario.save", SavePayload(position, expectedName, scenario), cancellationToken);

    private static string ProjectJsonPath(HandlerTestHost host, string projectId) =>
        new JetProjectFolder(host.ProjectsRoot).GetProjectJsonPath(projectId);

    private static async Task SetStepAsync(string projectPath, int step)
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(projectPath))!.AsObject();
        document["currentStep"] = step;
        await File.WriteAllTextAsync(projectPath, document.ToJsonString());
    }

    private static async Task<int> StepAsync(string projectPath) =>
        JsonNode.Parse(await File.ReadAllTextAsync(projectPath))!["currentStep"]!.GetValue<int>();

    // ── 資料庫讀寫 ──────────────────────────────────────────────────────────────

    private static ILocalProjectDatabase Database(HandlerTestHost host, string provider)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        return provider == ProjectDocument.DuckDbDatabaseProvider ? new DuckDbProjectDatabase(folder) : new SqliteProjectDatabase(folder);
    }

    private static Task ExecuteAsync(HandlerTestHost host, string projectId, string provider, string sql, params (string Name, object Value)[] parameters) =>
        Batch9ArtifactStateTests.ExecuteAsync(host, projectId, provider, sql, parameters);

    private static Task<long> ScalarAsync(HandlerTestHost host, string projectId, string provider, string sql) =>
        Batch9TransactionBoundaryTests.ScalarAsync(host, projectId, provider, sql);

    private static Task<long> StaleFlagAsync(HandlerTestHost host, string projectId, string provider) =>
        ScalarAsync(host, projectId, provider, "SELECT filter_stale FROM config_result_stale_state WHERE singleton = 1;");

    /// <summary>三張會被這個流程寫到的表，整表排序後的快照；用來斷言「沒有寫入」。</summary>
    private static async Task<Dictionary<string, string[]>> SnapshotAsync(HandlerTestHost host, string projectId, string provider)
    {
        var tables = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var table in WatchedTables)
        {
            tables[table] = await UsageScenarioMutationRollbackTests.ReadTableAsync(host, projectId, provider, table);
        }

        return tables;
    }

    private static async Task AssertUnchangedAsync(
        HandlerTestHost host, string projectId, string provider, Dictionary<string, string[]> before)
    {
        var after = await SnapshotAsync(host, projectId, provider);
        foreach (var table in WatchedTables)
        {
            Assert.Equal(before[table], after[table]);
        }
    }

    private static async Task<IReadOnlyList<StoredScenario>> ReadScenariosAsync(HandlerTestHost host, string projectId, string provider)
    {
        await using var connection = Database(host, provider).CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT position, name, definition_json, saved_utc FROM config_filter_scenario ORDER BY position;";
        var rows = new List<StoredScenario>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new StoredScenario(Convert.ToInt32(reader.GetValue(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }
}
