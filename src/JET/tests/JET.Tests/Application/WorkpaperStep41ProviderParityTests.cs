using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Step4-1 最終 Legacy 語意的 provider golden：
/// fixture 刻意讓 source entry 順序不同於 Legacy 文件／項次排序，並同時涵蓋
/// voucherDate suffix 排除、priority 12、remaining ordinal、native Time／Boolean／large Number、
/// compact TAG 與 signed amount。
/// 指紋只包含 Step4-1 的 logical cells／欄寬／BestFit，不納入 package timestamp 等揮發值。
/// </summary>
public sealed class WorkpaperStep41ProviderParityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task K5_WorkpaperManualAuto_UsesTextAndPreservesBlank(string provider)
    {
        using var host = CreateHost(provider, null);
        var result = await ExportAsync(host, provider, QueryDataPreviewHandlerTests.K5ManualFixture, manualBlankValueKind: "unclassified");
        var column = Array.IndexOf(result.Snapshot.Headers.ToArray(), "人工傳票否_JE_S");
        Assert.True(column >= 0);
        Assert.Equal(new[] { "人工", "自動", "" }, result.Snapshot.Rows.Select(r => r[column]));
    }

    private const string EvidenceRootVariable = "JET_STEP41_PARITY_EVIDENCE_ROOT";
    private const string Step41SheetName = "step4-1 符合高風險條件傳票明細";
    private const string LegacyFingerprint =
        "3190670025C0A2A6F077C6C16907F3E34CE6CE209D387F8A4A0A09115024EA76";
    private const string NumericCursorFingerprint =
        "1EDA198B596A886F319A9F05CCA0D0333EA310B695AE958617690CCCCB0A2FC6";

    private static readonly string[] ExpectedHeaders =
    [
        "傳票號碼_JE",
        "傳票文件項次_JE_S",
        "傳票核准日_JE",
        "總帳日期_JE",
        "傳票建立人員_JE",
        "傳票核准人員_JE",
        "會計科目編號_JE",
        "會計科目名稱_JE",
        "傳票金額_JE",
        "傳票摘要_JE",
        "分錄來源模組_JE",
        "人工傳票否_JE_S",
        "ROW_TOKEN_JE",
        "過帳碼_JE",
        "兩位小數_JE",
        "核准時間_JE",
        "原生布林_JE",
        "超大數_JE",
        "C1_TAG"
    ];

    private static readonly string[] ExpectedTokens =
    [
        "A-NULL",
        "A-01-FIRST",
        "A-01-SECOND",
        "A-01A",
        "A-01a",
        "B-02",
        "B-03"
    ];

    [Fact]
    public async Task FinalLegacyDetail_SqliteAndDuckDb_HaveIdenticalLogicalFingerprint()
    {
        var evidenceRoot = ResolveEvidenceRoot();
        using var sqliteHost = CreateHost("sqlite", evidenceRoot);
        var sqlite = await ExportAsync(sqliteHost, "sqlite");

        using var duckDbHost = CreateHost("duckdb", evidenceRoot);
        var duckDb = await ExportAsync(duckDbHost, "duckdb");

        output.WriteLine($"SQLite Step4-1 fingerprint: {sqlite.Snapshot.Fingerprint}");
        output.WriteLine($"DuckDB Step4-1 fingerprint: {duckDb.Snapshot.Fingerprint}");
        AssertLegacyOracle(sqlite.Snapshot);
        AssertLegacyOracle(duckDb.Snapshot);
        Assert.Equal(sqlite.Snapshot.Fingerprint, duckDb.Snapshot.Fingerprint);

        WriteEvidence(
            evidenceRoot,
            "provider-parity-local.json",
            new
            {
                oracle = "step4-1-legacy-v2-display-width",
                sqlite = sqlite.ToEvidence(),
                duckdb = duckDb.ToEvidence(),
                identicalFingerprint = true
            });
    }

    [Fact]
    public async Task FinalLegacyDetail_NumericLineCursorCrossesDefaultPageBoundary_OnSqliteAndDuckDb()
    {
        var evidenceRoot = ResolveEvidenceRoot();
        using var sqliteHost = CreateHost("sqlite-numeric-cursor", evidenceRoot);
        var sqlite = await ExportAsync(
            sqliteHost,
            "sqlite",
            ConfigureNumericCursorFixture,
            verifyNumericPaging: true);

        using var duckDbHost = CreateHost("duckdb-numeric-cursor", evidenceRoot);
        var duckDb = await ExportAsync(
            duckDbHost,
            "duckdb",
            ConfigureNumericCursorFixture,
            verifyNumericPaging: true);

        output.WriteLine($"SQLite numeric Step4-1 fingerprint: {sqlite.Snapshot.Fingerprint}");
        output.WriteLine($"DuckDB numeric Step4-1 fingerprint: {duckDb.Snapshot.Fingerprint}");
        AssertNumericCursorOracle(sqlite.Snapshot);
        AssertNumericCursorOracle(duckDb.Snapshot);
        Assert.Equal(sqlite.Snapshot.Fingerprint, duckDb.Snapshot.Fingerprint);

        WriteEvidence(
            evidenceRoot,
            "provider-numeric-cursor-local.json",
            new
            {
                oracle = "step4-1-numeric-line-cursor-v3-display-width",
                sqlite = sqlite.ToEvidence(),
                duckdb = duckDb.ToEvidence(),
                identicalFingerprint = true
            });
    }

    [SqlServerFact]
    public async Task FinalLegacyDetail_SqliteAndSqlServer_HaveIdenticalLogicalFingerprint()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync()
            ?? throw new InvalidOperationException(
                "SqlServerFact 已判定可用，但執行期無法取得 SQL Server 連線。");
        var evidenceRoot = ResolveEvidenceRoot();

        using var sqliteHost = CreateHost("sqlite-sql-oracle", evidenceRoot);
        var sqlite = await ExportAsync(sqliteHost, "sqlite");

        HandlerTestHost? sqlServerHost = null;
        ProviderResult? sqlServer = null;
        try
        {
            sqlServerHost = CreateHost(
                "sqlserver",
                evidenceRoot,
                connectionString);
            sqlServer = await ExportAsync(sqlServerHost, "sqlServer");

            AssertLegacyOracle(sqlite.Snapshot);
            AssertLegacyOracle(sqlServer.Snapshot);
            Assert.Equal(sqlite.Snapshot.Fingerprint, sqlServer.Snapshot.Fingerprint);

            WriteEvidence(
                evidenceRoot,
                "provider-parity-sqlserver.json",
                new
                {
                    oracle = "step4-1-legacy-v2-display-width",
                    sqlite = sqlite.ToEvidence(),
                    sqlserver = sqlServer.ToEvidence(),
                    identicalFingerprint = true
                });
        }
        finally
        {
            sqlServerHost?.Dispose();
            if (sqlServer is not null)
            {
                await TempSqlServerProject.DropDatabaseAsync(
                    connectionString,
                    sqlServer.ProjectId);
            }
        }
    }

    [SqlServerFact]
    public async Task FinalLegacyDetail_NumericLineCursorCrossesDefaultPageBoundary_OnSqliteAndSqlServer()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync()
            ?? throw new InvalidOperationException(
                "SqlServerFact 已判定可用，但執行期無法取得 SQL Server 連線。");
        var evidenceRoot = ResolveEvidenceRoot();

        using var sqliteHost = CreateHost("sqlite-sql-numeric-cursor", evidenceRoot);
        var sqlite = await ExportAsync(
            sqliteHost,
            "sqlite",
            ConfigureNumericCursorFixture);

        HandlerTestHost? sqlServerHost = null;
        ProviderResult? sqlServer = null;
        try
        {
            sqlServerHost = CreateHost(
                "sqlserver-numeric-cursor",
                evidenceRoot,
                connectionString);
            sqlServer = await ExportAsync(
                sqlServerHost,
                "sqlServer",
                ConfigureNumericCursorFixture);

            AssertNumericCursorOracle(sqlite.Snapshot);
            AssertNumericCursorOracle(sqlServer.Snapshot);
            Assert.Equal(sqlite.Snapshot.Fingerprint, sqlServer.Snapshot.Fingerprint);

            WriteEvidence(
                evidenceRoot,
                "provider-numeric-cursor-sqlserver.json",
                new
                {
                    oracle = "step4-1-numeric-line-cursor-v3-display-width",
                    sqlite = sqlite.ToEvidence(),
                    sqlserver = sqlServer.ToEvidence(),
                    identicalFingerprint = true
                });
        }
        finally
        {
            sqlServerHost?.Dispose();
            if (sqlServer is not null)
            {
                await TempSqlServerProject.DropDatabaseAsync(
                    connectionString,
                    sqlServer.ProjectId);
            }
        }
    }

    private static HandlerTestHost CreateHost(
        string evidenceDirectoryName,
        string? evidenceRoot,
        string? sqlServerConnectionString = null)
    {
        string? projectsRoot = null;
        if (evidenceRoot is not null)
        {
            projectsRoot = Path.Combine(evidenceRoot, evidenceDirectoryName);
            Directory.CreateDirectory(projectsRoot);
        }

        return new HandlerTestHost(
            sqlServerConnectionString: sqlServerConnectionString,
            projectsRootPath: projectsRoot);
    }

    private static async Task<ProviderResult> ExportAsync(
        HandlerTestHost host,
        string databaseProvider,
        Action<InlineGlWorkbookBuilder>? configure = null,
        bool verifyNumericPaging = false, string manualBlankValueKind = "reject")
    {
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            configure ?? ConfigureFixture,
            databaseProvider: databaseProvider,
            validateForDownstream: true, manualBlankValueKind: manualBlankValueKind);
        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = new object[]
                {
                    new
                    {
                        name = "全部命中",
                        rationale = "固定關鍵字命中所有 fixture rows",
                        groups = new[]
                        {
                            new
                            {
                                join = "AND",
                                rules = new[]
                                {
                                    new
                                    {
                                        join = "AND",
                                        type = "customKeywords",
                                        keywords = "命中"
                                    }
                                }
                            }
                        }
                    },
                    new
                    {
                        name = "零列命中",
                        rationale = "證明零 row-hit 情境不產生 TAG 欄",
                        groups = new[]
                        {
                            new
                            {
                                join = "AND",
                                rules = new[]
                                {
                                    new
                                    {
                                        join = "AND",
                                        type = "customKeywords",
                                        keywords = "絕不出現"
                                    }
                                }
                            }
                        }
                    }
                }
            }));
        var validationRunId = validation
            .GetProperty("resultRef")
            .GetProperty("runId")
            .GetString();
        var prescreenRunId = prescreen
            .GetProperty("resultRef")
            .GetProperty("runId")
            .GetString();
        var scenarioRevision = committed
            .GetProperty("resultRef")
            .GetProperty("revision")
            .GetString();
        if (verifyNumericPaging)
        {
            await AssertNumericRepositoryPagingAsync(
                host,
                projectId,
                databaseProvider);
        }
        _ = await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationRunId,
                prescreenRunId,
                revision = scenarioRevision
            }));
        var exported = await host.DispatchAsync(
            "export.workpaperStream",
            JsonSerializer.Serialize(new
            {
                validationRunId,
                prescreenRunId,
                scenarioRevision,
                scenarioPositions = new[] { 1, 2 }
            }));
        var workbookPath = Path.Combine(
            host.ProjectsRoot,
            projectId,
            exported.GetProperty("artifact").GetProperty("fileName").GetString()!);
        var snapshot = Step41Snapshot.Capture(workbookPath);
        return new ProviderResult(
            databaseProvider,
            projectId,
            workbookPath,
            snapshot);
    }

    private static async Task AssertNumericRepositoryPagingAsync(
        HandlerTestHost host,
        string projectId,
        string databaseProvider)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        ILocalProjectDatabase database = databaseProvider switch
        {
            ProjectDocument.DefaultDatabaseProvider => new SqliteProjectDatabase(folder),
            ProjectDocument.DuckDbDatabaseProvider => new DuckDbProjectDatabase(folder),
            _ => throw new ArgumentOutOfRangeException(
                nameof(databaseProvider),
                databaseProvider,
                "Numeric local paging oracle only supports SQLite and DuckDB.")
        };
        var repository = (IWorkpaperStep41PageRepository)
            new LocalTagMatrixRowPageRepository(database);
        var population = new GlPopulationContext(
            GlPopulationScope.AuditPeriod,
            "2025-01-01",
            "2025-12-31");
        var first = await repository.GetPageAsync(
            projectId,
            population,
            new PageRequest(null, PageRequest.DefaultPageSize),
            [1, 2],
            LegacyFieldKind.Number,
            CancellationToken.None);
        var second = await repository.GetPageAsync(
            projectId,
            population,
            new PageRequest(first.NextCursor, PageRequest.DefaultPageSize),
            [1, 2],
            LegacyFieldKind.Number,
            CancellationToken.None);
        if (first.Rows.Count != 200 || second.Rows.Count != 5)
        {
            _ = PageCursor.TryDecode(first.NextCursor, out var decoded);
            var last = first.Rows.LastOrDefault();
            throw new InvalidDataException(
                $"Numeric cursor paging failed on {databaseProvider}: "
                + $"page1={first.Rows.Count}, page2={second.Rows.Count}, "
                + $"last=({last?.DocumentNumber},{last?.LineItem},{last?.EntryId}), "
                + $"cursor={decoded}.");
        }
    }

    private static void ConfigureFixture(InlineGlWorkbookBuilder builder) =>
        builder
            .WithColumns(
                "ROW_TOKEN_JE",
                "傳票日期_僅條件_JE",
                "過帳碼_JE",
                "兩位小數_JE",
                "核准時間_JE",
                "原生布林_JE",
                "超大數_JE",
                "傳票號碼",
                "傳票項次",
                "傳票日期",
                "核准日期",
                "建立人員",
                "核准人員",
                "科目代號",
                "科目名稱",
                "摘要",
                "來源模組",
                "人工傳票",
                "金額",
                "借方旗標")
            // source entry 順序刻意不同於最終 doc／line／entry_id 排序。
            .AddRow(
                "B-02", new DateTime(2025, 2, 1), 40, 40.12m, new TimeSpan(6, 0, 0), true, 1E+100, "DOC-B", "02",
                new DateTime(2025, 2, 1), new DateTime(2025, 2, 2), "建立乙", "核准乙",
                "1101", "現金", "命中 B-02", "GL", 1, "100.0000", 1)
            .AddRow(
                "A-01a", new DateTime(2025, 1, 1), 30, 30.34m, new TimeSpan(12, 0, 0), false, 1E+100, "DOC-A", "01a",
                new DateTime(2025, 1, 1), new DateTime(2025, 1, 2), "建立甲", "核准甲",
                "5101", "費用", "命中 A-01a", "MANUAL", 0, "30.0000", 1)
            .AddRow(
                "A-01A", new DateTime(2025, 1, 1), 20, null, null, true, 1E+100, "DOC-A", "01A",
                new DateTime(2025, 1, 1), new DateTime(2025, 1, 2), "建立甲", "核准甲",
                "4101", "收入", "命中 A-01A", "MANUAL", 1, "30.0000", 0)
            .AddRow(
                "A-01-FIRST", new DateTime(2025, 1, 1), 10, 10.56m, new TimeSpan(18, 0, 0), false, 1E+100, "DOC-A", "01",
                new DateTime(2025, 1, 1), new DateTime(2025, 1, 2), "建立甲", "核准甲",
                "1101", "現金", "命中 A-01 first", "GL", 1, "20.0000", 1)
            .AddRow(
                "A-01-SECOND", new DateTime(2025, 1, 1), 11, 11.78m, new TimeSpan(6, 0, 0), true, 1E+100, "DOC-A", "01",
                new DateTime(2025, 1, 1), new DateTime(2025, 1, 2), "建立甲", "核准甲",
                "4101", "收入", "命中 A-01 second", "GL", 0, "20.0000", 0)
            .AddRow(
                "A-NULL", new DateTime(2025, 1, 1), 0, 0.25m, new TimeSpan(12, 0, 0), false, 1E+100, "DOC-A", null,
                new DateTime(2025, 1, 1), new DateTime(2025, 1, 2), "建立甲", "核准甲",
                "1101", "現金", "命中 A-null", "GL", 1, "0.0000", 1)
            .AddRow(
                "B-03", new DateTime(2025, 2, 1), 50, 50.90m, new TimeSpan(18, 0, 0), true, 1E+100, "DOC-B", "03",
                new DateTime(2025, 2, 1), new DateTime(2025, 2, 2), "建立乙", "核准乙",
                "4101", "收入", "命中 B-03", "GL", 0, "100.0000", 0);

    private static void ConfigureNumericCursorFixture(InlineGlWorkbookBuilder builder)
    {
        builder.WithColumns(
            "ROW_TOKEN_JE",
            "傳票日期_僅條件_JE",
            "過帳碼_JE",
            "兩位小數_JE",
            "核准時間_JE",
            "原生布林_JE",
            "超大數_JE",
            "傳票號碼",
            "傳票項次",
            "傳票日期",
            "核准日期",
            "建立人員",
            "核准人員",
            "科目代號",
            "科目名稱",
            "摘要",
            "來源模組",
            "人工傳票",
            "金額",
            "借方旗標");

        for (var sourceOrdinal = 1; sourceOrdinal <= 205; sourceOrdinal++)
        {
            // 199 個零把 scale=20 的精確相鄰值推到預設 200-row page boundary。
            // 1E-20／2E-20 在 XLSX/reader 的 double→decimal normalization 後仍是
            // 彼此不同且精確的 20 位小數，不依賴 Excel 無法保存的 28 位近 1 小數。
            // 較大的值刻意先匯入，避免 DECIMAL(38,18)+entry_id 假排序誤打誤撞通過；
            // 其後的 1E+100..4E+100 則鎖住超出 System.Decimal/DECIMAL(38,18)
            // 仍屬合法 finite XLSX Number 的 cursor/order 行為。
            object line = sourceOrdinal switch
            {
                1 => 2E-20,
                >= 2 and <= 200 => 0m,
                201 => 1E-20,
                202 => 1E+100,
                203 => 2E+100,
                204 => 3E+100,
                205 => 4E+100,
                _ => throw new InvalidOperationException("Unexpected numeric cursor fixture ordinal.")
            };
            builder.AddRow(
                $"N-{sourceOrdinal:D3}",
                new DateTime(2025, 3, 1),
                sourceOrdinal,
                sourceOrdinal % 100 / 100m,
                new TimeSpan(6, 0, 0),
                true,
                1E+100,
                "DOC-N",
                line,
                new DateTime(2025, 3, 1),
                new DateTime(2025, 3, 2),
                "建立者",
                "核准者",
                "1101",
                "現金",
                $"命中 numeric cursor {sourceOrdinal:D3}",
                "GL",
                sourceOrdinal % 2,
                sourceOrdinal == 205 ? 0 : 1,
                1);
        }
    }

    private static void AssertLegacyOracle(Step41Snapshot snapshot)
    {
        Assert.Equal(LegacyFingerprint, snapshot.Fingerprint);
        Assert.Equal(ExpectedHeaders, snapshot.Headers);
        Assert.Equal(ExpectedTokens, snapshot.Rows.Select(row => row[12]).ToArray());
        Assert.All(snapshot.Rows, row => Assert.Equal("Y", row[18]));
        Assert.DoesNotContain(
            "傳票日期_僅條件_JE",
            snapshot.Headers,
            StringComparer.Ordinal);
        Assert.DoesNotContain("C2_TAG", snapshot.Headers, StringComparer.Ordinal);
        Assert.Equal(7, snapshot.Rows.Count);
        Assert.Equal("-30", snapshot.Rows[3][8]);
        Assert.Equal("30", snapshot.Rows[4][8]);
        Assert.All(snapshot.CellKinds, row => Assert.Equal("N|yyyy-mm-dd", row[2]));
        Assert.All(snapshot.CellKinds, row => Assert.Equal("N|yyyy-mm-dd", row[3]));
        Assert.All(snapshot.CellKinds, row => Assert.Equal("N|#,##0.0000", row[8]));
        Assert.All(snapshot.CellKinds, row => Assert.Equal("T|builtin:49", row[11]));
        Assert.All(snapshot.CellKinds, row => Assert.Equal("N|0", row[13]));
        Assert.Equal(
            ["N|0.00", "N|0.00", "N|0.00", "B|0.00", "N|0.00", "N|0.00", "N|0.00"],
            snapshot.CellKinds.Select(row => row[14]).ToArray());
        Assert.Equal(
            ["N|hh:mm:ss", "N|hh:mm:ss", "N|hh:mm:ss", "B|hh:mm:ss", "N|hh:mm:ss", "N|hh:mm:ss", "N|hh:mm:ss"],
            snapshot.CellKinds.Select(row => row[15]).ToArray());
        Assert.Equal(
            ["人工", "人工", "自動", "人工", "自動", "人工", "自動"],
            snapshot.Rows.Select(row => row[11]).ToArray());
        Assert.Equal(
            ["0", "10", "11", "20", "30", "40", "50"],
            snapshot.Rows.Select(row => row[13]).ToArray());
        Assert.Equal(
            ["0.25", "10.56", "11.78", "", "30.34", "40.12", "50.9"],
            snapshot.Rows.Select(row => row[14]).ToArray());
        Assert.Equal(
            ["0.5", "0.75", "0.25", "", "0.5", "0.25", "0.75"],
            snapshot.Rows.Select(row => row[15]).ToArray());
        Assert.All(snapshot.CellKinds, row => Assert.Equal("N|0", row[16]));
        Assert.Equal(
            ["0", "0", "1", "1", "0", "1", "1"],
            snapshot.Rows.Select(row => row[16]).ToArray());
        Assert.All(snapshot.CellKinds, row => Assert.Equal("N|0", row[17]));
        Assert.All(snapshot.Rows, row => Assert.Equal("1E+100", row[17]));
        Assert.All(snapshot.BestFit, Assert.True);
    }

    private static void AssertNumericCursorOracle(Step41Snapshot snapshot)
    {
        Assert.Equal(NumericCursorFingerprint, snapshot.Fingerprint);
        var expectedTokens = Enumerable
            .Range(2, 199)
            .Concat([201, 1, 202, 203, 204, 205])
            .Select(ordinal => $"N-{ordinal:D3}")
            .ToArray();
        var expectedLineItems = Enumerable
            .Repeat("0", 199)
            .Concat(
            [
                "1E-20",
                "2E-20",
                "1E+100",
                "2E+100",
                "3E+100",
                "4E+100"
            ])
            .ToArray();

        Assert.Equal(ExpectedHeaders, snapshot.Headers);
        Assert.Equal(205, snapshot.Rows.Count);
        Assert.Equal(expectedTokens, snapshot.Rows.Select(row => row[12]).ToArray());
        Assert.Equal(expectedLineItems, snapshot.Rows.Select(row => row[1]).ToArray());
        Assert.All(
            snapshot.CellKinds,
            row => Assert.Equal(
                "N|0.00000000000000000000",
                row[1]));
        Assert.All(snapshot.CellKinds, row => Assert.Equal("N|0", row[13]));
        Assert.All(snapshot.CellKinds, row => Assert.Equal("N|0.00", row[14]));
        Assert.All(snapshot.CellKinds, row => Assert.Equal("N|hh:mm:ss", row[15]));
        Assert.All(snapshot.Rows, row => Assert.Equal("0.25", row[15]));
        Assert.All(snapshot.CellKinds, row => Assert.Equal("N|0", row[16]));
        Assert.All(snapshot.Rows, row => Assert.Equal("1", row[16]));
        Assert.All(snapshot.CellKinds, row => Assert.Equal("N|0", row[17]));
        Assert.All(snapshot.Rows, row => Assert.Equal("1E+100", row[17]));
        Assert.All(snapshot.BestFit, Assert.True);
    }

    private static string? ResolveEvidenceRoot()
    {
        var configured = Environment.GetEnvironmentVariable(EvidenceRootVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(configured);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private void WriteEvidence(string? root, string fileName, object evidence)
    {
        output.WriteLine(
            JsonSerializer.Serialize(
                evidence,
                new JsonSerializerOptions { WriteIndented = true }));
        if (root is null)
        {
            return;
        }

        var path = Path.Combine(root, fileName);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                evidence,
                new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
        output.WriteLine($"Step4-1 provider evidence: {path}");
    }

    private sealed record ProviderResult(
        string Provider,
        string ProjectId,
        string WorkbookPath,
        Step41Snapshot Snapshot)
    {
        internal object ToEvidence() => new
        {
            provider = Provider,
            projectId = ProjectId,
            workbookPath = WorkbookPath,
            logicalFingerprint = Snapshot.Fingerprint,
            headers = Snapshot.Headers,
            rowTokens = Snapshot.Rows.Select(row => row[12]).ToArray(),
            cellKinds = Snapshot.CellKinds,
            widths = Snapshot.Widths,
            bestFit = Snapshot.BestFit
        };
    }

    private sealed record Step41Snapshot(
        string Fingerprint,
        IReadOnlyList<string> Headers,
        IReadOnlyList<IReadOnlyList<string>> Rows,
        IReadOnlyList<IReadOnlyList<string>> CellKinds,
        IReadOnlyList<double> Widths,
        IReadOnlyList<bool> BestFit)
    {
        internal static Step41Snapshot Capture(string workbookPath)
        {
            using var document = SpreadsheetDocument.Open(workbookPath, false);
            var workbookPart = document.WorkbookPart
                ?? throw new InvalidDataException("WorkingPaper 缺少 workbook part。");
            var sheet = workbookPart.Workbook
                .Descendants<Sheet>()
                .Single(item => string.Equals(
                    item.Name?.Value,
                    Step41SheetName,
                    StringComparison.Ordinal));
            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(
                sheet.Id?.Value
                ?? throw new InvalidDataException("Step4-1 缺少 relationship id。"));
            var worksheet = worksheetPart.Worksheet;
            var sharedStrings = workbookPart.SharedStringTablePart?
                .SharedStringTable
                .Elements<SharedStringItem>()
                .Select(item => string.Concat(
                    item.Descendants<Text>().Select(text => text.Text)))
                .ToArray()
                ?? [];
            var styleFormats = ReadStyleFormats(workbookPart);

            var headerRow = worksheet
                .Descendants<Row>()
                .Single(row => row.RowIndex?.Value == 5);
            var headerByColumn = ReadRow(headerRow, sharedStrings, styleFormats)
                .ToDictionary(item => item.Column, item => item.Value);
            var lastHeaderColumn = headerByColumn
                .Where(item => !string.IsNullOrEmpty(item.Value))
                .Max(item => item.Key);
            var headers = Enumerable
                .Range(1, lastHeaderColumn)
                .Select(column => headerByColumn.GetValueOrDefault(column, string.Empty))
                .ToArray();
            var columnCount = headers.Length;
            var logicalRows = worksheet
                .Descendants<Row>()
                .Where(row => row.RowIndex?.Value >= 6)
                .OrderBy(row => row.RowIndex!.Value)
                .Select(row =>
                {
                    var byColumn = ReadRow(row, sharedStrings, styleFormats)
                        .ToDictionary(item => item.Column);
                    return Enumerable
                        .Range(1, columnCount)
                        .Select(column => byColumn.GetValueOrDefault(
                            column,
                            new LogicalCell(column, string.Empty, "B|General")))
                        .ToArray();
                })
                .Where(row => row.Any(cell => !string.IsNullOrEmpty(cell.Value)))
                .ToArray();
            var rows = logicalRows
                .Select(row => (IReadOnlyList<string>)row.Select(cell => cell.Value).ToArray())
                .ToArray();
            var cellKinds = logicalRows
                .Select(row => (IReadOnlyList<string>)row.Select(cell => cell.Kind).ToArray())
                .ToArray();
            var columns = worksheet
                .Elements<Columns>()
                .SelectMany(item => item.Elements<Column>())
                .OrderBy(column => column.Min?.Value)
                .ToArray();
            var widths = columns
                .Select(column => column.Width?.Value ?? 0d)
                .ToArray();
            var bestFit = columns
                .Select(column => column.BestFit?.Value ?? false)
                .ToArray();

            var canonical = new StringBuilder();
            canonical.Append("HEADERS\n");
            foreach (var header in headers)
            {
                canonical.Append(Escape(header)).Append('\n');
            }
            canonical.Append("ROWS\n");
            for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                canonical.AppendJoin(
                    '\u001f',
                    rows[rowIndex].Select((value, columnIndex) =>
                        $"{Escape(cellKinds[rowIndex][columnIndex])}\u001e{Escape(value)}"))
                    .Append('\n');
            }
            canonical.Append("WIDTHS\n");
            for (var index = 0; index < widths.Length; index++)
            {
                canonical
                    .Append(index + 1)
                    .Append(':')
                    .Append(widths[index].ToString("R", CultureInfo.InvariantCulture))
                    .Append(':')
                    .Append(bestFit[index] ? '1' : '0')
                    .Append('\n');
            }

            return new Step41Snapshot(
                Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))),
                headers,
                rows,
                cellKinds,
                widths,
                bestFit);
        }

        private static IReadOnlyList<LogicalCell> ReadRow(
            Row row,
            IReadOnlyList<string> sharedStrings,
            IReadOnlyList<string> styleFormats) =>
            row.Elements<Cell>()
                .Select(cell => new LogicalCell(
                    ColumnIndex(cell.CellReference?.Value),
                    CellText(cell, sharedStrings),
                    CellKind(cell, styleFormats)))
                .ToArray();

        private static string CellText(
            Cell cell,
            IReadOnlyList<string> sharedStrings)
        {
            if (cell.DataType?.Value == CellValues.SharedString
                && int.TryParse(
                    cell.CellValue?.Text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var sharedStringIndex))
            {
                return sharedStrings[sharedStringIndex];
            }

            if (cell.DataType?.Value == CellValues.InlineString)
            {
                return string.Concat(
                    cell.InlineString?.Descendants<Text>()
                        .Select(text => text.Text)
                    ?? []);
            }

            var text = cell.CellValue?.Text ?? string.Empty;
            if ((cell.DataType is null || cell.DataType?.Value == CellValues.Number)
                && decimal.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var number))
            {
                return number.ToString("G29", CultureInfo.InvariantCulture);
            }

            return text;
        }

        private static string CellKind(
            Cell cell,
            IReadOnlyList<string> styleFormats)
        {
            var format = styleFormats[(int)(cell.StyleIndex?.Value ?? 0)];
            var dataType = cell.DataType?.Value;
            return dataType == CellValues.InlineString
                || dataType == CellValues.SharedString
                || dataType == CellValues.String
                ? $"T|{format}"
                : string.IsNullOrEmpty(cell.CellValue?.Text)
                    ? $"B|{format}"
                    : $"N|{format}";
        }

        private static IReadOnlyList<string> ReadStyleFormats(
            WorkbookPart workbookPart)
        {
            var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet
                ?? throw new InvalidDataException("WorkingPaper 缺少 stylesheet。");
            var custom = stylesheet.NumberingFormats?
                .Elements<NumberingFormat>()
                .ToDictionary(
                    format => format.NumberFormatId?.Value ?? 0,
                    format => format.FormatCode?.Value ?? string.Empty)
                ?? new Dictionary<uint, string>();
            return (stylesheet.CellFormats?.Elements<CellFormat>()
                    ?? throw new InvalidDataException("WorkingPaper 缺少 cell formats。"))
                .Select(format =>
                {
                    var id = format.NumberFormatId?.Value ?? 0;
                    return id == 0
                        ? "General"
                        : custom.GetValueOrDefault(id, $"builtin:{id}");
                })
                .ToArray();
        }

        private static int ColumnIndex(string? reference)
        {
            if (string.IsNullOrEmpty(reference))
            {
                throw new InvalidDataException("Step4-1 cell 缺少 reference。");
            }

            var column = 0;
            foreach (var character in reference)
            {
                if (character is < 'A' or > 'Z')
                {
                    break;
                }
                column = checked(column * 26 + character - 'A' + 1);
            }
            return column;
        }

        private static string Escape(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\u001f", "\\u001f", StringComparison.Ordinal);

        private sealed record LogicalCell(int Column, string Value, string Kind);
    }
}
