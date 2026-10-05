using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.TestInfrastructure;
using Xunit;
using Xunit.v3;

namespace JET.Tests.Infrastructure;

internal static class Step41BaselineAvailability
{
    internal const string RootVariable = "JET_STEP41_BASELINE_ROOT";

    internal static string? RootPath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(RootVariable);
            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }

            try
            {
                var fullPath = Path.GetFullPath(configured);
                return Directory.Exists(fullPath) ? fullPath : null;
            }
            catch (Exception exception) when (exception is ArgumentException
                                               or NotSupportedException
                                               or PathTooLongException)
            {
                return null;
            }
        }
    }

    internal const string SkipReason =
        "未設定 JET_STEP41_BASELINE_ROOT，或該絕對目錄不存在；未執行固定 1,048,571 列 Step4-1 Release baseline。";

    public static bool IsAvailable => RootPath is not null;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class Step41BaselineFactAttribute : FactAttribute, ITraitAttribute
{
    public Step41BaselineFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = Step41BaselineAvailability.SkipReason;
        SkipType = typeof(Step41BaselineAvailability);
        SkipUnless = nameof(Step41BaselineAvailability.IsAvailable);
    }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        TestProfileTraits.Scale;
}

/// <summary>
/// Step4-1「正確但慢」baseline。只在明示 evidence root 時執行；fixture 以固定集合式 SQL
/// 在計時外建立，三次 Release export 則完整保留目前 page=200、每頁 schema ensure／新連線、
/// 兩個 reader commands、完整資料欄寬預掃與第二 workbook merge。
/// </summary>
public sealed class WorkpaperStep41LegacyBaselineBenchmarkTests(ITestOutputHelper output)
{
    private const long RowCount = 1_048_571;
    private const long FixedSeed = 20_260_727;
    private const int MoneyScale = 10_000;
    private const int FirstDataRow = 6;
    private const int LastExcelRow = 1_048_576;
    private const int ExpectedColumnCount = 16;
    private const int RowTokenColumn = 13;
    private const int LateWideColumn = 14;
    private const int C1TagColumn = 15;
    private const int C10TagColumn = 16;
    private const string ProjectId = "step41-baseline-v1";
    private const string BatchId = "step41-baseline-batch-v1";
    private const string Step41Sheet = "step4-1 符合高風險條件傳票明細";
    private const string FrozenFullPageFingerprint =
        "D59613C88E6863AF0DDD65363D3D7C9B49C9A697AFCB05422F131D09B8FF1018";

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
        "LATE_WIDE_JE",
        "C1_TAG",
        "C10_TAG"
    ];

    private static readonly string[] ExpectedDataFormats =
    [
        "@",
        "0",
        "yyyy-mm-dd",
        "yyyy-mm-dd",
        "@",
        "@",
        "@",
        "@",
        "#,##0.0000",
        "@",
        "@",
        "0",
        "@",
        "@",
        "@",
        "@"
    ];

    [Fact]
    public async Task BaselineHarness_SmallDuckDbFixture_ExercisesSeedPagingAndStreamingInspector()
    {
        const long smokeRows = 11;
        using var root = new TempProjectRoot();
        var database = new DuckDbProjectDatabase(new JetProjectFolder(root.Path));
        await database.EnsureCreatedAsync(ProjectId, CancellationToken.None);
        await SeedFixtureAsync(database, smokeRows, CancellationToken.None);
        var countedRows = new CountingStep41PreparedRepository(
            new LocalTagMatrixRowPageRepository(database));
        var writer = (IWorkpaperPlanWriter)BuildWriter(database, countedRows);
        var workbookPath = Path.Combine(root.Path, "step41-baseline-harness-smoke.xlsx");

        ExportStats stats;
        await using (var stream = new FileStream(
                         workbookPath,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.None,
                         bufferSize: 128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            stats = await writer.WriteAsync(
                stream,
                CreateContext(),
                CreateFinalizedPlan(smokeRows),
                CancellationToken.None);
        }

        Assert.Equal(
            smokeRows,
            Assert.Single(
                stats.SheetStats,
                item => item.SheetName == Step41Sheet).RowsWritten);
        Assert.Equal(0, countedRows.TypedPageCalls);
        Assert.NotNull(countedRows.LastMetrics);
        AssertPreparedMetrics(countedRows.LastMetrics!, smokeRows);
        var snapshot = await InspectAsync(
            workbookPath,
            smokeRows,
            CancellationToken.None);
        Assert.Equal(smokeRows, snapshot.DataRows);
        Assert.Equal("A1:P16", snapshot.Dimension);
        Assert.Equal(ExpectedHeaders, snapshot.Headers);
        Assert.Equal(ExpectedDataFormats, snapshot.NumberFormats);
        Assert.Equal(255d, snapshot.Widths[LateWideColumn - 1]);
    }

    [Trait(TestProfileTraits.Key, "Scale")]
    [Fact(Skip =
        "Frozen historical oracle only：legacy 三次數值與 evidence 已封存，不再以 current production writer 重跑。")]
    public async Task ReleaseDuckDb_ExactFullPage_ProducesReproducibleThreeRunBaseline()
    {
        var evidenceRoot = Step41BaselineAvailability.RootPath
            ?? throw new InvalidOperationException(
                "Step41BaselineFact 已判定可用，但執行期 evidence root 消失。");
        Assert.True(
            AppContext.BaseDirectory.Contains(
                $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase),
            $"Baseline 必須從 Release 輸出執行，實際為 {AppContext.BaseDirectory}。");

        var runId =
            $"step41-legacy-baseline-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var runRoot = Path.Combine(evidenceRoot, runId);
        Directory.CreateDirectory(runRoot);

        var database = new DuckDbProjectDatabase(new JetProjectFolder(runRoot));
        var fixtureTimer = Stopwatch.StartNew();
        await database.EnsureCreatedAsync(ProjectId, CancellationToken.None);
        await SeedFixtureAsync(database, RowCount, CancellationToken.None);
        fixtureTimer.Stop();

        var databasePath = database.GetDatabasePath(ProjectId);
        var databaseBytes = new FileInfo(databasePath).Length;
        var plan = CreateFinalizedPlan(RowCount);
        var context = CreateContext();
        var countedRows = new CountingLegacyStep41PageRepository(
            new LocalTagMatrixRowPageRepository(database));
        var writer = (IWorkpaperPlanWriter)BuildWriter(database, countedRows);
        var expectedPagesPerPass =
            (RowCount + PageRequest.DefaultPageSize - 1) / PageRequest.DefaultPageSize;
        var expectedTypedPageCalls = checked(expectedPagesPerPass * 2);
        var runs = new List<BaselineRun>();

        for (var run = 1; run <= 3; run++)
        {
            countedRows.Reset();
            var workbookPath = Path.Combine(runRoot, $"workingpaper-run-{run}.xlsx");
            ExportStats stats;
            var timer = Stopwatch.StartNew();
            await using (var stream = new FileStream(
                             workbookPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             bufferSize: 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                stats = await writer.WriteAsync(
                    stream,
                    context,
                    plan,
                    CancellationToken.None);
                await stream.FlushAsync(CancellationToken.None);
            }
            timer.Stop();

            var outputBytes = new FileInfo(workbookPath).Length;
            Assert.Equal(outputBytes, stats.BytesWritten);
            var step41Stat = Assert.Single(
                stats.SheetStats,
                item => string.Equals(
                    item.SheetName,
                    Step41Sheet,
                    StringComparison.Ordinal));
            Assert.Equal(RowCount, step41Stat.RowsWritten);
            Assert.Equal(expectedTypedPageCalls, countedRows.TypedPageCalls);

            var snapshot = await InspectAsync(
                workbookPath,
                RowCount,
                CancellationToken.None);
            Assert.Equal(RowCount, snapshot.DataRows);
            Assert.Equal(1, snapshot.Step41SheetCount);
            Assert.Equal($"A1:P{LastExcelRow}", snapshot.Dimension);
            Assert.Equal(ExpectedHeaders, snapshot.Headers);
            Assert.Equal(ExpectedDataFormats, snapshot.NumberFormats);
            Assert.Equal(ExpectedColumnCount, snapshot.Widths.Count);
            Assert.All(snapshot.BestFit, Assert.True);
            Assert.Equal(255d, snapshot.Widths[LateWideColumn - 1]);

            runs.Add(new BaselineRun(
                run,
                timer.Elapsed.TotalMilliseconds,
                outputBytes,
                snapshot.PackageSha256,
                snapshot.LogicalFingerprint,
                countedRows.TypedPageCalls,
                checked(countedRows.TypedPageCalls * 2),
                snapshot));
        }

        Assert.Single(
            runs.Select(run => run.LogicalFingerprint).Distinct(StringComparer.Ordinal));
        Assert.All(
            runs.Skip(1),
            run => Assert.Equal(runs[0].Snapshot.Widths, run.Snapshot.Widths));
        Assert.All(
            runs.Skip(1),
            run => Assert.Equal(runs[0].Snapshot.BestFit, run.Snapshot.BestFit));
        Assert.All(
            runs.Skip(1),
            run => Assert.Equal(runs[0].Snapshot.NumberFormats, run.Snapshot.NumberFormats));

        var timings = runs
            .Select(run => run.ElapsedMilliseconds)
            .Order()
            .ToArray();
        var medianMilliseconds = timings[1];
        var outputDrive = new DriveInfo(
            Path.GetPathRoot(runRoot)
            ?? throw new InvalidDataException("Baseline output root 沒有 drive root。"));
        var databaseDrive = new DriveInfo(
            Path.GetPathRoot(databasePath)
            ?? throw new InvalidDataException("Baseline database 沒有 drive root。"));
        var tempRoot = Path.GetTempPath();
        var tempDrive = new DriveInfo(
            Path.GetPathRoot(tempRoot)
            ?? throw new InvalidDataException("Baseline temp root 沒有 drive root。"));
        Assert.True(
            string.Equals(
                outputDrive.Name,
                databaseDrive.Name,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                outputDrive.Name,
                tempDrive.Name,
                StringComparison.OrdinalIgnoreCase),
            "Baseline DuckDB、final output 與 generated-workbook temp 必須位於同一磁碟。");
        var process = Process.GetCurrentProcess();
        var evidence = new
        {
            schemaVersion = 2,
            baselineId = "step4-1-legacy-v1",
            generatedUtc = DateTimeOffset.UtcNow,
            configuration = "Release",
            provider = "duckdb",
            fixedSeed = FixedSeed,
            rowCount = RowCount,
            firstDataRow = FirstDataRow,
            lastDataRow = LastExcelRow,
            expectedStep41Sheets = 1,
            fixture = new
            {
                setupExcludedFromExportTimings = true,
                setupElapsedMilliseconds = fixtureTimer.Elapsed.TotalMilliseconds,
                databasePath,
                databaseBytes
            },
            currentPagingContract = new
            {
                pageSize = PageRequest.DefaultPageSize,
                pagesPerPass = expectedPagesPerPass,
                passesPerExport = 2,
                typedPageCallsPerExport = expectedTypedPageCalls,
                readerDataCommandsPerNonemptyPage = 2,
                readerDataCommandsPerExport = checked(expectedTypedPageCalls * 2),
                schemaEnsureCommandsPerTypedPageCall = 1,
                freshConnectionsPerTypedPageCall = 2,
                preparedSet = false,
                singleReader = false,
                directTemplateSax = false,
                secondWorkbookMerge = true
            },
            runs = runs.Select(run => new
            {
                run = run.Number,
                elapsedMilliseconds = run.ElapsedMilliseconds,
                outputBytes = run.OutputBytes,
                packageSha256 = run.PackageSha256,
                normalizedLogicalFingerprint = run.LogicalFingerprint,
                typedPageCalls = run.TypedPageCalls,
                readerDataCommands = run.ReaderDataCommands,
                dimension = run.Snapshot.Dimension,
                step41SheetCount = run.Snapshot.Step41SheetCount,
                dataRows = run.Snapshot.DataRows
            }),
            medianMilliseconds,
            normalizedLogicalFingerprint = runs[0].LogicalFingerprint,
            fingerprintNormalization = new
            {
                values = true,
                nativeCellKinds = true,
                numberFormats = true,
                columnWidths = true,
                bestFitFlags = true,
                volatilePackageMetadataExcluded = true
            },
            widths = runs[0].Snapshot.Widths
                .Select((width, index) => new
                {
                    column = index + 1,
                    header = ExpectedHeaders[index],
                    width,
                    bestFit = runs[0].Snapshot.BestFit[index]
                }),
            numberFormats = runs[0].Snapshot.NumberFormats
                .Select((numberFormat, index) => new
                {
                    column = index + 1,
                    header = ExpectedHeaders[index],
                    numberFormat
                }),
            environment = new
            {
                os = RuntimeInformation.OSDescription,
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
                runtimeVersion = Environment.Version.ToString(),
                processorCount = Environment.ProcessorCount,
                processorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                serverGc = GCSettings.IsServerGC,
                gcLatencyMode = GCSettings.LatencyMode.ToString(),
                totalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                peakWorkingSetBytes = process.PeakWorkingSet64,
                testAssemblyBaseDirectory = AppContext.BaseDirectory,
                outputDrive = new
                {
                    outputDrive.Name,
                    outputDrive.DriveType,
                    outputDrive.DriveFormat,
                    outputDrive.TotalSize,
                    outputDrive.AvailableFreeSpace
                },
                databaseDrive = new
                {
                    databaseDrive.Name,
                    databaseDrive.DriveType,
                    databaseDrive.DriveFormat,
                    databaseDrive.TotalSize,
                    databaseDrive.AvailableFreeSpace
                },
                tempRoot,
                tempDrive = new
                {
                    tempDrive.Name,
                    tempDrive.DriveType,
                    tempDrive.DriveFormat,
                    tempDrive.TotalSize,
                    tempDrive.AvailableFreeSpace
                },
                sameOutputAndDatabaseDrive = string.Equals(
                    outputDrive.Name,
                    databaseDrive.Name,
                    StringComparison.OrdinalIgnoreCase),
                sameOutputDatabaseAndTempDrive =
                    string.Equals(
                        outputDrive.Name,
                        databaseDrive.Name,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        outputDrive.Name,
                        tempDrive.Name,
                        StringComparison.OrdinalIgnoreCase)
            }
        };
        var evidencePath = Path.Combine(runRoot, "baseline-evidence.json");
        var evidenceJson = JsonSerializer.Serialize(
            evidence,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            evidencePath,
            evidenceJson,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CancellationToken.None);
        output.WriteLine(evidenceJson);
        output.WriteLine($"Step4-1 baseline evidence: {evidencePath}");
    }

    [Step41BaselineFact]
    public async Task ReleaseDuckDb_ExactFullPage_PreparedCandidateProducesThreeRunFiveFoldEvidence()
    {
        var evidenceRoot = Step41BaselineAvailability.RootPath
            ?? throw new InvalidOperationException(
                "Step41BaselineFact 已判定可用，但執行期 evidence root 消失。");
        Assert.True(
            AppContext.BaseDirectory.Contains(
                $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase),
            $"Prepared candidate 必須從 Release 輸出執行，實際為 {AppContext.BaseDirectory}。");

        var runId =
            $"step41-prepared-candidate-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var runRoot = Path.Combine(evidenceRoot, runId);
        Directory.CreateDirectory(runRoot);
        var database = new DuckDbProjectDatabase(new JetProjectFolder(runRoot));
        var fixtureTimer = Stopwatch.StartNew();
        await database.EnsureCreatedAsync(ProjectId, CancellationToken.None);
        await SeedFixtureAsync(database, RowCount, CancellationToken.None);
        fixtureTimer.Stop();

        var countedRows = new CountingStep41PreparedRepository(
            new LocalTagMatrixRowPageRepository(database));
        var writer = (IWorkpaperPlanWriter)BuildWriter(database, countedRows);
        var context = CreateContext();
        var plan = CreateFinalizedPlan(RowCount);
        var runs = new List<CandidateRun>();
        for (var run = 1; run <= 3; run++)
        {
            countedRows.Reset();
            var workbookPath = Path.Combine(
                runRoot,
                $"workingpaper-prepared-run-{run}.xlsx");
            var timer = Stopwatch.StartNew();
            ExportStats stats;
            await using (var stream = new FileStream(
                             workbookPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             bufferSize: 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                stats = await writer.WriteAsync(
                    stream,
                    context,
                    plan,
                    CancellationToken.None);
                await stream.FlushAsync(CancellationToken.None);
            }
            timer.Stop();

            Assert.Equal(0, countedRows.TypedPageCalls);
            var metrics = countedRows.LastMetrics
                ?? throw new InvalidOperationException(
                    "Prepared candidate 未捕捉 session metrics。");
            AssertPreparedMetrics(metrics, RowCount);
            Assert.Equal(
                RowCount,
                Assert.Single(
                    stats.SheetStats,
                    item => item.SheetName == Step41Sheet).RowsWritten);
            var outputBytes = new FileInfo(workbookPath).Length;
            Assert.Equal(outputBytes, stats.BytesWritten);

            var snapshot = await InspectAsync(
                workbookPath,
                RowCount,
                CancellationToken.None);
            Assert.Equal(FrozenFullPageFingerprint, snapshot.LogicalFingerprint);
            Assert.Equal(RowCount, snapshot.DataRows);
            Assert.Equal(1, snapshot.Step41SheetCount);
            Assert.Equal($"A1:P{LastExcelRow}", snapshot.Dimension);
            Assert.Equal(ExpectedHeaders, snapshot.Headers);
            Assert.Equal(ExpectedDataFormats, snapshot.NumberFormats);
            Assert.Equal(ExpectedColumnCount, snapshot.Widths.Count);
            Assert.All(snapshot.BestFit, Assert.True);
            Assert.Equal(255d, snapshot.Widths[LateWideColumn - 1]);

            var process = Process.GetCurrentProcess();
            process.Refresh();
            runs.Add(new CandidateRun(
                run,
                timer.Elapsed.TotalMilliseconds,
                outputBytes,
                process.PeakWorkingSet64,
                countedRows.TypedPageCalls,
                metrics,
                snapshot));
        }

        Assert.All(
            runs,
            run => Assert.Equal(
                FrozenFullPageFingerprint,
                run.Snapshot.LogicalFingerprint));
        Assert.Single(
            runs.Select(run => run.Snapshot.LogicalFingerprint)
                .Distinct(StringComparer.Ordinal));
        Assert.All(
            runs.Skip(1),
            run => Assert.Equal(runs[0].Snapshot.Widths, run.Snapshot.Widths));
        Assert.All(
            runs.Skip(1),
            run => Assert.Equal(runs[0].Snapshot.NumberFormats, run.Snapshot.NumberFormats));

        var candidateMedianMilliseconds = runs
            .Select(run => run.ElapsedMilliseconds)
            .Order()
            .ElementAt(1);
        const double frozenBaselineMedianMilliseconds = 2_114_888.8913;
        const double maximumCandidateMedianMilliseconds =
            frozenBaselineMedianMilliseconds / 5.0;
        var speedup = frozenBaselineMedianMilliseconds / candidateMedianMilliseconds;
        Assert.True(
            speedup >= 5.0,
            $"Step4-1 five-fold gate failed: baseline/candidate={speedup:R}; "
            + $"candidate median={candidateMedianMilliseconds:R} ms; "
            + $"maximum={maximumCandidateMedianMilliseconds:R} ms.");

        var databasePath = database.GetDatabasePath(ProjectId);
        var outputDrive = new DriveInfo(
            Path.GetPathRoot(runRoot)
            ?? throw new InvalidDataException("Candidate output root 沒有 drive root。"));
        var databaseDrive = new DriveInfo(
            Path.GetPathRoot(databasePath)
            ?? throw new InvalidDataException("Candidate database 沒有 drive root。"));
        var tempRoot = Path.GetTempPath();
        var tempDrive = new DriveInfo(
            Path.GetPathRoot(tempRoot)
            ?? throw new InvalidDataException("Candidate temp root 沒有 drive root。"));
        Assert.True(
            string.Equals(
                outputDrive.Name,
                databaseDrive.Name,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                outputDrive.Name,
                tempDrive.Name,
                StringComparison.OrdinalIgnoreCase),
            "Candidate DuckDB、final output 與 projected-row spool 必須位於同一磁碟。");

        var evidence = new
        {
            schemaVersion = 3,
            candidateId = "workingpaper-step41-prepared-session-v1",
            generatedUtc = DateTimeOffset.UtcNow,
            configuration = "Release",
            provider = "duckdb",
            fixedSeed = FixedSeed,
            rowCount = RowCount,
            firstDataRow = FirstDataRow,
            lastDataRow = LastExcelRow,
            fixture = new
            {
                setupExcludedFromExportTimings = true,
                setupElapsedMilliseconds = fixtureTimer.Elapsed.TotalMilliseconds,
                databasePath,
                databaseBytes = new FileInfo(databasePath).Length
            },
            frozenBaseline = new
            {
                baselineId = "step4-1-legacy-v1",
                elapsedMilliseconds = new[]
                {
                    2_222_087.6258,
                    2_114_888.8913,
                    2_066_202.9736
                },
                medianMilliseconds = frozenBaselineMedianMilliseconds,
                evidence = "docs/specs/evidence/2026-07-27-step4-1-legacy-baseline.md"
            },
            candidateRuns = runs.Select(run => new
            {
                run = run.Number,
                elapsedMilliseconds = run.ElapsedMilliseconds,
                outputBytes = run.OutputBytes,
                peakWorkingSetBytes = run.PeakWorkingSetBytes,
                packageSha256 = run.Snapshot.PackageSha256,
                normalizedLogicalFingerprint = run.Snapshot.LogicalFingerprint,
                dimension = run.Snapshot.Dimension,
                step41SheetCount = run.Snapshot.Step41SheetCount,
                dataRows = run.Snapshot.DataRows,
                typedPageCalls = run.TypedPageCalls,
                commandShape = MetricsEvidence(run.Metrics)
            }),
            gate = new
            {
                candidateMedianMilliseconds,
                maximumCandidateMedianMilliseconds,
                baselineOverCandidate = speedup,
                minimumRequired = 5.0,
                passed = true
            },
            architecture = new
            {
                preparedSet = true,
                singleConnection = true,
                singleTransactionSnapshot = true,
                singleOrderedReader = true,
                exactWidthAggregation = true,
                typedProjectedRowSpool = true,
                directTemplateSax = true,
                secondWorkbookMerge = false,
                typedPageCalls = 0
            },
            normalizedLogicalFingerprint = FrozenFullPageFingerprint,
            fingerprintNormalization = new
            {
                values = true,
                nativeCellKinds = true,
                numberFormats = true,
                columnWidths = true,
                bestFitFlags = true,
                volatilePackageMetadataExcluded = true
            },
            widths = runs[0].Snapshot.Widths,
            bestFit = runs[0].Snapshot.BestFit,
            numberFormats = runs[0].Snapshot.NumberFormats,
            environment = new
            {
                os = RuntimeInformation.OSDescription,
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
                runtimeVersion = Environment.Version.ToString(),
                processorCount = Environment.ProcessorCount,
                processorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                serverGc = GCSettings.IsServerGC,
                gcLatencyMode = GCSettings.LatencyMode.ToString(),
                totalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                testAssemblyBaseDirectory = AppContext.BaseDirectory,
                outputDrive = outputDrive.Name,
                databaseDrive = databaseDrive.Name,
                tempRoot,
                tempDrive = tempDrive.Name,
                sameOutputDatabaseAndTempDrive = true
            }
        };
        var evidencePath = Path.Combine(runRoot, "prepared-candidate-evidence.json");
        var evidenceJson = JsonSerializer.Serialize(
            evidence,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            evidencePath,
            evidenceJson,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CancellationToken.None);
        output.WriteLine(evidenceJson);
        output.WriteLine($"Prepared candidate evidence: {evidencePath}");
    }

    private static void AssertPreparedMetrics(
        WorkpaperStep41PreparedSessionMetrics metrics,
        long expectedRows)
    {
        Assert.Equal("duckdb", metrics.Provider);
        // 2026-10-02 資料庫分流簡化：ProviderResolutions 指標隨分流層一起刪除，原本「為 0」的斷言拿掉；
        // 其餘斷言不變。第一次失敗收據（引用已刪除的屬性而無法建置）：20261002-120244042-2aa5edaa431d4f3e89e068361d5482f5。
        Assert.Equal(1, metrics.SchemaReadinessCommands);
        Assert.Equal(1, metrics.Connections);
        Assert.Equal(1, metrics.Transactions);
        Assert.Equal(1, metrics.TemporaryTableInitializationCommands);
        Assert.Equal(1, metrics.HitVoucherMaterializationCommands);
        Assert.Equal(1, metrics.RowTagMaterializationCommands);
        Assert.Equal(1, metrics.WidthAggregations);
        Assert.Equal(1, metrics.OrderedReaderCommands);
        Assert.Equal(1, metrics.CleanupCommands);
        Assert.Equal(expectedRows, metrics.RowsRead);
        Assert.True(metrics.ReaderCompleted);
        Assert.True(metrics.CleanupCommandSucceeded);
        Assert.True(metrics.TransactionCommitted);
        Assert.False(metrics.TransactionRolledBack);
        Assert.True(metrics.DedicatedConnection);
        Assert.True(metrics.TransactionDisposed);
        Assert.True(metrics.ConnectionDisposed);
        Assert.True(metrics.Disposed);
        Assert.True(metrics.TemporaryObjectsCleared);
        Assert.Equal(
            [
                "schemaReadiness",
                "initializeTemporaryTables",
                "materializeHitVouchers",
                "materializeRowTags",
                "orderedRows",
                "cleanup"
            ],
            metrics.Commands.Select(command => command.Operation));
    }

    private static object MetricsEvidence(
        WorkpaperStep41PreparedSessionMetrics metrics) => new
    {
        metrics.Provider,
        metrics.SchemaReadinessCommands,
        metrics.Connections,
        metrics.Transactions,
        metrics.TemporaryTableInitializationCommands,
        metrics.HitVoucherMaterializationCommands,
        metrics.RowTagMaterializationCommands,
        metrics.WidthAggregations,
        metrics.OrderedReaderCommands,
        metrics.CleanupCommands,
        metrics.RowsRead,
        metrics.TransactionIsolationLevel,
        metrics.ConsistencyMode,
        metrics.DedicatedConnection,
        metrics.ReaderCompleted,
        metrics.CleanupCommandSucceeded,
        metrics.TransactionCommitted,
        metrics.TransactionRolledBack,
        metrics.TransactionDisposed,
        metrics.ConnectionDisposed,
        metrics.Disposed,
        metrics.TemporaryObjectsCleared,
        operations = metrics.Commands.Select(command => command.Operation)
    };

    private static WorkpaperWriter BuildWriter(
        ILocalProjectDatabase database,
        ITagMatrixRowPageRepository tagMatrixRows) =>
        new(
            new LocalCompletenessAccountPageRepository(database),
            new LocalCompletenessDiffPageRepository(database),
            new LocalDocBalancePageRepository(database),
            new LocalCreatorSummaryExportRepository(database),
            new LocalInfSamplePageRepository(database),
            new LocalFilterScenarioStore(database),
            new LocalTagMatrixScenariosRepository(database),
            new LocalTagMatrixVoucherPageRepository(database),
            tagMatrixRows,
            new LocalMappingStateStore(database),
            new LocalCalendarExportRepository(database),
            new LocalAccountMappingExportRepository(database),
            new LocalRawGlExportRepository(database));

    private static WorkpaperContext CreateContext() =>
        new(
            ProjectId,
            "Step4-1 Baseline Co.",
            "2025-01-01",
            "2025-12-31",
            "2024-01-01",
            MoneyScale,
            "baseline-validation-v1",
            "baseline-scenarios-v1",
            [1, 7, 10],
            PopulationScope: GlPopulationScope.AuditPeriod);

    private static WorkpaperPlan CreateFinalizedPlan(long rowCount)
    {
        var request = new WorkpaperRequest(
            ProjectId,
            "2025-01-01",
            "2025-12-31",
            "2024-01-01",
            MoneyScale,
            "baseline-validation-v1",
            "baseline-scenarios-v1",
            [
                new WorkpaperScenarioSelection(1, "固定全列命中", "baseline C1"),
                new WorkpaperScenarioSelection(7, "固定零列命中", "baseline C7"),
                new WorkpaperScenarioSelection(10, "固定五列一命中", "baseline C10")
            ],
            GlPopulationScope.AuditPeriod);
        var initial = JetAuditProgram.Plan(request);
        var definitions = new[]
        {
            Definition(1, "傳票號碼_JE", LegacyFieldKind.Text),
            Definition(
                2,
                "傳票文件項次_JE_S",
                LegacyFieldKind.Number,
                decimalPlaces: 0),
            Definition(3, "傳票核准日_JE", LegacyFieldKind.Date),
            Definition(4, "總帳日期_JE", LegacyFieldKind.Date),
            Definition(5, "傳票建立人員_JE", LegacyFieldKind.Text),
            Definition(6, "傳票核准人員_JE", LegacyFieldKind.Text),
            Definition(7, "會計科目編號_JE", LegacyFieldKind.Text),
            Definition(8, "會計科目名稱_JE", LegacyFieldKind.Text),
            Definition(
                9,
                "傳票金額_JE",
                LegacyFieldKind.Number,
                decimalPlaces: 4),
            Definition(10, "傳票摘要_JE", LegacyFieldKind.Text),
            Definition(11, "分錄來源模組_JE", LegacyFieldKind.Text),
            Definition(
                12,
                "人工傳票否_JE_S",
                LegacyFieldKind.Number,
                decimalPlaces: 0),
            Definition(
                13,
                "ROW_TOKEN_JE",
                LegacyFieldKind.Text,
                description: "ROW_TOKEN_JE"),
            Definition(
                14,
                "LATE_WIDE_JE",
                LegacyFieldKind.Text,
                description: "LATE_WIDE_JE"),
            Definition(
                15,
                "傳票日期_僅條件_JE",
                LegacyFieldKind.Date)
        };
        var facts = new WorkpaperPlanningFacts(
            HasCompletenessDifferences: false,
            HasUnbalancedDocuments: false,
            new Dictionary<int, (long VoucherHitCount, long RowHitCount)>
            {
                [1] = (1, rowCount),
                [7] = (0, 0),
                [10] = (1, rowCount / 5)
            },
            TargetTbDefinitions:
            [
                Definition(1, "會計科目編號_TB", LegacyFieldKind.Text)
            ],
            TargetGlDefinitions: definitions,
            VoucherDateSourceField: "傳票日期_僅條件_JE");
        return JetAuditProgram.Finalize(initial, facts);
    }

    private static LegacyFieldDefinition Definition(
        int ordinal,
        string name,
        LegacyFieldKind kind,
        string? description = null,
        int? decimalPlaces = null) =>
        new(
            ordinal,
            name,
            description,
            kind,
            kind == LegacyFieldKind.Text ? 255 : null,
            kind == LegacyFieldKind.Number ? decimalPlaces : null);

    private static async Task SeedFixtureAsync(
        ILocalProjectDatabase database,
        long rowCount,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection(ProjectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            BEGIN TRANSACTION;

            INSERT INTO staging_gl_raw_row
                (batch_id, row_number, source_no, source_row_number, row_json)
            SELECT
                '{{BatchId}}',
                i,
                1,
                i,
                '{"ROW_TOKEN_JE":"' || printf('R%07d', i)
                    || '","LATE_WIDE_JE":"'
                    || CASE WHEN i = {{rowCount}} THEN repeat('界', 150) ELSE 'x' END
                    || '","傳票日期_僅條件_JE":"2025-06-29"}'
            FROM range(1, {{rowCount + 1}}) AS source(i);

            INSERT INTO target_gl_entry
                (entry_id, batch_id, source_row_number, document_number, line_item,
                 line_item_numeric_sort_key, post_date, approval_date, voucher_date,
                 account_code, account_name,
                 document_description, source_module, created_by, approved_by, is_manual,
                 is_effective, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
            SELECT
                {{rowCount}} - i + 1,
                '{{BatchId}}',
                i,
                'DOC-BASELINE',
                printf('%07d', line_no),
                CASE
                    WHEN line_no = 0 THEN '1' || repeat('0', 39)
                    ELSE '2'
                        || printf(
                            '%010d',
                            2147483648 + length(CAST(line_no AS VARCHAR)))
                        || CAST(line_no AS VARCHAR)
                        || repeat(
                            '0',
                            29 - length(CAST(line_no AS VARCHAR)))
                END,
                '2025-06-30',
                '2025-07-01',
                NULL,
                '1101',
                'Baseline Account',
                'baseline hit',
                 CASE WHEN (i + {{FixedSeed}}) % 3 = 0 THEN 'MANUAL' ELSE 'GL' END,
                'baseline-user',
                'baseline-approver',
                CASE WHEN i % 2 = 1 THEN 1 ELSE 0 END,
                1,
                CASE
                    WHEN i = {{rowCount}} THEN 0
                    WHEN i % 2 = 1 THEN {{MoneyScale}}
                    ELSE -{{MoneyScale}}
                END,
                CASE
                    WHEN i = {{rowCount}} THEN 0
                    WHEN i % 2 = 1 THEN {{MoneyScale}}
                    ELSE 0
                END,
                CASE
                    WHEN i = {{rowCount}} THEN 0
                    WHEN i % 2 = 0 THEN {{MoneyScale}}
                    ELSE 0
                END,
                CASE
                    WHEN i = {{rowCount}} OR i % 2 = 1 THEN 'DEBIT'
                    ELSE 'CREDIT'
                END
            FROM (
                SELECT
                    i,
                    CAST(floor((i - 1) / 4) AS BIGINT) AS line_no
                FROM range(1, {{rowCount + 1}}) AS generated(i)
            ) AS source;

            INSERT INTO result_filter_run (scenario_position, entry_id)
            SELECT 1, entry_id
            FROM target_gl_entry;

            INSERT INTO result_filter_run (scenario_position, entry_id)
            SELECT 10, entry_id
            FROM target_gl_entry
            WHERE source_row_number % 5 = 0;

            COMMIT;
            ANALYZE;
            CHECKPOINT;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<BaselineSnapshot> InspectAsync(
        string workbookPath,
        long expectedRowCount,
        CancellationToken cancellationToken)
    {
        string worksheetEntryName;
        string[] sheetNames;
        IReadOnlyList<string> styleFormats;
        using (var document = SpreadsheetDocument.Open(
                   workbookPath,
                   false,
                   new OpenSettings { AutoSave = false }))
        {
            var workbookPart = document.WorkbookPart
                ?? throw new InvalidDataException("Baseline workbook 缺少 workbook part。");
            styleFormats = ReadStyleFormats(workbookPart);
            var sheets = workbookPart.Workbook.Sheets?
                .Elements<Sheet>()
                .ToArray()
                ?? throw new InvalidDataException("Baseline workbook 缺少 sheets。");
            sheetNames = sheets
                .Select(sheet => sheet.Name?.Value ?? string.Empty)
                .ToArray();
            var step41 = sheets.Single(sheet => string.Equals(
                sheet.Name?.Value,
                Step41Sheet,
                StringComparison.Ordinal));
            var part = (WorksheetPart)workbookPart.GetPartById(
                step41.Id?.Value
                ?? throw new InvalidDataException("Step4-1 sheet 缺少 relationship id。"));
            worksheetEntryName = part.Uri.OriginalString.TrimStart('/');
        }

        var packageSha256 = await HashFileAsync(
            workbookPath,
            cancellationToken);
        var widths = new SortedDictionary<int, double>();
        var bestFit = new SortedDictionary<int, bool>();
        var headers = Array.Empty<string>();
        string[]? dataFormats = null;
        var dataRows = 0L;
        string? dimension = null;

        using var logicalHash = SHA256.Create();
        using var crypto = new CryptoStream(
            Stream.Null,
            logicalHash,
            CryptoStreamMode.Write,
            leaveOpen: true);
        using var hashWriter = new StreamWriter(
            crypto,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 64 * 1024,
            leaveOpen: true);
        using var archive = ZipFile.OpenRead(workbookPath);
        var worksheetEntry = archive.GetEntry(worksheetEntryName)
            ?? throw new InvalidDataException(
                $"Baseline package 缺少 {worksheetEntryName}。");
        await using var worksheetStream = worksheetEntry.Open();
        using var reader = XmlReader.Create(
            worksheetStream,
            new XmlReaderSettings
            {
                Async = false,
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreWhitespace = true,
                CloseInput = false
            });

        uint currentRow = 0;
        LogicalCell[]? rowCells = null;
        var currentCellColumn = 0;
        string? currentCellType = null;
        var currentCellStyleIndex = 0;
        var currentCellValue = string.Empty;
        var currentCellHasValue = false;
        var insideCell = false;
        var insideCellValue = false;

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "dimension":
                        dimension = reader.GetAttribute("ref");
                        break;
                    case "col":
                    {
                        var min = ParseRequiredInt(reader.GetAttribute("min"), "col min");
                        var max = ParseRequiredInt(reader.GetAttribute("max"), "col max");
                        var width = double.Parse(
                            reader.GetAttribute("width")
                            ?? throw new InvalidDataException("Step4-1 col 缺少 width。"),
                            CultureInfo.InvariantCulture);
                        var isBestFit = ParseOpenXmlBoolean(reader.GetAttribute("bestFit"));
                        for (var column = min; column <= max; column++)
                        {
                            widths[column] = width;
                            bestFit[column] = isBestFit;
                        }
                        break;
                    }
                    case "row":
                        currentRow = uint.Parse(
                            reader.GetAttribute("r")
                            ?? throw new InvalidDataException("Step4-1 row 缺少 r。"),
                            CultureInfo.InvariantCulture);
                        rowCells = Enumerable
                            .Repeat(LogicalCell.Blank, ExpectedColumnCount)
                            .ToArray();
                        break;
                    case "c":
                        insideCell = true;
                        currentCellColumn = ColumnIndex(
                            reader.GetAttribute("r")
                            ?? throw new InvalidDataException("Step4-1 cell 缺少 r。"));
                        currentCellType = reader.GetAttribute("t");
                        currentCellStyleIndex = reader.GetAttribute("s") is { } styleIndex
                            ? ParseRequiredInt(styleIndex, "cell style index")
                            : 0;
                        currentCellValue = string.Empty;
                        currentCellHasValue = false;
                        break;
                    case "t" or "v" when insideCell:
                        insideCellValue = true;
                        break;
                }
            }
            else if ((reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                     && insideCellValue)
            {
                currentCellValue += reader.Value;
                currentCellHasValue = true;
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName is "t" or "v" && insideCellValue)
                {
                    insideCellValue = false;
                }
                else if (reader.LocalName == "c" && insideCell)
                {
                    if (rowCells is not null
                        && currentCellColumn is >= 1 and <= ExpectedColumnCount)
                    {
                        rowCells[currentCellColumn - 1] = NormalizeCell(
                            currentCellType,
                            ResolveStyleFormat(
                                styleFormats,
                                currentCellStyleIndex),
                            currentCellValue,
                            currentCellHasValue);
                    }
                    insideCell = false;
                }
                else if (reader.LocalName == "row" && rowCells is not null)
                {
                    if (currentRow == 5)
                    {
                        headers = rowCells.Select(cell => cell.Value).ToArray();
                        for (var index = 0; index < ExpectedDataFormats.Length; index++)
                        {
                            if (!string.Equals(
                                    rowCells[index].NumberFormat,
                                    ExpectedDataFormats[index],
                                    StringComparison.Ordinal))
                            {
                                throw new InvalidDataException(
                                    $"Step4-1 表頭第 {index + 1} 欄 number format 不正確："
                                    + $"預期 {ExpectedDataFormats[index]}，實得 {rowCells[index].NumberFormat}。");
                            }
                        }
                        WriteCanonicalRow(hashWriter, "H", rowCells);
                    }
                    else if (currentRow >= FirstDataRow
                             && rowCells.Any(cell =>
                                 !string.Equals(cell.Kind, "B", StringComparison.Ordinal)))
                    {
                        var expectedRow = checked((uint)(FirstDataRow + dataRows));
                        if (currentRow != expectedRow)
                        {
                            throw new InvalidDataException(
                                $"Step4-1 data row 不連續：預期 {expectedRow}，實得 {currentRow}。");
                        }

                        dataRows++;
                        ValidateDataRow(dataRows, rowCells, expectedRowCount);
                        dataFormats ??= rowCells
                            .Select((cell, index) =>
                                string.Equals(cell.Kind, "B", StringComparison.Ordinal)
                                && string.Equals(cell.NumberFormat, "General", StringComparison.Ordinal)
                                && string.Equals(ExpectedDataFormats[index], "@", StringComparison.Ordinal)
                                    ? "@"
                                    : cell.NumberFormat)
                            .ToArray();
                        WriteCanonicalRow(hashWriter, "D", rowCells);
                    }
                    rowCells = null;
                }
            }
        }

        for (var column = 1; column <= ExpectedColumnCount; column++)
        {
            if (!widths.TryGetValue(column, out var width))
            {
                throw new InvalidDataException($"Step4-1 缺少第 {column} 欄欄寬。");
            }
            hashWriter.Write("W|");
            hashWriter.Write(column.ToString(CultureInfo.InvariantCulture));
            hashWriter.Write('|');
            hashWriter.Write(width.ToString("R", CultureInfo.InvariantCulture));
            hashWriter.Write('|');
            hashWriter.Write(bestFit.GetValueOrDefault(column) ? '1' : '0');
            hashWriter.Write('\n');
        }
        hashWriter.Flush();
        crypto.FlushFinalBlock();
        var logicalFingerprint = Convert.ToHexString(
            logicalHash.Hash
            ?? throw new InvalidDataException("Step4-1 logical SHA-256 未完成。"));

        return new BaselineSnapshot(
            packageSha256,
            logicalFingerprint,
            dimension ?? string.Empty,
            sheetNames.Count(name =>
                string.Equals(name, Step41Sheet, StringComparison.Ordinal)
                || name.StartsWith(Step41Sheet + " (續", StringComparison.Ordinal)),
            dataRows,
            headers,
            Enumerable.Range(1, ExpectedColumnCount)
                .Select(column => widths[column])
                .ToArray(),
            Enumerable.Range(1, ExpectedColumnCount)
                .Select(column => bestFit.GetValueOrDefault(column))
                .ToArray(),
            dataFormats
                ?? throw new InvalidDataException("Step4-1 baseline 沒有資料列格式證據。"));
    }

    private static void ValidateDataRow(
        long dataOrdinal,
        IReadOnlyList<LogicalCell> cells,
        long expectedRowCount)
    {
        for (var index = 0; index < ExpectedDataFormats.Length; index++)
        {
            var expectedFormat = ExpectedDataFormats[index];
            var absentBlankTextCell =
                string.Equals(cells[index].Kind, "B", StringComparison.Ordinal)
                && string.Equals(cells[index].NumberFormat, "General", StringComparison.Ordinal)
                && string.Equals(expectedFormat, "@", StringComparison.Ordinal);
            if (!absentBlankTextCell
                && !string.Equals(
                    cells[index].NumberFormat,
                    expectedFormat,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Step4-1 第 {index + 1} 欄 number format 不正確："
                    + $"預期 {expectedFormat}，實得 {cells[index].NumberFormat}。");
            }
        }

        var groupStart = ((dataOrdinal - 1) / 4) * 4 + 1;
        var groupSize = Math.Min(4, expectedRowCount - groupStart + 1);
        var groupOffset = (dataOrdinal - 1) % 4;
        var sourceRow = groupStart + groupSize - 1 - groupOffset;
        var expectedToken = $"R{sourceRow:D7}";
        var actualToken = cells[RowTokenColumn - 1].Value;
        if (!string.Equals(actualToken, expectedToken, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Step4-1 token 排序／完整性失敗於輸出第 {dataOrdinal} 列："
                + $"預期 {expectedToken}，實得 {actualToken}。");
        }

        var expectedLine = ((sourceRow - 1) / 4).ToString(
            CultureInfo.InvariantCulture);
        if (!string.Equals(
                cells[1].Value,
                expectedLine,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Step4-1 line item 排序失敗於 {expectedToken}："
                + $"預期 {expectedLine}，實得 {cells[1].Value} ({cells[1].Kind})。");
        }
        if (!string.Equals(cells[1].Kind, "N", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Step4-1 {expectedToken} 的 line item 未保留 Number 型態。");
        }
        if (!string.Equals(
                cells[11].Value,
                sourceRow % 2 == 1 ? "1" : "0",
                StringComparison.Ordinal)
            || !string.Equals(cells[11].Kind, "N", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Step4-1 {expectedToken} 的 manual 0/1 native 型態不正確。");
        }

        if (!string.Equals(
                cells[C1TagColumn - 1].Value,
                "Y",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Step4-1 {expectedToken} 缺少 C1_TAG。");
        }
        var expectedC10 = sourceRow % 5 == 0 ? "Y" : string.Empty;
        if (!string.Equals(
                cells[C10TagColumn - 1].Value,
                expectedC10,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Step4-1 {expectedToken} 的 C10_TAG 不正確。");
        }

        var expectedWide = sourceRow == expectedRowCount
            ? new string('界', 150)
            : "x";
        if (!string.Equals(
                cells[LateWideColumn - 1].Value,
                expectedWide,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Step4-1 {expectedToken} 的 late-width sentinel 不正確。");
        }
    }

    private static void WriteCanonicalRow(
        TextWriter writer,
        string rowKind,
        IReadOnlyList<LogicalCell> cells)
    {
        writer.Write(rowKind);
        foreach (var cell in cells)
        {
            writer.Write('\u001f');
            writer.Write(cell.Kind);
            writer.Write('@');
            WriteEscaped(writer, cell.NumberFormat);
            writer.Write(':');
            WriteEscaped(writer, cell.Value);
        }
        writer.Write('\n');
    }

    private static void WriteEscaped(TextWriter writer, string value)
    {
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    writer.Write("\\\\");
                    break;
                case '\r':
                    writer.Write("\\r");
                    break;
                case '\n':
                    writer.Write("\\n");
                    break;
                case '\u001f':
                    writer.Write("\\u001f");
                    break;
                default:
                    writer.Write(character);
                    break;
            }
        }
    }

    private static LogicalCell NormalizeCell(
        string? cellType,
        string numberFormat,
        string value,
        bool hasValue)
    {
        if (!hasValue)
        {
            return new LogicalCell("B", string.Empty, numberFormat);
        }
        if (string.IsNullOrEmpty(cellType)
            || string.Equals(cellType, "n", StringComparison.Ordinal))
        {
            return decimal.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number)
                ? new LogicalCell(
                    "N",
                    number.ToString("G29", CultureInfo.InvariantCulture),
                    numberFormat)
                : new LogicalCell("N", value, numberFormat);
        }
        return new LogicalCell("T", value, numberFormat);
    }

    private static IReadOnlyList<string> ReadStyleFormats(
        WorkbookPart workbookPart)
    {
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet
            ?? throw new InvalidDataException("Baseline workbook 缺少 stylesheet。");
        var custom = stylesheet.NumberingFormats?
            .Elements<NumberingFormat>()
            .ToDictionary(
                format => format.NumberFormatId?.Value ?? 0,
                format => format.FormatCode?.Value ?? string.Empty)
            ?? new Dictionary<uint, string>();
        return (stylesheet.CellFormats?.Elements<CellFormat>()
                ?? throw new InvalidDataException("Baseline workbook 缺少 cell formats。"))
            .Select(format =>
            {
                var id = format.NumberFormatId?.Value ?? 0;
                return id == 0
                    ? "General"
                    : id == 49
                        ? "@"
                    : custom.GetValueOrDefault(id, $"builtin:{id}");
            })
            .ToArray();
    }

    private static string ResolveStyleFormat(
        IReadOnlyList<string> styleFormats,
        int styleIndex)
    {
        if (styleIndex < 0 || styleIndex >= styleFormats.Count)
        {
            throw new InvalidDataException(
                $"Step4-1 cell style index {styleIndex} 超出 stylesheet 範圍。");
        }

        return styleFormats[styleIndex];
    }

    private static int ParseRequiredInt(string? value, string label) =>
        int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new InvalidDataException($"Step4-1 {label} 無效。");

    private static bool ParseOpenXmlBoolean(string? value) =>
        value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static int ColumnIndex(string reference)
    {
        var result = 0;
        foreach (var character in reference)
        {
            if (character is < 'A' or > 'Z')
            {
                break;
            }
            result = checked(result * 26 + character - 'A' + 1);
        }
        return result;
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private sealed class CountingLegacyStep41PageRepository(
        LocalTagMatrixRowPageRepository inner)
        : ITagMatrixRowPageRepository, IWorkpaperStep41PageRepository
    {
        private long _typedPageCalls;

        internal long TypedPageCalls => Interlocked.Read(ref _typedPageCalls);

        internal void Reset() => Interlocked.Exchange(ref _typedPageCalls, 0);

        public Task<(
            PageResult<RowTagRow> Page,
            IReadOnlyList<long> EntryIds,
            IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> GetPageAsync(
            string projectId,
            GlPopulationContext context,
            PageRequest request,
            IReadOnlyList<int>? scenarioPositions,
            CancellationToken cancellationToken) =>
            inner.GetPageAsync(
                projectId,
                context,
                request,
                scenarioPositions,
                cancellationToken);

        async Task<WorkpaperStep41Page> IWorkpaperStep41PageRepository.GetPageAsync(
            string projectId,
            GlPopulationContext context,
            PageRequest request,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _typedPageCalls);
            return await ((IWorkpaperStep41PageRepository)inner).GetPageAsync(
                projectId,
                context,
                request,
                scenarioPositions,
                lineItemKind,
                cancellationToken);
        }
    }

    private sealed class CountingStep41PreparedRepository(
        LocalTagMatrixRowPageRepository inner)
        : ITagMatrixRowPageRepository,
          IWorkpaperStep41PageRepository,
          IWorkpaperStep41PreparedSessionFactory
    {
        private long _typedPageCalls;

        internal long TypedPageCalls => Interlocked.Read(ref _typedPageCalls);

        internal WorkpaperStep41PreparedSessionMetrics? LastMetrics { get; private set; }

        internal void Reset()
        {
            Interlocked.Exchange(ref _typedPageCalls, 0);
            LastMetrics = null;
        }

        public Task<(
            PageResult<RowTagRow> Page,
            IReadOnlyList<long> EntryIds,
            IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> GetPageAsync(
            string projectId,
            GlPopulationContext context,
            PageRequest request,
            IReadOnlyList<int>? scenarioPositions,
            CancellationToken cancellationToken) =>
            inner.GetPageAsync(
                projectId,
                context,
                request,
                scenarioPositions,
                cancellationToken);

        async Task<WorkpaperStep41Page>
            IWorkpaperStep41PageRepository.GetPageAsync(
                string projectId,
                GlPopulationContext context,
                PageRequest request,
                IReadOnlyList<int> scenarioPositions,
                LegacyFieldKind lineItemKind,
                CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _typedPageCalls);
            return await ((IWorkpaperStep41PageRepository)inner).GetPageAsync(
                projectId,
                context,
                request,
                scenarioPositions,
                lineItemKind,
                cancellationToken);
        }

        async Task<IWorkpaperStep41PreparedSession>
            IWorkpaperStep41PreparedSessionFactory.PrepareAsync(
                string projectId,
                GlPopulationContext context,
                IReadOnlyList<int> scenarioPositions,
                LegacyFieldKind lineItemKind,
                CancellationToken cancellationToken)
        {
            var session = await ((IWorkpaperStep41PreparedSessionFactory)inner)
                .PrepareAsync(
                    projectId,
                    context,
                    scenarioPositions,
                    lineItemKind,
                    cancellationToken);
            LastMetrics = session.Metrics;
            return session;
        }
    }

    private sealed record LogicalCell(
        string Kind,
        string Value,
        string NumberFormat)
    {
        internal static LogicalCell Blank { get; } =
            new("B", string.Empty, "General");
    }

    private sealed record BaselineSnapshot(
        string PackageSha256,
        string LogicalFingerprint,
        string Dimension,
        int Step41SheetCount,
        long DataRows,
        IReadOnlyList<string> Headers,
        IReadOnlyList<double> Widths,
        IReadOnlyList<bool> BestFit,
        IReadOnlyList<string> NumberFormats);

    private sealed record BaselineRun(
        int Number,
        double ElapsedMilliseconds,
        long OutputBytes,
        string PackageSha256,
        string LogicalFingerprint,
        long TypedPageCalls,
        long ReaderDataCommands,
        BaselineSnapshot Snapshot);

    private sealed record CandidateRun(
        int Number,
        double ElapsedMilliseconds,
        long OutputBytes,
        long PeakWorkingSetBytes,
        long TypedPageCalls,
        WorkpaperStep41PreparedSessionMetrics Metrics,
        BaselineSnapshot Snapshot);
}
