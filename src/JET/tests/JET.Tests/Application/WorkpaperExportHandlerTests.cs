using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>WorkingPaper 新契約：project-local artifact、明確來源版本、情境選擇與固定工作表。</summary>
public sealed class WorkpaperExportHandlerTests
{
    private sealed class CancelOnFirstWorkpaperSheetProgress(CancellationTokenSource source) : IJetEventPublisher
    {
        private int _cancelled;

        public List<JsonElement> WorkpaperEvents { get; } = [];

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
                    update.GetProperty("artifactKind").GetString(),
                    ReportArtifactKindValues.WorkingPaper,
                    StringComparison.Ordinal))
            {
                return;
            }

            WorkpaperEvents.Add(update);
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

    private sealed class ArmableCancelOnWorkpaperPhase(
        CancellationTokenSource source,
        string targetPhase) : IJetEventPublisher
    {
        private int _armed;
        private int _cancelled;

        public List<JsonElement> WorkpaperEvents { get; } = [];

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
                    update.GetProperty("artifactKind").GetString(),
                    ReportArtifactKindValues.WorkingPaper,
                    StringComparison.Ordinal))
            {
                return;
            }

            WorkpaperEvents.Add(update);
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

    private sealed record Prepared(
        string ProjectId,
        string ValidationRunId,
        string PrescreenRunId,
        string Revision,
        int[] Positions);

    private static string ScenarioPayload() => JsonSerializer.Serialize(new
    {
        scenarios = new object[]
        {
            new
            {
                name = "回溯過帳",
                rationale = "測試第一個情境",
                groups = new[] { new { join = "and", rules = new object[] {
                    new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" }
                } } }
            },
            new
            {
                name = "借方行",
                rationale = "測試第二個情境",
                groups = new[] { new { join = "and", rules = new object[] {
                    new { join = "and", type = "drCrOnly", drCr = "debit" }
                } } }
            }
        }
    });

    private static async Task<Prepared> PrepareAsync(
        HandlerTestHost host,
        bool publishCriteria = true)
    {
        var project = await DemoProjectPipeline.SetupAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var filter = await host.DispatchAsync("filter.commit", ScenarioPayload());
        var prepared = new Prepared(
            project.ProjectId,
            validation.GetProperty("resultRef").GetProperty("runId").GetString()!,
            prescreen.GetProperty("resultRef").GetProperty("runId").GetString()!,
            filter.GetProperty("resultRef").GetProperty("revision").GetString()!,
            [1, 2]);

        if (publishCriteria)
        {
            await host.DispatchAsync(
                "export.criteriaSelectionReport",
                JsonSerializer.Serialize(new
                {
                    validationRunId = prepared.ValidationRunId,
                    revision = prepared.Revision
                }));
        }

        return prepared;
    }

    private static string Payload(Prepared prepared, IReadOnlyList<int>? positions = null) =>
        JsonSerializer.Serialize(new
        {
            validationRunId = prepared.ValidationRunId,
            scenarioRevision = prepared.Revision,
            scenarioPositions = positions ?? prepared.Positions
        });

    [Fact]
    public async Task FilterCompleteness_WorkpaperIncludesNewConditions()
    {
        using var host = new HandlerTestHost();
        var project = await DemoProjectPipeline.SetupAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString();
        var saved = await host.DispatchAsync("filter.commit", """
          {"scenarios":[{"name":"新條件底稿","rationale":"核對條件與輸出","groups":[{"rules":[
            {"type":"fieldValue","field":"postDate","operator":"in","values":["2025-01-15","2025-02-01"],"includeBlank":false},
            {"type":"fieldValue","field":"description","operator":"notContains","value":"TEST","includeBlank":false}
          ]}]}]}
          """);
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString();
        await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new { validationRunId, revision }));
        var exported = await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        { validationRunId, scenarioRevision = revision, scenarioPositions = new[] { 1 } }));
        using var workbook = new XLWorkbook(Path.Combine(host.ProjectsRoot, project.ProjectId,
            exported.GetProperty("artifact").GetProperty("fileName").GetString()!));
        var text = workbook.Worksheet(WorkpaperSheetCatalog.Step3).Cell("C19").GetString();
        Assert.StartsWith("新條件底稿" + Environment.NewLine + "篩選條件：", text, StringComparison.Ordinal);
        Assert.Contains("2025-01-15", text, StringComparison.Ordinal);
        Assert.Contains("2025-02-01", text, StringComparison.Ordinal);
        Assert.Contains("不包含任何文字「TEST」", text, StringComparison.Ordinal);
        Assert.DoesNotContain("排除區域", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilterCompleteness_LongConditionsRemainCompleteWithinExcelCellLimits()
    {
        using var host = new HandlerTestHost();
        var project = await DemoProjectPipeline.SetupAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString();
        var value = new string('X', 33_000);
        var expected = "傳票摘要 開頭符合「" + value + "」；空白不列入";
        var scenario = new { name = "Long conditions", rationale = "Synthetic", groups = new[] { new { rules = new[] {
            new { type = "fieldValue", field = "description", @operator = "startsWith", value, includeBlank = false }
        } } } };
        var saved = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString();
        var criteria = await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new { validationRunId, revision }));
        var exported = await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        { validationRunId, scenarioRevision = revision, scenarioPositions = new[] { 1 } }));
        XLWorkbook Open(JsonElement response) => new(Path.Combine(host.ProjectsRoot, project.ProjectId,
            response.GetProperty("artifact").GetProperty("fileName").GetString()!));
        using var paper = Open(exported);
        var chunks = paper.Worksheet(WorkpaperSheetCatalog.Step3).Column(3).CellsUsed()
            .Where(cell => cell.Address.RowNumber >= 19).Select(cell => cell.GetString()).ToArray();
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 32_767));
        Assert.Equal("Long conditions\n篩選條件：" + expected, string.Concat(chunks).Replace("\r\n", "\n", StringComparison.Ordinal));
        using var report = Open(criteria);
        var sheet = report.Worksheet("Summary Inforamtion");
        Assert.Contains("完整條件", sheet.Cell("B5").GetString(), StringComparison.Ordinal);
        var reportChunks = sheet.Column(2).CellsUsed().Where(cell => cell.Address.RowNumber >= 16).Select(cell => cell.GetString()).ToArray();
        Assert.All(reportChunks, chunk => Assert.InRange(chunk.Length, 1, 32_767));
        Assert.Equal(expected, string.Concat(reportChunks));
    }

    [Fact]
    public async Task Export_WritesProjectLocalArtifact_WithoutAbsolutePath_AndFixedSheets()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);

        var response = await host.DispatchAsync("export.workpaperStream", Payload(prepared));
        var artifact = response.GetProperty("artifact");
        Assert.False(artifact.TryGetProperty("outputPath", out _));
        Assert.False(response.TryGetProperty("outputPath", out _));
        Assert.Equal("workingPaper", artifact.GetProperty("kind").GetString());

        var fileName = artifact.GetProperty("fileName").GetString()!;
        Assert.Equal(fileName, Path.GetFileName(fileName));
        var path = Path.Combine(host.ProjectsRoot, prepared.ProjectId, fileName);
        Assert.True(File.Exists(path));

        using var workbook = new XLWorkbook(path);
        var sheetNames = workbook.Worksheets.Select(sheet => sheet.Name).ToArray();
        var emittedLegacySheetNames = sheetNames
            .Where(name => !string.Equals(
                name,
                ReportWorkbookMetadataFormat.WorksheetName,
                StringComparison.Ordinal))
            .ToArray();
        var canonicalEmittedOrder = WorkpaperSheetCatalog.All
            .Where(emittedLegacySheetNames.Contains)
            .ToArray();
        var responseSheetNames = response.GetProperty("sheetStats")
            .EnumerateArray()
            .Select(item => item.GetProperty("sheetName").GetString()!)
            .ToArray();
        Assert.Equal(canonicalEmittedOrder, emittedLegacySheetNames);
        Assert.Equal(
            ReportWorkbookMetadataFormat.WorksheetName,
            Assert.Single(sheetNames.Skip(emittedLegacySheetNames.Length)));
        Assert.Equal(
            XLWorksheetVisibility.VeryHidden,
            workbook.Worksheet(ReportWorkbookMetadataFormat.WorksheetName).Visibility);
        Assert.Equal(emittedLegacySheetNames, responseSheetNames);
        Assert.DoesNotContain("step1-3-1完整性差異調節", sheetNames);
        Assert.Equal(WorkpaperSheetCatalog.Cover, workbook.Worksheet(1).Name);
        Assert.Equal(
            "篩選測試母體 : 查核期間",
            workbook.Worksheet(WorkpaperSheetCatalog.Cover).Cell("A3").GetString());
        Assert.Contains(
            "查核期間內",
            workbook.Worksheet(WorkpaperSheetCatalog.Step3).Cell("B7").GetString(),
            StringComparison.Ordinal);
        foreach (var required in new[]
        {
            WorkpaperSheetCatalog.Intro,
            WorkpaperSheetCatalog.Step1,
            WorkpaperSheetCatalog.Step11,
            WorkpaperSheetCatalog.Step12,
            WorkpaperSheetCatalog.Step2,
            WorkpaperSheetCatalog.Step3,
            WorkpaperSheetCatalog.Step4,
            WorkpaperSheetCatalog.Step41,
            WorkpaperSheetCatalog.Step5,
            WorkpaperSheetCatalog.FieldInfo,
            WorkpaperSheetCatalog.CalendarInfo,
            WorkpaperSheetCatalog.AccountMapping
        })
        {
            Assert.True(workbook.TryGetWorksheet(required, out _), required);
        }
    }

    [Fact]
    public async Task Export_SelectedScenario_ChangesScenarioColumns_NotWorksheetSet()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        var response = await host.DispatchAsync("export.workpaperStream", Payload(prepared, [2]));
        var path = Path.Combine(
            host.ProjectsRoot,
            prepared.ProjectId,
            response.GetProperty("artifact").GetProperty("fileName").GetString()!);

        using var workbook = new XLWorkbook(path);
        var step3 = workbook.Worksheet(WorkpaperSheetCatalog.Step3);
        Assert.Equal("C2", step3.Cell("B19").GetString());
        Assert.True(step3.Cell("B20").IsEmpty());
        Assert.All(
            step3.Range("F18:F28").Cells(),
            cell => Assert.True(cell.IsEmpty()));
        var step4 = workbook.Worksheet(WorkpaperSheetCatalog.Step4);
        Assert.Equal("C1", step4.Cell("F11").GetString());
        Assert.Equal("C2", step4.Cell("G11").GetString());
        Assert.Equal("C10", step4.Cell("O11").GetString());
        Assert.True(workbook.TryGetWorksheet(WorkpaperSheetCatalog.Step1, out _));
        Assert.True(workbook.TryGetWorksheet(WorkpaperSheetCatalog.Step5, out _));
    }

    [Fact]
    public async Task ValidationNullSummary_1234289_PreservesWorkpaperCatalogAndNormalizedFingerprint()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);

        var baselineResponse = await host.DispatchAsync(
            "export.workpaperStream",
            Payload(prepared));
        var baselinePath = Path.Combine(
            host.ProjectsRoot,
            prepared.ProjectId,
            baselineResponse.GetProperty("artifact").GetProperty("fileName").GetString()!);
        var baseline = NormalizedOpenXmlWorkbookSnapshot.Capture(baselinePath);
        using var baselineWorkbook = new XLWorkbook(baselinePath);
        var baselineCatalog = baselineWorkbook.Worksheets
            .Select(sheet => sheet.Name)
            .ToArray();

        var counts = new[] { 1_234_000, 100, 100, 50, 39 };
        Assert.Equal(1_234_289, counts.Sum());
        await ReplaceValidationNullSummaryAsync(
            host,
            prepared.ProjectId,
            prepared.ValidationRunId,
            counts);

        var hugeResponse = await host.DispatchAsync(
            "export.workpaperStream",
            Payload(prepared));
        var hugePath = Path.Combine(
            host.ProjectsRoot,
            prepared.ProjectId,
            hugeResponse.GetProperty("artifact").GetProperty("fileName").GetString()!);
        var huge = NormalizedOpenXmlWorkbookSnapshot.Capture(hugePath);
        using var hugeWorkbook = new XLWorkbook(hugePath);

        Assert.Equal(baseline.Fingerprint, huge.Fingerprint);
        Assert.Equal(
            baselineCatalog,
            hugeWorkbook.Worksheets.Select(sheet => sheet.Name).ToArray());
    }

    [Fact]
    public async Task Export_StaleRunOrRevision_IsRejectedWithoutArtifact()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        var folder = Path.Combine(host.ProjectsRoot, prepared.ProjectId);

        var staleRun = JsonSerializer.Serialize(new
        {
            validationRunId = new string('a', 32),
            scenarioRevision = prepared.Revision,
            scenarioPositions = prepared.Positions
        });
        var runError = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workpaperStream", staleRun));
        Assert.Equal(JetErrorCodes.StaleResult, runError.Code);
        Assert.Equal(
            "指定的 validate 執行結果已不是目前版本，請重新執行並使用最新結果。",
            runError.Message);

        var staleRevision = JsonSerializer.Serialize(new
        {
            validationRunId = prepared.ValidationRunId,
            scenarioRevision = "stale",
            scenarioPositions = prepared.Positions
        });
        var revisionError = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workpaperStream", staleRevision));
        Assert.Equal(JetErrorCodes.StaleResult, revisionError.Code);
        Assert.Empty(Directory.GetFiles(folder, "*_WorkingPaper.xlsx"));
    }

    [Fact]
    public async Task Export_MissingCriteriaArtifact_ThrowsStaleResultWithoutArtifact()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, publishCriteria: false);

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workpaperStream", Payload(prepared)));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
        Assert.Empty(Directory.GetFiles(
            Path.Combine(host.ProjectsRoot, prepared.ProjectId),
            "*_WorkingPaper.xlsx"));
    }

    [Fact]
    public async Task Export_StaleCriteriaArtifact_ThrowsStaleResultWithoutArtifact()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        var store = ArtifactStore(host);
        await store.MarkStaleAsync(
            prepared.ProjectId,
            ReportArtifactKind.CriteriaSelectionReport,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workpaperStream", Payload(prepared)));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
        Assert.Empty(Directory.GetFiles(
            Path.Combine(host.ProjectsRoot, prepared.ProjectId),
            "*_WorkingPaper.xlsx"));
    }

    [Fact]
    public async Task Export_CriteriaArtifactFileWasDeleted_ThrowsStaleResultWithoutArtifact()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, prepared.ProjectId);
        var criteriaPath = Assert.Single(Directory.GetFiles(
            projectDirectory,
            "*_CriteriaSelectionReport.xlsx"));
        File.Delete(criteriaPath);

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workpaperStream", Payload(prepared)));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
        Assert.Empty(Directory.GetFiles(projectDirectory, "*_WorkingPaper.xlsx"));
    }

    [Theory]
    [InlineData("validationRun")]
    [InlineData("revision")]
    [InlineData("positions")]
    public async Task Export_CriteriaArtifactDoesNotMatchCompleteCurrentSource_ThrowsStaleResult(
        string variant)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, publishCriteria: false);
        var source = new ReportArtifactSourceRefs(
            variant == "validationRun" ? new string('a', 32) : prepared.ValidationRunId,
            prepared.PrescreenRunId,
            variant == "revision" ? "outdated-revision" : prepared.Revision,
            variant == "positions" ? [1] : prepared.Positions);
        await ArtifactStore(host).WriteAsync(
            prepared.ProjectId,
            new ReportArtifactWriteRequest(
                ReportArtifactKind.CriteriaSelectionReport,
                source,
                (output, cancellationToken) => output.WriteAsync(
                    new byte[] { 1 },
                    cancellationToken).AsTask()),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workpaperStream", Payload(prepared)));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
        Assert.Empty(Directory.GetFiles(
            Path.Combine(host.ProjectsRoot, prepared.ProjectId),
            "*_WorkingPaper.xlsx"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public async Task Export_InvalidScenarioPositions_IsRejected(string variant)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        int[] positions = variant switch
        {
            "empty" => [],
            "duplicate" => [1, 1],
            _ => [99]
        };

        var error = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workpaperStream", Payload(prepared, positions)));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
    }

    [Theory]
    [InlineData("sheets")]
    [InlineData("outputPath")]
    public async Task Export_DeprecatedSheetOrPathField_IsRejected(string field)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        var payload = new Dictionary<string, object?>
        {
            ["validationRunId"] = prepared.ValidationRunId,
            ["scenarioRevision"] = prepared.Revision,
            ["scenarioPositions"] = prepared.Positions,
            [field] = field == "sheets" ? new[] { WorkpaperSheetCatalog.Cover } : "C:\\outside.xlsx"
        };

        var error = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(payload)));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
    }

    [Fact]
    public async Task Export_RematerializesMatrixBeforeWriting()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        await DemoProjectPipeline.QueryScalarAsync(
            host, prepared.ProjectId, "DELETE FROM result_filter_run; SELECT changes();");

        await host.DispatchAsync("export.workpaperStream", Payload(prepared));

        var count = await DemoProjectPipeline.QueryScalarAsync(
            host, prepared.ProjectId, "SELECT COUNT(*) FROM result_filter_run;");
        Assert.True(count > 0);
    }

    [Fact]
    public async Task Cancellation_CleansAllTemporaryAndUnindexedFiles()
    {
        using var source = new CancellationTokenSource();
        var publisher = new CancelOnFirstWorkpaperSheetProgress(source);
        using var host = new HandlerTestHost(eventPublisher: publisher);
        var prepared = await PrepareAsync(host);
        var folder = Path.Combine(host.ProjectsRoot, prepared.ProjectId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.DispatchAsync("export.workpaperStream", Payload(prepared), source.Token));

        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        Assert.Empty(Directory.GetFiles(folder, "*_WorkingPaper.xlsx"));
        Assert.NotEmpty(publisher.WorkpaperEvents);
        Assert.Equal("preparingData", publisher.WorkpaperEvents[0].GetProperty("phase").GetString());
        Assert.Equal("writingSheet", publisher.WorkpaperEvents[^1].GetProperty("phase").GetString());
        Assert.DoesNotContain(
            publisher.WorkpaperEvents,
            update => update.GetProperty("phase").GetString() is "finalizingWorkbook" or "publishingArtifact");
    }

    [Fact]
    public async Task CancellationAtFinalizingProgress_PreservesPriorArtifactAndManifest()
    {
        using var source = new CancellationTokenSource();
        var publisher = new ArmableCancelOnWorkpaperPhase(
            source,
            "finalizingWorkbook");
        using var host = new HandlerTestHost(eventPublisher: publisher);
        var prepared = await PrepareAsync(host);
        var folder = Path.Combine(host.ProjectsRoot, prepared.ProjectId);
        var first = await host.DispatchAsync(
            "export.workpaperStream",
            Payload(prepared));
        var fileName = first.GetProperty("artifact").GetProperty("fileName").GetString()!;
        var artifactPath = Path.Combine(folder, fileName);
        var manifestPath = Path.Combine(folder, ProjectReportArtifactStore.ManifestFileName);
        var artifactBytes = await File.ReadAllBytesAsync(artifactPath);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var eventStart = publisher.WorkpaperEvents.Count;
        publisher.Arm();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.DispatchAsync(
                "export.workpaperStream",
                Payload(prepared),
                source.Token));

        Assert.Equal(artifactBytes, await File.ReadAllBytesAsync(artifactPath));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(manifestPath));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(
            folder,
            ProjectReportArtifactStore.JournalFileName)));
        var cancelledEvents = publisher.WorkpaperEvents.Skip(eventStart).ToArray();
        Assert.Contains(
            cancelledEvents,
            update => update.GetProperty("phase").GetString() == "finalizingWorkbook");
        Assert.DoesNotContain(
            cancelledEvents,
            update => update.GetProperty("phase").GetString() == "publishingArtifact");
    }

    private static async Task ReplaceValidationNullSummaryAsync(
        HandlerTestHost host,
        string projectId,
        string runId,
        IReadOnlyList<int> counts)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        await using var read = connection.CreateCommand();
        read.CommandText =
            "SELECT summary_json FROM result_rule_run WHERE run_id = @runId;";
        read.AddWithValue("@runId", runId);
        var raw = Assert.IsType<string>(await read.ExecuteScalarAsync());
        var summary = Assert.IsType<JsonObject>(JsonNode.Parse(raw));
        var nullRecords = Assert.IsType<JsonObject>(summary["nullRecordsTest"]);
        nullRecords["nullAccountCount"] = counts[0];
        nullRecords["nullDocumentCount"] = counts[1];
        nullRecords["nullDescriptionCount"] = counts[2];
        nullRecords["outOfRangeDateCount"] = counts[3];
        var sourceQuality = Assert.IsType<JsonObject>(summary["sourceQuality"]);
        sourceQuality["findingCount"] = counts[4];

        await using var update = connection.CreateCommand();
        update.CommandText =
            "UPDATE result_rule_run SET summary_json = @summary WHERE run_id = @runId;";
        update.AddWithValue("@summary", summary.ToJsonString());
        update.AddWithValue("@runId", runId);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
    }

    private static ProjectReportArtifactStore ArtifactStore(HandlerTestHost host) =>
        new(new JetProjectFolder(host.ProjectsRoot));
}
