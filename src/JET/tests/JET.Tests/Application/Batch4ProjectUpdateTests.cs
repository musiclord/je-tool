using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch4ProjectUpdateTests
{
    private static readonly object PreparationScenario = new
    {
        name = "Synthetic preparation date", rationale = "Fixed date boundary",
        groups = new[] { new { rules = new[] { new { type = "prescreen", prescreenKey = "postPeriodApproval" } } } }
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
                entityName = "  Synthetic revised client  ", projectCode = "  SYN-NEW  ",
                caseName = "must-not-rename", projectId = "must-not-switch",
                periodStart = "2020-01-01", periodEnd = "2020-12-31", databaseProvider = "sqlServer"
            });
            var project = updated.GetProperty("project");
            Assert.Equal("Synthetic revised client", project.GetProperty("entityName").GetString());
            Assert.Equal("SYN-NEW", project.GetProperty("projectCode").GetString());
            Assert.Equal("2025-09-01", project.GetProperty("lastPeriodStart").GetString());
            foreach (var key in new[] { "projectId", "periodStart", "periodEnd", "databaseProvider", "operatorId", "moneyScale", "createdUtc" })
                Assert.Equal(before.GetProperty(key).GetRawText(), project.GetProperty(key).GetRawText());
            Assert.Empty(updated.GetProperty("warnings").EnumerateArray());
            Assert.Equal(JsonValueKind.Array, updated.GetProperty("artifacts").ValueKind);

            var partial = await UpdateAsync(host, new { entityName = (string?)null });
            Assert.Equal("", partial.GetProperty("project").GetProperty("entityName").GetString());
            Assert.Equal("SYN-NEW", partial.GetProperty("project").GetProperty("projectCode").GetString());
        }
        using var reopened = new HandlerTestHost(projectsRootPath: root.Path);
        var restored = (await LoadAsync(reopened, projectId)).GetProperty("project");
        Assert.Equal("", restored.GetProperty("entityName").GetString());
        Assert.Equal("SYN-NEW", restored.GetProperty("projectCode").GetString());
        Assert.Equal("2025-09-01", restored.GetProperty("lastPeriodStart").GetString());
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
        await UpdateAsync(host, new { entityName = "Synthetic revised display", projectCode = "NEXT" });
        var noOp = await UpdateAsync(host, new { lastPeriodStart = "2025-09-01" });
        AssertStale(noOp.GetProperty("staleState"), false, false, false);
        var loaded = await LoadAsync(host, projectId);
        Assert.Equal(before.GetProperty("latestRuns").GetRawText(), loaded.GetProperty("latestRuns").GetRawText());
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
        var updated = await UpdateAsync(host, new { lastPeriodStart = "2025-11-01" });
        AssertStale(updated.GetProperty("staleState"), false, true, true);
        var loaded = await LoadAsync(host, projectId);
        AssertStale(loaded.GetProperty("staleState"), false, true, true);
        Assert.Equal(before.GetProperty("latestRuns").GetProperty("validate").GetRawText(),
            loaded.GetProperty("latestRuns").GetProperty("validate").GetRawText());
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("latestRuns").GetProperty("prescreen").ValueKind);
        Assert.Equal(before.GetProperty("filterScenarios").GetRawText(), loaded.GetProperty("filterScenarios").GetRawText());
        Assert.Equal(2, await PreviewCountAsync(host));
        var rerun = await host.DispatchAsync("prescreen.run");
        Assert.Equal(2, rerun.GetProperty("postPeriodApproval").GetProperty("count").GetInt64());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ClearingPreparationDate_PreservesScenarios_AndExplainsWhereToRestoreIt(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host, provider);
        await MaterializeAsync(host);
        var updated = await UpdateAsync(host, new { lastPeriodStart = (string?)null, projectCode = (string?)null });
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("project").GetProperty("lastPeriodStart").ValueKind);
        Assert.Equal("", updated.GetProperty("project").GetProperty("projectCode").GetString());
        var loaded = await LoadAsync(host, projectId);
        Assert.Single(loaded.GetProperty("filterScenarios").EnumerateArray());
        var error = await Assert.ThrowsAsync<JetActionException>(() => PreviewCountAsync(host));
        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
        Assert.Contains("修改案件資料", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("lastPeriodStart", error.Message, StringComparison.Ordinal);
        var prescreen = await host.DispatchAsync("prescreen.run");
        var reason = prescreen.GetProperty("postPeriodApproval").GetProperty("naReason").GetString()!;
        Assert.Contains("修改案件資料", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("lastPeriodStart", reason, StringComparison.Ordinal);
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
        var updated = await UpdateAsync(host, new { lastPeriodStart = date });
        Assert.NotEmpty(updated.GetProperty("warnings").EnumerateArray());
        Assert.Equal(date, updated.GetProperty("project").GetProperty("lastPeriodStart").GetString());
        var loaded = await LoadAsync(host, projectId);
        Assert.Equal(date, loaded.GetProperty("project").GetProperty("lastPeriodStart").GetString());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ChangedPreparationDate_InvalidatesUnsavedPreviewEvenWithoutPersistedFilterResults(string provider)
    {
        using var host = new HandlerTestHost();
        await SetupAsync(host, provider);
        Assert.Equal(4, await PreviewCountAsync(host));

        var updated = await UpdateAsync(host, new { lastPeriodStart = "2025-11-01" });
        AssertStale(updated.GetProperty("staleState"), false, false, false);
        AssertStale(updated.GetProperty("invalidatedResults"), false, true, true);
        Assert.Equal(2, await PreviewCountAsync(host));

        var sameDate = await UpdateAsync(host, new { lastPeriodStart = "2025-11-01" });
        AssertStale(sameDate.GetProperty("invalidatedResults"), false, false, false);
        var textOnly = await UpdateAsync(host, new { entityName = "Synthetic display only" });
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
        Assert.Equal(4, prescreen.GetProperty("postPeriodApproval").GetProperty("count").GetInt64());
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
            lastPeriodStart: "2025-09-01", databaseProvider: provider, validateForDownstream: true);
}
