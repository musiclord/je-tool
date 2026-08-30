using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>master spec「匯出進度契約」：五個正式 action 的 event wire 與單調生命週期。</summary>
public sealed class ExportProgressEventTests
{
    private static readonly string[] AllowedPhases =
    [
        "preparingData",
        "writingSheet",
        "finalizingWorkbook",
        "publishingArtifact"
    ];

    [Fact]
    public void AccountMappingHandler_RetainsPreProgressPublicConstructor()
    {
        Assert.NotNull(typeof(ExportAccountMappingTemplateHandler).GetConstructor(
        [
            typeof(IAccountMappingExportRepository),
            typeof(IAccountMappingTemplateWriter),
            typeof(IRuleRunStore),
            typeof(IProjectStore),
            typeof(IReportArtifactStore),
            typeof(ProjectSession)
        ]));
    }

    [Fact]
    public async Task FormalExports_PublishExactMonotonicLifecycle_AndResponseRemainsCompletionAuthority()
    {
        using var host = new HandlerTestHost();
        _ = await ReportArtifactExportFixture.SetupProjectAsync(host);

        var validation = await host.DispatchAsync("validate.run");
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var validationExport = await CaptureAsync(
            host,
            "export.validationArtifacts",
            JsonSerializer.Serialize(new { runId = validationRunId }));
        AssertProgressContract(
            validationExport,
            ["validationReport", "accountMapping", "infReport"]);
        Assert.Equal(
            ["validationReport", "accountMapping", "infReport"],
            validationExport.Events
                .TakeLast(3)
                .Select(update =>
                {
                    Assert.Equal("publishingArtifact", update.GetProperty("phase").GetString());
                    return update.GetProperty("artifactKind").GetString()!;
                }));

        var accountMappingExport = await CaptureAsync(
            host,
            "export.accountMappingTemplate",
            JsonSerializer.Serialize(new { runId = validationRunId }));
        AssertProgressContract(accountMappingExport, ["accountMapping"]);

        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var prescreenExport = await CaptureAsync(
            host,
            "export.prescreenReport",
            JsonSerializer.Serialize(new { runId = prescreenRunId }));
        AssertProgressContract(prescreenExport, ["prescreenReport"]);

        var filter = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = new[]
                {
                    new
                    {
                        name = "進度契約情境",
                        rationale = "以合成摘要測試正式報表進度",
                        groups = new[]
                        {
                            new
                            {
                                join = "AND",
                                rules = new[]
                                {
                                    new { join = "AND", type = "customKeywords", keywords = "調整" }
                                }
                            }
                        }
                    }
                }
            }));
        var revision = filter.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var criteriaPayload = JsonSerializer.Serialize(new
        {
            validationRunId,
            prescreenRunId,
            revision
        });
        var criteriaExport = await CaptureAsync(
            host,
            "export.criteriaSelectionReport",
            criteriaPayload);
        AssertProgressContract(criteriaExport, ["criteriaSelectionReport"]);

        var workpaperExport = await CaptureAsync(
            host,
            "export.workpaperStream",
            JsonSerializer.Serialize(new
            {
                validationRunId,
                prescreenRunId,
                scenarioRevision = revision,
                scenarioPositions = new[] { 1 }
            }));
        AssertProgressContract(workpaperExport, ["workingPaper"]);
    }

    private static async Task<CapturedExport> CaptureAsync(
        HandlerTestHost host,
        string action,
        string payload)
    {
        var firstEvent = host.PublishedEvents.Count;
        var response = await host.DispatchAsync(action, payload);
        var events = host.PublishedEvents
            .Skip(firstEvent)
            .Where(item => item.EventName == "export.progress")
            .Select(item => JsonSerializer.SerializeToElement(
                item.Payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .ToArray();

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.NotEmpty(events);
        return new CapturedExport(action, response, events);
    }

    private static void AssertProgressContract(
        CapturedExport export,
        IReadOnlyList<string> expectedArtifactKinds)
    {
        var expectedKeys = new[]
        {
            "artifactKind",
            "phase",
            "sheetName",
            "sheetsCompleted",
            "rowsWritten",
            "elapsedMilliseconds"
        };
        long previousElapsed = -1;
        foreach (var update in export.Events)
        {
            Assert.Equal(expectedKeys, update.EnumerateObject().Select(property => property.Name));
            Assert.Contains(update.GetProperty("phase").GetString(), AllowedPhases);
            Assert.False(update.TryGetProperty("ok", out _));
            Assert.False(update.TryGetProperty("completed", out _));

            var elapsed = update.GetProperty("elapsedMilliseconds").GetInt64();
            Assert.True(
                elapsed >= previousElapsed,
                $"{export.Action} elapsedMilliseconds 從 {previousElapsed} 回退到 {elapsed}。\n{update}");
            previousElapsed = elapsed;
        }

        Assert.Equal(
            expectedArtifactKinds,
            export.Events
                .Select(update => update.GetProperty("artifactKind").GetString()!)
                .Distinct(StringComparer.Ordinal));

        foreach (var artifactKind in expectedArtifactKinds)
        {
            var updates = export.Events
                .Where(update => string.Equals(
                    update.GetProperty("artifactKind").GetString(),
                    artifactKind,
                    StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(updates);

            var transitions = updates
                .Select(update => update.GetProperty("phase").GetString()!)
                .Where((phase, index) => index == 0
                    || !string.Equals(
                        phase,
                        updates[index - 1].GetProperty("phase").GetString(),
                        StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(AllowedPhases, transitions);

            long previousRows = -1;
            var previousSheets = -1;
            foreach (var update in updates)
            {
                var phase = update.GetProperty("phase").GetString();
                var rows = update.GetProperty("rowsWritten").GetInt64();
                var sheets = update.GetProperty("sheetsCompleted").GetInt32();
                Assert.True(rows >= previousRows, $"{artifactKind} rowsWritten 不得回退。\n{update}");
                Assert.True(sheets >= previousSheets, $"{artifactKind} sheetsCompleted 不得回退。\n{update}");
                Assert.True(sheets - previousSheets <= 1, $"{artifactKind} sheetsCompleted 不得跳號。\n{update}");

                if (phase == "writingSheet")
                {
                    Assert.False(string.IsNullOrWhiteSpace(update.GetProperty("sheetName").GetString()));
                }
                else
                {
                    Assert.Equal(JsonValueKind.Null, update.GetProperty("sheetName").ValueKind);
                }

                previousRows = rows;
                previousSheets = sheets;
            }

            Assert.Equal(0, updates[0].GetProperty("sheetsCompleted").GetInt32());
            Assert.Equal(0, updates[0].GetProperty("rowsWritten").GetInt64());
            Assert.Equal("publishingArtifact", updates[^1].GetProperty("phase").GetString());
        }

        // 事件沒有成功旗標；只有 awaited action response 的 ok/artifact(s) 是完成權威。
        Assert.True(export.Response.GetProperty("ok").GetBoolean());
    }

    private sealed record CapturedExport(
        string Action,
        JsonElement Response,
        IReadOnlyList<JsonElement> Events);
}
