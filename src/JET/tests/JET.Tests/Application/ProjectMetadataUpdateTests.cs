using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class ProjectMetadataUpdateTests
{
    private static readonly object PreparationScenario = new
    {
        name = "Synthetic preparation date", rationale = "Fixed date boundary",
        groups = new[] { new { conditions = new[] { new { type = "prescreen", prescreenKey = "prepPeriodApproval" } } } }
    };

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task PartialMetadataUpdate_PersistsAfterRestart_AndDoesNotChangeImmutableFields(string provider)
    {
        using var root = new TempProjectRoot();
        string projectId;
        using (var host = new HandlerTestHost(projectsRootPath: root.Path))
        {
            projectId = await SetupAsync(host, provider);
            var before = (await LoadAsync(host, projectId)).GetProperty("project");
            var updated = await UpdateAsync(host, new
            {
                clientName = "  Synthetic revised client  ", projectCode = "  SYN-NEW  ",
                projectName = "must-not-rename", projectId = "must-not-switch",
                periodStart = "2020-01-01", periodEnd = "2020-12-31", databaseProvider = "sqlServer"
            });
            var project = updated.GetProperty("project");
            Assert.Equal("Synthetic revised client", project.GetProperty("clientName").GetString());
            Assert.Equal("SYN-NEW", project.GetProperty("projectCode").GetString());
            Assert.Equal("2025-09-01", project.GetProperty("prepStartDate").GetString());
            foreach (var key in new[] { "projectId", "periodStart", "periodEnd", "databaseProvider", "operatorId", "moneyScale", "createdUtc" })
                Assert.Equal(before.GetProperty(key).GetRawText(), project.GetProperty(key).GetRawText());
            Assert.Empty(updated.GetProperty("warnings").EnumerateArray());
            Assert.Equal(JsonValueKind.Array, updated.GetProperty("reports").ValueKind);

            var partial = await UpdateAsync(host, new { clientName = (string?)null });
            Assert.Equal("", partial.GetProperty("project").GetProperty("clientName").GetString());
            Assert.Equal("SYN-NEW", partial.GetProperty("project").GetProperty("projectCode").GetString());
        }
        using var reopened = new HandlerTestHost(projectsRootPath: root.Path);
        var restored = (await LoadAsync(reopened, projectId)).GetProperty("project");
        Assert.Equal("", restored.GetProperty("clientName").GetString());
        Assert.Equal("SYN-NEW", restored.GetProperty("projectCode").GetString());
        Assert.Equal("2025-09-01", restored.GetProperty("prepStartDate").GetString());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TextOnlyAndSameDateUpdates_PreserveValidationPrescreenAndSavedResults(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host, provider);
        await MaterializeAsync(host);
        var before = await LoadAsync(host, projectId);
        await UpdateAsync(host, new { clientName = "Synthetic revised display", projectCode = "NEXT" });
        var noOp = await UpdateAsync(host, new { prepStartDate = "2025-09-01" });
        AssertStale(noOp.GetProperty("staleState"), false, false, false);
        var loaded = await LoadAsync(host, projectId);
        Assert.Equal(before.GetProperty("validationResult").GetRawText(), loaded.GetProperty("validationResult").GetRawText());
        Assert.Equal(before.GetProperty("prescreenResult").GetRawText(), loaded.GetProperty("prescreenResult").GetRawText());
        Assert.Equal(before.GetProperty("filterScenarios").GetRawText(), loaded.GetProperty("filterScenarios").GetRawText());
        Assert.Equal(before.GetProperty("filterResultRef").GetRawText(), loaded.GetProperty("filterResultRef").GetRawText());
        Assert.Equal(4, await PreviewCountAsync(host));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ChangedPreparationDate_InvalidatesOnlyDependentResults_AndUsesNewFixedAnswer(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host, provider);
        await MaterializeAsync(host);
        var before = await LoadAsync(host, projectId);
        Assert.Equal(4, await PreviewCountAsync(host));
        var updated = await UpdateAsync(host, new { prepStartDate = "2025-11-01" });
        // 上游修改會清除下游（裁定 2026-10-07-03）：改財報準備日會清掉已存情境，篩選回到從未執行，
        // 不再標成待重跑也不保留情境。第一次失敗收據 20261007-032952783-7147565fec1c42be845dac22acf7263b。
        AssertStale(updated.GetProperty("staleState"), false, true, false);
        Assert.True(updated.GetProperty("invalidatedResults").GetProperty("filterScenarios").GetBoolean());
        var loaded = await LoadAsync(host, projectId);
        AssertStale(loaded.GetProperty("staleState"), false, true, false);
        Assert.Equal(before.GetProperty("validationResult").GetRawText(),
            loaded.GetProperty("validationResult").GetRawText());
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("prescreenResult").ValueKind);
        Assert.Single(before.GetProperty("filterScenarios").EnumerateArray());
        Assert.Empty(loaded.GetProperty("filterScenarios").EnumerateArray());
        Assert.Equal(2, await PreviewCountAsync(host));
        var rerun = await host.DispatchAsync("prescreen.run");
        Assert.Equal(2, rerun.GetProperty("prepPeriodApproval").GetProperty("count").GetInt64());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ClearingPreparationDate_ClearsScenarios_AndExplainsWhereToRestoreIt(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host, provider);
        await MaterializeAsync(host);
        var updated = await UpdateAsync(host, new { prepStartDate = (string?)null, projectCode = (string?)null });
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("project").GetProperty("prepStartDate").ValueKind);
        Assert.Equal("", updated.GetProperty("project").GetProperty("projectCode").GetString());
        var loaded = await LoadAsync(host, projectId);
        // 上游修改會清除下游（裁定 2026-10-07-03）：清除財報準備日會清掉已存情境，原本斷言情境保留。
        // 第一次失敗收據 20261007-032952783-7147565fec1c42be845dac22acf7263b。
        Assert.Empty(loaded.GetProperty("filterScenarios").EnumerateArray());
        var error = await Assert.ThrowsAsync<JetActionException>(() => PreviewCountAsync(host));
        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
        Assert.Contains("修改案件資料", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("prepStartDate", error.Message, StringComparison.Ordinal);
        var prescreen = await host.DispatchAsync("prescreen.run");
        var reason = prescreen.GetProperty("prepPeriodApproval").GetProperty("naReason").GetString()!;
        Assert.Contains("修改案件資料", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("prepStartDate", reason, StringComparison.Ordinal);
    }

    // 依裁定 2026-10-07-12 改善財報準備日的存檔：案件設定檔存不進去時，資料庫的清除一起回復，
    // 情境、預篩選結果與舊日期都留著，審計員排除檔案問題後重送即可。
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ChangedPreparationDate_WhenSettingsCannotBeSaved_KeepsScenariosResultsAndOldDate(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host, provider);
        await MaterializeAsync(host);
        var before = await LoadAsync(host, projectId);
        var settings = Path.Combine(host.ProjectsRoot, projectId, "project.json");
        using (new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => UpdateAsync(host, new { prepStartDate = "2025-11-01" }));
            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        }

        var loaded = await LoadAsync(host, projectId);
        Assert.Equal("2025-09-01", loaded.GetProperty("project").GetProperty("prepStartDate").GetString());
        Assert.Equal(before.GetProperty("filterScenarios").GetRawText(), loaded.GetProperty("filterScenarios").GetRawText());
        Assert.Equal(before.GetProperty("validationResult").GetRawText(), loaded.GetProperty("validationResult").GetRawText());
        Assert.Equal(before.GetProperty("prescreenResult").GetRawText(), loaded.GetProperty("prescreenResult").GetRawText());
        Assert.Equal(before.GetProperty("filterResultRef").GetRawText(), loaded.GetProperty("filterResultRef").GetRawText());
        AssertStale(loaded.GetProperty("staleState"), false, false, false);
        Assert.Equal(4, await PreviewCountAsync(host));

        var retried = await UpdateAsync(host, new { prepStartDate = "2025-11-01" });
        Assert.True(retried.GetProperty("invalidatedResults").GetProperty("filterScenarios").GetBoolean());
        Assert.Empty((await LoadAsync(host, projectId)).GetProperty("filterScenarios").EnumerateArray());
        Assert.Equal(2, await PreviewCountAsync(host));
    }

    [Theory]
    [InlineData("sqlite", "2024-12-31")]
    [InlineData("duckdb", "2024-12-31")]
    [InlineData("sqlite", "2027-01-01")]
    [InlineData("duckdb", "2027-01-01")]
    public async Task UnusualPreparationDate_ReturnsWarningButStillPersists(string provider, string date)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host, provider);
        var updated = await UpdateAsync(host, new { prepStartDate = date });
        Assert.NotEmpty(updated.GetProperty("warnings").EnumerateArray());
        Assert.Equal(date, updated.GetProperty("project").GetProperty("prepStartDate").GetString());
        var loaded = await LoadAsync(host, projectId);
        Assert.Equal(date, loaded.GetProperty("project").GetProperty("prepStartDate").GetString());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ChangedPreparationDate_InvalidatesUnsavedPreviewEvenWithoutPersistedFilterResults(string provider)
    {
        using var host = new HandlerTestHost();
        await SetupAsync(host, provider);
        Assert.Equal(4, await PreviewCountAsync(host));

        var updated = await UpdateAsync(host, new { prepStartDate = "2025-11-01" });
        AssertStale(updated.GetProperty("staleState"), false, false, false);
        AssertStale(updated.GetProperty("invalidatedResults"), false, true, true);
        Assert.Equal(2, await PreviewCountAsync(host));

        var sameDate = await UpdateAsync(host, new { prepStartDate = "2025-11-01" });
        AssertStale(sameDate.GetProperty("invalidatedResults"), false, false, false);
        var textOnly = await UpdateAsync(host, new { clientName = "Synthetic display only" });
        AssertStale(textOnly.GetProperty("invalidatedResults"), false, false, false);
    }

    private static void AssertStale(JsonElement state, bool validation, bool prescreen, bool filter)
    {
        Assert.Equal(validation, state.GetProperty("validation").GetBoolean());
        Assert.Equal(prescreen, state.GetProperty("prescreen").GetBoolean());
        Assert.Equal(filter, state.GetProperty("filter").GetBoolean());
    }

    private static async Task MaterializeAsync(HandlerTestHost host)
    {
        var prescreen = await host.DispatchAsync("prescreen.run");
        Assert.Equal(4, prescreen.GetProperty("prepPeriodApproval").GetProperty("count").GetInt64());
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { PreparationScenario } }));
    }

    private static Task<JsonElement> UpdateAsync(HandlerTestHost host, object payload) =>
        host.DispatchAsync("project.update", JsonSerializer.Serialize(payload));

    private static Task<JsonElement> LoadAsync(HandlerTestHost host, string projectId) =>
        host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));

    private static async Task<long> PreviewCountAsync(HandlerTestHost host)
    {
        var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario = PreparationScenario }));
        return preview.GetProperty("scenario").GetProperty("count").GetInt64();
    }

    private static Task<string> SetupAsync(HandlerTestHost host, string provider) =>
        InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("UPDATE-1", "2025-03-01", "2025-06-01", "1101", "合成資產", "Synthetic 1", "1", 1)
            .AddRow("UPDATE-1", "2025-03-01", "2025-06-01", "4101", "合成收入", "Synthetic 1", "1", 0)
            .AddRow("UPDATE-2", "2025-03-01", "2025-09-01", "1101", "合成資產", "Synthetic 2", "1", 1)
            .AddRow("UPDATE-2", "2025-03-01", "2025-09-01", "4101", "合成收入", "Synthetic 2", "1", 0)
            .AddRow("UPDATE-3", "2025-03-01", "2025-12-01", "1101", "合成資產", "Synthetic 3", "1", 1)
            .AddRow("UPDATE-3", "2025-03-01", "2025-12-01", "4101", "合成收入", "Synthetic 3", "1", 0),
            prepStartDate: "2025-09-01", databaseProvider: provider, validateForDownstream: true);
}
