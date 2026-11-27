using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using JET.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.v3;

namespace JET.Tests.Application;

internal static class ScaleJourneyAvailability
{
    internal const string EnabledVariable = "JET_SCALE_JOURNEY";
    internal const string RowsVariable = "JET_SCALE_JOURNEY_ROWS";
    internal const string ProviderVariable = "JET_SCALE_JOURNEY_PROVIDER";
    internal const string OutputVariable = "JET_SCALE_JOURNEY_OUTPUT";
    internal const string ShapeVariable = "JET_SCALE_JOURNEY_SHAPE";
    internal const string MeasureWorkVariable = "JET_SCALE_JOURNEY_MEASURE_WORK";

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal);

    internal const string SkipReason = "未設定 JET_SCALE_JOURNEY=1；未執行一百萬筆合成 GL 的完整流程量測。";
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class ScaleJourneyTheoryAttribute : TheoryAttribute, ITraitAttribute
{
    public ScaleJourneyTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = ScaleJourneyAvailability.SkipReason;
        SkipType = typeof(ScaleJourneyAvailability);
        SkipUnless = nameof(ScaleJourneyAvailability.IsEnabled);
    }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => TestProfileTraits.Scale;
}

/// <summary>合成 GL 的形狀。</summary>
internal enum ScaleJourneyShape
{
    /// <summary>每張傳票一借一貸，每一百張有一張符合條件。</summary>
    Spread,

    /// <summary>每張傳票一借一貸，一半的傳票符合條件：單一情境命中的分錄很多。</summary>
    ManyHits,

    /// <summary>固定 40 張傳票，分錄平均分到每一張：資料量加倍時每張傳票的分錄也加倍，一半的分錄符合條件。</summary>
    BigVouchers,
}

/// <summary>
/// 用程式生成的合成 GL，照審計員的順序走完建案、匯入 GL 與 TB、
/// 確認兩份配對、驗證、預篩選、條件保存、條件篩選報告與底稿，每一步記下耗時、程式的記憶體、資料庫檔與記錄檔大小，
/// 最後檢查資料庫完整性。只記數字，不斷言快慢。
/// 只在設定 JET_SCALE_JOURNEY=1 時執行，帶 Scale 特徵，一般的測試執行不會跑到它；要量時直接執行建置好的測試程式，
/// 用 --filter-class 指定這個類別。
/// 列數預設一百萬，JET_SCALE_JOURNEY_ROWS 可以寫一個數字，或用逗號隔開的幾個數字，依序各量一次，最後列出相鄰兩次的倍數。
/// JET_SCALE_JOURNEY_SHAPE 選資料形狀：spread（預設）、manyHits 或 bigVouchers，說明見 <see cref="ScaleJourneyShape"/>。
/// JET_SCALE_JOURNEY_MEASURE_WORK=1 時另記每一步的資料庫工作量與 SQL 次數；量工作量會讓 SQLite 變慢，所以預設不開，
/// 量時間與量工作量分開執行。
/// 這個量測另外記：每一步期間每 50 毫秒取樣的工作集與受控堆積尖峰、GC 暫停的合計與取樣到的最長一次，
/// 以及條件篩選報告匯出到一半時取消，從要求取消到真的停下花了幾秒。
/// 只量 SQLite；JET_SCALE_JOURNEY_PROVIDER 設成其他名稱時不量；
/// JET_SCALE_JOURNEY_OUTPUT 指定時另把結果寫成檔案。資料全部合成，放在系統暫存資料夾，結束時刪除。
/// </summary>
public sealed class ScaleJourneyTests(ITestOutputHelper output)
{
    private const int DefaultRows = 1_000_000;
    private const int AccountCount = 200;
    private const int PreparerCount = 20;
    private const int BigVoucherCount = 40;

    internal sealed record Measurement(
        string Step,
        double Seconds,
        long WorkingSetBytes,
        long PeakWorkingSetBytes,
        long DatabaseBytes,
        long LogBytes)
    {
        /// <summary>這一步 SQLite 執行的虛擬機指令數，每 16 個算 1；沒有量時是 null。</summary>
        public long? DatabaseWork { get; init; }

        /// <summary>這一步執行的 SQL 語句數；沒有量時是 null。</summary>
        public int? Statements { get; init; }

        /// <summary>這一步期間取樣到的工作集最大值。</summary>
        public long SampledPeakWorkingSetBytes { get; init; }

        /// <summary>這一步期間取樣到的受控堆積最大值。</summary>
        public long SampledPeakManagedBytes { get; init; }

        /// <summary>這一步期間 GC 暫停所有執行緒的合計秒數。</summary>
        public double GcPauseSeconds { get; init; }

        /// <summary>這一步期間取樣到的最長一次 GC 暫停秒數。</summary>
        public double LongestGcPauseSeconds { get; init; }

        /// <summary>這一步期間 SQLite 配置的記憶體最大值，是整個程式所有連線的合計。</summary>
        public long SqliteMemoryHighwaterBytes { get; init; }
    }

    internal sealed record JourneyOptions(
        bool AllExports,
        SqliteStatementWorkMeter? Meter);

    internal sealed record SizeResult(
        int Rows,
        double GenerationSeconds,
        IReadOnlyList<Measurement> Measurements,
        string Integrity,
        double IntegritySeconds,
        string GlFingerprint);

    [ScaleJourneyTheory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    public async Task SyntheticGlJourney_RecordsTimeMemoryAndFileSizes(string provider)
    {
        var only = Environment.GetEnvironmentVariable(ScaleJourneyAvailability.ProviderVariable);
        if (!string.IsNullOrWhiteSpace(only) && !string.Equals(only, provider, StringComparison.Ordinal))
        {
            output.WriteLine($"{provider}：JET_SCALE_JOURNEY_PROVIDER={only}，這次不量。");
            return;
        }

        var shape = ResolveShape();
        var measureWork = string.Equals(
            Environment.GetEnvironmentVariable(ScaleJourneyAvailability.MeasureWorkVariable), "1", StringComparison.Ordinal);
        var results = new List<SizeResult>();
        foreach (var rows in ResolveRows())
        {
            using var workspace = new TempProjectRoot();
            var projects = Path.Combine(workspace.Path, "projects");
            Directory.CreateDirectory(projects);
            var generation = Stopwatch.StartNew();
            var (glPath, tbPath) = WriteSources(workspace.Path, rows, shape);
            var generationSeconds = generation.Elapsed.TotalSeconds;
            var name = $"scale-{provider}";
            var meter = measureWork ? new SqliteStatementWorkMeter(static _ => "all") : null;
            var measurements = await RunJourneyAsync(
                projects, name, provider, glPath, tbPath, new JourneyOptions(AllExports: true, meter));

            SqliteConnection.ClearAllPools();
            var integrity = Stopwatch.StartNew();
            var integrityResult = await CheckIntegrityAsync(projects, name, provider);
            var integritySeconds = integrity.Elapsed.TotalSeconds;
            var glFingerprint = await ReadGlFingerprintAsync(projects, name);
            results.Add(new SizeResult(rows, generationSeconds, measurements, integrityResult, integritySeconds, glFingerprint));
            Assert.Equal("ok", integrityResult);
        }

        var report = Render(provider, shape, measureWork, results);
        output.WriteLine(report);
        await WriteOutputIfRequestedAsync(provider, shape, measureWork, report);
    }

    /// <summary>
    /// 照審計員的順序走完建案到底稿，每一步記下耗時、記憶體與檔案大小，最後離開案件。
    /// 一般測試執行裡的長案件名稱測試（CaseLimitJourneyTests）也用這一段，只是資料量小，也不另外匯出其他報告。
    /// <paramref name="options"/> 的 AllExports 為 true 時，另外匯出資料驗證報告與預篩選報告，
    /// 最後再匯出一次條件篩選報告，在上一次耗時的一半時取消，記下停下來花的時間。
    /// </summary>
    internal static async Task<List<Measurement>> RunJourneyAsync(
        string projects, string name, string provider, string glPath, string tbPath, JourneyOptions? options = null)
    {
        options ??= new JourneyOptions(AllExports: false, Meter: null);
        var databasePath = Path.Combine(projects, name, JetProjectFolder.DatabaseFileName);
        var logPath = databasePath + "-wal";
        var measurements = new List<Measurement>();

        using (var host = new HandlerTestHost(enableDevTools: false, projectsRootPath: projects, sqlStatementObserver: options.Meter))
        {
            async Task<JsonElement> Step(string step, string action, object? payload = null)
            {
                var workBefore = options.Meter?.Steps("all");
                var statementsBefore = options.Meter?.Statements("all");
                // 把 SQLite 的記憶體最大值重設成目前用量，這一步結束時讀到的就是這一步期間的最大值。
                SqliteMemoryHighwater(reset: true);
                await using var sampler = ResourceSampler.Start();
                var timer = Stopwatch.StartNew();
                var result = await host.DispatchAsync(action, payload is null ? "{}" : JsonSerializer.Serialize(payload));
                timer.Stop();
                var sqliteHighwater = SqliteMemoryHighwater(reset: false);
                var sample = await sampler.StopAsync();
                using var process = Process.GetCurrentProcess();
                measurements.Add(new Measurement(
                    step,
                    timer.Elapsed.TotalSeconds,
                    process.WorkingSet64,
                    process.PeakWorkingSet64,
                    FileLength(databasePath),
                    FileLength(logPath))
                {
                    DatabaseWork = options.Meter is null ? null : options.Meter.Steps("all") - workBefore,
                    Statements = options.Meter is null ? null : options.Meter.Statements("all") - statementsBefore,
                    SampledPeakWorkingSetBytes = sample.PeakWorkingSetBytes,
                    SampledPeakManagedBytes = sample.PeakManagedBytes,
                    GcPauseSeconds = sample.GcPauseSeconds,
                    LongestGcPauseSeconds = sample.LongestGcPauseSeconds,
                    SqliteMemoryHighwaterBytes = sqliteHighwater,
                });
                return result;
            }

            await Step("建案", "project.create", new
            {
                projectName = name, periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider,
            });
            await Step("匯入 GL", "import.gl.fromFile", new
            {
                mode = "replace",
                sources = new[] { new { filePath = glPath, fileName = "合成總帳.csv", encoding = "utf-8", delimiter = "," } },
            });
            await Step("匯入 TB", "import.tb.fromFile", new
            {
                mode = "replace",
                sources = new[] { new { filePath = tbPath, fileName = "合成試算表.csv", encoding = "utf-8", delimiter = "," } },
            });
            await Step("確認 GL 配對", "mapping.commit.gl", new
            {
                mapping = new Dictionary<string, string>
                {
                    ["voucherNumber"] = "傳票號碼", ["glDate"] = "傳票日期", ["accountNumber"] = "科目代號",
                    ["accountName"] = "科目名稱", ["description"] = "摘要", ["amount"] = "金額", ["createdBy"] = "製單人員",
                },
                amountMode = "signed",
                rdeFields = Array.Empty<object>(),
            });
            await Step("確認 TB 配對", "mapping.commit.tb", new
            {
                changeMode = "direct",
                mapping = new { accountNumber = "科目代號", accountName = "科目名稱", changeAmount = "本期變動" },
            });
            var validation = await Step("驗證", "validate.run");
            var prescreen = await Step("預篩選", "prescreen.run");
            var committed = await Step("條件保存", "filter.commit", new
            {
                scenarios = new[]
                {
                    new
                    {
                        name = "合成量測情境",
                        rationale = "量測大資料量的條件保存與報告",
                        groups = new[]
                        {
                            new
                            {
                                join = "AND",
                                conditions = new[]
                                {
                                    new { join = "AND", type = "fieldValue", field = "description", @operator = "contains", values = new[] { "調整" } },
                                },
                            },
                        },
                    },
                },
            });

            var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
            var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString()!;
            var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
            if (options.AllExports)
            {
                await Step("資料驗證報告", "export.validationReports", new { resultId = validationResultId });
                await Step("預篩選報告", "export.prescreenReport", new { resultId = prescreenResultId });
            }

            await Step("條件篩選報告", "export.criteriaSelectionReport", new { validationResultId, prescreenResultId, revision });
            await Step("底稿", "export.workingPaperStream", new
            {
                validationResultId, prescreenResultId, scenarioRevision = revision, scenarioPositions = new[] { 1 },
            });
            if (options.AllExports)
            {
                var reportSeconds = measurements.Single(static item => item.Step == "條件篩選報告").Seconds;
                measurements.Add(await MeasureCancellationAsync(
                    host,
                    "export.criteriaSelectionReport",
                    JsonSerializer.Serialize(new { validationResultId, prescreenResultId, revision }),
                    TimeSpan.FromSeconds(reportSeconds / 2)));
            }

            await host.DispatchAsync("project.releaseLock");
        }

        return measurements;
    }

    /// <summary>
    /// SQLite 配置的記憶體最大值；<paramref name="reset"/> 為 true 時同時把最大值重設成目前用量。
    /// SQLite 的原生函式庫在第一次建立 <see cref="SqliteConnection"/> 時才載入，所以先建立一個不開啟的連線物件。
    /// </summary>
    private static long SqliteMemoryHighwater(bool reset)
    {
        using (new SqliteConnection())
        {
        }

        return SQLitePCL.raw.sqlite3_memory_highwater(reset ? 1 : 0);
    }

    /// <summary>
    /// 再匯出一次，在 <paramref name="cancelAfter"/> 時要求取消，記下從要求取消到動作返回的秒數。
    /// 動作在取消前就做完時，秒數記成負的經過時間，報表寫「取消前已完成」。
    /// </summary>
    private static async Task<Measurement> MeasureCancellationAsync(
        HandlerTestHost host, string action, string payload, TimeSpan cancelAfter)
    {
        using var cancellation = new CancellationTokenSource();
        var requested = new Stopwatch();
        SqliteMemoryHighwater(reset: true);
        await using var sampler = ResourceSampler.Start();
        var dispatch = host.DispatchAsync(action, payload, cancellation.Token);
        var finishedFirst = await Task.WhenAny(dispatch, Task.Delay(cancelAfter)) == dispatch;
        double seconds;
        if (finishedFirst)
        {
            await dispatch;
            seconds = -1;
        }
        else
        {
            requested.Start();
            await cancellation.CancelAsync();
            try
            {
                await dispatch;
            }
            catch (OperationCanceledException)
            {
            }

            seconds = requested.Elapsed.TotalSeconds;
        }

        var sqliteHighwater = SqliteMemoryHighwater(reset: false);
        var sample = await sampler.StopAsync();
        using var process = Process.GetCurrentProcess();
        return new Measurement("取消條件篩選報告", seconds, process.WorkingSet64, process.PeakWorkingSet64, 0, 0)
        {
            SampledPeakWorkingSetBytes = sample.PeakWorkingSetBytes,
            SampledPeakManagedBytes = sample.PeakManagedBytes,
            GcPauseSeconds = sample.GcPauseSeconds,
            LongestGcPauseSeconds = sample.LongestGcPauseSeconds,
            SqliteMemoryHighwaterBytes = sqliteHighwater,
        };
    }

    private static IReadOnlyList<int> ResolveRows()
    {
        var text = Environment.GetEnvironmentVariable(ScaleJourneyAvailability.RowsVariable);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [DefaultRows];
        }

        var rows = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(static item => int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 2
                ? value - value % 2
                : 0)
            .Where(static value => value > 0)
            .ToArray();
        return rows.Length == 0 ? [DefaultRows] : rows;
    }

    private static ScaleJourneyShape ResolveShape() =>
        Environment.GetEnvironmentVariable(ScaleJourneyAvailability.ShapeVariable)?.Trim() switch
        {
            "manyHits" => ScaleJourneyShape.ManyHits,
            "bigVouchers" => ScaleJourneyShape.BigVouchers,
            _ => ScaleJourneyShape.Spread,
        };

    /// <summary>
    /// 合成 GL：每兩筆分錄一借一貸、金額相同，分散在查核期間的每一天。符合條件的分錄摘要寫「調整」，
    /// 比例依 <paramref name="shape"/> 而定。TB 的本期變動等於 GL 各科目的合計，完整性測試沒有差異。
    /// </summary>
    internal static (string Gl, string Tb) WriteSources(
        string directory, int rows, ScaleJourneyShape shape = ScaleJourneyShape.Spread)
    {
        var totals = new long[AccountCount];
        var glPath = Path.Combine(directory, "gl.csv");
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using (var writer = new StreamWriter(glPath, append: false, encoding, bufferSize: 1 << 20))
        {
            writer.Write("傳票號碼,傳票日期,科目代號,科目名稱,摘要,金額,製單人員\n");
            var start = new DateOnly(2025, 1, 1);
            var pairsPerBigVoucher = Math.Max(1, rows / 2 / BigVoucherCount);
            for (var pair = 0; pair < rows / 2; pair++)
            {
                var voucher = shape == ScaleJourneyShape.BigVouchers ? Math.Min(pair / pairsPerBigVoucher, BigVoucherCount - 1) : pair;
                var date = start.AddDays(voucher % 365).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var debit = pair % AccountCount;
                var credit = (pair * 7 + 3) % AccountCount;
                if (credit == debit)
                {
                    credit = (credit + 1) % AccountCount;
                }

                var cents = 100L + pair * 7919L % 9_999_900L;
                var hit = shape switch
                {
                    ScaleJourneyShape.ManyHits or ScaleJourneyShape.BigVouchers => pair % 2 == 0,
                    _ => pair % 100 == 0,
                };
                var description = hit ? $"調整分錄 {pair}" : $"合成分錄 {pair}";
                var preparer = $"USER{pair % PreparerCount + 1:00}";
                WriteLine(writer, voucher, date, debit, description, cents, preparer);
                WriteLine(writer, voucher, date, credit, description, -cents, preparer);
                totals[debit] += cents;
                totals[credit] -= cents;
            }
        }

        var tbPath = Path.Combine(directory, "tb.csv");
        var tb = new StringBuilder("科目代號,科目名稱,本期變動\n");
        for (var account = 0; account < AccountCount; account++)
        {
            tb.Append(CultureInfo.InvariantCulture, $"{AccountNumber(account)},合成科目 {account},{Money(totals[account])}\n");
        }

        File.WriteAllText(tbPath, tb.ToString(), encoding);
        return (glPath, tbPath);
    }

    private static void WriteLine(StreamWriter writer, int voucher, string date, int account, string description, long cents, string preparer) =>
        writer.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"V{voucher:0000000},{date},{AccountNumber(account)},合成科目 {account},{description},{Money(cents)},{preparer}\n"));

    private static string AccountNumber(int account) => (1101 + account * 17).ToString(CultureInfo.InvariantCulture);

    private static string Money(long cents) =>
        (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static long FileLength(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
    }

    /// <summary>
    /// 用 SQLite 的 integrity_check 檢查資料庫。
    /// </summary>
    internal static async Task<string> CheckIntegrityAsync(string projects, string name, string provider)
    {
        var folder = new JetProjectFolder(projects);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        await using var connection = database.CreateConnection(name);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;

        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
        await connection.CloseAsync();
        SqliteConnection.ClearAllPools();
        return result;
    }

    /// <summary>
    /// gl 的列數、內容摘要與索引摘要；同一份合成資料在不同次量測寫出的 gl，可以用它比對是否相同。
    /// 內容摘要依分錄編號逐列逐欄計算，不含每次匯入都不同的批次編號；索引摘要依建立的先後計算索引名稱與定義文字。
    /// 兩個摘要都是 SHA-256 的前 16 個十六進位字。
    /// </summary>
    internal static async Task<string> ReadGlFingerprintAsync(string projects, string name)
    {
        var folder = new JetProjectFolder(projects);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = folder.GetDatabasePath(name),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();

        using var content = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        long rows = 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = 0;
            command.CommandText =
                """
                SELECT entry_id, source_row_number, voucher_number, line_item, line_item_numeric_sort_key, gl_date, approval_date,
                       account_number, account_name, description, source_module, created_by, approved_by, is_manual,
                       amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, posting_status, is_excluded, exclusion_reason
                FROM gl
                ORDER BY entry_id;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            var line = new StringBuilder();
            while (await reader.ReadAsync())
            {
                rows++;
                line.Clear();
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    // 每個值前面寫型別與長度，NULL、空字串與數字 0 才分得開。
                    var value = reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
                    line.Append(value is null ? "N;" : $"{reader.GetFieldType(ordinal).Name}:{value.Length}:{value};");
                }

                line.Append('\n');
                content.AppendData(Encoding.UTF8.GetBytes(line.ToString()));
            }
        }

        var indexes = new StringBuilder();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'gl' ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                indexes.Append(reader.GetString(0)).Append('\n').Append(reader.IsDBNull(1) ? "N" : reader.GetString(1)).Append('\n');
            }
        }

        var contentHash = Convert.ToHexString(content.GetHashAndReset())[..16];
        var indexHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(indexes.ToString())))[..16];
        return $"gl {rows:N0} 列，內容摘要 {contentHash}，索引摘要 {indexHash}。";
    }

    private static string Render(
        string provider,
        ScaleJourneyShape shape,
        bool measureWork,
        IReadOnlyList<SizeResult> results)
    {
        static string Megabytes(long bytes) => (bytes / 1024d / 1024d).ToString("N1", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"## {provider}：資料形狀 {shape}，{(measureWork ? "量資料庫工作量" : "量時間")}，{Environment.ProcessorCount} 個邏輯處理器");
        foreach (var result in results)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"### 合成 GL {result.Rows:N0} 列");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture,
                $"產生合成來源檔用了 {result.GenerationSeconds:F1} 秒；完整性檢查 {result.Integrity}，用了 {result.IntegritySeconds:F1} 秒。");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"{result.GlFingerprint}");
            text.AppendLine();
            text.AppendLine("| 步驟 | 秒 | 資料庫工作量 | SQL 次數 | 工作集尖峰（MB） | 受控堆積尖峰（MB） | SQLite 記憶體高峰（MB） | GC 暫停合計（秒） | 最長 GC 暫停（秒） | 資料庫檔（MB） |");
            text.AppendLine("|:---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var item in result.Measurements)
            {
                var seconds = item.Step == "取消條件篩選報告" && item.Seconds < 0 ? "取消前已完成" : item.Seconds.ToString("F2", CultureInfo.InvariantCulture);
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"| {item.Step} | {seconds} | {item.DatabaseWork?.ToString("N0", CultureInfo.InvariantCulture) ?? "-"} | "
                    + $"{item.Statements?.ToString("N0", CultureInfo.InvariantCulture) ?? "-"} | {Megabytes(item.SampledPeakWorkingSetBytes)} | "
                    + $"{Megabytes(item.SampledPeakManagedBytes)} | {Megabytes(item.SqliteMemoryHighwaterBytes)} | "
                    + $"{item.GcPauseSeconds:F2} | {item.LongestGcPauseSeconds:F2} | {Megabytes(item.DatabaseBytes)} |");
            }
        }

        for (var index = 1; index < results.Count; index++)
        {
            var before = results[index - 1];
            var after = results[index];
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture,
                $"### {before.Rows:N0} 列到 {after.Rows:N0} 列（資料量 {(double)after.Rows / before.Rows:F2} 倍）");
            text.AppendLine();
            text.AppendLine("| 步驟 | 時間倍數 | 資料庫工作量倍數 |");
            text.AppendLine("|:---|---:|---:|");
            foreach (var item in after.Measurements)
            {
                var previous = before.Measurements.FirstOrDefault(candidate => candidate.Step == item.Step);
                if (previous is null || item.Step == "建案" || item.Step == "取消條件篩選報告")
                {
                    continue;
                }

                var time = previous.Seconds > 0 ? (item.Seconds / previous.Seconds).ToString("F2", CultureInfo.InvariantCulture) : "-";
                var work = previous.DatabaseWork is > 0 && item.DatabaseWork is { } current
                    ? ((double)current / previous.DatabaseWork.Value).ToString("F2", CultureInfo.InvariantCulture)
                    : "-";
                text.AppendLine(CultureInfo.InvariantCulture, $"| {item.Step} | {time} | {work} |");
            }
        }

        return text.ToString();
    }

    private static async Task WriteOutputIfRequestedAsync(string provider, ScaleJourneyShape shape, bool measureWork, string report)
    {
        var outputPath = Environment.GetEnvironmentVariable(ScaleJourneyAvailability.OutputVariable);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(fullPath);
        var kind = measureWork ? "work" : "time";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(
            Path.Combine(fullPath, $"scale-journey-{provider}-{shape}-{kind}-{stamp}.md"), report, new UTF8Encoding(false));
    }

    /// <summary>
    /// 一步期間每 50 毫秒取樣一次工作集、受控堆積與最近一次 GC 的暫停時間；GC 暫停合計用前後的累計值相減。
    /// </summary>
    private sealed class ResourceSampler : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly TimeSpan _pauseBefore = GC.GetTotalPauseDuration();
        private readonly Task _loop;
        private long _peakWorkingSet;
        private long _peakManaged;
        private TimeSpan _longestPause;
        private long _lastGcIndex = GC.GetGCMemoryInfo(GCKind.Any).Index;
        private Sample? _result;

        private ResourceSampler() => _loop = Task.Run(LoopAsync);

        internal sealed record Sample(
            long PeakWorkingSetBytes, long PeakManagedBytes, double GcPauseSeconds, double LongestGcPauseSeconds);

        public static ResourceSampler Start() => new();

        public async Task<Sample> StopAsync()
        {
            if (_result is not null)
            {
                return _result;
            }

            await _stop.CancelAsync();
            await _loop;
            Take();
            _result = new Sample(
                _peakWorkingSet,
                _peakManaged,
                (GC.GetTotalPauseDuration() - _pauseBefore).TotalSeconds,
                _longestPause.TotalSeconds);
            return _result;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _stop.Dispose();
        }

        private async Task LoopAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            try
            {
                do
                {
                    Take();
                }
                while (await timer.WaitForNextTickAsync(_stop.Token));
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void Take()
        {
            using var process = Process.GetCurrentProcess();
            _peakWorkingSet = Math.Max(_peakWorkingSet, process.WorkingSet64);
            _peakManaged = Math.Max(_peakManaged, GC.GetTotalMemory(forceFullCollection: false));
            var info = GC.GetGCMemoryInfo(GCKind.Any);
            if (info.Index != _lastGcIndex)
            {
                _lastGcIndex = info.Index;
                foreach (var pause in info.PauseDurations)
                {
                    if (pause > _longestPause)
                    {
                        _longestPause = pause;
                    }
                }
            }
        }
    }
}
