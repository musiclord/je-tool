using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// KCT D／E／G／J 的 action-level provider matrix：同一份真匯入、mapping、validation 與
/// account mapping 在三個 provider 上驗證「缺欄先拒絕」及「前置齊備後 preview＝commit 命中」。
/// </summary>
public sealed class KctPrerequisiteProviderTests
{
    private static readonly KctReadyCase[] ReadyCases =
    [
        new(
            "D",
            KctScenario("KCT D", new { join = "AND", type = "manualRevenueEntry" }),
            ["D-1|1"]),
        new(
            "E",
            KctScenario("KCT E", new
            {
                join = "AND",
                type = "text",
                field = "createBy",
                keywords = "特定建立者",
                mode = "exact"
            }),
            ["E-1|1"]),
        new(
            "G",
            KctScenario("KCT G", new
            {
                join = "AND",
                type = "prescreen",
                prescreenKey = "blankDescription"
            }),
            ["G-1|1"]),
        new(
            "J",
            KctScenario("KCT J", new { join = "AND", type = "preparerEqualsApprover" }),
            ["J-1|1"])
    ];

    private static readonly KctMissingCase[] MissingCases =
    [
        new("D", ReadyCases[0].Scenario, [GlMappingKeys.Manual], ["人工/自動分錄"]),
        new("E", ReadyCases[1].Scenario, [GlMappingKeys.CreateBy], ["傳票建立人員"]),
        new("G", ReadyCases[2].Scenario, [GlMappingKeys.Description], ["傳票摘要"]),
        new("J-createBy", ReadyCases[3].Scenario, [GlMappingKeys.CreateBy], ["傳票建立人員"]),
        new("J-approveBy", ReadyCases[3].Scenario, [GlMappingKeys.ApproveBy], ["傳票核准人員"]),
        new(
            "J-both",
            ReadyCases[3].Scenario,
            [GlMappingKeys.CreateBy, GlMappingKeys.ApproveBy],
            ["傳票建立人員", "傳票核准人員"])
    ];

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public Task PrerequisitesAndReadyHits_AreConsistentForLocalProviders(string databaseProvider) =>
        RunMatrixAsync(databaseProvider, sqlServerConnectionString: null);

    [SqlServerFact]
    public async Task PrerequisitesAndReadyHits_AreConsistentForSqlServer()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync()
            ?? throw new InvalidOperationException(
                "SqlServerFact 已判定可用，但執行期無法取得 SQL Server 連線。");

        await RunMatrixAsync("sqlServer", connectionString);
    }

    private static async Task RunMatrixAsync(
        string databaseProvider,
        string? sqlServerConnectionString)
    {
        using var host = new HandlerTestHost(sqlServerConnectionString: sqlServerConnectionString);
        try
        {
            var projectId = await InlineWorkbookProject.SetupAsync(
                host,
                builder => builder
                    .WithColumns(
                        "傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱",
                        "摘要", "建立人員", "核准人員", "人工傳票", "金額", "借方旗標")
                    .AddRow("D-1", "1", "2025-03-01", "4001", "收入", "人工收入", "建立者 D", "核准者 D", true, "100.00", 0)
                    .AddRow("D-1", "2", "2025-03-01", "1101", "現金", "人工收入對方", "建立者 D2", "核准者 D2", false, "100.00", 1)
                    .AddRow("E-1", "1", "2025-03-02", "1101", "現金", "特定人員", "特定建立者", "不同核准者", false, "50.00", 1)
                    .AddRow("E-1", "2", "2025-03-02", "4001", "收入", "特定人員對方", "其他建立者", "不同核准者", false, "50.00", 0)
                    .AddRow("G-1", "1", "2025-03-03", "1101", "現金", null, "建立者 G", "核准者 G", false, "30.00", 1)
                    .AddRow("G-1", "2", "2025-03-03", "4001", "收入", "同傳票非空摘要", "建立者 G2", "核准者 G2", false, "30.00", 0)
                    .AddRow("J-1", "1", "2025-03-04", "1101", "現金", "同人核准", "Same User", " same user ", false, "20.00", 1)
                    .AddRow("J-1", "2", "2025-03-04", "4001", "收入", "不同人核准", "建立者 J2", "核准者 J2", false, "20.00", 0),
                databaseProvider: databaseProvider,
                validateForDownstream: true);

            await ImportAccountMappingAsync(host);

            var mappingStore = CreateMappingStore(
                host,
                databaseProvider,
                sqlServerConnectionString);
            var completeMapping = await mappingStore.FindAsync(
                projectId,
                DatasetKind.Gl,
                CancellationToken.None)
                ?? throw new InvalidOperationException("測試安排缺少已提交 GL mapping。");

            foreach (var testCase in MissingCases)
            {
                var incomplete = completeMapping with
                {
                    Mapping = completeMapping.Mapping
                        .Where(pair => !testCase.MissingMappingKeys.Contains(
                            pair.Key,
                            StringComparer.Ordinal))
                        .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)
                };
                await mappingStore.SaveAsync(projectId, incomplete, CancellationToken.None);

                var previewError = await Assert.ThrowsAsync<JetActionException>(() =>
                    host.DispatchAsync("filter.preview", PreviewPayload(testCase.Scenario)));
                AssertMissingField(previewError, testCase);

                var commitError = await Assert.ThrowsAsync<JetActionException>(() =>
                    host.DispatchAsync("filter.commit", CommitPayload([testCase.Scenario])));
                AssertMissingField(commitError, testCase);
            }

            var loaded = await host.DispatchAsync(
                "project.load",
                JsonSerializer.Serialize(new { projectId }));
            Assert.Empty(loaded.GetProperty("filterScenarios").EnumerateArray());

            await mappingStore.SaveAsync(projectId, completeMapping, CancellationToken.None);

            foreach (var testCase in ReadyCases)
            {
                var preview = await host.DispatchAsync(
                    "filter.preview",
                    PreviewPayload(testCase.Scenario));
                Assert.Equal(
                    testCase.ExpectedRows,
                    RowIdentities(preview.GetProperty("scenario").GetProperty("previewRows")));
            }

            var committed = await host.DispatchAsync(
                "filter.commit",
                CommitPayload(ReadyCases.Select(static testCase => testCase.Scenario).ToArray()));
            Assert.Equal(ReadyCases.Length, committed.GetProperty("savedCount").GetInt32());

            for (var index = 0; index < ReadyCases.Length; index++)
            {
                var page = await host.DispatchAsync(
                    "query.filterHitsPage",
                    JsonSerializer.Serialize(new { scenarioPosition = index + 1, pageSize = 50 }));
                Assert.Equal(ReadyCases[index].ExpectedRows, RowIdentities(page.GetProperty("rows")));
            }
        }
        finally
        {
            await CleanupSqlServerProjectsAsync(
                host.ProjectsRoot,
                databaseProvider,
                sqlServerConnectionString);
        }
    }

    private static object KctScenario(string name, object rule) => new
    {
        source = "kct",
        name,
        rationale = "KCT mapping prerequisite provider oracle",
        groups = new object[] { new { join = "AND", rules = new[] { rule } } }
    };

    private static string PreviewPayload(object scenario) =>
        JsonSerializer.Serialize(new { populationScope = "auditPeriod", scenario });

    private static string CommitPayload(IReadOnlyList<object> scenarios) =>
        JsonSerializer.Serialize(new { populationScope = "auditPeriod", scenarios });

    private static string[] RowIdentities(JsonElement rows) =>
        rows.EnumerateArray()
            .Select(static row => string.Join(
                "|",
                row.GetProperty("documentNumber").GetString(),
                row.GetProperty("lineItem").GetString()))
            .ToArray();

    private static void AssertMissingField(JetActionException exception, KctMissingCase testCase)
    {
        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Contains("缺少前置資料", exception.Message, StringComparison.Ordinal);
        foreach (var label in testCase.MissingFieldLabels)
        {
            Assert.Contains(label, exception.Message, StringComparison.Ordinal);
        }
    }

    private static async Task ImportAccountMappingAsync(HandlerTestHost host)
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "GL_NUMBER";
            sheet.Cell(1, 2).Value = "GL_NAME";
            sheet.Cell(1, 3).Value = "STANDARDIZED_ACCOUNT_NAME";
            sheet.Cell(2, 1).Value = "1101";
            sheet.Cell(2, 2).Value = "現金";
            sheet.Cell(2, 3).Value = AccountMappingCategories.Cash;
            sheet.Cell(3, 1).Value = "4001";
            sheet.Cell(3, 2).Value = "收入";
            sheet.Cell(3, 3).Value = AccountMappingCategories.Revenue;
        });

        try
        {
            await host.DispatchAsync(
                "import.accountMapping.fromFile",
                JsonSerializer.Serialize(new { filePath = path, fileName = "kct-account-mapping.xlsx" }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    private static IMappingStateStore CreateMappingStore(
        HandlerTestHost host,
        string databaseProvider,
        string? sqlServerConnectionString) =>
        databaseProvider switch
        {
            "sqlite" => new LocalMappingStateStore(
                new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot))),
            "duckdb" => new LocalMappingStateStore(
                new DuckDbProjectDatabase(new JetProjectFolder(host.ProjectsRoot))),
            "sqlServer" => new SqlServerMappingStateStore(
                new SqlServerProjectDatabase(new SqlServerConnectionOptions(sqlServerConnectionString))),
            _ => throw new ArgumentOutOfRangeException(
                nameof(databaseProvider),
                databaseProvider,
                "未知的測試資料庫 provider。")
        };

    private static async Task CleanupSqlServerProjectsAsync(
        string projectsRoot,
        string databaseProvider,
        string? sqlServerConnectionString)
    {
        if (databaseProvider != "sqlServer"
            || string.IsNullOrWhiteSpace(sqlServerConnectionString)
            || !Directory.Exists(projectsRoot))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(projectsRoot))
        {
            await TempSqlServerProject.DropDatabaseAsync(
                sqlServerConnectionString,
                Path.GetFileName(directory));
        }
    }

    private sealed record KctReadyCase(
        string Letter,
        object Scenario,
        string[] ExpectedRows);

    private sealed record KctMissingCase(
        string Name,
        object Scenario,
        string[] MissingMappingKeys,
        string[] MissingFieldLabels);
}
