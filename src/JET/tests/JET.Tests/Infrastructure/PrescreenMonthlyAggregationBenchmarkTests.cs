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
    // 2026-10-22 清理技術債：產品不再提供只給基準測試用的明示期間片段，基準自己寫這一句。
    private static string PeriodBoundsWith(string columnPrefix) =>
        $"({columnPrefix}gl_date >= @periodStart AND {columnPrefix}gl_date <= @periodEnd)";

    private const long SmallScaleRows = 20_000;
    private const long FirstStageRows = 5_000_000;
    private const long FullScaleRows = 20_000_000;
    private const int MeasurementRuns = 3;
    // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，gl 少了 voucher_date 欄。
    private const int ExpectedTargetColumnCount = 22;
    private const int ExpectedMonthCount = 12;
    // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，列層規則由 13 條減為 12 條。
    // 第四步照舊版拿掉編製分錄較少的人員與使用較少的科目（裁定 2026-10-29-27），列層規則再減為 10 條；第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad。
    private const int ExpectedRuleCount = 10;
    private const int MoneyScale = 10_000;
    // 尾數 0 的個數照 IDEA 版由 GL 算出（裁定 2026-11-05-25）。種子資料每列金額都是 10,000.00，借方的列占一半，
    // 借方合計除以列數的平均是 5,000，共 4 位數，所以是 3 個 0。預篩選讀 GL 控制總數算出同一個個數，月份彙總用這個常數，兩邊才比得起來。
    // 種子資料直接寫進 gl，因此照正式投影的算法另寫一列控制總數。第一次失敗：Public 收據 20261105-084802022-bb026abaa79c492799abea536c518413。
    private const int BenchmarkZerosThreshold = 3;
    private const string ProjectId = "prescreen-monthly-benchmark";
    private const string PeriodStart = "2025-01-01";
    private const string PeriodEnd = "2025-12-31";
    private const string PrepStartDate = "2025-12-31";

    private static readonly string[] OrderedRuleKeys =
        PrescreenRuleKeys.FilterableKeys
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        WriteIndented = true
    };

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

        // 月份彙總用 BenchmarkZerosThreshold，預篩選由 GL 控制總數算出 0 的個數，兩邊要是同一個個數，下面的計數比較才有意義。
        Assert.Equal(
            BenchmarkZerosThreshold,
            baseline.GetProperty("trailingZeros").GetProperty("zerosThreshold").GetInt32());
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
            [PrescreenRuleKeys.PrepPeriodApproval] = Count(result, "prepPeriodApproval"),
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
            [PrescreenRuleKeys.NonAuthorizedPreparer] =
                Count(result, "nonAuthorizedPreparer")
            // 編製分錄較少的人員與使用較少的科目照舊版拿掉（裁定 2026-10-29-27），這裡跟著少兩條；第一次失敗是建置失敗：收據 20261030-163008036-bb2e987946cc4c72a351610e661109c5。
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
        var resultStore = new LocalResultStore(database);
        await SeedEligibleValidationAsync(resultStore, ProjectId, cancellationToken);
        // handler 從作用中案件的資料庫組取 repository，把這些物件放進該種資料庫組後再進入 session。
        var session = new ProjectSession();
        session.Enter(
            ProjectId,
            // 第四步的原因要寫分類名稱，所以預篩選多讀科目分類設定，取原因裡的分類名稱，資料庫組一併放進去。
            // 第一次失敗：Focused 收據 20261023-033416473-0f25ea4f0cd94c14b96c7607c741f7e7（處理器讀分類設定時遇到空參考）。
            TestProjectRepositories.Unconfigured(provider) with
            {
                PrescreenFacts = new LocalPrescreenRunRepository(database),
                MappingStates = mappingStore,
                Calendar = new LocalCalendarStore(database),
                AccountMappings = new LocalAccountMappingRepository(database),
                AccountCategories = new LocalAccountCategoryStore(database),
                AuthorizedPreparers = new LocalAuthorizedPreparerRepository(database),
                Results = resultStore,
            });

        return new PrescreenRunHandler(projectStore, session);
    }

    private static Task SeedEligibleValidationAsync(
        IResultStore resultStore,
        string projectId,
        CancellationToken cancellationToken)
    {
        var generatedUtc = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        return resultStore.SaveAsync(
            projectId,
            new ResultRecord(
                "benchmark-validation",
                ResultKind.Validation,
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
            ClientName: "S2a synthetic benchmark",
            OperatorId: "benchmark",
            PeriodStart,
            PeriodEnd,
            LastAccountingPeriodDate: PrepStartDate,
            MoneyScale,
            ProjectDocument.DefaultRoundingMode,
            CreatedUtc: DateTimeOffset.Parse("2026-07-30T00:00:00+08:00"),
            CurrentStep: 3,
            ProjectDocument.CurrentSchemaVersion,
            DatabaseProvider: provider,
            RocDateEnabled: false,
            NonWorkingDays: [0, 6],
            SampleSeed: 20_260_730,
            // 目前版本建案一定寫入 INF 抽樣種子與版本；缺欄位的文件會被當成舊版案件拒絕。
            SampleSeedVersion: JetAuditProgram.CurrentInfSamplingAlgorithmVersion);

    // 原本只寫傳票核准日與建立人員兩欄，因為預篩選只看這兩欄有沒有配對。後來
    // 能用的欄位改由整份欄位目錄判斷，必填欄沒有來源欄就算沒配對，總帳日期這一類規則因此變成不適用。正式流程確認配對時
    // 必填欄一定有來源欄，所以補上必填欄，讓配對和真實案件一樣；資料直接寫進資料庫，來源欄名稱不影響查詢。
    // 第一次失敗：Focused 收據 20261023-033930413-87a4245a648e47288e7acf0740bcd07a（假日過帳的回應是 0 筆，月份彙總是 220 筆）。
    private static CommittedMapping BenchmarkGlMapping() =>
        new(
            DatasetKind.Gl,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.VoucherNumber] = "voucher_number",
                [GlMappingKeys.LineItem] = "line_item",
                [GlMappingKeys.GlDate] = "gl_date",
                [GlMappingKeys.AccountNumber] = "account_number",
                [GlMappingKeys.AccountName] = "account_name",
                [GlMappingKeys.Description] = "description",
                [GlMappingKeys.Amount] = "amount",
                [GlMappingKeys.ApprovalDate] = "approval_date",
                [GlMappingKeys.CreatedBy] = "created_by"
            },
            GlAmountModeNames.Signed,
            "s2a-gl",
            DateTimeOffset.Parse("2026-07-30T00:00:00+08:00"));

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
        command.CommandText = SqliteSeedSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ReadLocalTargetColumnCountAsync(
        ILocalProjectDatabase database,
        CancellationToken cancellationToken)
    {
        var sql = "SELECT COUNT(*) FROM pragma_table_info('gl');";
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
        private readonly FilterConditionContext _context = new(
            MoneyScale,
            PrepStartDate,
            PeriodStart,
            PeriodEnd,
            [0, 6],
            GlPopulationScope.AuditPeriod)
        {
            ZerosThreshold = BenchmarkZerosThreshold
        };
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
                    $"FROM {Table("gl")} g " +
                    $"WHERE {PeriodBoundsWith("g.")} AND ({rule.Sql}) " +
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
                    $"CASE WHEN voucher_number IS NULL THEN 0 ELSE MAX(f{index:00}) END AS hit_voucher_{index:00}"));
            var monthlyAggregates = string.Join(
                ",\n       ",
                _rules.Select((_, index) =>
                    $"{SumBig($"hit_lines_{index:00}")} AS hit_lines_{index:00}, " +
                    $"{SumBig($"hit_voucher_{index:00}")} AS hit_vouchers_{index:00}"));

            command.CommandText =
                $"""
                WITH flagged AS (
                    SELECT {MonthExpression} AS month_key,
                           g.voucher_number,
                           {flags}
                    FROM {Table("gl")} g
                    WHERE {PeriodBoundsWith("g.")}
                ),
                voucher_flags AS (
                    SELECT month_key,
                           voucher_number,
                           {CountAll} AS row_count,
                           {voucherAggregates}
                    FROM flagged
                    GROUP BY month_key, voucher_number
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
                $"FROM {Table("gl")} g " +
                $"WHERE {PeriodBoundsWith("g.")} " +
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
                // 第四步照舊版列整張命中傳票的規則（未預期借貸組合），月份格子和 prescreen.run 用同一個列出方式。
                rules.Add(new CompiledRule(
                    key, LocalPrescreenPageRepository.HitRowsPredicate(key, sql, _schemaPrefix), parameters));
            }

            return rules;
        }

        private string MonthExpression =>
            "SUBSTR(g.gl_date, 1, 7)";

        private string CountAll =>
            "COUNT(*)";

        private string CountDistinctDocument =>
            "COUNT(DISTINCT g.voucher_number)";

        private string SumBig(string expression) =>
            $"SUM({expression})";

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

    private const string SqliteSeedSql =
        """
        BEGIN TRANSACTION;
        DELETE FROM gl;
        DELETE FROM account_mapping;
        DELETE FROM authorized_preparer;
        DELETE FROM calendar_day;
        DELETE FROM import_batch WHERE batch_id = 's2a-map';

        INSERT INTO import_batch
            (batch_id, dataset_kind, source_file_path, source_file_name,
             imported_utc, row_count, columns_json)
        VALUES
            ('s2a-map', 'account_mapping', 's2a-map.csv', 's2a-map.csv',
             '2026-07-30T00:00:00.0000000+08:00', 5,
             '["account_number","account_name","standardized_category"]');

        -- 科目配對一律以 category_id 連到分類表（2026-10-02 起刪除依分類名稱對應的退路），測試資料補上 category_id。
        INSERT INTO account_mapping
            (batch_id, source_row_number, account_number, account_name, standardized_category, category_id)
        VALUES
            ('s2a-map', 1, '1000', 'Cash', 'Cash', 'builtin.cash'),
            ('s2a-map', 2, '2000', 'Receivables', 'Receivables', 'builtin.receivables'),
            ('s2a-map', 3, '4000', 'Revenue', 'Revenue', 'builtin.revenue'),
            ('s2a-map', 4, '5000', 'Expense A', 'Others', 'builtin.others'),
            ('s2a-map', 5, '6000', 'Expense B', 'Others', 'builtin.others');

        WITH RECURSIVE preparers(i) AS (
            SELECT 0
            UNION ALL
            SELECT i + 1 FROM preparers WHERE i < 499
        )
        INSERT INTO authorized_preparer (name)
        SELECT printf('P%04d', i) FROM preparers;

        INSERT INTO calendar_day (day_type, date, day_name)
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
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, line_item,
             line_item_numeric_sort_key, gl_date, approval_date,
             account_number, account_name, description, source_module,
             created_by, approved_by, is_manual, is_excluded, amount_scaled,
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
            0,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN -100000000 ELSE 100000000 END,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN 0 ELSE 100000000 END,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN 100000000 ELSE 0 END,
            CASE WHEN (i - 1) % 4 IN (0, 3) THEN 'CREDIT' ELSE 'DEBIT' END
        FROM generated;

        DELETE FROM gl_control_total;
        INSERT INTO gl_control_total
            (singleton, pbc_row_count, stored_row_count, stored_debit_scaled, stored_credit_scaled,
             gl_row_count, excluded_by_period_count, excluded_by_posting_status_count,
             gl_debit_scaled, gl_credit_scaled)
        SELECT 1, COUNT(*), COUNT(*), SUM(debit_amount_scaled), SUM(credit_amount_scaled),
               COUNT(*), 0, 0, SUM(debit_amount_scaled), SUM(credit_amount_scaled)
        FROM gl;

        COMMIT;
        ANALYZE;
        PRAGMA wal_checkpoint(TRUNCATE);
        """;
}
