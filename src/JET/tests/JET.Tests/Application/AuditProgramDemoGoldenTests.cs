using System.Text.Json;
using JET.AuditCore;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Golden oracle：以正式 demo 匯入與配對後的 SQLite 與 DuckDB 母體，直接走 Plan、facts port 與 Finalize，
/// 釘住資料驗證與預篩選每項程序的順序、狀態、計數與 N/A 原因。
/// </summary>
public sealed class AuditProgramDemoGoldenTests(DemoGoldenProviderFixture fixture)
    : IClassFixture<DemoGoldenProviderFixture>
{
    // 第 9 批中低 13：只擴充 provider；兩個引擎必須符合下方同一份手寫固定答案。
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Finalize_DemoValidationRun_MatchesGoldenVerdicts(string provider)
    {
        var (host, context) = fixture.For(provider);
        var projectPath = Path.Combine(host.ProjectsRoot, context.ProjectId, "project.json");
        using var projectJson = JsonDocument.Parse(await File.ReadAllTextAsync(projectPath));
        var project = projectJson.RootElement;

        var plan = JetAuditProgram.Plan(new ValidationRequest(
            context.ProjectId,
            HasGlMapping: true,
            HasTbMapping: true,
            project.GetProperty("periodStart").GetString()!,
            project.GetProperty("periodEnd").GetString()!,
            project.GetProperty("moneyScale").GetInt32(),
            project.GetProperty("sampleSeed").GetInt64(),
            "audit-core-explain-golden",
            new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero),
            SampleSize: 59));
        IValidationFactsPort factsPort = new LocalValidationRunRepository(
            DatabaseFor(provider, host));

        // 第9批中低9：走正式同交易port擷取facts；原固定verdict不變。
        var facts = await JET.Tests.Infrastructure.ValidationExecutionTestData.ExecuteForFactsAsync(factsPort, plan, CancellationToken.None);
        var result = JetAuditProgram.Finalize(plan, facts);

        AssertPeriod(result.Manifest);
        AssertVerdicts(
            result.Manifest,
            ("completeness_test", "na", 0L),
            ("doc_balance_test", "na", 0L),
            ("inf_sampling_test", "V", 59L),
            ("null_records_test", "V", 58L));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Finalize_DemoPrescreenRun_MatchesGoldenVerdicts(string provider)
    {
        var (host, context) = fixture.For(provider);
        var projectPath = Path.Combine(host.ProjectsRoot, context.ProjectId, "project.json");
        using var projectJson = JsonDocument.Parse(await File.ReadAllTextAsync(projectPath));
        var project = projectJson.RootElement;
        var demoProject = context.Demo.GetProperty("project");

        var plan = JetAuditProgram.Plan(
            new PrescreenRequest(
                context.ProjectId,
                HasGlMapping: true,
                project.GetProperty("periodStart").GetString()!,
                project.GetProperty("periodEnd").GetString()!,
                project.GetProperty("moneyScale").GetInt32(),
                project.GetProperty("sampleSeed").GetInt64(),
                RunId: "audit-core-prescreen-explain-golden",
                GeneratedUtc: new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero),
                LastPeriodStart: demoProject.GetProperty("lastPeriodStart").GetString(),
                HasApprovalDate: true,
                HasCreatedBy: true,
                HasHolidays: true,
                HasAccountMapping: true,
                HasRevenue: true,
                HasCounterpart: true,
                HasAuthorizedPreparers: true,
                NonWorkingDays: [0, 6], HasVoucherDate: true));
        IPrescreenFactsPort factsPort =
            new LocalPrescreenRunRepository(DatabaseFor(provider, host));

        var facts = await factsPort.ExecuteAsync(plan, CancellationToken.None);
        var result = JetAuditProgram.Finalize(plan, facts);

        AssertPeriod(result.Manifest);
        AssertVerdicts(
            result.Manifest,
            ("post_period_approval", "V", 40L),
            ("suspicious_keywords", "V", 25L),
            ("unexpected_account_pair", "V", 30L),
            ("trailing_zeros", "V", 30L),
            ("creator_summary", "V", 8L),
            ("rare_accounts", "V", 17L),
            ("weekend_posting", "V", 24L),
            ("weekend_approval", "V", 20L),
            ("holiday_posting", "V", 28L),
            ("holiday_approval", "V", 16L),
            ("blank_description", "V", 18L),
            ("backdated_posting", "V", 44L),
            ("non_authorized_preparer", "V", 32L),
            ("low_frequency_preparer", "V", 10L),
            ("low_frequency_account", "V", 6L));
    }

    private static ILocalProjectDatabase DatabaseFor(string provider, HandlerTestHost host) => provider switch
    {
        "sqlite" => new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot)),
        "duckdb" => new DuckDbProjectDatabase(new JetProjectFolder(host.ProjectsRoot)),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static void AssertPeriod(AuditRunManifest manifest)
    {
        Assert.Equal("2025-01-01", manifest.Plan.CaseSnapshot.PeriodStart);
        Assert.Equal("2025-12-31", manifest.Plan.CaseSnapshot.PeriodEnd);
    }

    // 逐項比對程序順序、狀態與計數；demo 母體每項程序都適用，所以 N/A 原因一律為空。
    private static void AssertVerdicts(
        AuditRunManifest manifest,
        params (string Slug, string Status, long Count)[] expected)
    {
        Assert.Equal(
            expected,
            manifest.Procedures
                .Select(verdict => (verdict.Definition.Slug, verdict.Status!, verdict.Count!.Value))
                .ToArray());
        Assert.All(manifest.Procedures, verdict =>
        {
            Assert.True(verdict.IsApplicable);
            Assert.Null(verdict.NaReason);
        });
    }
}

/// <summary>兩平台各建一份示範案件並循序匯入，同類別測試重用，避免每條 golden 重建整份資料。</summary>
public sealed class DemoGoldenProviderFixture : IAsyncLifetime
{
    private readonly Dictionary<string, (HandlerTestHost Host, DemoProjectPipeline.Context Context)> cases = [];

    internal (HandlerTestHost Host, DemoProjectPipeline.Context Context) For(string provider) => cases[provider];

    public async ValueTask InitializeAsync()
    {
        foreach (var provider in new[] { "sqlite", "duckdb" })
        {
            var host = new HandlerTestHost();
            try
            {
                cases.Add(provider, (host, await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider)));
            }
            catch
            {
                host.Dispose();
                await DisposeAsync();
                throw;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var entry in cases.Values) entry.Host.Dispose();
        cases.Clear();
        return ValueTask.CompletedTask;
    }
}
