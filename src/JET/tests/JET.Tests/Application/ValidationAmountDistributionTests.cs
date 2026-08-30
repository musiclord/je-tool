using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Architecture;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// validate.run 的查核期間金額級距與 ECDF wire 契約。
/// Oracle 使用 scaled integer 邊界的自含小母體；零元不進 ECDF 分母，
/// 且 provider 不得以浮點數或 Application 列集合重算。
/// </summary>
public sealed class ValidationAmountDistributionTests
{
    private static readonly long[] ExpectedCounts =
        [1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1];

    private static readonly decimal?[] ExpectedEcdfPct =
        [null, 3.8m, 11.5m, 19.2m, 26.9m, 34.6m, 42.3m, 50.0m,
         57.7m, 65.4m, 73.1m, 80.8m, 88.5m, 96.2m, 100.0m];

    [Fact]
    public async Task ValidateRun_AmountDistribution_Sqlite_MatchesScaledBoundaryOracle()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, BuildBoundaryGl, databaseProvider: "sqlite");

        var data = await host.DispatchAsync("validate.run");

        AssertBoundaryOracle(data);
    }

    [Fact]
    public async Task ValidateRun_AmountDistribution_DuckDb_MatchesScaledBoundaryOracle()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, BuildBoundaryGl, databaseProvider: "duckdb");

        var data = await host.DispatchAsync("validate.run");

        AssertBoundaryOracle(data);
    }

    [SqlServerFact]
    public async Task ValidateRun_AmountDistribution_SqlServer_MatchesScaledBoundaryOracle()
    {
        await WithSqlServerHostAsync(async host =>
        {
            await InlineWorkbookProject.SetupAsync(
                host,
                BuildBoundaryGl,
                databaseProvider: "sqlServer");

            var data = await host.DispatchAsync("validate.run");

            AssertBoundaryOracle(data);
        });
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MappingCommitGl_EmptyEffectivePopulation_RollsBack(
        string databaseProvider)
    {
        using var host = new HandlerTestHost();
        await AssertEmptyPeriodAsync(host, databaseProvider);
    }

    [SqlServerFact]
    public async Task MappingCommitGl_SqlServerEmptyEffectivePopulation_RollsBack()
    {
        await WithSqlServerHostAsync(host => AssertEmptyPeriodAsync(host, "sqlServer"));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MappingCommitGl_AllZeroEffectivePopulation_IgnoresExcludedNonzeroForDegenerateGuard(
        string databaseProvider)
    {
        using var host = new HandlerTestHost();
        await AssertAllZeroPeriodAsync(host, databaseProvider);
    }

    [SqlServerFact]
    public async Task MappingCommitGl_SqlServerAllZeroEffectivePopulation_IgnoresExcludedNonzeroForDegenerateGuard()
    {
        await WithSqlServerHostAsync(host => AssertAllZeroPeriodAsync(host, "sqlServer"));
    }

    private static async Task AssertEmptyPeriodAsync(
        HandlerTestHost host,
        string databaseProvider)
    {
        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            InlineWorkbookProject.SetupAsync(
                host,
                builder => builder
                    .WithColumns(
                        "傳票號碼", "傳票日期", "科目代號", "科目名稱",
                        "摘要", "金額", "借方旗標")
                    .AddRow("OUTSIDE", "2024-12-31", "1101", "現金", "界外列", "1000.00", 1),
                databaseProvider: databaseProvider,
                periodStart: "2025-01-01",
                periodEnd: "2025-12-31"));

        Assert.Equal(JetErrorCodes.EmptyEffectivePopulation, exception.Code);
    }

    private static async Task AssertAllZeroPeriodAsync(
        HandlerTestHost host,
        string databaseProvider)
    {
        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            InlineWorkbookProject.SetupAsync(
                host,
                builder => builder
                    .WithColumns(
                        "傳票號碼", "傳票日期", "科目代號", "科目名稱",
                        "摘要", "金額", "借方旗標")
                    .AddRow("ZERO", "2025-06-30", "1101", "現金", "期內零元", "0.0000", 1)
                    // 期外非零列仍會保留，但不得掩蓋有效母體退化為全零。
                    .AddRow("OUTSIDE", "2024-12-31", "4101", "收入", "界外非零", "1.0000", 0),
                databaseProvider: databaseProvider,
                periodStart: "2025-01-01",
                periodEnd: "2025-12-31"));

        Assert.Equal(JetErrorCodes.GlAmountsAllZero, exception.Code);
    }

    [Fact]
    public async Task ProjectLoad_MalformedCurrentValidationSummary_DoesNotResume()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, BuildBoundaryGl);
        await SaveMalformedCurrentValidationSummaryAsync(host, projectId);

        var loaded = await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId }));

        var resumed = loaded.GetProperty("latestRuns").GetProperty("validate");
        Assert.Equal(JsonValueKind.Null, resumed.ValueKind);
    }

    [Fact]
    public async Task ValidateRun_AmountDistribution_EmitsExactWireKeysAndOrder()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, BuildBoundaryGl);

        var data = await host.DispatchAsync("validate.run");
        var block = data.GetProperty("amountDistribution");
        var bins = block.GetProperty("bins").EnumerateArray().ToArray();

        AssertPropertyNames(AmountDistributionWireKeys.Block, block);
        Assert.Equal(AmountDistributionWireKeys.BinKeys, bins.Select(BinKey).ToArray());
        Assert.All(bins, bin => AssertPropertyNames(AmountDistributionWireKeys.BinRow, bin));
    }

    private static void BuildBoundaryGl(InlineGlWorkbookBuilder builder)
    {
        builder.WithColumns(
            "傳票號碼", "傳票日期", "科目代號", "科目名稱",
            "摘要", "金額", "借方旗標");

        var amounts = new[]
        {
            "0.0000",
            "999.9999",
            "1000.0000", "2000.0000",
            "2000.0001", "5000.0000",
            "5000.0001", "10000.0000",
            "10000.0001", "20000.0000",
            "20000.0001", "50000.0000",
            "50000.0001", "100000.0000",
            "100000.0001", "200000.0000",
            "200000.0001", "500000.0000",
            "500000.0001", "1000000.0000",
            "1000000.0001", "2000000.0000",
            "2000000.0001", "5000000.0000",
            "5000000.0001", "10000000.0000",
            "10000000.0001"
        };

        for (var index = 0; index < amounts.Length; index++)
        {
            builder.AddRow(
                $"BIN-{index:D2}",
                "2025-06-30",
                $"A{index:D3}",
                $"級距科目 {index:D2}",
                "金額級距邊界",
                amounts[index],
                index % 2);
        }
    }

    private static void AssertBoundaryOracle(JsonElement data)
    {
        var bins = ReadBins(data);

        Assert.Equal(AmountDistributionWireKeys.BinKeys, bins.Select(BinKey).ToArray());
        Assert.Equal(
            ExpectedCounts,
            bins.Select(bin => bin.GetProperty("count").GetInt64()).ToArray());

        for (var index = 0; index < bins.Length; index++)
        {
            var ecdf = bins[index].GetProperty("ecdfPct");
            if (ExpectedEcdfPct[index] is { } expected)
            {
                Assert.Equal(expected, ecdf.GetDecimal());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, ecdf.ValueKind);
            }
        }
    }

    private static JsonElement[] ReadBins(JsonElement data) =>
        data.GetProperty("amountDistribution")
            .GetProperty("bins")
            .EnumerateArray()
            .ToArray();

    private static string BinKey(JsonElement bin) =>
        bin.GetProperty("key").GetString()!;

    private static void AssertPropertyNames(
        IReadOnlyList<string> expected,
        JsonElement element)
    {
        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            element.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
    }

    private static async Task WithSqlServerHostAsync(
        Func<HandlerTestHost, Task> assertion)
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return;
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await assertion(host);
        }
        finally
        {
            foreach (var directory in Directory.GetDirectories(host.ProjectsRoot))
            {
                await TempSqlServerProject.DropDatabaseAsync(
                    connectionString,
                    Path.GetFileName(directory));
            }
        }
    }

    private static async Task SaveMalformedCurrentValidationSummaryAsync(
        HandlerTestHost host,
        string projectId)
    {
        var runId = Guid.NewGuid().ToString("N");
        var generatedUtc = DateTimeOffset.UtcNow;
        var summaryJson = JsonSerializer.Serialize(new
        {
            stats = new
            {
                glRowCount = 27,
                voucherCount = 27,
                totalDebit = 0m,
                totalCredit = 0m,
                net = 0m,
                periodStart = "2025-01-01",
                periodEnd = "2025-12-31"
            },
            resultRef = new
            {
                runId,
                generatedUtc,
                logicVersion = RuleLogicVersions.Validation
            }
        });

        var runStore = new LocalRuleRunStore(
            new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot)));
        await runStore.SaveAsync(
            projectId,
            new RuleRunRecord(runId, RuleRunKinds.Validate, generatedUtc, summaryJson),
            CancellationToken.None);
    }
}
