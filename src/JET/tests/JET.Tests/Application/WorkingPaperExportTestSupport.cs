using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>WorkingPaperExportHandler 各測試類別共用的輔助方法與事件發布器。</summary>
internal static class WorkingPaperExportTestSupport
{
    internal static IEnumerable<string> FindWorkingPapers(string folder) => Directory.EnumerateFiles(folder, "*WorkingPaper*.xlsx", SearchOption.AllDirectories);
    internal sealed class CancelOnFirstWorkingPaperSheetProgress(CancellationTokenSource source) : IJetEventPublisher
    {
        private int _cancelled;

        public List<JsonElement> WorkingPaperEvents { get; } = [];

        public void Publish(string eventName, object? payload)
        {
            if (eventName != "export.progress")
            {
                return;
            }

            var update = JsonSerializer.SerializeToElement(
                payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (!string.Equals(
                    update.GetProperty("reportKind").GetString(),
                    ReportKindValues.WorkingPaper,
                    StringComparison.Ordinal))
            {
                return;
            }

            WorkingPaperEvents.Add(update);
            if (string.Equals(
                    update.GetProperty("phase").GetString(),
                    "writingSheet",
                    StringComparison.Ordinal)
                && Interlocked.Exchange(ref _cancelled, 1) == 0)
            {
                source.Cancel();
            }
        }
    }

    internal sealed class ArmableCancelOnWorkingPaperPhase(
        CancellationTokenSource source,
        string targetPhase) : IJetEventPublisher
    {
        private int _armed;
        private int _cancelled;

        public List<JsonElement> WorkingPaperEvents { get; } = [];

        public void Arm()
        {
            Interlocked.Exchange(ref _cancelled, 0);
            Interlocked.Exchange(ref _armed, 1);
        }

        public void Publish(string eventName, object? payload)
        {
            if (eventName != "export.progress")
            {
                return;
            }

            var update = JsonSerializer.SerializeToElement(
                payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (!string.Equals(
                    update.GetProperty("reportKind").GetString(),
                    ReportKindValues.WorkingPaper,
                    StringComparison.Ordinal))
            {
                return;
            }

            WorkingPaperEvents.Add(update);
            if (Volatile.Read(ref _armed) != 0
                && string.Equals(
                    update.GetProperty("phase").GetString(),
                    targetPhase,
                    StringComparison.Ordinal)
                && Interlocked.Exchange(ref _cancelled, 1) == 0)
            {
                source.Cancel();
            }
        }
    }

    internal sealed record Prepared(
        string ProjectId,
        string ValidationResultId,
        string PrescreenResultId,
        string Revision,
        int[] Positions);

    internal static string ScenarioPayload() => JsonSerializer.Serialize(new
    {
        scenarios = new object[]
        {
            new
            {
                name = "非授權編製人員",
                rationale = "測試第一個情境",
                groups = new[] { new { join = "and", conditions = new object[] {
                    // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，改用示範資料同樣整張傳票兩列皆命中的非授權編製人員。
                    new { join = "and", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" }
                } } }
            },
            new
            {
                name = "借方行",
                rationale = "測試第二個情境",
                groups = new[] { new { join = "and", conditions = new object[] {
                    new { join = "and", type = "drCrOnly", drCr = "debit" }
                } } }
            }
        }
    });

    /// <summary>在一個測試程序裡只建一次，之後每個測試複製一份（<see cref="CaseTemplates"/>）。</summary>
    internal static async Task<Prepared> PrepareAsync(
        HandlerTestHost host,
        bool publishCriteria = true)
    {
        var copied = await CaseTemplates.CopyIntoAsync(host, $"workingPaper-export:{publishCriteria}", async templateHost =>
        {
            var prepared = await BuildPreparedAsync(templateHost, publishCriteria);
            return (prepared.ProjectId, prepared);
        });
        return copied.State with { Positions = [.. copied.State.Positions] };
    }

    private static async Task<Prepared> BuildPreparedAsync(
        HandlerTestHost host,
        bool publishCriteria)
    {
        var project = await DemoProjectPipeline.SetupAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var filter = await host.DispatchAsync("filter.commit", ScenarioPayload());
        var prepared = new Prepared(
            project.ProjectId,
            validation.GetProperty("resultRef").GetProperty("resultId").GetString()!,
            prescreen.GetProperty("resultRef").GetProperty("resultId").GetString()!,
            filter.GetProperty("resultRef").GetProperty("revision").GetString()!,
            [1, 2]);

        if (publishCriteria)
        {
            await host.DispatchAsync(
                "export.criteriaSelectionReport",
                JsonSerializer.Serialize(new
                {
                    validationResultId = prepared.ValidationResultId,
                    revision = prepared.Revision
                }));
        }

        return prepared;
    }

    internal static string Payload(Prepared prepared, IReadOnlyList<int>? positions = null) =>
        JsonSerializer.Serialize(new
        {
            validationResultId = prepared.ValidationResultId,
            scenarioRevision = prepared.Revision,
            scenarioPositions = positions ?? prepared.Positions
        });

    internal static async Task ReplaceValidationNullSummaryAsync(
        HandlerTestHost host,
        string projectId,
        string resultId,
        IReadOnlyList<int> counts)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        await using var read = connection.CreateCommand();
        read.CommandText =
            "SELECT summary_json FROM validation_result WHERE result_id = @resultId;";
        read.AddWithValue("@resultId", resultId);
        var raw = Assert.IsType<string>(await read.ExecuteScalarAsync());
        var summary = Assert.IsType<JsonObject>(JsonNode.Parse(raw));
        var blankRecords = Assert.IsType<JsonObject>(summary["blankRecordsTest"]);
        blankRecords["nullAccountCount"] = counts[0];
        blankRecords["nullDocumentCount"] = counts[1];
        blankRecords["nullDescriptionCount"] = counts[2];
        blankRecords["approvalDateNotInPeriodCount"] = counts[3];
        var sourceQuality = Assert.IsType<JsonObject>(summary["sourceQuality"]);
        sourceQuality["findingCount"] = counts[4];

        await using var update = connection.CreateCommand();
        update.CommandText =
            "UPDATE validation_result SET summary_json = @summary WHERE result_id = @resultId;";
        update.AddWithValue("@summary", summary.ToJsonString());
        update.AddWithValue("@resultId", resultId);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
    }

    internal static ProjectReportStore ReportStore(HandlerTestHost host) =>
        new(new JetProjectFolder(host.ProjectsRoot));

    internal static void AssertCurrentWorkingPaper(HandlerTestHost host, Prepared prepared, JsonElement response)
    {
        var report = response.GetProperty("publishedReport");
        Assert.Equal("workingPaper", report.GetProperty("kind").GetString());
        Assert.False(report.GetProperty("stale").GetBoolean());
        Assert.Equal(prepared.ValidationResultId, report.GetProperty("sourceRef").GetProperty("validationResultId").GetString());
        Assert.Equal(prepared.Revision, report.GetProperty("sourceRef").GetProperty("scenarioRevision").GetString());
        Assert.Equal(prepared.Positions, report.GetProperty("sourceRef").GetProperty("scenarioPositions").EnumerateArray().Select(p => p.GetInt32()));
        using var book = new XLWorkbook(Path.Combine(host.ProjectsRoot, prepared.ProjectId, report.GetProperty("fileName").GetString()!));
        // This fixed demo has no completeness differences: Step 1-3 is omitted, metadata is last.
        Assert.Equal(WorkingPaperSheetCatalog.All.Where(name => name != WorkingPaperSheetCatalog.Step13)
            .Append(ReportWorkbookMetadataFormat.WorksheetName), book.Worksheets.Select(sheet => sheet.Name));
        Assert.Equal(XLWorksheetVisibility.VeryHidden, book.Worksheet(ReportWorkbookMetadataFormat.WorksheetName).Visibility);
        Assert.True(book.Worksheet(WorkingPaperSheetCatalog.Step41).RowsUsed().Count() > 1);
    }
}
