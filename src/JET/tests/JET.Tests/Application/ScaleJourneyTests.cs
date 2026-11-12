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

/// <summary>
/// 用程式生成的合成 GL，照審計員的順序走完建案、匯入 GL 與 TB、
/// 確認兩份配對、驗證、預篩選、條件保存、條件篩選報告與底稿，每一步記下耗時、程式的記憶體、資料庫檔與記錄檔大小，
/// 最後檢查資料庫完整性。只記數字，不斷言快慢。
/// 只在設定 JET_SCALE_JOURNEY=1 時執行，帶 Scale 特徵，一般的測試執行不會跑到它；要量時直接執行建置好的測試程式，
/// 用 --filter-class 指定這個類別。記憶體尖峰是整個程式的數字。
/// 列數預設一百萬，可用 JET_SCALE_JOURNEY_ROWS 改小先試流程；JET_SCALE_JOURNEY_PROVIDER 指定只量哪一種資料庫；
/// JET_SCALE_JOURNEY_OUTPUT 指定時另把結果寫成檔案。資料全部合成，放在系統暫存資料夾，結束時刪除。
/// </summary>
public sealed class ScaleJourneyTests(ITestOutputHelper output)
{
    private const int DefaultRows = 1_000_000;
    private const int AccountCount = 200;
    private const int PreparerCount = 20;

    internal sealed record Measurement(
        string Step,
        double Seconds,
        long WorkingSetBytes,
        long PeakWorkingSetBytes,
        long DatabaseBytes,
        long LogBytes);

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

        var rows = ResolveRows();
        using var workspace = new TempProjectRoot();
        var projects = Path.Combine(workspace.Path, "projects");
        Directory.CreateDirectory(projects);
        var generation = Stopwatch.StartNew();
        var (glPath, tbPath) = WriteSources(workspace.Path, rows);
        var generationSeconds = generation.Elapsed.TotalSeconds;
        var name = $"scale-{provider}";
        var measurements = await RunJourneyAsync(projects, name, provider, glPath, tbPath);

        SqliteConnection.ClearAllPools();
        var integrity = Stopwatch.StartNew();
        var integrityResult = await CheckIntegrityAsync(projects, name, provider);
        var integritySeconds = integrity.Elapsed.TotalSeconds;

        var report = Render(provider, rows, generationSeconds, measurements, integrityResult, integritySeconds);
        output.WriteLine(report);
        await WriteOutputIfRequestedAsync(provider, report);
        Assert.Equal("ok", integrityResult);
    }

    /// <summary>
    /// 照審計員的順序走完建案到底稿，每一步記下耗時、記憶體與檔案大小，最後離開案件。
    /// 一般驗證裡的長案件名稱測試也用這一段，只是資料量小。
    /// </summary>
    internal static async Task<List<Measurement>> RunJourneyAsync(
        string projects, string name, string provider, string glPath, string tbPath)
    {
        var databasePath = Path.Combine(projects, name, JetProjectFolder.DatabaseFileName);
        var logPath = databasePath + "-wal";
        var measurements = new List<Measurement>();

        using (var host = new HandlerTestHost(enableDevTools: false, projectsRootPath: projects))
        {
            async Task<JsonElement> Step(string step, string action, object? payload = null)
            {
                var timer = Stopwatch.StartNew();
                var result = await host.DispatchAsync(action, payload is null ? "{}" : JsonSerializer.Serialize(payload));
                timer.Stop();
                using var process = Process.GetCurrentProcess();
                measurements.Add(new Measurement(
                    step,
                    timer.Elapsed.TotalSeconds,
                    process.WorkingSet64,
                    process.PeakWorkingSet64,
                    FileLength(databasePath),
                    FileLength(logPath)));
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
            await Step("條件篩選報告", "export.criteriaSelectionReport", new { validationResultId, prescreenResultId, revision });
            await Step("底稿", "export.workingPaperStream", new
            {
                validationResultId, prescreenResultId, scenarioRevision = revision, scenarioPositions = new[] { 1 },
            });
            await host.DispatchAsync("project.releaseLock");
        }

        return measurements;
    }

    private static int ResolveRows()
    {
        var text = Environment.GetEnvironmentVariable(ScaleJourneyAvailability.RowsVariable);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows) && rows >= 2
            ? rows - rows % 2
            : DefaultRows;
    }

    /// <summary>
    /// 合成 GL：每張傳票一借一貸，分散在查核期間的每一天；每一百張有一張摘要寫「調整」，讓條件保存有命中。
    /// TB 的本期變動等於 GL 各科目的合計，完整性測試沒有差異。
    /// </summary>
    internal static (string Gl, string Tb) WriteSources(string directory, int rows)
    {
        var totals = new long[AccountCount];
        var glPath = Path.Combine(directory, "gl.csv");
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using (var writer = new StreamWriter(glPath, append: false, encoding, bufferSize: 1 << 20))
        {
            writer.Write("傳票號碼,傳票日期,科目代號,科目名稱,摘要,金額,製單人員\n");
            var start = new DateOnly(2025, 1, 1);
            for (var voucher = 0; voucher < rows / 2; voucher++)
            {
                var date = start.AddDays(voucher % 365).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var debit = voucher % AccountCount;
                var credit = (voucher * 7 + 3) % AccountCount;
                if (credit == debit)
                {
                    credit = (credit + 1) % AccountCount;
                }

                var cents = 100L + voucher * 7919L % 9_999_900L;
                var description = voucher % 100 == 0 ? $"調整分錄 {voucher}" : $"合成分錄 {voucher}";
                var preparer = $"USER{voucher % PreparerCount + 1:00}";
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

    private static string Render(
        string provider,
        int rows,
        double generationSeconds,
        IReadOnlyList<Measurement> measurements,
        string integrity,
        double integritySeconds)
    {
        static string Megabytes(long bytes) => (bytes / 1024d / 1024d).ToString("N1", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"### {provider}：合成 GL {rows:N0} 列");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{Environment.ProcessorCount} 個邏輯處理器；產生合成來源檔用了 {generationSeconds:F1} 秒；完整性檢查 {integrity}，用了 {integritySeconds:F1} 秒。");
        text.AppendLine();
        text.AppendLine("| 步驟 | 秒 | 記憶體（MB） | 記憶體尖峰（MB） | 資料庫檔（MB） | 記錄檔（MB） |");
        text.AppendLine("|:---|---:|---:|---:|---:|---:|");
        foreach (var item in measurements)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {item.Step} | {item.Seconds:F1} | {Megabytes(item.WorkingSetBytes)} | {Megabytes(item.PeakWorkingSetBytes)} | {Megabytes(item.DatabaseBytes)} | {Megabytes(item.LogBytes)} |");
        }

        return text.ToString();
    }

    private static async Task WriteOutputIfRequestedAsync(string provider, string report)
    {
        var outputPath = Environment.GetEnvironmentVariable(ScaleJourneyAvailability.OutputVariable);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(fullPath);
        await File.WriteAllTextAsync(Path.Combine(fullPath, $"scale-journey-{provider}.md"), report, new UTF8Encoding(false));
    }
}
