using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using JET.Tests.TestInfrastructure;
using Xunit;
using Xunit.v3;

namespace JET.Tests.Infrastructure;

internal static class PrescreenMonthlyAggregationBenchmarkAvailability
{
    internal const string EnabledVariable = "JET_PRESCREEN_MONTHLY_BENCHMARK";
    internal const string OutputVariable = "JET_PRESCREEN_MONTHLY_BENCHMARK_OUTPUT";

    public static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable(EnabledVariable),
            "1",
            StringComparison.Ordinal);

    internal const string SkipReason =
        "未設定 JET_PRESCREEN_MONTHLY_BENCHMARK=1；未執行 500 萬列 DuckDB prescreen 月份聚合 benchmark。";
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class PrescreenMonthlyAggregationBenchmarkFactAttribute : FactAttribute, ITraitAttribute
{
    public PrescreenMonthlyAggregationBenchmarkFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = PrescreenMonthlyAggregationBenchmarkAvailability.SkipReason;
        SkipType = typeof(PrescreenMonthlyAggregationBenchmarkAvailability);
        SkipUnless = nameof(PrescreenMonthlyAggregationBenchmarkAvailability.IsEnabled);
    }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        TestProfileTraits.Scale;
}

/// <summary>
/// S2a 熱圖前置量測。大型 DuckDB 測試只在明示 opt-in 時執行；fixture 一律建立在
/// system temp 下並於場末刪除。Baseline 是 production <c>PrescreenRunHandler</c>
/// 完整 action；兩個候選都量「同一 action + 月份聚合與 bounded materialization」。
/// </summary>
public sealed class PrescreenMonthlyAggregationBenchmarkTests(ITestOutputHelper output)
{
    private const long SmallScaleRows = 20_000;
    private const long FirstStageRows = 5_000_000;
    private const long FullScaleRows = 20_000_000;
    private const int MeasurementRuns = 3;
    private const int ExpectedTargetColumnCount = 23;
    private const int ExpectedMonthCount = 12;
    private const int ExpectedRuleCount = 13;
    private const int MoneyScale = 10_000;
    private const string ProjectId = "prescreen-monthly-benchmark";
    private const string PeriodStart = "2025-01-01";
    private const string PeriodEnd = "2025-12-31";
    private const string LastPeriodStart = "2025-12-31";

    private static readonly string[] OrderedRuleKeys =
        PrescreenRuleKeys.FilterableKeys
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        WriteIndented = true
    };

    [Fact]
    public async Task LocalProviderSmoke_SqliteAndDuckDb_ProducesEquivalentMonthlyCells()
    {
        Assert.Equal(ExpectedRuleCount, OrderedRuleKeys.Length);

        var sqlite = await MeasureLocalScaleAsync(
            "sqlite",
            SmallScaleRows,
            measurementRuns: 1,
            CancellationToken.None);
        var duckDb = await MeasureLocalScaleAsync(
            "duckdb",
            SmallScaleRows,
            measurementRuns: 1,
            CancellationToken.None);

        Assert.Equal(sqlite.SemanticFingerprint, duckDb.SemanticFingerprint);
        Assert.True(sqlite.CleanupVerified);
        Assert.True(duckDb.CleanupVerified);

        output.WriteLine(
            $"S2a local smoke: sqlite={CompactTimings(sqlite)}, duckdb={CompactTimings(duckDb)}, " +
            $"fingerprint={sqlite.SemanticFingerprint}");
    }

    [SqlServerFact]
    public async Task SqlServerSmoke_ProducesSameMonthlyCellsAsCanonicalPredicates()
    {
        var project = await TempSqlServerProject.TryCreateAsync()
            ?? throw new InvalidOperationException(SqlServerAvailability.SkipReason);
        var projectRoot = new TempProjectRoot();
        var projectRootPath = projectRoot.Path;
        ScaleMeasurement? measurement = null;
        var schemaCleanupVerified = false;

        try
        {
            var schemaPrefix = SqlServerProjectSchema.QualifierFor(project.ProjectId);
            var seedTimer = Stopwatch.StartNew();
            await SeedSqlServerAsync(
                project.Database,
                project.ProjectId,
                schemaPrefix,
                SmallScaleRows,
                CancellationToken.None);
            seedTimer.Stop();

            var harness = new MonthlyAggregationHarness(
                "sqlServer",
                SqlServerDialect.Instance,
                schemaPrefix,
                () => project.Database.CreateConnection(project.ProjectId));
            var handler = await CreateSqlServerActionHandlerAsync(
                project.Database,
                project.ProjectId,
                new JetProjectFolder(projectRootPath),
                CancellationToken.None);
            measurement = await MeasurePreparedScaleAsync(
                "sqlServer",
                SmallScaleRows,
                databaseBytes: null,
                seedTimer.Elapsed.TotalMilliseconds,
                targetColumnCount: ExpectedTargetColumnCount,
                harness,
                token => RunPrescreenActionAsync(handler, token),
                measurementRuns: 1,
                CancellationToken.None);
        }
        finally
        {
            await project.DisposeAsync();
            schemaCleanupVerified = await SqlServerSchemaIsAbsentAsync(
                project.Database,
                project.ProjectId,
                CancellationToken.None);
            projectRoot.Dispose();
        }

        Assert.NotNull(measurement);
        Assert.True(schemaCleanupVerified);
        Assert.False(Directory.Exists(projectRootPath));
        measurement = measurement! with
        {
            CleanupVerified = schemaCleanupVerified && !Directory.Exists(projectRootPath)
        };
        output.WriteLine(
            $"S2a SQL Server smoke: {CompactTimings(measurement!)}, " +
            $"fingerprint={measurement!.SemanticFingerprint}; schema cleanup verified.");
    }

    [PrescreenMonthlyAggregationBenchmarkFact]
    public async Task DuckDb_FiveMillionRows_ProducesD7GateEvidence()
    {
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            AppContext.BaseDirectory,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExpectedRuleCount, OrderedRuleKeys.Length);

        var startedUtc = DateTimeOffset.UtcNow;
        var firstStage = await MeasureLocalScaleAsync(
            "duckdb",
            FirstStageRows,
            MeasurementRuns,
            CancellationToken.None);

        ScaleMeasurement? fullScale = null;
        if (string.Equals(firstStage.Gate.Branch, "gray", StringComparison.Ordinal))
        {
            fullScale = await MeasureLocalScaleAsync(
                "duckdb",
                FullScaleRows,
                MeasurementRuns,
                CancellationToken.None);
        }

        var conclusion = FinalConclusion(firstStage, fullScale);
        var evidence = new BenchmarkEvidence(
            StartedUtc: startedUtc,
            CompletedUtc: DateTimeOffset.UtcNow,
            Environment: EnvironmentEvidence.Capture(),
            Method:
                "Warm-cache, one warm-up per strategy, balanced measured order " +
                "B/S/M → S/M/B → M/B/S. Candidate timings include the same complete " +
                "PrescreenRunHandler action plus bounded aggregation materialization.",
            FirstStage: firstStage,
            FullScale: fullScale,
            Conclusion: conclusion);

        Assert.True(firstStage.CleanupVerified);
        if (fullScale is not null)
        {
            Assert.True(fullScale.CleanupVerified);
        }

        var json = JsonSerializer.Serialize(evidence, EvidenceJsonOptions);
        output.WriteLine("S2A_BENCHMARK_JSON");
        output.WriteLine(json);
        await WriteEvidenceOutputIfRequestedAsync(json);
    }

    private async Task<ScaleMeasurement> MeasureLocalScaleAsync(
        string provider,
        long rowCount,
        int measurementRuns,
        CancellationToken cancellationToken)
    {
        var root = new TempProjectRoot();
        var rootPath = root.Path;
        ScaleMeasurement? measurement = null;

        try
        {
            var folder = new JetProjectFolder(rootPath);
            ILocalProjectDatabase database = provider switch
            {
                "sqlite" => new SqliteProjectDatabase(folder),
                "duckdb" => new DuckDbProjectDatabase(folder),
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
            };

            await database.EnsureCreatedAsync(ProjectId, cancellationToken);
            var seedTimer = Stopwatch.StartNew();
            await SeedLocalAsync(database, rowCount, cancellationToken);
            seedTimer.Stop();

            var databasePath = database.GetDatabasePath(ProjectId);
            var databaseBytes = new FileInfo(databasePath).Length;
            var targetColumnCount = await ReadLocalTargetColumnCountAsync(
                database,
                cancellationToken);
            var harness = new MonthlyAggregationHarness(
                provider,
                database.Dialect,
                schemaPrefix: string.Empty,
                () => database.CreateConnection(ProjectId));
            var handler = await CreateLocalActionHandlerAsync(
                folder,
                database,
                provider,
                cancellationToken);

            measurement = await MeasurePreparedScaleAsync(
                provider,
                rowCount,
                databaseBytes,
                seedTimer.Elapsed.TotalMilliseconds,
                targetColumnCount,
                harness,
                token => RunPrescreenActionAsync(handler, token),
                measurementRuns,
                cancellationToken);

            if (provider == "duckdb")
            {
                await ExecuteNonQueryAsync(
                    database.CreateConnection(ProjectId),
                    "CHECKPOINT;",
                    [],
                    cancellationToken);
                measurement = measurement with
                {
                    DatabaseBytes = new FileInfo(databasePath).Length
                };
            }
        }
        finally
        {
            root.Dispose();
        }

        Assert.NotNull(measurement);
        return measurement! with
        {
            CleanupVerified = !Directory.Exists(rootPath)
        };
    }

    private async Task<ScaleMeasurement> MeasurePreparedScaleAsync(
        string provider,
        long rowCount,
        long? databaseBytes,
        double seedMilliseconds,
        int targetColumnCount,
        MonthlyAggregationHarness harness,
        Func<CancellationToken, Task<JsonElement>> runAction,
        int measurementRuns,
        CancellationToken cancellationToken)
    {
        Assert.Equal(ExpectedTargetColumnCount, targetColumnCount);

        var warmBaseline = await runAction(cancellationToken);
        var warmSeparateBaseline = await runAction(cancellationToken);
        var warmSeparate = await harness.RunSeparateAsync(cancellationToken);
        var warmMultiBaseline = await runAction(cancellationToken);
        var warmMulti = await harness.RunMultiFlagAsync(cancellationToken);
        AssertSnapshotMatchesBaseline(rowCount, warmBaseline, warmSeparate);
        AssertSnapshotMatchesBaseline(rowCount, warmSeparateBaseline, warmSeparate);
        AssertSnapshotMatchesBaseline(rowCount, warmMultiBaseline, warmMulti);
        Assert.Equal(warmSeparate.Fingerprint, warmMulti.Fingerprint);

        var timings = new Dictionary<Strategy, List<double>>
        {
            [Strategy.Baseline] = [],
            [Strategy.Separate] = [],
            [Strategy.MultiFlag] = []
        };
        var schedules = new[]
        {
            new[] { Strategy.Baseline, Strategy.Separate, Strategy.MultiFlag },
            new[] { Strategy.Separate, Strategy.MultiFlag, Strategy.Baseline },
            new[] { Strategy.MultiFlag, Strategy.Baseline, Strategy.Separate }
        };
        var peakWorkingSetBefore = Process.GetCurrentProcess().PeakWorkingSet64;

        for (var run = 0; run < measurementRuns; run++)
        {
            foreach (var strategy in schedules[run % schedules.Length])
            {
                var timer = Stopwatch.StartNew();
                var baseline = await runAction(cancellationToken);
                AggregationSnapshot? snapshot = strategy switch
                {
                    Strategy.Baseline => null,
                    Strategy.Separate => await harness.RunSeparateAsync(cancellationToken),
                    Strategy.MultiFlag => await harness.RunMultiFlagAsync(cancellationToken),
                    _ => throw new ArgumentOutOfRangeException()
                };
                timer.Stop();

                if (snapshot is not null)
                {
                    AssertSnapshotMatchesBaseline(rowCount, baseline, snapshot);
                    Assert.Equal(warmSeparate.Fingerprint, snapshot.Fingerprint);
                }

                timings[strategy].Add(timer.Elapsed.TotalMilliseconds);
            }
        }

        Assert.All(timings.Values, values => Assert.Equal(measurementRuns, values.Count));
        var baselineRuns = StrategyRuns.Create(
            "currentPrescreenRunAction",
            timings[Strategy.Baseline]);
        var separateRuns = StrategyRuns.Create(
            "perRuleGroupByMonth",
            timings[Strategy.Separate]);
        var multiRuns = StrategyRuns.Create(
            "singleCommandMultiFlag",
            timings[Strategy.MultiFlag]);
        var gate = GateCalculation.For(rowCount, baselineRuns, separateRuns, multiRuns);

        return new ScaleMeasurement(
            Provider: provider,
            RowCount: rowCount,
            TargetColumnCount: targetColumnCount,
            DistinctVoucherCount: (rowCount + 3) / 4,
            MonthCount: ExpectedMonthCount,
            AccountCount: 5,
            PreparerCount: 1_000,
            DatabaseBytes: databaseBytes,
            SeedMilliseconds: seedMilliseconds,
            Baseline: baselineRuns,
            PerRule: separateRuns,
            MultiFlag: multiRuns,
            Gate: gate,
            SemanticFingerprint: warmSeparate.Fingerprint,
            PeakWorkingSetBeforeBytes: peakWorkingSetBefore,
            PeakWorkingSetAfterBytes: Process.GetCurrentProcess().PeakWorkingSet64,
            CleanupVerified: false);
    }

    private static async Task<JsonElement> RunPrescreenActionAsync(
        PrescreenRunHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(default, cancellationToken);
        return Assert.IsType<JsonElement>(result);
    }

    private static void AssertSnapshotMatchesBaseline(
        long rowCount,
        JsonElement baseline,
        AggregationSnapshot snapshot)
    {
        Assert.Equal(ExpectedMonthCount, snapshot.Months.Count);
        Assert.Equal(rowCount, snapshot.Months.Sum(month => month.Population));
        Assert.All(snapshot.Months, month => Assert.Equal(ExpectedRuleCount, month.Rules.Count));

        var expected = BaselineRuleCounts(baseline);
        var actual = snapshot.Months
            .SelectMany(month => month.Rules)
            .GroupBy(rule => rule.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(rule => rule.HitLines),
                StringComparer.Ordinal);
        Assert.Equal(
            expected.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            actual.OrderBy(pair => pair.Key, StringComparer.Ordinal));

        Assert.All(
            snapshot.Months.SelectMany(month => month.Rules),
            rule =>
            {
                Assert.InRange(rule.HitLines, 0, rowCount);
                Assert.InRange(rule.HitVouchers, 0, rule.HitLines);
            });
    }

    private static IReadOnlyDictionary<string, long> BaselineRuleCounts(
        JsonElement result) =>
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [PrescreenRuleKeys.PostPeriodApproval] = Count(result, "postPeriodApproval"),
            [PrescreenRuleKeys.SuspiciousKeywords] = Count(result, "suspiciousKeywords"),
            [PrescreenRuleKeys.UnexpectedAccountPair] = Count(result, "unexpectedAccountPair"),
            [PrescreenRuleKeys.TrailingZeros] = Count(result, "trailingZeros"),
            [PrescreenRuleKeys.WeekendPosting] =
                Count(result, "weekendActivity", "postingCount"),
            [PrescreenRuleKeys.WeekendApproval] =
                Count(result, "weekendActivity", "approvalCount"),
            [PrescreenRuleKeys.HolidayPosting] =
                Count(result, "holidayActivity", "postingCount"),
            [PrescreenRuleKeys.HolidayApproval] =
                Count(result, "holidayActivity", "approvalCount"),
            [PrescreenRuleKeys.BlankDescription] = Count(result, "blankDescription"),
            [PrescreenRuleKeys.BackdatedPosting] = Count(result, "backdatedPosting"),
            [PrescreenRuleKeys.NonAuthorizedPreparer] =
                Count(result, "nonAuthorizedPreparer"),
            [PrescreenRuleKeys.LowFrequencyPreparer] =
                Count(result, "lowFrequencyPreparer"),
            [PrescreenRuleKeys.LowFrequencyAccount] =
                Count(result, "lowFrequencyAccount")
        };

    private static long Count(JsonElement result, string objectName) =>
        Count(result, objectName, "count");

    private static long Count(JsonElement result, string objectName, string fieldName)
    {
        var value = result.GetProperty(objectName).GetProperty(fieldName);
        return value.ValueKind == JsonValueKind.Null ? 0 : value.GetInt64();
    }

    private static async Task<PrescreenRunHandler> CreateLocalActionHandlerAsync(
        JetProjectFolder folder,
        ILocalProjectDatabase database,
        string provider,
        CancellationToken cancellationToken)
    {
        var projectStore = new JsonFileProjectStore(folder);
        await projectStore.SaveAsync(
            BenchmarkProject(ProjectId, provider),
            cancellationToken);
        var mappingStore = new LocalMappingStateStore(database);
        await mappingStore.SaveAsync(
            ProjectId,
            BenchmarkGlMapping(),
            cancellationToken);
        var runStore = new LocalRuleRunStore(database);
        await SeedEligibleValidationAsync(runStore, ProjectId, cancellationToken);
        var session = new ProjectSession();
        session.Enter(ProjectId);

        return new PrescreenRunHandler(
            new LocalPrescreenRunRepository(database),
            mappingStore,
            new LocalCalendarStore(database),
            new LocalAccountMappingRepository(database),
            new LocalAuthorizedPreparerRepository(database),
            runStore,
            projectStore,
            session);
    }

    private static async Task<PrescreenRunHandler> CreateSqlServerActionHandlerAsync(
        SqlServerProjectDatabase database,
        string projectId,
        JetProjectFolder folder,
        CancellationToken cancellationToken)
    {
        var projectStore = new JsonFileProjectStore(folder);
        await projectStore.CreateAsync(
            BenchmarkProject(projectId, ProjectDocument.SqlServerDatabaseProvider),
            cancellationToken);
        var mappingStore = new SqlServerMappingStateStore(database);
        await mappingStore.SaveAsync(
            projectId,
            BenchmarkGlMapping(),
            cancellationToken);
        var runStore = new SqlServerRuleRunStore(database);
        await SeedEligibleValidationAsync(runStore, projectId, cancellationToken);
        var session = new ProjectSession();
        session.Enter(projectId);

        return new PrescreenRunHandler(
            new SqlServerPrescreenRunRepository(database),
            mappingStore,
            new SqlServerCalendarStore(database),
            new SqlServerAccountMappingRepository(database),
            new SqlServerAuthorizedPreparerRepository(database),
            runStore,
            projectStore,
            session);
    }

    private static Task SeedEligibleValidationAsync(
        IRuleRunStore runStore,
        string projectId,
        CancellationToken cancellationToken)
    {
        var generatedUtc = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        return runStore.SaveAsync(
            projectId,
            new RuleRunRecord(
                "benchmark-validation",
                RuleRunKinds.Validate,
                generatedUtc,
                CurrentValidationSummaryTestData.Create(
                    "benchmark-validation",
                    generatedUtc)),
            cancellationToken);
    }

    private static ProjectDocument BenchmarkProject(string projectId, string provider) =>
        new(
            ProjectId: projectId,
            ProjectCode: "S2A-BENCHMARK",
            EntityName: "S2a synthetic benchmark",
            OperatorId: "benchmark",
            PeriodStart,
            PeriodEnd,
            LastAccountingPeriodDate: LastPeriodStart,
            MoneyScale,
            ProjectDocument.DefaultRoundingMode,
            CreatedUtc: DateTimeOffset.Parse("2026-07-30T00:00:00+08:00"),
            CurrentStep: 3,
            ProjectDocument.CurrentSchemaVersion,
            DatabaseProvider: provider,
            RocDateEnabled: false,
            NonWorkingDays: [0, 6],
            SampleSeed: 20_260_730);

    private static CommittedMapping BenchmarkGlMapping() =>
        new(
            DatasetKind.Gl,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.DocDate] = "approval_date",
                [GlMappingKeys.VoucherDate] = "voucher_date",
                [GlMappingKeys.CreateBy] = "created_by"
            },
            GlAmountModeNames.Signed,
            "s2a-gl",
            DateTimeOffset.Parse("2026-07-30T00:00:00+08:00"));

    private static async Task<bool> SqlServerSchemaIsAbsentAsync(
        SqlServerProjectDatabase database,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.schemas WHERE name = @schema;";
        command.AddWithValue("@schema", SqlServerProjectSchema.For(projectId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 0;
    }

    private static async Task SeedLocalAsync(
        ILocalProjectDatabase database,
        long rowCount,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection(ProjectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.AddWithValue("@rowCount", rowCount);
        command.CommandText = database.Dialect.ProviderName == "duckdb"
            ? DuckDbSeedSql
            : SqliteSeedSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedSqlServerAsync(
        SqlServerProjectDatabase database,
        string projectId,
        string schemaPrefix,
        long rowCount,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.AddWithValue("@rowCount", rowCount);
        command.CommandText = SqlServerSeedSql(schemaPrefix);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ReadLocalTargetColumnCountAsync(
        ILocalProjectDatabase database,
        CancellationToken cancellationToken)
    {
        var sql = database.Dialect.ProviderName == "duckdb"
            ? "SELECT COUNT(*) FROM information_schema.columns " +
              "WHERE table_schema = 'main' AND table_name = 'target_gl_entry';"
            : "SELECT COUNT(*) FROM pragma_table_info('target_gl_entry');";
        await using var connection = database.CreateConnection(ProjectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        string sql,
        IReadOnlyList<FilterSqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        await using (connection)
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 0;
            command.CommandText = sql;
            Bind(command, parameters);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string CompactTimings(ScaleMeasurement result) =>
        $"baseline={result.Baseline.MedianMilliseconds:N1}ms, " +
        $"perRule={result.PerRule.MedianMilliseconds:N1}ms, " +
        $"multi={result.MultiFlag.MedianMilliseconds:N1}ms";

    private static BenchmarkConclusion FinalConclusion(
        ScaleMeasurement firstStage,
        ScaleMeasurement? fullScale)
    {
        if (fullScale is not null)
        {
            return new BenchmarkConclusion(
                Verdict: fullScale.Gate.D4Pass ? "pass" : "degrade",
                Source: "20,000,000-row measured",
                SelectedStrategy: fullScale.Gate.SelectedStrategy,
                Note:
                    "500 萬列落在 D7 灰帶，已依規格補跑 2,000 萬列；D4 結論以全尺度實測為準。");
        }

        return firstStage.Gate.Branch switch
        {
            "pass" => new BenchmarkConclusion(
                Verdict: "pass",
                Source: "5,000,000-row extrapolation",
                SelectedStrategy: firstStage.Gate.SelectedStrategy,
                Note: "D4 結論來自 500 萬列外推，未經全尺度實測。"),
            "degrade" => new BenchmarkConclusion(
                Verdict: "degrade",
                Source: "5,000,000-row measured threshold breach",
                SelectedStrategy: firstStage.Gate.SelectedStrategy,
                Note: "小規模已超過 D4 門檻，依 D7 不補跑全尺度。"),
            _ => throw new InvalidOperationException(
                "D7 灰帶必須已有 2,000 萬列結果才能產生結論。")
        };
    }

    private async Task WriteEvidenceOutputIfRequestedAsync(string json)
    {
        var outputPath = Environment.GetEnvironmentVariable(
            PrescreenMonthlyAggregationBenchmarkAvailability.OutputVariable);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, json, new UTF8Encoding(false));
        output.WriteLine($"S2A_BENCHMARK_OUTPUT={fullPath}");
    }

    private sealed class MonthlyAggregationHarness
    {
        private readonly string _provider;
        private readonly IProviderSqlDialect _dialect;
        private readonly string _schemaPrefix;
        private readonly Func<DbConnection> _connectionFactory;
        private readonly FilterRuleContext _context = new(
            MoneyScale,
            LastPeriodStart,
            PeriodStart,
            PeriodEnd,
            [0, 6],
            GlPopulationScope.AuditPeriod);
        private readonly IReadOnlyList<CompiledRule> _rules;

        public MonthlyAggregationHarness(
            string provider,
            IProviderSqlDialect dialect,
            string schemaPrefix,
            Func<DbConnection> connectionFactory)
        {
            _provider = provider;
            _dialect = dialect;
            _schemaPrefix = schemaPrefix;
            _connectionFactory = connectionFactory;
            _rules = CompileRules();
            Assert.Equal(ExpectedRuleCount, _rules.Count);
        }

        public async Task<AggregationSnapshot> RunSeparateAsync(
            CancellationToken cancellationToken)
        {
            await using var connection = _connectionFactory();
            await connection.OpenAsync(cancellationToken);
            var months = await ReadMonthPopulationAsync(connection, cancellationToken);
            var cells = months.Keys.ToDictionary(
                month => month,
                month => OrderedRuleKeys.ToDictionary(
                    key => key,
                    key => new RuleCell(key, 0, 0),
                    StringComparer.Ordinal),
                StringComparer.Ordinal);

            foreach (var rule in _rules)
            {
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 0;
                command.CommandText =
                    $"SELECT {MonthExpression}, {CountAll}, {CountDistinctDocument} " +
                    $"FROM {Table("target_gl_entry")} g " +
                    $"WHERE {ValidationProcedures.PeriodBoundsWith("g.")} AND ({rule.Sql}) " +
                    $"GROUP BY {MonthExpression} ORDER BY {MonthExpression};";
                Bind(command, CommonParameters.Concat(rule.Parameters));

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var month = reader.GetString(0);
                    cells[month][rule.Key] = new RuleCell(
                        rule.Key,
                        ToInt64(reader.GetValue(1)),
                        ToInt64(reader.GetValue(2)));
                }
            }

            return AggregationSnapshot.Create(months, cells);
        }

        public async Task<AggregationSnapshot> RunMultiFlagAsync(
            CancellationToken cancellationToken)
        {
            await using var connection = _connectionFactory();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 0;

            var flags = string.Join(
                ",\n       ",
                _rules.Select((rule, index) =>
                    $"CASE WHEN ({rule.Sql}) THEN 1 ELSE 0 END AS f{index:00}"));
            var voucherAggregates = string.Join(
                ",\n       ",
                _rules.Select((_, index) =>
                    $"{SumBig($"f{index:00}")} AS hit_lines_{index:00}, " +
                    $"CASE WHEN document_number IS NULL THEN 0 ELSE MAX(f{index:00}) END AS hit_voucher_{index:00}"));
            var monthlyAggregates = string.Join(
                ",\n       ",
                _rules.Select((_, index) =>
                    $"{SumBig($"hit_lines_{index:00}")} AS hit_lines_{index:00}, " +
                    $"{SumBig($"hit_voucher_{index:00}")} AS hit_vouchers_{index:00}"));

            command.CommandText =
                $"""
                WITH flagged AS (
                    SELECT {MonthExpression} AS month_key,
                           g.document_number,
                           {flags}
                    FROM {Table("target_gl_entry")} g
                    WHERE {ValidationProcedures.PeriodBoundsWith("g.")}
                ),
                voucher_flags AS (
                    SELECT month_key,
                           document_number,
                           {CountAll} AS row_count,
                           {voucherAggregates}
                    FROM flagged
                    GROUP BY month_key, document_number
                )
                SELECT month_key,
                       {SumBig("row_count")} AS population,
                       {monthlyAggregates}
                FROM voucher_flags
                GROUP BY month_key
                ORDER BY month_key;
                """;
            Bind(command, CommonParameters.Concat(_rules.SelectMany(rule => rule.Parameters)));

            var populations = new Dictionary<string, long>(StringComparer.Ordinal);
            var cells = new Dictionary<string, Dictionary<string, RuleCell>>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var month = reader.GetString(0);
                populations[month] = ToInt64(reader.GetValue(1));
                var monthCells = new Dictionary<string, RuleCell>(StringComparer.Ordinal);
                for (var index = 0; index < _rules.Count; index++)
                {
                    monthCells[_rules[index].Key] = new RuleCell(
                        _rules[index].Key,
                        ToInt64(reader.GetValue(2 + (index * 2))),
                        ToInt64(reader.GetValue(3 + (index * 2))));
                }

                cells[month] = monthCells;
            }

            return AggregationSnapshot.Create(populations, cells);
        }

        private async Task<Dictionary<string, long>> ReadMonthPopulationAsync(
            DbConnection connection,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 0;
            command.CommandText =
                $"SELECT {MonthExpression}, {CountAll} " +
                $"FROM {Table("target_gl_entry")} g " +
                $"WHERE {ValidationProcedures.PeriodBoundsWith("g.")} " +
                $"GROUP BY {MonthExpression} ORDER BY {MonthExpression};";
            Bind(command, CommonParameters);

            var months = new Dictionary<string, long>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                months[reader.GetString(0)] = ToInt64(reader.GetValue(1));
            }

            return months;
        }

        private IReadOnlyList<CompiledRule> CompileRules()
        {
            var predicates = new GlRulePredicates(_dialect, GlPopulationScopeSql.Predicate);
            var builder = new GlFilterWhereBuilder(_dialect, predicates);
            var rules = new List<CompiledRule>(OrderedRuleKeys.Length);

            for (var index = 0; index < OrderedRuleKeys.Length; index++)
            {
                var key = OrderedRuleKeys[index];
                var plan = builder.BuildPlan(
                    LocalPrescreenPageRepository.PrescreenScenario(key),
                    _context,
                    TrailingZeroThreshold.UnitModulus(
                        TrailingZeroThreshold.DefaultZerosThreshold),
                    _schemaPrefix,
                    includePopulationParameters: false);
                var replacements = plan.Parameters.ToDictionary(
                    parameter => parameter.Name,
                    parameter => $"@r{index:00}{parameter.Name.TrimStart('@', '$')}",
                    StringComparer.Ordinal);
                var sql = Regex.Replace(
                    plan.Sql,
                    @"(?<![A-Za-z0-9_])@p\d+(?![A-Za-z0-9_])",
                    match => replacements[match.Value],
                    RegexOptions.CultureInvariant);
                var parameters = plan.Parameters
                    .Select(parameter => new FilterSqlParameter(
                        replacements[parameter.Name],
                        parameter.Value))
                    .ToArray();
                rules.Add(new CompiledRule(key, sql, parameters));
            }

            return rules;
        }

        private string MonthExpression =>
            _provider == "sqlServer"
                ? "LEFT(g.post_date, 7)"
                : "SUBSTR(g.post_date, 1, 7)";

        private string CountAll =>
            _provider == "sqlServer" ? "COUNT_BIG(*)" : "COUNT(*)";

        private string CountDistinctDocument =>
            _provider == "sqlServer"
                ? "COUNT_BIG(DISTINCT g.document_number)"
                : "COUNT(DISTINCT g.document_number)";

        private string SumBig(string expression) =>
            _provider == "sqlServer"
                ? $"SUM(CAST({expression} AS BIGINT))"
                : $"SUM({expression})";

        private string Table(string name) => _schemaPrefix + name;

        private static IReadOnlyList<FilterSqlParameter> CommonParameters =>
        [
            new("@periodStart", PeriodStart),
            new("@periodEnd", PeriodEnd)
        ];
    }

    private static void Bind(
        DbCommand command,
        IEnumerable<FilterSqlParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            command.AddWithValue(parameter.Name, parameter.Value);
        }
    }

    private static long ToInt64(object value) =>
        value is BigInteger bigInteger
            ? checked((long)bigInteger)
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private sealed record CompiledRule(
        string Key,
        string Sql,
        IReadOnlyList<FilterSqlParameter> Parameters);

    private sealed record RuleCell(string Key, long HitLines, long HitVouchers);

    private sealed record MonthCell(
        string Month,
        long Population,
        IReadOnlyList<RuleCell> Rules);

    private sealed record AggregationSnapshot(
        IReadOnlyList<MonthCell> Months,
        string Fingerprint)
    {
        public static AggregationSnapshot Create(
            IReadOnlyDictionary<string, long> populations,
            IReadOnlyDictionary<string, Dictionary<string, RuleCell>> cells)
        {
            var months = populations
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new MonthCell(
                    pair.Key,
                    pair.Value,
                    cells[pair.Key]
                        .OrderBy(rule => rule.Key, StringComparer.Ordinal)
                        .Select(rule => rule.Value)
                        .ToArray()))
                .ToArray();
            var canonical = JsonSerializer.Serialize(months);
            var fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            return new AggregationSnapshot(months, fingerprint);
        }
    }

    private enum Strategy
    {
        Baseline,
        Separate,
        MultiFlag
    }

    private sealed record StrategyRuns(
        string Name,
        IReadOnlyList<double> RunMilliseconds,
        double MedianMilliseconds)
    {
        public static StrategyRuns Create(string name, IReadOnlyList<double> values)
        {
            var ordered = values.OrderBy(value => value).ToArray();
            return new StrategyRuns(name, values.ToArray(), ordered[ordered.Length / 2]);
        }
    }

    private sealed record GateCalculation(
        string SelectedStrategy,
        double BaselineMedianMilliseconds,
        double CandidateMedianMilliseconds,
        double AbsoluteDeltaMilliseconds,
        double IncreasePercent,
        double ExtrapolatedTwentyMillionDeltaMilliseconds,
        string Branch,
        bool D4Pass)
    {
        public static GateCalculation For(
            long rowCount,
            StrategyRuns baseline,
            StrategyRuns separate,
            StrategyRuns multi)
        {
            var candidate = separate.MedianMilliseconds <= multi.MedianMilliseconds
                ? separate
                : multi;
            var delta = Math.Max(0, candidate.MedianMilliseconds - baseline.MedianMilliseconds);
            var percent = baseline.MedianMilliseconds <= 0
                ? double.PositiveInfinity
                : delta * 100d / baseline.MedianMilliseconds;
            var extrapolated = rowCount == FirstStageRows ? delta * 4d : delta;
            string branch;
            bool d4Pass;

            if (rowCount == FirstStageRows)
            {
                branch = percent > 25d || extrapolated > 90_000d
                    ? "degrade"
                    : percent <= 12.5d && extrapolated <= 45_000d
                        ? "pass"
                        : "gray";
                d4Pass = branch == "pass";
            }
            else
            {
                d4Pass = percent <= 25d && delta <= 90_000d;
                branch = d4Pass ? "pass" : "degrade";
            }

            return new GateCalculation(
                candidate.Name,
                baseline.MedianMilliseconds,
                candidate.MedianMilliseconds,
                delta,
                percent,
                extrapolated,
                branch,
                d4Pass);
        }
    }

    private sealed record ScaleMeasurement(
        string Provider,
        long RowCount,
        int TargetColumnCount,
        long DistinctVoucherCount,
        int MonthCount,
        int AccountCount,
        int PreparerCount,
        long? DatabaseBytes,
        double SeedMilliseconds,
        StrategyRuns Baseline,
        StrategyRuns PerRule,
        StrategyRuns MultiFlag,
        GateCalculation Gate,
        string SemanticFingerprint,
        long PeakWorkingSetBeforeBytes,
        long PeakWorkingSetAfterBytes,
        bool CleanupVerified);

    private sealed record BenchmarkConclusion(
        string Verdict,
        string Source,
        string SelectedStrategy,
        string Note);

    private sealed record BenchmarkEvidence(
        DateTimeOffset StartedUtc,
        DateTimeOffset CompletedUtc,
        EnvironmentEvidence Environment,
        string Method,
        ScaleMeasurement FirstStage,
        ScaleMeasurement? FullScale,
        BenchmarkConclusion Conclusion);

    private sealed record EnvironmentEvidence(
        string OsDescription,
        string ProcessArchitecture,
        string FrameworkDescription,
        int ProcessorCount,
        bool ServerGc,
        string TempDrive,
        long TempDriveFreeBytes)
    {
        public static EnvironmentEvidence Capture()
        {
            var tempRoot = Path.GetPathRoot(Path.GetTempPath()) ?? string.Empty;
            var drive = new DriveInfo(tempRoot);
            return new EnvironmentEvidence(
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription,
                Environment.ProcessorCount,
                GCSettings.IsServerGC,
                drive.Name,
                drive.AvailableFreeSpace);
        }
    }

    private const string DuckDbSeedSql =
        """
        BEGIN TRANSACTION;
        DELETE FROM target_gl_entry;
        DELETE FROM target_account_mapping;
        DELETE FROM target_authorized_preparer;
        DELETE FROM staging_calendar_raw_day;
        DELETE FROM import_batch WHERE batch_id = 's2a-map';

        INSERT INTO import_batch
            (batch_id, dataset_kind, source_file_path, source_file_name,
             imported_utc, row_count, columns_json)
        VALUES
            ('s2a-map', 'account_mapping', 's2a-map.csv', 's2a-map.csv',
             '2026-07-30T00:00:00.0000000+08:00', 5,
             '["account_code","account_name","standardized_category"]');

        INSERT INTO target_account_mapping
            (batch_id, source_row_number, account_code, account_name, standardized_category)
        VALUES
            ('s2a-map', 1, '1000', 'Cash', 'Cash'),
            ('s2a-map', 2, '2000', 'Receivables', 'Receivables'),
            ('s2a-map', 3, '4000', 'Revenue', 'Revenue'),
            ('s2a-map', 4, '5000', 'Expense A', 'Others'),
            ('s2a-map', 5, '6000', 'Expense B', 'Others');

        INSERT INTO target_authorized_preparer (name)
        SELECT printf('P%04d', i)
        FROM range(0, 500) AS preparers(i);

        INSERT INTO staging_calendar_raw_day (day_type, date, day_name)
        VALUES
            ('holiday', '2025-01-01', 'New Year'),
            ('holiday', '2025-02-28', 'Holiday'),
            ('holiday', '2025-05-01', 'Holiday'),
            ('holiday', '2025-10-10', 'Holiday'),
            ('makeup', '2025-02-08', 'Make-up day');

        INSERT INTO target_gl_entry
            (batch_id, source_row_number, document_number, line_item,
             line_item_numeric_sort_key, post_date, approval_date, voucher_date,
             account_code, account_name, document_description, source_module,
             created_by, approved_by, is_manual, is_effective, amount_scaled,
             debit_amount_scaled, credit_amount_scaled, dr_cr)
        SELECT
            's2a-gl',
            i,
            printf('JV%09d', CAST(floor((i - 1) / 4) AS BIGINT)),
            CAST(((i - 1) % 4) + 1 AS VARCHAR),
            printf('%04d', ((i - 1) % 4) + 1),
            strftime(DATE '2025-01-01' + CAST((i - 1) % 365 AS INTEGER), '%Y-%m-%d'),
            CASE
                WHEN i % 17 = 0 THEN '2025-12-31'
                ELSE strftime(DATE '2025-01-01' + CAST((i - 1) % 365 AS INTEGER), '%Y-%m-%d')
            END,
            CASE
                WHEN i % 19 = 0
                    THEN strftime(DATE '2025-01-01' + CAST(((i - 1) % 364) + 1 AS INTEGER), '%Y-%m-%d')
                ELSE strftime(DATE '2025-01-01' + CAST((i - 1) % 365 AS INTEGER), '%Y-%m-%d')
            END,
            CASE ((i - 1) % 4)
                WHEN 0 THEN '4000'
                WHEN 1 THEN CASE WHEN floor((i - 1) / 4) % 10 = 0 THEN '5000' ELSE '1000' END
                WHEN 2 THEN '5000'
                ELSE '6000'
            END,
            CASE ((i - 1) % 4)
                WHEN 0 THEN 'Revenue'
                WHEN 1 THEN CASE WHEN floor((i - 1) / 4) % 10 = 0 THEN 'Expense A' ELSE 'Cash' END
                WHEN 2 THEN 'Expense A'
                ELSE 'Expense B'
            END,
            CASE
                WHEN i % 97 = 0 THEN ''
                WHEN i % 23 = 0 THEN 'ADJ synthetic entry'
                ELSE 'ordinary synthetic entry'
            END,
            CASE WHEN i % 3 = 0 THEN 'MANUAL' ELSE 'GL' END,
            printf('P%04d', (i - 1) % 1000),
            printf('A%03d', (i - 1) % 100),
            CASE WHEN i % 3 = 0 THEN 1 ELSE 0 END,
            1,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN -100000000 ELSE 100000000 END,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN 0 ELSE 100000000 END,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN 100000000 ELSE 0 END,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN 'CREDIT' ELSE 'DEBIT' END
        FROM range(1, @rowCount + 1) AS generated(i);

        COMMIT;
        ANALYZE;
        CHECKPOINT;
        """;

    private const string SqliteSeedSql =
        """
        BEGIN TRANSACTION;
        DELETE FROM target_gl_entry;
        DELETE FROM target_account_mapping;
        DELETE FROM target_authorized_preparer;
        DELETE FROM staging_calendar_raw_day;
        DELETE FROM import_batch WHERE batch_id = 's2a-map';

        INSERT INTO import_batch
            (batch_id, dataset_kind, source_file_path, source_file_name,
             imported_utc, row_count, columns_json)
        VALUES
            ('s2a-map', 'account_mapping', 's2a-map.csv', 's2a-map.csv',
             '2026-07-30T00:00:00.0000000+08:00', 5,
             '["account_code","account_name","standardized_category"]');

        INSERT INTO target_account_mapping
            (batch_id, source_row_number, account_code, account_name, standardized_category)
        VALUES
            ('s2a-map', 1, '1000', 'Cash', 'Cash'),
            ('s2a-map', 2, '2000', 'Receivables', 'Receivables'),
            ('s2a-map', 3, '4000', 'Revenue', 'Revenue'),
            ('s2a-map', 4, '5000', 'Expense A', 'Others'),
            ('s2a-map', 5, '6000', 'Expense B', 'Others');

        WITH RECURSIVE preparers(i) AS (
            SELECT 0
            UNION ALL
            SELECT i + 1 FROM preparers WHERE i < 499
        )
        INSERT INTO target_authorized_preparer (name)
        SELECT printf('P%04d', i) FROM preparers;

        INSERT INTO staging_calendar_raw_day (day_type, date, day_name)
        VALUES
            ('holiday', '2025-01-01', 'New Year'),
            ('holiday', '2025-02-28', 'Holiday'),
            ('holiday', '2025-05-01', 'Holiday'),
            ('holiday', '2025-10-10', 'Holiday'),
            ('makeup', '2025-02-08', 'Make-up day');

        WITH RECURSIVE generated(i) AS (
            SELECT 1
            UNION ALL
            SELECT i + 1 FROM generated WHERE i < @rowCount
        )
        INSERT INTO target_gl_entry
            (batch_id, source_row_number, document_number, line_item,
             line_item_numeric_sort_key, post_date, approval_date, voucher_date,
             account_code, account_name, document_description, source_module,
             created_by, approved_by, is_manual, is_effective, amount_scaled,
             debit_amount_scaled, credit_amount_scaled, dr_cr)
        SELECT
            's2a-gl',
            i,
            printf('JV%09d', CAST((i - 1) / 4 AS INTEGER)),
            CAST(((i - 1) % 4) + 1 AS TEXT),
            printf('%04d', ((i - 1) % 4) + 1),
            date('2025-01-01', printf('+%d days', (i - 1) % 365)),
            CASE
                WHEN i % 17 = 0 THEN '2025-12-31'
                ELSE date('2025-01-01', printf('+%d days', (i - 1) % 365))
            END,
            CASE
                WHEN i % 19 = 0
                    THEN date('2025-01-01', printf('+%d days', ((i - 1) % 364) + 1))
                ELSE date('2025-01-01', printf('+%d days', (i - 1) % 365))
            END,
            CASE ((i - 1) % 4)
                WHEN 0 THEN '4000'
                WHEN 1 THEN CASE WHEN CAST((i - 1) / 4 AS INTEGER) % 10 = 0 THEN '5000' ELSE '1000' END
                WHEN 2 THEN '5000'
                ELSE '6000'
            END,
            CASE ((i - 1) % 4)
                WHEN 0 THEN 'Revenue'
                WHEN 1 THEN CASE WHEN CAST((i - 1) / 4 AS INTEGER) % 10 = 0 THEN 'Expense A' ELSE 'Cash' END
                WHEN 2 THEN 'Expense A'
                ELSE 'Expense B'
            END,
            CASE
                WHEN i % 97 = 0 THEN ''
                WHEN i % 23 = 0 THEN 'ADJ synthetic entry'
                ELSE 'ordinary synthetic entry'
            END,
            CASE WHEN i % 3 = 0 THEN 'MANUAL' ELSE 'GL' END,
            printf('P%04d', (i - 1) % 1000),
            printf('A%03d', (i - 1) % 100),
            CASE WHEN i % 3 = 0 THEN 1 ELSE 0 END,
            1,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN -100000000 ELSE 100000000 END,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN 0 ELSE 100000000 END,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN 100000000 ELSE 0 END,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN 'CREDIT' ELSE 'DEBIT' END
        FROM generated;

        COMMIT;
        ANALYZE;
        PRAGMA wal_checkpoint(TRUNCATE);
        """;

    private static string SqlServerSeedSql(string schemaPrefix) =>
        $"""
        DELETE FROM {schemaPrefix}target_gl_entry;
        DELETE FROM {schemaPrefix}target_account_mapping;
        DELETE FROM {schemaPrefix}target_authorized_preparer;
        DELETE FROM {schemaPrefix}staging_calendar_raw_day;
        DELETE FROM {schemaPrefix}import_batch WHERE batch_id = N's2a-map';

        INSERT INTO {schemaPrefix}import_batch
            (batch_id, dataset_kind, source_file_path, source_file_name,
             imported_utc, row_count, columns_json)
        VALUES
            (N's2a-map', N'account_mapping', N's2a-map.csv', N's2a-map.csv',
             N'2026-07-30T00:00:00.0000000+08:00', 5,
             N'["account_code","account_name","standardized_category"]');

        INSERT INTO {schemaPrefix}target_account_mapping
            (batch_id, source_row_number, account_code, account_name, standardized_category)
        VALUES
            (N's2a-map', 1, N'1000', N'Cash', N'Cash'),
            (N's2a-map', 2, N'2000', N'Receivables', N'Receivables'),
            (N's2a-map', 3, N'4000', N'Revenue', N'Revenue'),
            (N's2a-map', 4, N'5000', N'Expense A', N'Others'),
            (N's2a-map', 5, N'6000', N'Expense B', N'Others');

        ;WITH preparers(i) AS (
            SELECT 0
            UNION ALL
            SELECT i + 1 FROM preparers WHERE i < 499
        )
        INSERT INTO {schemaPrefix}target_authorized_preparer (name)
        SELECT CONCAT(N'P', RIGHT(REPLICATE(N'0', 4) + CAST(i AS nvarchar(10)), 4))
        FROM preparers
        OPTION (MAXRECURSION 0);

        INSERT INTO {schemaPrefix}staging_calendar_raw_day (day_type, date, day_name)
        VALUES
            (N'holiday', N'2025-01-01', N'New Year'),
            (N'holiday', N'2025-02-28', N'Holiday'),
            (N'holiday', N'2025-05-01', N'Holiday'),
            (N'holiday', N'2025-10-10', N'Holiday'),
            (N'makeup', N'2025-02-08', N'Make-up day');

        ;WITH generated(i) AS (
            SELECT CAST(1 AS bigint)
            UNION ALL
            SELECT i + 1 FROM generated WHERE i < @rowCount
        )
        INSERT INTO {schemaPrefix}target_gl_entry
            (batch_id, source_row_number, document_number, line_item,
             line_item_numeric_sort_key, post_date, approval_date, voucher_date,
             account_code, account_name, document_description, source_module,
             created_by, approved_by, is_manual, is_effective, amount_scaled,
             debit_amount_scaled, credit_amount_scaled, dr_cr)
        SELECT
            N's2a-gl',
            i,
            CONCAT(N'JV', RIGHT(REPLICATE(N'0', 9) + CAST((i - 1) / 4 AS nvarchar(20)), 9)),
            CAST(((i - 1) % 4) + 1 AS nvarchar(10)),
            RIGHT(REPLICATE(N'0', 4) + CAST(((i - 1) % 4) + 1 AS nvarchar(10)), 4),
            CONVERT(nvarchar(10), DATEADD(day, (i - 1) % 365, CONVERT(date, '20250101')), 23),
            CASE
                WHEN i % 17 = 0 THEN N'2025-12-31'
                ELSE CONVERT(nvarchar(10), DATEADD(day, (i - 1) % 365, CONVERT(date, '20250101')), 23)
            END,
            CASE
                WHEN i % 19 = 0
                    THEN CONVERT(nvarchar(10), DATEADD(day, ((i - 1) % 364) + 1, CONVERT(date, '20250101')), 23)
                ELSE CONVERT(nvarchar(10), DATEADD(day, (i - 1) % 365, CONVERT(date, '20250101')), 23)
            END,
            CASE ((i - 1) % 4)
                WHEN 0 THEN N'4000'
                WHEN 1 THEN CASE WHEN ((i - 1) / 4) % 10 = 0 THEN N'5000' ELSE N'1000' END
                WHEN 2 THEN N'5000'
                ELSE N'6000'
            END,
            CASE ((i - 1) % 4)
                WHEN 0 THEN N'Revenue'
                WHEN 1 THEN CASE WHEN ((i - 1) / 4) % 10 = 0 THEN N'Expense A' ELSE N'Cash' END
                WHEN 2 THEN N'Expense A'
                ELSE N'Expense B'
            END,
            CASE
                WHEN i % 97 = 0 THEN N''
                WHEN i % 23 = 0 THEN N'ADJ synthetic entry'
                ELSE N'ordinary synthetic entry'
            END,
            CASE WHEN i % 3 = 0 THEN N'MANUAL' ELSE N'GL' END,
            CONCAT(N'P', RIGHT(REPLICATE(N'0', 4) + CAST((i - 1) % 1000 AS nvarchar(10)), 4)),
            CONCAT(N'A', RIGHT(REPLICATE(N'0', 3) + CAST((i - 1) % 100 AS nvarchar(10)), 3)),
            CASE WHEN i % 3 = 0 THEN 1 ELSE 0 END,
            1,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN -100000000 ELSE 100000000 END,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN 0 ELSE 100000000 END,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN 100000000 ELSE 0 END,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN N'CREDIT' ELSE N'DEBIT' END
        FROM generated
        OPTION (MAXRECURSION 0);

        UPDATE STATISTICS {schemaPrefix}target_gl_entry WITH FULLSCAN;
        """;
}
