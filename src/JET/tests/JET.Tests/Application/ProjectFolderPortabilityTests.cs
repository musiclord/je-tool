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
/// 不測、不承諾 SQLite WAL 或 DuckDB WAL 仍開啟時的熱複製。
/// </summary>
public sealed class ProjectFolderPortabilityTests
{
    private const string PersistedMessage = "可攜性驗收訊息";

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ClosedProjectFolderCopiedToNewRoot_ResumesCompleteJourney(string databaseProvider)
    {
        using var sourceRoot = new TempProjectRoot();
        using var targetRoot = new TempProjectRoot();

        string projectId;
        string artifactId;
        string artifactFileName;
        IReadOnlyList<string> sourceInfSample;
        long sourceSampleSeed;
        int sourceSampleSeedVersion;

        var source = new HandlerTestHost(projectsRootPath: sourceRoot.Path);
        try
        {
            projectId = await InlineWorkbookProject.SetupAsync(
                source,
                ConfigureGl,
                lastPeriodStart: "2025-12-31",
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
                    validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString(),
                    prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString(),
                    revision = filter.GetProperty("resultRef").GetProperty("revision").GetString()
                }));
            artifactId = report.GetProperty("artifact").GetProperty("artifactId").GetString()!;
            artifactFileName = report.GetProperty("artifact").GetProperty("fileName").GetString()!;

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
            var list = await target.DispatchAsync("project.list");
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
            Assert.Equal(JsonValueKind.Object, loaded.GetProperty("latestRuns").GetProperty("validate").ValueKind);
            Assert.Equal(JsonValueKind.Object, loaded.GetProperty("latestRuns").GetProperty("prescreen").ValueKind);
            Assert.Single(loaded.GetProperty("filterScenarios").EnumerateArray());
            Assert.Equal(JsonValueKind.Object, loaded.GetProperty("filterResultRef").ValueKind);
            Assert.Contains(
                loaded.GetProperty("reportArtifacts").EnumerateArray(),
                artifact => artifact.GetProperty("artifactId").GetString() == artifactId);
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

            var cleanupPreview = await target.DispatchAsync("report.cleanupPreview");
            Assert.True(cleanupPreview.GetProperty("candidateCount").GetInt32() >= 1);
            var movedArtifactCandidate = Assert.Single(
                cleanupPreview.GetProperty("candidates").EnumerateArray(),
                candidate => candidate.GetProperty("artifactId").GetString() == artifactId);
            Assert.Equal(artifactFileName, movedArtifactCandidate.GetProperty("fileName").GetString());
            Assert.DoesNotContain(sourceRoot.Path, cleanupPreview.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(targetRoot.Path, cleanupPreview.GetRawText(), StringComparison.OrdinalIgnoreCase);

            AssertManifestUsesRelativeFileNames(targetProjectDirectory, artifactId, artifactFileName);
            await target.DispatchAsync(
                "host.openFolder",
                JsonSerializer.Serialize(new { artifactId }));
            await target.DispatchAsync("host.openFolder", """{ "target": "projectFolder" }""");
            Assert.Equal(
                Path.GetFullPath(Path.Combine(targetProjectDirectory, artifactFileName)),
                shell.RevealedPaths[0]);
            Assert.Equal(Path.GetFullPath(targetProjectDirectory), shell.RevealedPaths[1]);

            // 變更非工作日會清除既有 prescreen/filter 命中，但保留情境定義；首頁查詢應在新根惰性補算。
            await target.DispatchAsync("calendar.setNonWorkingDays", """{ "days": [5, 6] }""");
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
                  "rules": [
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
            """{ "dataset": "glEntries", "limit": 5 }""");
        Assert.Equal("glEntries", preview.GetProperty("dataset").GetString());
        Assert.True(preview.GetProperty("totalCount").GetInt64() > 0);
        Assert.NotEmpty(preview.GetProperty("rows").EnumerateArray());

        var pageRequests = new (string Action, string Payload)[]
        {
            ("query.completenessDiffPage", """{ "pageSize": 7 }"""),
            ("query.docBalancePage", """{ "pageSize": 7 }"""),
            ("query.nullRecordsPage", """{ "category": "nullDescription", "pageSize": 7 }"""),
            ("query.sourceQualityPage", """{ "pageSize": 7 }"""),
            ("query.prescreenPage", """{ "ruleKey": "suspiciousKeywords", "pageSize": 7 }"""),
            ("query.filterHitsPage", """{ "scenarioPosition": 1, "pageSize": 7 }"""),
            ("query.tagMatrixVoucherPage", """{ "pageSize": 7 }"""),
            ("query.tagMatrixRowPage", """{ "pageSize": 7 }""")
        };

        foreach (var request in pageRequests)
        {
            var page = await host.DispatchAsync(request.Action, request.Payload);
            Assert.Equal(JsonValueKind.Array, page.GetProperty("rows").ValueKind);
            Assert.True(page.TryGetProperty("nextCursor", out _), request.Action);
        }

        var matrix = await host.DispatchAsync("query.tagMatrixScenarios");
        Assert.Single(matrix.GetProperty("scenarios").EnumerateArray());
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
                     ProjectReportArtifactStore.ManifestFileName
                 })
        {
            var json = await File.ReadAllTextAsync(Path.Combine(projectDirectory, jsonFileName));
            Assert.DoesNotContain(sourceRoot, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(targetRoot, json, StringComparison.OrdinalIgnoreCase);
        }

        var folder = new JetProjectFolder(targetRoot);
        ILocalProjectDatabase database = databaseProvider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
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
            FROM audit_event_log
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
        string artifactId,
        string expectedFileName)
    {
        var manifestPath = Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var artifact = Assert.Single(
            document.RootElement.EnumerateArray(),
            item => item.GetProperty("artifactId").GetString() == artifactId);
        var relativeFileName = artifact.GetProperty("relativeFileName").GetString()!;
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

        public Task<string?> PickSavePathAsync(string baseFileName, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

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
