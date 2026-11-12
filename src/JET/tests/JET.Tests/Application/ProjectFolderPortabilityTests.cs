using System.Globalization;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 本地案件資料夾可攜性 characterization：來源 app 關閉後整夾複製，目標根必須能完整續作。
/// 不測、不承諾 SQLite WAL 仍開啟時的熱複製。
/// </summary>
public sealed class ProjectFolderPortabilityTests
{
    private const string PersistedMessage = "可攜性驗收訊息";

    [Theory]
    [InlineData("sqlite")]
    public async Task ClosedProjectFolderCopiedToNewRoot_ResumesCompleteJourney(string databaseProvider)
    {
        using var sourceRoot = new TempProjectRoot();
        using var targetRoot = new TempProjectRoot();

        string projectId;
        string reportId;
        string reportFileName;
        IReadOnlyList<string> sourceInfSample;
        long sourceSampleSeed;
        int sourceSampleSeedVersion;

        var source = new HandlerTestHost(projectsRootPath: sourceRoot.Path);
        try
        {
            projectId = await InlineWorkbookProject.SetupAsync(
                source,
                ConfigureGl,
                prepStartDate: "2025-12-31",
                databaseProvider: databaseProvider,
                configureTb: ConfigureTb);

            var validation = await source.DispatchAsync("validate.run");
            Assert.Equal(59, validation.GetProperty("infSamplingTest").GetProperty("sampleSize").GetInt64());
            var prescreen = await source.DispatchAsync("prescreen.run");
            var filter = await source.DispatchAsync("filter.commit", FilterPayload);

            var report = await source.DispatchAsync(
                "export.criteriaSelectionReport",
                JsonSerializer.Serialize(new
                {
                    validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString(),
                    prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString(),
                    revision = filter.GetProperty("resultRef").GetProperty("revision").GetString()
                }));
            reportId = report.GetProperty("publishedReport").GetProperty("reportId").GetString()!;
            reportFileName = report.GetProperty("publishedReport").GetProperty("fileName").GetString()!;

            await source.DispatchAsync(
                "log.append",
                JsonSerializer.Serialize(new { level = "info", text = PersistedMessage }));
            sourceInfSample = await WalkInfSampleAsync(source);
            Assert.Equal(59, sourceInfSample.Count);
            sourceSampleSeed = ReadSampleSeed(sourceRoot.Path, projectId);
            sourceSampleSeedVersion = ReadSampleSeedVersion(sourceRoot.Path, projectId);

            await source.DispatchAsync("project.saveProgress", """{ "currentStep": 4 }""");
            await source.DispatchAsync("project.releaseLock");
        }
        finally
        {
            source.Dispose();
            ReleaseProviderFileHandles(databaseProvider, sourceRoot.Path, projectId: null);
        }

        ReleaseProviderFileHandles(databaseProvider, sourceRoot.Path, projectId);
        var sourceProjectDirectory = Path.Combine(sourceRoot.Path, projectId);
        var targetProjectDirectory = Path.Combine(targetRoot.Path, projectId);
        CopyDirectory(sourceProjectDirectory, targetProjectDirectory);
        Directory.Delete(sourceProjectDirectory, recursive: true);

        var shell = new RecordingHostShell();
        var target = new HandlerTestHost(shell, projectsRootPath: targetRoot.Path);
        try
        {
            var list = await target.DispatchAsync("project.listLocal");
            Assert.Contains(
                list.GetProperty("projects").EnumerateArray(),
                project => project.GetProperty("projectId").GetString() == projectId);

            var loaded = await target.DispatchAsync(
                "project.load",
                JsonSerializer.Serialize(new { projectId }));
            Assert.Equal(databaseProvider, loaded.GetProperty("project").GetProperty("databaseProvider").GetString());
            Assert.Equal(4, loaded.GetProperty("project").GetProperty("currentStep").GetInt32());
            Assert.True(loaded.GetProperty("importState").GetProperty("gl").GetProperty("rowCount").GetInt64() >= 62);
            Assert.True(loaded.GetProperty("importState").GetProperty("tb").GetProperty("rowCount").GetInt64() >= 2);
            Assert.Equal(JsonValueKind.Object, loaded.GetProperty("mapping").GetProperty("gl").ValueKind);
            Assert.Equal(JsonValueKind.Object, loaded.GetProperty("mapping").GetProperty("tb").ValueKind);
            Assert.Equal(JsonValueKind.Object, loaded.GetProperty("validationResult").ValueKind);
            Assert.Equal(JsonValueKind.Object, loaded.GetProperty("prescreenResult").ValueKind);
            Assert.Single(loaded.GetProperty("filterScenarios").EnumerateArray());
            Assert.Equal(JsonValueKind.Object, loaded.GetProperty("filterResultRef").ValueKind);
            Assert.Contains(
                loaded.GetProperty("reports").EnumerateArray(),
                report => report.GetProperty("reportId").GetString() == reportId);
            Assert.DoesNotContain(sourceRoot.Path, loaded.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(targetRoot.Path, loaded.GetRawText(), StringComparison.OrdinalIgnoreCase);

            await AssertMovedPersistenceIsPortableAsync(
                databaseProvider,
                sourceRoot.Path,
                targetRoot.Path,
                projectId);
            await AssertPortableQuerySurfacesAsync(target);

            var recent = await target.DispatchAsync("log.recent", """{ "limit": 10 }""");
            Assert.Contains(
                recent.GetProperty("messages").EnumerateArray(),
                message => message.GetProperty("text").GetString() == PersistedMessage);

            var copiedInfSample = await WalkInfSampleAsync(target);
            Assert.Equal(sourceInfSample, copiedInfSample);
            Assert.Equal(sourceSampleSeed, ReadSampleSeed(targetRoot.Path, projectId));
            Assert.Equal(sourceSampleSeedVersion, ReadSampleSeedVersion(targetRoot.Path, projectId));

            // 不能只證明已落地樣本隨資料夾搬過來：在新根實際重跑 validation，
            // 再比對完整樣本集合，鎖住抽樣只依 project.json seed 與資料列穩定鍵、不得依賴機器或根路徑。
            var rerunValidation = await target.DispatchAsync("validate.run");
            Assert.Equal(59, rerunValidation.GetProperty("infSamplingTest").GetProperty("sampleSize").GetInt64());
            var resampledInfSample = await WalkInfSampleAsync(target);
            Assert.Equal(sourceInfSample, resampledInfSample);

            // 搬到新根後，報告清單仍列出同一筆產物，且 wire 上沒有任何一邊的絕對路徑。
            var reloaded = await target.DispatchAsync(
                "project.load",
                JsonSerializer.Serialize(new { projectId }));
            var movedReport = Assert.Single(
                reloaded.GetProperty("reports").EnumerateArray(),
                report => report.GetProperty("reportId").GetString() == reportId);
            Assert.Equal(reportFileName, movedReport.GetProperty("fileName").GetString());
            Assert.DoesNotContain(sourceRoot.Path, reloaded.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(targetRoot.Path, reloaded.GetRawText(), StringComparison.OrdinalIgnoreCase);

            AssertManifestUsesRelativeFileNames(targetProjectDirectory, reportId, reportFileName);
            await target.DispatchAsync(
                "host.openFolder",
                JsonSerializer.Serialize(new { reportId }));
            await target.DispatchAsync("host.openFolder", """{ "target": "projectFolder" }""");
            Assert.Equal(
                Path.GetFullPath(Path.Combine(targetProjectDirectory, reportFileName)),
                shell.RevealedPaths[0]);
            Assert.Equal(Path.GetFullPath(targetProjectDirectory), shell.RevealedPaths[1]);

            // 情境定義保留、命中清空時，首頁查詢應在新根惰性補算。上游修改會清除下游，所以
            // 變更非工作日會連情境一起清掉，這裡用開案規則升版的情境整批取代製造這個狀態。
            await SavedScenarioReplay.ReplaceLikeRuleUpgradeAsync(target, projectId, databaseProvider);
            var lazyPage = await target.DispatchAsync(
                "query.filterHitsPage",
                """{ "scenarioPosition": 1, "pageSize": 200 }""");
            Assert.True(lazyPage.GetProperty("rows").GetArrayLength() > 0);
        }
        finally
        {
            target.Dispose();
            ReleaseProviderFileHandles(databaseProvider, targetRoot.Path, projectId);
        }
    }

    private const string FilterPayload =
        """
        {
          "scenarios": [
            {
              "name": "可攜關鍵字情境",
              "rationale": "確認案件搬移後仍可續作",
              "groups": [
                {
                  "join": "and",
                  "conditions": [
                    {
                      "join": "and",
                      "type": "text",
                      "field": "description",
                      "keywords": "可攜關鍵字",
                      "mode": "contains"
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static void ConfigureGl(InlineGlWorkbookBuilder builder)
    {
        builder.WithColumns(
            "傳票號碼",
            "傳票項次",
            "傳票日期",
            "核准日期",
            "科目代號",
            "科目名稱",
            "摘要",
            "建立人員",
            "金額",
            "借方旗標");

        for (var index = 1; index <= 31; index++)
        {
            var voucher = $"PORT-{index:000}";
            var date = $"2025-03-{((index - 1) % 28) + 1:00}";
            var amount = index.ToString("0.00", CultureInfo.InvariantCulture);
            builder
                .AddRow(voucher, 1, date, date, "1101", "現金", $"可攜關鍵字 {index}", "tester", amount, 1)
                .AddRow(voucher, 2, date, date, "4101", "銷貨收入", $"一般貸方 {index}", "tester", amount, 0);
        }
    }

    private static void ConfigureTb(InlineTbWorkbookBuilder builder)
        => builder
            .AddRow("1101", "現金", "496.00")
            .AddRow("4101", "銷貨收入", "-496.00");

    private static async Task<IReadOnlyList<string>> WalkInfSampleAsync(HandlerTestHost host)
    {
        var rows = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync(
                "query.infSamplePage",
                JsonSerializer.Serialize(new { cursor, pageSize = 17 }));
            rows.AddRange(page.GetProperty("rows").EnumerateArray().Select(row => row.GetRawText()));
            var next = page.GetProperty("nextCursor");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();
        } while (cursor is not null);

        return rows;
    }

    private static async Task AssertPortableQuerySurfacesAsync(HandlerTestHost host)
    {
        var preview = await host.DispatchAsync(
            "query.dataPreview",
            """{ "dataset": "gl", "limit": 5 }""");
        Assert.Equal("gl", preview.GetProperty("dataset").GetString());
        Assert.True(preview.GetProperty("totalCount").GetInt64() > 0);
        Assert.NotEmpty(preview.GetProperty("rows").EnumerateArray());

        var pageRequests = new (string Action, string Payload)[]
        {
            ("query.completenessDiffPage", """{ "pageSize": 7 }"""),
            ("query.voucherBalancePage", """{ "pageSize": 7 }"""),
            ("query.blankRecordsPage", """{ "category": "blankDescription", "pageSize": 7 }"""),
            ("query.sourceQualityPage", """{ "pageSize": 7 }"""),
            ("query.prescreenPage", """{ "ruleKey": "suspiciousKeywords", "pageSize": 7 }"""),
            ("query.filterHitsPage", """{ "scenarioPosition": 1, "pageSize": 7 }"""),
            ("query.highRiskSummaryVoucherPage", """{ "pageSize": 7 }"""),
            ("query.highRiskSummaryRowPage", """{ "pageSize": 7 }""")
        };

        foreach (var request in pageRequests)
        {
            var page = await host.DispatchAsync(request.Action, request.Payload);
            Assert.Equal(JsonValueKind.Array, page.GetProperty("rows").ValueKind);
            Assert.True(page.TryGetProperty("nextCursor", out _), request.Action);
        }

        var summary = await host.DispatchAsync("query.highRiskSummaryScenarios");
        Assert.Single(summary.GetProperty("scenarios").EnumerateArray());
    }

    private static async Task AssertMovedPersistenceIsPortableAsync(
        string databaseProvider,
        string sourceRoot,
        string targetRoot,
        string projectId)
    {
        var projectDirectory = Path.Combine(targetRoot, projectId);
        foreach (var jsonFileName in new[]
                 {
                     JetProjectFolder.ProjectJsonFileName,
                     ProjectReportStore.ManifestFileName
                 })
        {
            var json = await File.ReadAllTextAsync(Path.Combine(projectDirectory, jsonFileName));
            Assert.DoesNotContain(sourceRoot, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(targetRoot, json, StringComparison.OrdinalIgnoreCase);
        }

        var folder = new JetProjectFolder(targetRoot);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        await using var connection = database.CreateReadOnlyConnection(projectId);
        await connection.OpenAsync(CancellationToken.None);

        await using (var sourceCommand = connection.CreateCommand())
        {
            sourceCommand.CommandText =
                """
                SELECT source_file_path, source_file_name FROM import_batch
                UNION ALL
                SELECT source_file_path, source_file_name FROM import_batch_source
                ORDER BY source_file_name, source_file_path;
                """;
            var sourceReferences = new List<(string Path, string FileName)>();
            await using var reader = await sourceCommand.ExecuteReaderAsync(CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                sourceReferences.Add((reader.GetString(0), reader.GetString(1)));
            }

            Assert.NotEmpty(sourceReferences);
            Assert.All(sourceReferences, reference =>
            {
                Assert.Equal(reference.FileName, reference.Path);
                Assert.False(Path.IsPathRooted(reference.Path));
                Assert.Equal(reference.Path, Path.GetFileName(reference.Path));
            });
        }

        await using var auditCommand = connection.CreateCommand();
        auditCommand.CommandText =
            """
            SELECT operation, target_type, target_id
            FROM operation_log
            ORDER BY occurred_utc, event_id;
            """;
        var auditRows = new List<string>();
        await using var auditReader = await auditCommand.ExecuteReaderAsync(CancellationToken.None);
        while (await auditReader.ReadAsync(CancellationToken.None))
        {
            auditRows.Add(string.Join('|',
                auditReader.GetString(0),
                auditReader.GetString(1),
                auditReader.GetString(2)));
        }

        Assert.NotEmpty(auditRows);
        Assert.All(auditRows, row =>
        {
            Assert.DoesNotContain(sourceRoot, row, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(targetRoot, row, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static long ReadSampleSeed(string projectsRoot, string projectId)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(projectsRoot, projectId, JetProjectFolder.ProjectJsonFileName)));
        return document.RootElement.GetProperty("sampleSeed").GetInt64();
    }

    private static int ReadSampleSeedVersion(string projectsRoot, string projectId)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(projectsRoot, projectId, JetProjectFolder.ProjectJsonFileName)));
        return document.RootElement.GetProperty("sampleSeedVersion").GetInt32();
    }

    private static void AssertManifestUsesRelativeFileNames(
        string projectDirectory,
        string reportId,
        string expectedFileName)
    {
        var manifestPath = Path.Combine(projectDirectory, ProjectReportStore.ManifestFileName);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var report = Assert.Single(
            document.RootElement.EnumerateArray(),
            item => item.GetProperty("reportId").GetString() == reportId);
        var relativeFileName = report.GetProperty("relativeFileName").GetString()!;
        Assert.Equal(expectedFileName, relativeFileName);
        Assert.False(Path.IsPathRooted(relativeFileName));
        Assert.Equal(relativeFileName, Path.GetFileName(relativeFileName));
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(subdirectory, Path.Combine(targetDirectory, Path.GetFileName(subdirectory)));
        }
    }

    private static void ReleaseProviderFileHandles(string databaseProvider, string projectsRoot, string? projectId)
    {
        if (databaseProvider == "sqlite")
        {
            if (projectId is not null)
            {
                SqliteTestPool.Clear(projectsRoot, projectId);
            }
            else
            {
                SqliteTestPool.ClearAllUnder(projectsRoot);
            }

            return;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private sealed class RecordingHostShell : IHostShell
    {
        public List<string> RevealedPaths { get; } = [];

        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken)
        {
            RevealedPaths.Add(Path.GetFullPath(path));
            return Task.CompletedTask;
        }

        public void RequestExit()
        {
        }
    }
}
