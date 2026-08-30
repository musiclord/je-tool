using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 完整性後端硬閘的 provider integration matrix。四個 action 都走真 dispatcher、
/// 真 validation persistence 與各 provider 的資料庫；前置不足時必須在任何
/// action-specific run／revision 檢查之前 fail closed。
/// </summary>
public sealed class CompletenessBackendGateProviderTests
{
    private const string ExpectedErrorCode = "completeness_prerequisite_failed";
    private const string NoValidation = "noValidation";
    private const string CompletenessDifference = "completenessDifference";
    private const string ReimportedGl = "reimportedGl";

    [Theory]
    [InlineData("sqlite", NoValidation)]
    [InlineData("sqlite", CompletenessDifference)]
    [InlineData("sqlite", ReimportedGl)]
    [InlineData("duckdb", NoValidation)]
    [InlineData("duckdb", CompletenessDifference)]
    [InlineData("duckdb", ReimportedGl)]
    public Task IneligibleCompletenessState_RejectsAllFourDownstreamActions_LocalProviders(
        string databaseProvider,
        string state) =>
        RunRejectedStateAsync(databaseProvider, state, sqlServerConnectionString: null);

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public Task EligibleCompletenessState_AllowsAllFourDownstreamActions_LocalProviders(
        string databaseProvider) =>
        RunEligibleStateAsync(databaseProvider, sqlServerConnectionString: null);

    [SqlServerTheory]
    [InlineData(NoValidation)]
    [InlineData(CompletenessDifference)]
    [InlineData(ReimportedGl)]
    public async Task IneligibleCompletenessState_RejectsAllFourDownstreamActions_SqlServer(
        string state)
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync()
            ?? throw new InvalidOperationException(
                "SqlServerTheory 已判定可用，但執行期無法取得 SQL Server 連線。");

        await RunRejectedStateAsync("sqlServer", state, connectionString);
    }

    [SqlServerFact]
    public async Task EligibleCompletenessState_AllowsAllFourDownstreamActions_SqlServer()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync()
            ?? throw new InvalidOperationException(
                "SqlServerFact 已判定可用，但執行期無法取得 SQL Server 連線。");

        await RunEligibleStateAsync("sqlServer", connectionString);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public Task ReimportedTb_RetainsPrescreenRunButRejectsPrescreenReport_LocalProviders(
        string databaseProvider) =>
        RunPrescreenReportAfterTbReimportAsync(
            databaseProvider,
            sqlServerConnectionString: null);

    [SqlServerFact]
    public async Task ReimportedTb_RetainsPrescreenRunButRejectsPrescreenReport_SqlServer()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync()
            ?? throw new InvalidOperationException(
                "SqlServerFact 已判定可用，但執行期無法取得 SQL Server 連線。");

        await RunPrescreenReportAfterTbReimportAsync("sqlServer", connectionString);
    }

    private static async Task RunRejectedStateAsync(
        string databaseProvider,
        string state,
        string? sqlServerConnectionString)
    {
        using var host = new HandlerTestHost(sqlServerConnectionString: sqlServerConnectionString);
        try
        {
            var references = state switch
            {
                NoValidation => await ArrangeNoValidationAsync(host, databaseProvider),
                CompletenessDifference => await ArrangeCompletenessDifferenceAsync(host, databaseProvider),
                ReimportedGl => await ArrangeReimportedGlAsync(host, databaseProvider),
                _ => throw new ArgumentOutOfRangeException(nameof(state), state, "未知的完整性閘門測試狀態。")
            };

            await AssertAllFourActionsRejectedAsync(host, references);
        }
        finally
        {
            await CleanupSqlServerProjectsAsync(
                host.ProjectsRoot,
                databaseProvider,
                sqlServerConnectionString);
        }
    }

    private static async Task RunEligibleStateAsync(
        string databaseProvider,
        string? sqlServerConnectionString)
    {
        using var host = new HandlerTestHost(sqlServerConnectionString: sqlServerConnectionString);
        try
        {
            await SetupProjectAsync(host, databaseProvider, hasCompletenessDifference: false);
            var references = await SeedEligibleReferencesWithoutPrescreenAsync(host);

            var criteria = await host.DispatchAsync(
                "export.criteriaSelectionReport",
                CriteriaPayload(references));
            var workpaper = await host.DispatchAsync(
                "export.workpaperStream",
                WorkpaperPayload(references));

            Assert.True(criteria.GetProperty("ok").GetBoolean());
            Assert.Equal(
                "criteriaSelectionReport",
                criteria.GetProperty("artifact").GetProperty("kind").GetString());
            Assert.True(workpaper.GetProperty("ok").GetBoolean());
            Assert.Equal(
                "workingPaper",
                workpaper.GetProperty("artifact").GetProperty("kind").GetString());
            Assert.Equal(
                JsonValueKind.Null,
                criteria.GetProperty("artifact").GetProperty("sourceRef")
                    .GetProperty("prescreenRunId").ValueKind);
            Assert.Equal(
                JsonValueKind.Null,
                workpaper.GetProperty("artifact").GetProperty("sourceRef")
                    .GetProperty("prescreenRunId").ValueKind);

            var prescreenReportError = await Assert.ThrowsAsync<JetActionException>(() =>
                host.DispatchAsync(
                    "export.prescreenReport",
                    JsonSerializer.Serialize(new { runId = "missing-prescreen-run" })));
            Assert.Equal(JetErrorCodes.StaleResult, prescreenReportError.Code);

            await host.DispatchAsync(
                "calendar.setNonWorkingDays",
                """{ "days": [1] }""");
            var staleFilterError = await Assert.ThrowsAsync<JetActionException>(() =>
                host.DispatchAsync(
                    "export.workpaperStream",
                    WorkpaperPayload(references)));
            Assert.Equal(JetErrorCodes.StaleResult, staleFilterError.Code);

            var refreshedCriteria = await host.DispatchAsync(
                "export.criteriaSelectionReport",
                CriteriaPayload(references));
            var refreshedWorkpaper = await host.DispatchAsync(
                "export.workpaperStream",
                WorkpaperPayload(references));
            Assert.True(refreshedCriteria.GetProperty("ok").GetBoolean());
            Assert.True(refreshedWorkpaper.GetProperty("ok").GetBoolean());
        }
        finally
        {
            await CleanupSqlServerProjectsAsync(
                host.ProjectsRoot,
                databaseProvider,
                sqlServerConnectionString);
        }
    }

    private static async Task RunPrescreenReportAfterTbReimportAsync(
        string databaseProvider,
        string? sqlServerConnectionString)
    {
        using var host = new HandlerTestHost(sqlServerConnectionString: sqlServerConnectionString);
        try
        {
            var projectId = await SetupProjectAsync(
                host,
                databaseProvider,
                hasCompletenessDifference: false);
            var references = await SeedEligibleReferencesAsync(host);

            await ReimportTbAsync(host);

            var loaded = await host.DispatchAsync(
                "project.load",
                JsonSerializer.Serialize(new { projectId }));
            Assert.Equal(
                JsonValueKind.Null,
                loaded.GetProperty("latestRuns").GetProperty("validate").ValueKind);
            Assert.Equal(
                references.PrescreenRunId,
                loaded.GetProperty("latestRuns").GetProperty("prescreen")
                    .GetProperty("resultRef").GetProperty("runId").GetString());

            var exception = await Assert.ThrowsAsync<JetActionException>(() =>
                host.DispatchAsync(
                    "export.prescreenReport",
                    JsonSerializer.Serialize(new { runId = references.PrescreenRunId })));
            Assert.Equal(ExpectedErrorCode, exception.Code);
        }
        finally
        {
            await CleanupSqlServerProjectsAsync(
                host.ProjectsRoot,
                databaseProvider,
                sqlServerConnectionString);
        }
    }

    private static async Task<RunReferences> ArrangeNoValidationAsync(
        HandlerTestHost host,
        string databaseProvider)
    {
        await SetupProjectAsync(host, databaseProvider, hasCompletenessDifference: false);
        return new RunReferences("missing-validation", "missing-prescreen", "missing-revision");
    }

    private static async Task<RunReferences> ArrangeCompletenessDifferenceAsync(
        HandlerTestHost host,
        string databaseProvider)
    {
        await SetupProjectAsync(host, databaseProvider, hasCompletenessDifference: true);
        var validation = await host.DispatchAsync("validate.run");
        var completeness = validation.GetProperty("completenessTest");
        var partA = completeness.GetProperty("partA");

        Assert.True(partA.GetProperty("rowCountMatch").GetBoolean());
        Assert.True(partA.GetProperty("amountMatch").GetBoolean());
        Assert.True(
            completeness.GetProperty("diffAccountCount").GetInt64() > 0,
            "測試前置必須形成 part(b) 科目差異，不能以空差異假性驗證硬閘。");

        return new RunReferences(
            validation.GetProperty("resultRef").GetProperty("runId").GetString()!,
            "missing-prescreen",
            "missing-revision");
    }

    private static async Task<RunReferences> ArrangeReimportedGlAsync(
        HandlerTestHost host,
        string databaseProvider)
    {
        var projectId = await SetupProjectAsync(
            host,
            databaseProvider,
            hasCompletenessDifference: false);
        var references = await SeedEligibleReferencesAsync(host);

        await ReimportGlAsync(host);

        var loaded = await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(
            JsonValueKind.Null,
            loaded.GetProperty("latestRuns").GetProperty("validate").ValueKind);

        return references;
    }

    private static async Task<RunReferences> SeedEligibleReferencesAsync(HandlerTestHost host)
    {
        var validation = await host.DispatchAsync("validate.run");
        AssertEligibleCompletenessPrecondition(validation);

        var prescreen = await host.DispatchAsync("prescreen.run");
        var committed = await host.DispatchAsync("filter.commit", FilterCommitPayload());

        return new RunReferences(
            validation.GetProperty("resultRef").GetProperty("runId").GetString()!,
            prescreen.GetProperty("resultRef").GetProperty("runId").GetString()!,
            committed.GetProperty("resultRef").GetProperty("revision").GetString()!);
    }

    private static async Task<RunReferences> SeedEligibleReferencesWithoutPrescreenAsync(
        HandlerTestHost host)
    {
        var validation = await host.DispatchAsync("validate.run");
        AssertEligibleCompletenessPrecondition(validation);

        var committed = await host.DispatchAsync("filter.commit", FilterCommitPayload());

        return new RunReferences(
            validation.GetProperty("resultRef").GetProperty("runId").GetString()!,
            string.Empty,
            committed.GetProperty("resultRef").GetProperty("revision").GetString()!);
    }

    private static void AssertEligibleCompletenessPrecondition(JsonElement validation)
    {
        var completeness = validation.GetProperty("completenessTest");
        var partA = completeness.GetProperty("partA");

        Assert.True(partA.GetProperty("rowCountMatch").GetBoolean());
        Assert.True(partA.GetProperty("amountMatch").GetBoolean());
        Assert.Equal(0, completeness.GetProperty("diffAccountCount").GetInt64());
        Assert.Equal(JsonValueKind.Null, completeness.GetProperty("naReason").ValueKind);
    }

    private static async Task AssertAllFourActionsRejectedAsync(
        HandlerTestHost host,
        RunReferences references)
    {
        var invocations = new[]
        {
            new ActionInvocation("prescreen.run", "{}"),
            new ActionInvocation("filter.commit", FilterCommitPayload()),
            new ActionInvocation("export.criteriaSelectionReport", CriteriaPayload(references)),
            new ActionInvocation("export.workpaperStream", WorkpaperPayload(references))
        };
        var observed = new List<string>(invocations.Length);

        foreach (var invocation in invocations)
        {
            try
            {
                await host.DispatchAsync(invocation.Action, invocation.Payload);
                observed.Add($"{invocation.Action}=<success>");
            }
            catch (JetActionException exception)
            {
                observed.Add($"{invocation.Action}={exception.Code}");
            }
            catch (Exception exception)
            {
                observed.Add($"{invocation.Action}=<{exception.GetType().Name}>");
            }
        }

        Assert.Equal(
            invocations.Select(invocation => $"{invocation.Action}={ExpectedErrorCode}"),
            observed);
    }

    private static Task<string> SetupProjectAsync(
        HandlerTestHost host,
        string databaseProvider,
        bool hasCompletenessDifference) =>
        InlineWorkbookProject.SetupAsync(
            host,
            ConfigureBalancedGl,
            databaseProvider: databaseProvider,
            configureTb: hasCompletenessDifference
                ? ConfigureMismatchedTb
                : ConfigureMatchingTb);

    private static void ConfigureBalancedGl(InlineGlWorkbookBuilder builder) =>
        builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "完整性閘門借方", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "收入", "完整性閘門貸方", "100.00", 0);

    private static void ConfigureMatchingTb(InlineTbWorkbookBuilder builder) =>
        builder
            .AddRow("1101", "現金", "100.00")
            .AddRow("4101", "收入", "-100.00");

    private static void ConfigureMismatchedTb(InlineTbWorkbookBuilder builder) =>
        builder
            .AddRow("1101", "現金", "90.00")
            .AddRow("4101", "收入", "-100.00");

    private static async Task ReimportGlAsync(HandlerTestHost host)
    {
        var builder = new InlineGlWorkbookBuilder();
        ConfigureBalancedGl(builder);
        var filePath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync(
                "import.gl.fromFile",
                JsonSerializer.Serialize(new
                {
                    filePath,
                    fileName = "completeness-gate-reimport.xlsx",
                    mode = "replace"
                }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(filePath);
        }
    }

    private static async Task ReimportTbAsync(HandlerTestHost host)
    {
        var builder = new InlineTbWorkbookBuilder();
        ConfigureMatchingTb(builder);
        var filePath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync(
                "import.tb.fromFile",
                JsonSerializer.Serialize(new
                {
                    filePath,
                    fileName = "completeness-gate-tb-reimport.xlsx",
                    mode = "replace"
                }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(filePath);
        }
    }

    private static string FilterCommitPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new[]
            {
                new
                {
                    name = "完整性硬閘測試情境",
                    rationale = "驗證完整性不適格時不得保存篩選",
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
                                    keywords = "完整性閘門"
                                }
                            }
                        }
                    }
                }
            }
        });

    private static string CriteriaPayload(RunReferences references) =>
        JsonSerializer.Serialize(new
        {
            validationRunId = references.ValidationRunId,
            revision = references.Revision
        });

    private static string WorkpaperPayload(RunReferences references) =>
        JsonSerializer.Serialize(new
        {
            validationRunId = references.ValidationRunId,
            scenarioRevision = references.Revision,
            scenarioPositions = new[] { 1 }
        });

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

    private sealed record RunReferences(
        string ValidationRunId,
        string PrescreenRunId,
        string Revision);

    private sealed record ActionInvocation(string Action, string Payload);
}
