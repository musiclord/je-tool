using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 匯出底稿 SAX 寫出器(E1 Task 2 封面/固定文字表 + Task 3 step1 家族)。
/// 寫到 MemoryStream → 用 ClosedXML 讀回斷言(brief 允許測試端用 ClosedXML;鐵律只禁寫出器用)。
///
/// oracle 來源:
///   - 封面/固定文字:.git/sdd/xlsx_inspect.py 對本機參考樣本逐字復解的 cell 內容、合併範圍、表名。
///   - step1 家族資料列:獨立參數化 SQL recount(DemoProjectPipeline.QueryScalarAsync),
///     鎖「數值＋身分」(科目差異、名單筆數、diff≠0 子集),非弱斷言非空。
///
/// 母體以 InlineWorkbookProject(flag 金額模式:借方旗標 1=借/0=貸 → amount_scaled ±|金額|)自造,
/// 借貸平衡 vs 不平衡、完整性 diff=0 vs diff≠0 皆可控,證實條件表(step1-1 例外、step1-3)的存在性。
/// 寫出器以真 SQLite repo 注入(比照 CreatorSummaryExportTests 直接建 repo)。
/// </summary>
public sealed partial class WorkpaperWriterTests
{
    private const string CoverSheet = "資料預先整理之說明";
    private const string IntroSheet = "JE WorkingPaper說明";
    private const string Step5Sheet = "step5 財務報表關帳後調整之分錄";
    private const string Step1Sheet = "step1 完整性測試";
    private const string Step11Sheet = "step1-1 借貸不平測試";
    private const string Step12Sheet = "step1-2 分錄編製人員說明";
    private const string Step13Sheet = "step1-3 完整性測試之差異說明";
    private const string RemovedStep131Sheet = "step1-3-1完整性差異調節";
    private const string Step2Sheet = "step2 可靠性測試";
    private const string Step3Sheet = "step3 高風險條件彙總";
    private const string Step4Sheet = "step4 符合高風險條件傳票";
    private const string Step41Sheet = "step4-1 符合高風險條件傳票明細";
    private const string FieldInfoSheet = "自動化工具-檔案欄位資訊";
    private const string CalendarInfoSheet = "自動化工具-假期假日資訊";
    private const string AccountMappingSheet = "自動化工具-科目配對資訊";

    private const int MoneyScale = 10_000;

    // ---- 寫出器組裝(真 SQLite repo;此 task 尚無 handler/DI 入口)----

    private static WorkpaperWriter BuildWriter(
        HandlerTestHost host,
        WorkpaperWriterOptions? options = null,
        int progressRowInterval = 10_000,
        ITagMatrixRowPageRepository? tagMatrixRowsOverride = null,
        ITagMatrixVoucherPageRepository? tagMatrixVouchersOverride = null,
        ICompletenessDiffPageRepository? completenessDiffsOverride = null,
        ICalendarExportRepository? calendarDaysOverride = null,
        IAccountMappingExportRepository? accountMappingsOverride = null,
        IAccountMappingStore? accountMappingStateStoreOverride = null)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var completenessAccounts = new LocalCompletenessAccountPageRepository(database);
        var completenessDiffs = completenessDiffsOverride
            ?? new LocalCompletenessDiffPageRepository(database);
        var docBalances = new LocalDocBalancePageRepository(database);
        var creatorSummaries = new LocalCreatorSummaryExportRepository(database);
        var infSamples = new LocalInfSamplePageRepository(database);
        var filterScenarios = new LocalFilterScenarioStore(database);
        var tagMatrixCounts = new LocalTagMatrixScenariosRepository(database);
        var tagMatrixVouchers = tagMatrixVouchersOverride
            ?? new LocalTagMatrixVoucherPageRepository(database);
        var tagMatrixRows = tagMatrixRowsOverride
            ?? new LocalTagMatrixRowPageRepository(database);
        var mappingStates = new LocalMappingStateStore(database);
        var calendarDays = calendarDaysOverride
            ?? new LocalCalendarExportRepository(database);
        var accountMappings = accountMappingsOverride
            ?? new LocalAccountMappingExportRepository(database);
        var accountMappingStateStore = accountMappingStateStoreOverride
            ?? new LocalAccountMappingRepository(database);
        var rawRows = new LocalRawGlExportRepository(database);

        if (options is null && progressRowInterval == 10_000)
        {
            return new WorkpaperWriter(
                completenessAccounts,
                completenessDiffs,
                docBalances,
                creatorSummaries,
                infSamples,
                filterScenarios,
                tagMatrixCounts,
                tagMatrixVouchers,
                tagMatrixRows,
                mappingStates,
                calendarDays,
                accountMappings,
                accountMappingStateStore,
                rawRows);
        }

        return new WorkpaperWriter(
            completenessAccounts,
            completenessDiffs,
            docBalances,
            creatorSummaries,
            infSamples,
            filterScenarios,
            tagMatrixCounts,
            tagMatrixVouchers,
            tagMatrixRows,
            mappingStates,
            calendarDays,
            accountMappings,
            options ?? new WorkpaperWriterOptions(),
            progressRowInterval,
            rawRows,
            accountMappingStateStore);
    }

    private static WorkpaperContext ContextFor(string projectId) => new(
        ProjectId: projectId,
        CompanyName: "示範科技股份有限公司",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastPeriodStart: "2023-01-01",
        MoneyScale: MoneyScale,
        ValidationRunId: string.Empty,
        ScenarioRevision: string.Empty,
        ScenarioPositions: Array.Empty<int>());

    private static async Task<WorkpaperContext> CurrentContextForAsync(
        HandlerTestHost host,
        string projectId)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var runStore = new LocalRuleRunStore(database);
        var validation = await runStore.FindLatestAsync(projectId, RuleRunKinds.Validate, CancellationToken.None);
        var scenarios = await new LocalFilterScenarioStore(database).ListAsync(projectId, CancellationToken.None);
        var revision = scenarios.Count == 0
            ? string.Empty
            : scenarios[0].SavedUtc.ToUniversalTime().ToString("O");
        var populationScope = GlPopulationScope.AuditPeriod;
        if (scenarios.Count > 0)
        {
            using var definition = JsonDocument.Parse(scenarios[0].DefinitionJson);
            Assert.True(
                GlPopulationScopeValues.TryParse(
                    definition.RootElement.GetProperty("populationScope").GetString(),
                    out populationScope),
                "已存測試情境應含正準 populationScope。");
        }

        return ContextFor(projectId) with
        {
            ValidationRunId = validation?.RunId ?? string.Empty,
            ScenarioRevision = revision,
            ScenarioPositions = scenarios.Select(item => item.Position).ToArray(),
            PopulationScope = populationScope
        };
    }

    private static async Task<WorkpaperPlan> CurrentPlanForAsync(
        HandlerTestHost host,
        WorkpaperContext context,
        ICompletenessDiffPageRepository? completenessDiffsOverride = null)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var savedScenarios = await new LocalFilterScenarioStore(database).ListAsync(
            context.ProjectId,
            CancellationToken.None);
        var selectedPositions = context.ScenarioPositions.ToHashSet();
        var selections = savedScenarios
            .Where(scenario => selectedPositions.Contains(scenario.Position))
            .Select(scenario => new WorkpaperScenarioSelection(
                scenario.Position,
                scenario.Name,
                scenario.Rationale))
            .ToArray();
        var initial = JetAuditProgram.Plan(new WorkpaperRequest(
            context.ProjectId,
            context.PeriodStart,
            context.PeriodEnd,
            context.LastPeriodStart,
            context.MoneyScale,
            context.ValidationRunId,
            context.ScenarioRevision,
            selections,
            context.PopulationScope));
        var factsPort = new WorkpaperPlanningFactsPort(
            completenessDiffsOverride ?? new LocalCompletenessDiffPageRepository(database),
            new LocalDocBalancePageRepository(database),
            new LocalTagMatrixScenariosRepository(database),
            new LocalFieldDefinitionFactsPort(database),
            new LocalMappingStateStore(database));
        var facts = await JetAuditProgram.ExecuteAsync(
            initial,
            factsPort,
            CancellationToken.None);
        return JetAuditProgram.Finalize(initial, facts);
    }

    private static async Task<XLWorkbook> WriteAndReadAsync(HandlerTestHost host, string projectId)
    {
        var bytes = await WriteToBytesAsync(host, projectId);
        return new XLWorkbook(new MemoryStream(bytes));
    }

    private static async Task<byte[]> WriteToBytesAsync(HandlerTestHost host, string projectId)
    {
        await using var stream = new MemoryStream();
        var stats = await BuildWriter(host).WriteAsync(
            stream, await CurrentContextForAsync(host, projectId), CancellationToken.None);
        Assert.Equal(stream.Length, stats.BytesWritten);

        return stream.ToArray();
    }

    private static async Task ExecuteProjectSqlAsync(
        HandlerTestHost host,
        string projectId,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(29)]
    public void LegacyNumberStyles_RejectDecimalScaleOutsideDecimalRange(int decimalPlaces)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WorkpaperStyles.Number(decimalPlaces));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WorkpaperStyles.GroupedNumber(decimalPlaces));
    }

    [Fact]
    public async Task FullWorkbook_ReportsMonotonicProgressAfterEachCompletedSheet()
    {
        using var host = new HandlerTestHost();
        var project = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        await using var stream = new MemoryStream();
        var progress = new List<WorkpaperProgress>();
        var context = await CurrentContextForAsync(host, project.ProjectId);

        var stats = await BuildWriter(host).WriteAsync(
            stream, context, CancellationToken.None, progress.Add);

        for (var index = 0; index < stats.SheetStats.Count; index++)
        {
            var stat = stats.SheetStats[index];
            Assert.Single(progress, update =>
                update.SheetName == stat.SheetName
                && update.SheetsCompleted == index + 1
                && update.RowsWritten == stat.RowsWritten);
        }
        Assert.Equal(
            progress.Select(update => update.SheetsCompleted).Order(),
            progress.Select(update => update.SheetsCompleted));
    }

    [Fact]
    public async Task WriteAsync_LongDataSheets_ReportRowsBeforeTheirCompletionEvent()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host, voucherCount: 3);
        await ImportContinuationReferencesAsync(host, voucherCount: 3);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        var progress = new List<WorkpaperProgress>();
        await using var stream = new MemoryStream();

        var stats = await BuildWriter(
                host,
                new WorkpaperWriterOptions(),
                progressRowInterval: 2)
            .WriteAsync(
                stream,
                await CurrentContextForAsync(host, projectId),
                CancellationToken.None,
                progress.Add);

        AssertIntermediateBeforeCompletion(progress, stats, Step1Sheet, checkpointRows: 2);
        AssertIntermediateBeforeCompletion(progress, stats, Step4Sheet, checkpointRows: 2);
        AssertIntermediateBeforeCompletion(progress, stats, Step41Sheet, checkpointRows: 2);
    }

    [Fact]
    public async Task WriteAsync_Step4ReportsSheetStartBeforeFirstRowOrCompletion()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host, voucherCount: 3);
        await ImportContinuationReferencesAsync(host, voucherCount: 3);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        var progress = new List<WorkpaperProgress>();
        await using var stream = new MemoryStream();

        await ((IWorkpaperPlanWriter)BuildWriter(host)).WriteAsync(
            stream,
            await CurrentContextForAsync(host, projectId),
            await CurrentPlanForAsync(
                host,
                await CurrentContextForAsync(host, projectId)),
            CancellationToken.None,
            progress.Add);

        var step3Completion = Assert.Single(
            progress,
            update => update.SheetName == Step3Sheet);
        var step4Updates = progress
            .Where(update => update.SheetName == Step4Sheet)
            .ToArray();
        var start = Assert.Single(
            step4Updates,
            update => update.SheetsCompleted == step3Completion.SheetsCompleted
                      && update.RowsWritten == 0);
        var completion = Assert.Single(
            step4Updates,
            update => update.SheetsCompleted == step3Completion.SheetsCompleted + 1);
        Assert.True(
            progress.IndexOf(start) < progress.IndexOf(completion),
            "Step4 sheet-start checkpoint must precede its first completion event.");
    }

    [Fact]
    public async Task FinalizedStep4_StreamsOnceAndLateLongTextUsesFullDataWidths()
    {
        const int voucherCount = 757;
        using var host = new HandlerTestHost();
        var longScenarioName = string.Concat(Enumerable.Repeat("晚頁高風險條件", 8));
        var longScenarioRationale = "LATE-RATIONALE-" + new string('R', 72);
        var longDocumentNumber = "ZZZ-VOUCHER-" + new string('9', 70);
        var longCreator = "ZZZ-" + string.Concat(Enumerable.Repeat("晚頁編製人員", 9));
        var projectId = await SetupContinuationMatrixAsync(host, voucherCount);
        await ImportContinuationReferencesAsync(host, voucherCount);
        await host.DispatchAsync(
            "filter.commit",
            ContinuationScenarioPayload(longScenarioName, longScenarioRationale));
        await ExecuteProjectSqlAsync(
            host,
            projectId,
            """
            UPDATE target_gl_entry
            SET document_number = @documentNumber,
                created_by = @creator
            WHERE document_number = @originalDocumentNumber;
            """,
            ("@documentNumber", longDocumentNumber),
            ("@creator", longCreator),
            ("@originalDocumentNumber", $"PAGE-{voucherCount:000}"));
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var captured = new CapturingStep4StreamRepository(
            new LocalTagMatrixVoucherPageRepository(database));
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();

        var stats = await ((IWorkpaperPlanWriter)BuildWriter(
                host,
                tagMatrixVouchersOverride: captured))
            .WriteAsync(
                stream,
                context,
                plan,
                CancellationToken.None);

        Assert.Equal(0, captured.PublicPageCalls);
        Assert.Equal(1, captured.StreamCalls);
        Assert.Equal(
            voucherCount,
            Assert.Single(
                stats.SheetStats,
                item => item.SheetName == Step4Sheet).RowsWritten);

        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var step3 = workbook.Worksheet(Step3Sheet);
        Assert.Equal(longScenarioName, step3.Cell("C19").GetString());
        Assert.Equal(longScenarioRationale, step3.Cell("D19").GetString());
        AssertFullDataWidth(step3, 3, longScenarioName);
        AssertFullDataWidth(step3, 4, longScenarioRationale);
        AssertSafeCountColumn(step3, 5);
        AssertSingleLine(step3.Cell("C19"));
        AssertSingleLine(step3.Cell("D19"));
        AssertSingleLine(step3.Cell("E19"));

        var step4 = workbook.Worksheet(Step4Sheet);
        Assert.Equal("PAGE-001", step4.Cell("B13").GetString());
        Assert.Equal(longDocumentNumber, step4.Cell(12 + voucherCount, 2).GetString());
        Assert.Equal(longCreator, step4.Cell(12 + voucherCount, 4).GetString());
        Assert.Equal(voucherCount, step4.Cell(12 + voucherCount, 1).GetValue<int>());
        Assert.Equal("Y", step4.Cell("F13").GetString());
        AssertFullDataWidth(step4, 2, longDocumentNumber);
        AssertFullDataWidth(step4, 4, longCreator);
        AssertSafeAmountColumn(step4, 5);
        AssertSingleLine(step4.Cell(12 + voucherCount, 2));
        AssertSingleLine(step4.Cell(12 + voucherCount, 4));
        AssertSingleLine(step4.Cell(12 + voucherCount, 5));
        AssertSingleLine(step4.Cell(12 + voucherCount, 16));
    }

    [Theory]
    [InlineData(Step12Sheet)]
    [InlineData(Step4Sheet)]
    [InlineData(Step41Sheet)]
    [InlineData(CalendarInfoSheet)]
    public async Task WriteAsync_CancelDuringCollectionBackedSheet_StopsBeforeSheetCompletion(string targetSheet)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host, voucherCount: 3);
        await ImportContinuationReferencesAsync(host, voucherCount: 3);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        using var cancellation = new CancellationTokenSource();
        var progress = new List<WorkpaperProgress>();
        await using var stream = new MemoryStream();
        var context = await CurrentContextForAsync(host, projectId);

        void OnProgress(WorkpaperProgress update)
        {
            progress.Add(update);
            if (update.SheetName == targetSheet && update.RowsWritten == 1)
            {
                cancellation.Cancel();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildWriter(
                host,
                new WorkpaperWriterOptions(),
                progressRowInterval: 1)
            .WriteAsync(
                stream,
                context,
                cancellation.Token,
                OnProgress));

        var intermediate = Assert.Single(progress, update =>
            update.SheetName == targetSheet && update.RowsWritten == 1);
        Assert.DoesNotContain(progress, update =>
            update.SheetName == targetSheet && update.SheetsCompleted > intermediate.SheetsCompleted);
    }

    [Fact]
    public async Task WriteAsync_TypedPlan_CancellationPropagatesBeforeSheetCompletion()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host, voucherCount: 3);
        await ImportContinuationReferencesAsync(host, voucherCount: 3);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        using var cancellation = new CancellationTokenSource();
        var progress = new List<WorkpaperProgress>();
        await using var stream = new MemoryStream();
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        var writer = (IWorkpaperPlanWriter)BuildWriter(
            host,
            new WorkpaperWriterOptions(),
            progressRowInterval: 1);

        void OnProgress(WorkpaperProgress update)
        {
            progress.Add(update);
            if (update.SheetName == Step4Sheet && update.RowsWritten == 1)
            {
                cancellation.Cancel();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(
            stream,
            context,
            plan,
            cancellation.Token,
            OnProgress));

        var intermediate = Assert.Single(progress, update =>
            update.SheetName == Step4Sheet && update.RowsWritten == 1);
        Assert.DoesNotContain(progress, update =>
            update.SheetName == Step4Sheet
            && update.SheetsCompleted > intermediate.SheetsCompleted);
    }

    // ---- 母體建構 ----

    /// <summary>
    /// 不平衡母體:刻意造一個完整性差異科目 + 一張借貸不平傳票,使條件表(step1-1 例外、step1-3)均出現。
    /// - 1101:JV1 借 100/貸 100(GL 淨 0),TB 變動 0 → diff=0(平衡科目,只在 step1 全科目列出)
    /// - 2201:JV2 借 80/貸 80(GL 淨 0),TB 變動 500 → diff=500≠0(step1-3 出)
    /// - 3301:JV3 借 300/貸 100(GL 淨 200,傳票借貸不平),TB 變動 200 → diff=0(只進 step1-1 例外)
    /// </summary>
    private static Task<string> SetupUnbalancedAsync(HandlerTestHost host) =>
        InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                gl.AddRow("JV1", "2025-03-01", "1101", "現金", "說明", "甲", "100.00", 1);
                gl.AddRow("JV1", "2025-03-01", "1101", "現金", "說明", "甲", "100.00", 0);
                gl.AddRow("JV2", "2025-03-02", "2201", "應付帳款", "說明", "乙", "80.00", 1);
                gl.AddRow("JV2", "2025-03-02", "2201", "應付帳款", "說明", "乙", "80.00", 0);
                gl.AddRow("JV3", "2025-03-03", "3301", "資本", "說明", "甲", "300.00", 1);
                gl.AddRow("JV3", "2025-03-03", "3301", "資本", "說明", "甲", "100.00", 0);
            },
            configureTb: tb =>
            {
                tb.AddRow("1101", "現金", 0);
                tb.AddRow("2201", "應付帳款", 500);
                tb.AddRow("3301", "資本", 200);
            });

    /// <summary>
    /// 全平衡母體:每張傳票借貸相等、每科目 TB 變動 == GL 淨額 → 無完整性差異、無借貸不平。
    /// 用於條件表「不出現」的對照(step1-1 無例外表、step1-3 不存在)。
    /// </summary>
    private static Task<string> SetupBalancedAsync(HandlerTestHost host) =>
        InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                // 1101 借 100、6101 貸 100:同傳票借貸相等;各科目 GL 淨額由 TB 對齊
                gl.AddRow("JV1", "2025-03-01", "1101", "現金", "說明", "甲", "100.00", 1);
                gl.AddRow("JV1", "2025-03-01", "6101", "費用", "說明", "甲", "100.00", 0);
            },
            configureTb: tb =>
            {
                tb.AddRow("1101", "現金", 100);   // GL 1101 淨 +100
                tb.AddRow("6101", "費用", -100);  // GL 6101 淨 -100
            });

    /// <summary>命中(backdatedPosting=傳票層+行層)+ 命中(借方行=行層子集)+ 0 命中(天價金額);position 1/2/3。</summary>
    private static string MatrixScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            populationScope = GlPopulationScopeValues.AuditPeriod,
            scenarios = new object[]
            {
                new {
                    name = "提前過帳", rationale = "過帳日期早於傳票日期,可能顯示回溯日期",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } },
                new {
                    name = "借方行", rationale = "借方側可靠性較高之子集",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "drCrOnly", drCr = "debit" } } } } },
                new {
                    name = "天價(打不中)", rationale = "0 命中情境(對照欄集邊界)",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "numRange", field = "amount", from = "999999999999" } } } } }
            }
        });

    /// <summary>
    /// 兩個互斥傳票情境：C1 只命中 DOC-C1，C2 只命中 DOC-C2 的第一條借方行。
    /// DOC-C2 另有一條未命中借方 30，所以 step4 完整傳票借方總額的手算 oracle 是 10+30=40。
    /// </summary>
    private static Task<string> SetupSelectedScenarioMatrixAsync(HandlerTestHost host) =>
        InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns(
                    "傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱",
                    "摘要", "建立人員", "金額", "借方旗標");
                gl.AddRow("DOC-C1", "1", "2025-03-01", "D-C1", "C1 借方", "MATCH-C1", "甲", "10.00", 1);
                gl.AddRow("DOC-C1", "2", "2025-03-01", "C-C1", "C1 貸方", "C1-OTHER", "甲", "10.00", 0);
                gl.AddRow("DOC-C2", "1", "2025-03-02", "D-C2-A", "C2 命中借方", "MATCH-C2", "乙", "10.00", 1);
                gl.AddRow("DOC-C2", "2", "2025-03-02", "D-C2-B", "C2 未命中借方", "C2-OTHER", "乙", "30.00", 1);
                gl.AddRow("DOC-C2", "3", "2025-03-02", "C-C2", "C2 貸方", "C2-CREDIT", "乙", "40.00", 0);
            },
            configureTb: tb =>
            {
                tb.AddRow("D-C1", "C1 借方", 10);
                tb.AddRow("C-C1", "C1 貸方", -10);
                tb.AddRow("D-C2-A", "C2 命中借方", 10);
                tb.AddRow("D-C2-B", "C2 未命中借方", 30);
                tb.AddRow("C-C2", "C2 貸方", -40);
            },
            validateForDownstream: true);

    private static string SelectedScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            populationScope = GlPopulationScopeValues.AuditPeriod,
            scenarios = new object[]
            {
                new {
                    name = "C1 專屬", rationale = "只命中 DOC-C1",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "text", field = "description", keywords = "MATCH-C1", mode = "contains" } } } } },
                new {
                    name = "C2 專屬", rationale = "只命中 DOC-C2 的一行",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "text", field = "description", keywords = "MATCH-C2", mode = "contains" } } } } }
            }
        });

    private static Task<string> SetupContinuationMatrixAsync(HandlerTestHost host, int voucherCount = 26) =>
        InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns(
                    "傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                for (var index = 1; index <= voucherCount; index++)
                {
                    var documentNumber = $"PAGE-{index:000}";
                    var debitAccount = $"D{index:000}";
                    var creator = $"人員{index:000}";
                    gl.AddRow(documentNumber, "2025-06-30", debitAccount, $"借方科目{index:000}",
                        "續頁測試", creator, "10.00", 1);
                    gl.AddRow(documentNumber, "2025-06-30", "C999", "貸方科目",
                        "續頁測試", creator, "9.00", 0);
                }
            },
            configureTb: tb =>
            {
                for (var index = 1; index <= voucherCount; index++)
                {
                    tb.AddRow($"D{index:000}", $"借方科目{index:000}", 10);
                }
                tb.AddRow("C999", "貸方科目", voucherCount * -9);
            },
            validateForDownstream: true);

    private static async Task ImportContinuationReferencesAsync(
        HandlerTestHost host,
        int voucherCount = 26)
    {
        var mappingFile = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "GL_NUMBER";
            sheet.Cell(1, 2).Value = "GL_NAME";
            sheet.Cell(1, 3).Value = "STANDARDIZED_ACCOUNT_NAME";
            for (var index = 1; index <= voucherCount; index++)
            {
                sheet.Cell(index + 1, 1).Value = $"D{index:000}";
                sheet.Cell(index + 1, 2).Value = $"借方科目{index:000}";
                sheet.Cell(index + 1, 3).Value = "Cash";
            }
            sheet.Cell(voucherCount + 2, 1).Value = "C999";
            sheet.Cell(voucherCount + 2, 2).Value = "貸方科目";
            sheet.Cell(voucherCount + 2, 3).Value = "Others";
        });

        try
        {
            await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
            {
                filePath = mappingFile,
                fileName = "continuation-account-mapping.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(mappingFile);
        }

        var holidays = Enumerable.Range(1, 18)
            .Select(day => $"2025-01-{day:00}")
            .ToArray();
        var makeups = Enumerable.Range(1, 18)
            .Select(day => $"2025-02-{day:00}")
            .ToArray();
        await host.DispatchAsync("import.holiday", JsonSerializer.Serialize(new { dates = holidays }));
        await host.DispatchAsync("import.makeupDay", JsonSerializer.Serialize(new { dates = makeups }));
    }

    private static string ContinuationScenarioPayload(
        string name = "所有借方",
        string rationale = "建立可預期的傳票與行層續頁母體") =>
        JsonSerializer.Serialize(new
        {
            populationScope = GlPopulationScopeValues.AuditPeriod,
            scenarios = new[]
            {
                new
                {
                    name,
                    rationale,
                    groups = new[]
                    {
                        new
                        {
                            join = "and",
                            rules = new[] { new { join = "and", type = "drCrOnly", drCr = "debit" } }
                        }
                    }
                }
            }
        });

    private sealed class FixedCompletenessDiffPageRepository(
        IReadOnlyList<CompletenessDiffAccount> rows) : ICompletenessDiffPageRepository
    {
        private readonly IReadOnlyList<CompletenessDiffAccount> _orderedRows = rows
            .OrderBy(row => row.AccountCode, StringComparer.Ordinal)
            .ToArray();

        public Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
            string projectId,
            int moneyScale,
            string periodStart,
            string periodEnd,
            PageRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var after = PageCursor.TryDecode(request.Cursor, out var decoded)
                ? decoded
                : null;
            var remaining = _orderedRows
                .Where(row => after is null
                    || string.CompareOrdinal(row.AccountCode, after) > 0)
                .ToArray();
            var pageRows = remaining.Take(request.ClampedPageSize).ToArray();
            var nextCursor = pageRows.Length < remaining.Length
                ? PageCursor.Encode(pageRows[^1].AccountCode)
                : null;
            return Task.FromResult(
                new PageResult<CompletenessDiffAccount>(pageRows, nextCursor));
        }
    }

    private static async Task<XLWorkbook> WriteDemoAndReadAsync(HandlerTestHost host, string projectId)
    {
        var stream = new MemoryStream();
        await BuildWriter(host).WriteAsync(
            stream, await CurrentContextForAsync(host, projectId), CancellationToken.None);
        stream.Position = 0;
        return new XLWorkbook(stream);
    }

    // ================= Task 4:step2 可靠性(legacy 借貸代號＋帶號金額)=================

    [Fact]
    public async Task Step2_SampleRows_LegacyDirectionAndSignedAmount_MatchInfSampleRecount()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        var validate = await host.DispatchAsync("validate.run");
        var sampleSize = validate.GetProperty("infSamplingTest").GetProperty("sampleSize").GetInt64();
        Assert.Equal(59, sampleSize);

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(Step2Sheet);

        // 資料第 53 列起;A 樣本序號為列序 1..N(emitter 自累計),B 傳票號連續至首空。
        var docs = ReadColumnFrom(sheet, "B", 53);
        Assert.Equal(sampleSize, docs.Count);
        Assert.Equal("1", sheet.Cell("A53").GetString()); // A 欄樣本序號自 1 起
        Assert.Equal(sampleSize, (long)sheet.Cell($"A{52 + (int)sampleSize}").GetDouble());
        Assert.True(sheet.Cell("A112").IsEmpty());
        Assert.Single(sheet.DataValidations.GetAllInRange(sheet.Cell("S111").AsRange().RangeAddress));
        Assert.Empty(sheet.DataValidations.GetAllInRange(sheet.Cell("S112").AsRange().RangeAddress));

        // E/F 依範本為原始借貸代號與帶號金額；取首樣本 entry 獨立 recount。
        var firstEntryId = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT g.entry_id FROM result_inf_sampling_test_sample s " +
            "JOIN target_gl_entry g ON g.entry_id = s.entry_id " +
            "WHERE s.run_id = (SELECT run_id FROM result_rule_run WHERE run_kind='validate' " +
            "                  ORDER BY generated_utc DESC, run_id DESC LIMIT 1) " +
            "ORDER BY g.entry_id LIMIT 1;");
        var expectedDebit = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT debit_amount_scaled FROM target_gl_entry WHERE entry_id=@id;", ("@id", firstEntryId));
        var expectedCredit = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT credit_amount_scaled FROM target_gl_entry WHERE entry_id=@id;", ("@id", firstEntryId));

        // 首樣本在第 53 列(INF 抽樣 ORDER BY entry_id 升冪,emitter 逐頁同序)。
        Assert.Equal(expectedCredit > 0 ? "0" : "1", sheet.Cell("E53").GetString());
        Assert.Equal(
            (double)(expectedDebit - expectedCredit) / MoneyScale,
            sheet.Cell("F53").GetDouble());
        AssertSafeAmountColumn(sheet, 6);
        AssertSingleLine(sheet.Cell("F53"));

        // Demo 未配對來源模組；L 欄採 current-required 核准人員欄標，T 說明手填留空。
        Assert.True(sheet.Cell("J53").IsEmpty());
        Assert.Equal("傳票核准人員", sheet.Cell("L51").GetString());
        Assert.True(sheet.Cell("T53").IsEmpty());
    }

    [Fact]
    public async Task Step2_LateSampleLongText_UsesFullDataWidthsAndSingleLineStyles()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        var validate = await host.DispatchAsync("validate.run");
        Assert.Equal(
            59,
            validate.GetProperty("infSamplingTest").GetProperty("sampleSize").GetInt64());
        var context = await CurrentContextForAsync(host, ctx.ProjectId);
        var longDocumentNumber = "ZZZ-SAMPLE-" + new string('8', 68);
        var longAccountCode = "ZZZ-ACCOUNT-" + new string('A', 56);
        var longAccountName = string.Concat(Enumerable.Repeat("晚頁樣本科目名稱", 9));
        var longCreator = string.Concat(Enumerable.Repeat("晚頁編製人員", 8));
        var cappedDescription = new string('界', 140);
        var longApprover = "APPROVER-" + new string('Z', 58);

        await ExecuteProjectSqlAsync(
            host,
            ctx.ProjectId,
            """
            UPDATE target_gl_entry
            SET document_number = @documentNumber,
                account_code = @accountCode,
                account_name = @accountName,
                created_by = @creator,
                approved_by = @approver,
                document_description = @description
            WHERE entry_id = (
                SELECT MAX(entry_id)
                FROM result_inf_sampling_test_sample
                WHERE run_id = @runId
            );
            """,
            ("@documentNumber", longDocumentNumber),
            ("@accountCode", longAccountCode),
            ("@accountName", longAccountName),
            ("@creator", longCreator),
            ("@approver", longApprover),
            ("@description", cappedDescription),
            ("@runId", context.ValidationRunId));

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(Step2Sheet);
        var lateRow = FindRowByColumnValue(sheet, "B", longDocumentNumber, 53);
        Assert.Equal(111, lateRow);
        Assert.Equal(longAccountName, sheet.Cell(lateRow, 4).GetString());
        Assert.Equal(cappedDescription, sheet.Cell(lateRow, 11).GetString());
        AssertFullDataWidth(sheet, 2, longDocumentNumber);
        AssertFullDataWidth(sheet, 3, longAccountCode);
        AssertFullDataWidth(sheet, 4, longAccountName);
        AssertFullDataWidth(sheet, 9, longCreator);
        AssertFullDataWidth(sheet, 11, cappedDescription);
        AssertFullDataWidth(sheet, 12, longApprover);
        AssertSingleLine(sheet.Cell(lateRow, 2));
        AssertSingleLine(sheet.Cell(lateRow, 4));
        AssertSingleLine(sheet.Cell(lateRow, 11));
        AssertSingleLine(sheet.Cell(lateRow, 20));
    }

    [Fact]
    public async Task Step2To4_DynamicDataCells_PreserveTemplateFillAndBorders()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());
        var outputBytes = await WriteToBytesAsync(host, ctx.ProjectId);
        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            "WorkingPaper.xlsx");

        using var template = SpreadsheetDocument.Open(templatePath, false);
        using var output = SpreadsheetDocument.Open(
            new MemoryStream(outputBytes, writable: false),
            false);

        AssertTemplateFillAndBorder(
            template,
            output,
            Step2Sheet,
            ["A53", "F53", "M53", "T53"]);
        AssertTemplateFillAndBorder(
            template,
            output,
            Step3Sheet,
            ["B19", "C19", "D19", "E19"]);
        AssertTemplateFillAndBorder(
            template,
            output,
            Step4Sheet,
            ["A13", "E13", "F13", "O13"]);
    }

    [Fact]
    public async Task Step4_ContinuationFirstDataRow_PreservesTemplateFillAndBorders()
    {
        const int voucherCount = 9;
        const uint continuationRowLimit = 20;
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host, voucherCount);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());

        await using var stream = new MemoryStream();
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        var writer = (IWorkpaperPlanWriter)BuildWriter(
            host,
            new WorkpaperWriterOptions(continuationRowLimit));
        var stats = await writer.WriteAsync(
            stream,
            context,
            plan,
            CancellationToken.None);
        var continuationSheetName = ExcelWorksheetConstraints.ContinuationSheetName(Step4Sheet, 2);
        var continuationStats = Assert.Single(
            stats.SheetStats,
            stat => string.Equals(
                stat.SheetName,
                continuationSheetName,
                StringComparison.Ordinal));
        Assert.Equal(1L, continuationStats.RowsWritten);

        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            "WorkingPaper.xlsx");
        using var template = SpreadsheetDocument.Open(templatePath, false);
        stream.Position = 0;
        using var output = SpreadsheetDocument.Open(stream, false);

        AssertTemplateFillAndBorder(
            template,
            output,
            Step4Sheet,
            ["A13", "E13", "F13", "O13", "P13", "U13"],
            continuationSheetName);
        AssertTemplateCellFormats(
            template,
            output,
            Step4Sheet,
            ["A1", "A5", "B5", "A11", "E11"],
            continuationSheetName);
    }

    [Fact]
    public async Task Step4_FirstPageRowsBeyondTemplateExtent_PreserveTemplateFillAndBorders()
    {
        const int voucherCount = 989;
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host, voucherCount);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        var outputBytes = await WriteToBytesAsync(host, projectId);
        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            "WorkingPaper.xlsx");

        using var template = SpreadsheetDocument.Open(templatePath, false);
        using var output = SpreadsheetDocument.Open(
            new MemoryStream(outputBytes, writable: false),
            false);
        foreach (var column in new[] { "A", "E", "F", "P", "U" })
        {
            AssertTemplateFillAndBorderAt(
                template,
                output,
                Step4Sheet,
                expectedReference: $"{column}13",
                outputReference: $"{column}1001");
        }
    }

    [Fact]
    public async Task Step2To4_FillAndBorders_MatchBothTrackedLegacyGoldenSnapshots()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());
        var outputBytes = await WriteToBytesAsync(host, ctx.ProjectId);
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"jet-working-paper-appearance-{Guid.NewGuid():N}.xlsx");
        var targetLocations = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{Step2Sheet}!A53",
            $"{Step2Sheet}!F53",
            $"{Step2Sheet}!M53",
            $"{Step2Sheet}!T53",
            $"{Step3Sheet}!B19",
            $"{Step3Sheet}!C19",
            $"{Step3Sheet}!D19",
            $"{Step3Sheet}!E19",
            $"{Step4Sheet}!A13",
            $"{Step4Sheet}!E13",
            $"{Step4Sheet}!F13",
            $"{Step4Sheet}!O13"
        };

        try
        {
            await File.WriteAllBytesAsync(outputPath, outputBytes);
            var actual = SpreadsheetAppearanceFingerprint.Capture(outputPath).Entries
                .Where(entry => targetLocations.Contains(entry.Location)
                    && (entry.Property.StartsWith("fill.", StringComparison.Ordinal)
                        || entry.Property.StartsWith("border.", StringComparison.Ordinal)))
                .ToArray();
            var fixtureDirectory = Path.Combine(
                TestRepositoryPaths.RepositoryRoot,
                SpreadsheetAppearanceSnapshotSchemaGuard.FixtureRelativeDirectory.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            var workingPaperSnapshots = SpreadsheetAppearanceSnapshotSchemaGuard.ExpectedSnapshots
                .Where(identity => identity.Report == LegacyReportKind.WorkingPaper)
                .ToArray();
            Assert.Equal(2, workingPaperSnapshots.Length);

            foreach (var identity in workingPaperSnapshots)
            {
                var golden = SpreadsheetAppearanceSnapshotSchemaGuard.ReadEntries(
                        await File.ReadAllBytesAsync(Path.Combine(fixtureDirectory, identity.FileName)),
                        identity.Report)
                    .Where(entry => targetLocations.Contains(entry.Location)
                        && (entry.Property.StartsWith("fill.", StringComparison.Ordinal)
                            || entry.Property.StartsWith("border.", StringComparison.Ordinal)))
                    .ToArray();
                Assert.NotEmpty(golden);
                Assert.Equal(golden, actual);
            }
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    // ================= Task 4:step3 高風險條件彙總 =================

    [Fact]
    public async Task Step3_ConditionTable_MatchesScenariosNameRationaleAndVoucherHits()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(Step3Sheet);

        // 欄標第 18 列(C/D/E),資料自第 19 列;B 代號 C{position} 升冪。
        var codes = ReadColumnFrom(sheet, "B", 19);
        Assert.Equal(new[] { "C1", "C2", "C3" }, codes);

        // C1 列:C 條件描述==情境 name、D 原因==rationale、E 符合傳票數==voucherHitCount recount。
        var c1Row = FindRowByColumnValue(sheet, "B", "C1", 19);
        Assert.Equal("提前過帳", sheet.Cell($"C{c1Row}").GetString());
        Assert.Equal("過帳日期早於傳票日期,可能顯示回溯日期", sheet.Cell($"D{c1Row}").GetString());

        var c1Vouchers = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(DISTINCT g.document_number) FROM result_filter_run r " +
            "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
            "WHERE r.scenario_position = 1 AND g.document_number IS NOT NULL;");
        Assert.Equal(c1Vouchers, (long)sheet.Cell($"E{c1Row}").GetDouble());

        // 0 命中情境(C3 天價)仍列出、傳票數 0。
        var c3Row = FindRowByColumnValue(sheet, "B", "C3", 19);
        Assert.Equal(0d, sheet.Cell($"E{c3Row}").GetDouble());
    }

    /// <summary>
    /// WorkingPaper 的 step3 可見情境表固定止於 E 欄；即使 public compatibility
    /// context 帶入舊 ConditionLogic，F 欄也不得再鏡射條件布林式。
    /// </summary>
    [Fact]
    public async Task Step3_VisibleConditionTable_EndsAtColumnE()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());

        // 真 store 列出已存情境 → 真渲染器 → position→條件邏輯（鏡射 ExportWorkpaperStreamHandler 的計算）。
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var saved = await new LocalFilterScenarioStore(database).ListAsync(ctx.ProjectId, CancellationToken.None);
        var conditionLogic = saved.ToDictionary(s => s.Position, s =>
        {
            using var definition = JsonDocument.Parse(s.DefinitionJson);
            return FilterConditionRenderer.Render(definition.RootElement);
        });

        var context = (await CurrentContextForAsync(host, ctx.ProjectId)) with
        {
            ScenarioConditionLogic = conditionLogic
        };
        var stream = new MemoryStream();
        await BuildWriter(host).WriteAsync(stream, context, CancellationToken.None);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(Step3Sheet);

        Assert.All(
            Enumerable.Range(18, 11),
            row => Assert.True(sheet.Cell(row, 6).IsEmpty(), $"F{row} 應保持空白。"));
    }

    // ================= Task 4:step4 符合高風險條件傳票(動態全 position 欄)=================

    [Fact]
    public async Task Step4_VoucherMatrix_DynamicColumnsAllPositions_YMatchesMatchedPositions()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(Step4Sheet);

        // 欄標第 11 列:A 編號/B 傳票號碼/C 總帳日期/D 編製者/E 傳票總金額 + 動態 C1..CN(全 position 升冪)。
        Assert.Equal("傳票號碼", sheet.Cell("B11").GetString());
        Assert.Equal("C1", sheet.Cell("F11").GetString());
        Assert.Equal("C2", sheet.Cell("G11").GetString());
        Assert.Equal("C3", sheet.Cell("H11").GetString()); // 全 position(含 0 命中的 C3)都建欄
        AssertSafeAmountColumn(sheet, 5);
        AssertSingleLine(sheet.Cell("E13"));

        // 資料自第 13 列(第 12 列為固定說明)。取一張命中傳票,核對其 Y 欄集 == matchedPositions。
        var hitDoc = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
            "SELECT g.document_number FROM result_filter_run r " +
            "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
            "WHERE g.document_number IS NOT NULL ORDER BY g.document_number LIMIT 1;"))[0]!;
        var docRow = FindRowByColumnValue(sheet, "B", hitDoc, 13);
        Assert.True(docRow > 0, "命中傳票應出現在 step4");

        var matched = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
                "SELECT DISTINCT r.scenario_position FROM result_filter_run r " +
                "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
                "WHERE g.document_number = @doc ORDER BY r.scenario_position;", ("@doc", hitDoc)))
            .Select(s => int.Parse(s!)).ToHashSet();

        // 動態欄集 C1=F、C2=G、C3=H;每欄依該傳票是否含該 position 標 Y 或空(data-structure 對映)。
        foreach (var (pos, col) in new[] { (1, "F"), (2, "G"), (3, "H") })
        {
            var cell = sheet.Cell($"{col}{docRow}").GetString();
            if (matched.Contains(pos))
            {
                Assert.Equal("Y", cell);
            }
            else
            {
                Assert.NotEqual("Y", cell);
            }
        }

        // 手填欄(P-U)留空。
        Assert.True(sheet.Cell($"P{docRow}").IsEmpty());
        Assert.True(sheet.Cell($"U{docRow}").IsEmpty());
    }

    [Fact]
    public async Task Step4AndStep41_SelectedC2_ExcludeC1OnlyVoucher_AndUseWholeVoucherDebitTotal()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupSelectedScenarioMatrixAsync(host);
        await host.DispatchAsync("filter.commit", SelectedScenarioPayload());

        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var savedScenarios = await new LocalFilterScenarioStore(database)
            .ListAsync(projectId, CancellationToken.None);
        var c2Position = Assert.Single(savedScenarios, scenario => scenario.Name == "C2 專屬").Position;
        Assert.Equal(2, c2Position);

        var context = (await CurrentContextForAsync(host, projectId)) with
        {
            ScenarioPositions = [c2Position]
        };
        await using var stream = new MemoryStream();
        await BuildWriter(host).WriteAsync(stream, context, CancellationToken.None);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);

        // 等價分割負向：C1-only 傳票不得混入只選 C2 的 step4/4-1 母體。
        var vouchers = workbook.Worksheet(Step4Sheet);
        // 範本固定列舉 C1-C10 欄標；選取範圍只控制資料列的 Y，不刪範本文字。
        Assert.Equal("C1", vouchers.Cell("F11").GetString());
        Assert.Equal($"C{c2Position}", vouchers.Cell("G11").GetString());
        Assert.Equal(0, FindRowByColumnValue(vouchers, "B", "DOC-C1", 13));
        var c2VoucherRow = FindRowByColumnValue(vouchers, "B", "DOC-C2", 13);
        Assert.True(c2VoucherRow > 0, "只選 C2 時 DOC-C2 應列入 step4。");
        Assert.Equal(
            new[] { "DOC-C2" },
            ReadColumnFrom(vouchers, "B", 13));

        // 規格 oracle：傳票總額是命中傳票的完整 GL 借方，不是只加總 C2 命中行。
        Assert.Equal(40d, vouchers.Cell(c2VoucherRow, 5).GetDouble());
        Assert.Equal("Y", vouchers.Cell(c2VoucherRow, 7).GetString());

        var details = workbook.Worksheet(Step41Sheet);
        Assert.Equal(0, FindRowByColumnValue(details, "A", "DOC-C1", 6));
        var c2Rows = Enumerable.Range(6, Math.Max(0, details.LastRowUsed()!.RowNumber() - 5))
            .Where(row => details.Cell(row, 1).GetString() == "DOC-C2")
            .ToList();
        Assert.Equal(3, c2Rows.Count); // 命中傳票的三條 GL 行要完整列出。
        Assert.Equal("C1_Tag", details.Cell("Q5").GetString());
        Assert.Equal("C2_Tag", details.Cell("R5").GetString());
        Assert.Equal(1, c2Rows.Count(row => details.Cell(row, 18).GetString() == "Y"));
    }

    [Fact]
    public async Task Step4_ZeroHitSelection_EmitsProtectedFixedHeaderWithZeroVoucherRows()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());
        var context = (await CurrentContextForAsync(host, ctx.ProjectId)) with
        {
            ScenarioPositions = [3]
        };

        await using var stream = new MemoryStream();
        await BuildWriter(host).WriteAsync(stream, context, CancellationToken.None);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(Step4Sheet);

        Assert.Equal(
            Enumerable.Range(1, 10).Select(position => $"C{position}"),
            Enumerable.Range(6, 10).Select(column => sheet.Cell(11, column).GetString()));
        Assert.Empty(ReadColumnFrom(sheet, "B", 13));
        Assert.True(sheet.Protection.IsProtected);
        Assert.All(
            sheet.Range("P13:U13").Cells(),
            cell => Assert.True(cell.IsEmpty()));
    }

    // ================= Task 4:step4-1 行層明細(動態欄只 rowHitCount>0 的 position)=================

    [Fact]
    public async Task Step41_RowMatrix_DynamicColumnsOnlyRowHitPositions_YMatchesMatchedPositions()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());

        var context = await CurrentContextForAsync(host, ctx.ProjectId);
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();
        await ((IWorkpaperPlanWriter)BuildWriter(host)).WriteAsync(
            stream,
            context,
            plan,
            CancellationToken.None);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(Step41Sheet);

        var expectedHeaders = plan.Step41Columns.Select(column => column.Header)
            .Concat(plan.RowHitScenarioPositions.Select(position => $"C{position}_TAG"))
            .ToArray();
        Assert.Equal(
            expectedHeaders,
            Enumerable.Range(1, expectedHeaders.Length)
                .Select(column => sheet.Cell(5, column).GetString())
                .ToArray());
        Assert.True(sheet.Cell(5, expectedHeaders.Length + 1).IsEmpty());

        // rowHitCount>0 的 position 集(獨立 recount)= 動態欄集;0 行命中的 position(如 C3 天價)不建欄。
        var rowHitPositions = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
                "SELECT scenario_position FROM result_filter_run " +
                "GROUP BY scenario_position HAVING COUNT(*) > 0 ORDER BY scenario_position;"))
            .Select(s => int.Parse(s!)).ToList();
        Assert.True(rowHitPositions.Count > 0, "demo 應有行層命中情境");
        Assert.Equal(rowHitPositions, plan.RowHitScenarioPositions);

        // 取一個命中行(entry_id),核對其在 step4-1 對應列的 Y 欄集 == matchedPositions。
        var hitEntryId = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT MIN(entry_id) FROM result_filter_run;");
        var hitDoc = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
            "SELECT document_number FROM target_gl_entry WHERE entry_id=@id;", ("@id", hitEntryId)))[0]!;
        var hitLine = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
            "SELECT line_item FROM target_gl_entry WHERE entry_id=@id;", ("@id", hitEntryId)))[0];

        var matched = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
                "SELECT scenario_position FROM result_filter_run WHERE entry_id=@id ORDER BY scenario_position;",
                ("@id", hitEntryId)))
            .Select(s => int.Parse(s!)).ToHashSet();
        Assert.NotEmpty(matched);

        var documentColumn = plan.Step41Columns
            .Select((column, index) => (column, index))
            .Single(item => string.Equals(
                item.column.Header,
                "傳票號碼_JE",
                StringComparison.Ordinal))
            .index + 1;
        var lineColumn = plan.Step41Columns
            .Select((column, index) => (column, index))
            .Single(item => string.Equals(
                item.column.Header,
                "傳票文件項次_JE_S",
                StringComparison.Ordinal))
            .index + 1;
        var rowNo = Enumerable.Range(
                6,
                Math.Max(0, (sheet.LastRowUsed()?.RowNumber() ?? 5) - 5))
            .Single(row =>
                sheet.Cell(row, documentColumn).GetString() == hitDoc
                && sheet.Cell(row, lineColumn).GetString() == (hitLine ?? string.Empty));
        Assert.True(rowNo > 0, "命中行應出現在 step4-1");

        for (var index = 0; index < rowHitPositions.Count; index++)
        {
            var position = rowHitPositions[index];
            var tagCol = plan.Step41Columns.Count + index + 1;
            var cell = sheet.Cell(rowNo, tagCol).GetString();
            if (matched.Contains(position))
            {
                Assert.Equal("Y", cell);
            }
            else
            {
                Assert.NotEqual("Y", cell);
            }
        }

        var zeroHitPositions = Enumerable.Range(1, 10).Except(rowHitPositions);
        foreach (var position in zeroHitPositions)
        {
            Assert.DoesNotContain(
                $"C{position}_TAG",
                expectedHeaders,
                StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task Step41_FinalSchema_UsesLatePageRawValuesSignedAmountAndFullDataWidths()
    {
        const int rowCount = 205;
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns(
                    "傳票號碼",
                    "傳票項次",
                    "傳票日期",
                    "核准日期",
                    "科目代號",
                    "科目名稱",
                    "摘要",
                    "金額",
                    "借方旗標",
                    "ROW_TOKEN_JE",
                    "LATE_NOCAP_JE",
                    "LATE_CAP_JE",
                    "時間_JE");
                for (var index = 1; index <= rowCount; index++)
                {
                    gl.AddRow(
                        $"DOC-{index:D4}",
                        "01",
                        "2025-03-05",
                        "2025-03-06",
                        "1101",
                        "現金",
                        "命中",
                        index == rowCount ? 123.4567m : 1m,
                        index == rowCount ? 0 : 1,
                        $"SEQ-{index:D4}",
                        index == rowCount ? new string('L', 60) : "short",
                        index == rowCount ? new string('界', 150) : "short",
                        index == rowCount
                            ? new TimeSpan(0, 6, 7, 8, 900)
                            : new TimeSpan(6, 7, 8));
                }
            },
            validateForDownstream: true);
        await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                populationScope = GlPopulationScopeValues.AuditPeriod,
                scenarios = new[]
                {
                    new
                    {
                        name = "完整欄寬",
                        rationale = "晚頁最大值必須參與 AutoFit",
                        groups = new[]
                        {
                            new
                            {
                                join = "and",
                                rules = new[]
                                {
                                    new
                                    {
                                        join = "and",
                                        type = "customKeywords",
                                        keywords = "命中"
                                    }
                                }
                            }
                        }
                    }
                }
            }));

        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();
        var stats = await ((IWorkpaperPlanWriter)BuildWriter(host)).WriteAsync(
            stream,
            context,
            plan,
            CancellationToken.None);

        var step41 = Assert.Single(
            stats.SheetStats,
            stat => string.Equals(stat.SheetName, Step41Sheet, StringComparison.Ordinal));
        Assert.Equal(rowCount, step41.RowsWritten);
        var headers = plan.Step41Columns.Select(column => column.Header)
            .Concat(["C1_TAG"])
            .ToArray();
        var tokenColumn = Array.IndexOf(headers, "ROW_TOKEN_JE") + 1;
        var noCapColumn = Array.IndexOf(headers, "LATE_NOCAP_JE") + 1;
        var capColumn = Array.IndexOf(headers, "LATE_CAP_JE") + 1;
        var timeColumn = Array.IndexOf(headers, "時間_JE") + 1;
        var amountColumn = Array.IndexOf(headers, "傳票金額_JE") + 1;
        var tagColumn = headers.Length;
        Assert.All(
            new[] { tokenColumn, noCapColumn, capColumn, timeColumn, amountColumn },
            column => Assert.True(column > 0));

        stream.Position = 0;
        using (var workbook = new XLWorkbook(stream))
        {
            var sheet = workbook.Worksheet(Step41Sheet);
            Assert.Equal(
                headers,
                Enumerable.Range(1, headers.Length)
                    .Select(column => sheet.Cell(5, column).GetString())
                    .ToArray());
            Assert.Equal("SEQ-0001", sheet.Cell(6, tokenColumn).GetString());
            Assert.Equal($"SEQ-{rowCount:D4}", sheet.Cell(5 + rowCount, tokenColumn).GetString());
            Assert.Equal(
                new string('L', 60),
                sheet.Cell(5 + rowCount, noCapColumn).GetString());
            Assert.Equal(
                new string('界', 150),
                sheet.Cell(5 + rowCount, capColumn).GetString());
            Assert.Equal(-123.4567d, sheet.Cell(5 + rowCount, amountColumn).GetDouble());
            Assert.Equal("Y", sheet.Cell(5 + rowCount, tagColumn).GetString());
            Assert.True(sheet.Cell(5, headers.Length + 1).IsEmpty());
            Assert.False(sheet.Cell(5 + rowCount, noCapColumn).Style.Alignment.WrapText);
            Assert.Equal(0, sheet.Cell(5 + rowCount, noCapColumn).Style.Alignment.Indent);
            Assert.False(sheet.Cell(5 + rowCount, noCapColumn).Style.Alignment.ShrinkToFit);
        }

        stream.Position = 0;
        using var package = SpreadsheetDocument.Open(stream, false);
        var workbookPart = Assert.IsType<WorkbookPart>(package.WorkbookPart);
        var sheetRef = workbookPart.Workbook.Sheets!.Elements<Sheet>()
            .Single(sheet => string.Equals(
                sheet.Name?.Value,
                Step41Sheet,
                StringComparison.Ordinal));
        var worksheet = Assert.IsType<WorksheetPart>(
                workbookPart.GetPartById(sheetRef.Id!.Value!))
            .Worksheet;
        var widths = Assert.Single(worksheet.Elements<Columns>())
            .Elements<Column>()
            .ToDictionary(column => checked((int)column.Min!.Value));
        Assert.Equal(71d, widths[noCapColumn].Width!.Value);
        Assert.Equal(255d, widths[capColumn].Width!.Value);
        Assert.Equal(12d, widths[timeColumn].Width!.Value);
        Assert.All(widths.Values, column => Assert.True(column.BestFit?.Value));
    }

    [Fact]
    public async Task DataDrivenSheets_ContinueBeforeConfiguredRowLimit_WithoutLossOrJetAddedPhysicalSettings()
    {
        const uint continuationRowLimit = 25;
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host);
        await ImportContinuationReferencesAsync(host);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());

        var completenessDiffs = new FixedCompletenessDiffPageRepository(
            Enumerable.Range(1, 26)
                .Select(index => new CompletenessDiffAccount(
                    AccountCode: $"D{index:000}",
                    AccountName: $"借方科目{index:000}",
                    TbAmountScaled: 110_000,
                    GlAmountScaled: 100_000,
                    DiffScaled: 10_000,
                    NotInTb: false))
                .ToArray());

        await using var stream = new MemoryStream();
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context, completenessDiffs);
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var capturedStep41 = new CapturingStep41PreparedRepository(
            new LocalTagMatrixRowPageRepository(database));
        var writer = (IWorkpaperPlanWriter)BuildWriter(
            host,
            new WorkpaperWriterOptions(continuationRowLimit),
            tagMatrixRowsOverride: capturedStep41,
            completenessDiffsOverride: completenessDiffs);
        var stats = await writer.WriteAsync(
            stream,
            context,
            plan,
            CancellationToken.None);

        var step4Stats = stats.SheetStats.Where(stat => IsSheetSeries(stat.SheetName, Step4Sheet)).ToList();
        var step41Stats = stats.SheetStats.Where(stat => IsSheetSeries(stat.SheetName, Step41Sheet)).ToList();
        Assert.True(step4Stats.Count > 1, "縮小列上限後 step4 應建立續頁");
        Assert.True(step41Stats.Count > 1, "縮小列上限後 step4-1 應建立續頁");
        Assert.Equal(26L, step4Stats.Sum(stat => stat.RowsWritten));
        Assert.Equal(52L, step41Stats.Sum(stat => stat.RowsWritten));
        Assert.All(step4Stats, stat => Assert.InRange(stat.RowsWritten, 1L, 13L));
        Assert.All(step41Stats, stat => Assert.InRange(stat.RowsWritten, 1L, 20L));
        Assert.Equal(step4Stats.Count, step4Stats.Select(stat => stat.SheetName).Distinct().Count());
        Assert.Equal(step41Stats.Count, step41Stats.Select(stat => stat.SheetName).Distinct().Count());
        Assert.Equal(0, capturedStep41.TypedPageCalls);
        var preparedMetrics = Assert.IsType<WorkpaperStep41PreparedSessionMetrics>(
            capturedStep41.Metrics);
        Assert.Equal(1, preparedMetrics.SchemaReadinessCommands);
        Assert.Equal(1, preparedMetrics.Connections);
        Assert.Equal(1, preparedMetrics.Transactions);
        Assert.Equal(1, preparedMetrics.TemporaryTableInitializationCommands);
        Assert.Equal(1, preparedMetrics.HitVoucherMaterializationCommands);
        Assert.Equal(1, preparedMetrics.RowTagMaterializationCommands);
        Assert.Equal(1, preparedMetrics.WidthAggregations);
        Assert.Equal(1, preparedMetrics.OrderedReaderCommands);
        Assert.Equal(1, preparedMetrics.CleanupCommands);
        Assert.Equal(52, preparedMetrics.RowsRead);
        Assert.True(preparedMetrics.ReaderCompleted);
        Assert.True(preparedMetrics.CleanupCommandSucceeded);
        Assert.True(preparedMetrics.TransactionCommitted);
        Assert.False(preparedMetrics.TransactionRolledBack);
        Assert.True(preparedMetrics.DedicatedConnection);
        Assert.True(preparedMetrics.TransactionDisposed);
        Assert.True(preparedMetrics.ConnectionDisposed);
        Assert.True(preparedMetrics.Disposed);
        Assert.True(preparedMetrics.TemporaryObjectsCleared);
        Assert.Equal(
            [
                "schemaReadiness",
                "initializeTemporaryTables",
                "materializeHitVouchers",
                "materializeRowTags",
                "orderedRows",
                "cleanup"
            ],
            preparedMetrics.Commands.Select(command => command.Operation));
        Assert.All(step4Stats.Concat(step41Stats), stat =>
            Assert.InRange(stat.SheetName.Length, 1, ExcelWorksheetConstraints.MaxSheetNameLength));

        var expectedContinuedSeries = new[]
        {
            Step1Sheet,
            Step11Sheet,
            Step12Sheet,
            Step13Sheet,
            Step4Sheet,
            Step41Sheet,
            CalendarInfoSheet,
            AccountMappingSheet
        };
        foreach (var baseName in expectedContinuedSeries)
        {
            Assert.True(stats.SheetStats.Count(stat => IsSheetSeries(stat.SheetName, baseName)) > 1,
                $"{baseName} 的合成資料超過本測試列容量，應建立續頁");
        }
        Assert.Equal(27L, SumSeriesRows(stats, Step1Sheet));
        Assert.Equal(26L, SumSeriesRows(stats, Step11Sheet));
        Assert.Equal(26L, SumSeriesRows(stats, Step12Sheet));
        Assert.Equal(26L, SumSeriesRows(stats, Step13Sheet));
        Assert.Equal(36L, SumSeriesRows(stats, CalendarInfoSheet));
        Assert.Equal(27L, SumSeriesRows(stats, AccountMappingSheet));

        stream.Position = 0;
        using (var workbook = new XLWorkbook(stream))
        {
            var step4Ordinals = new List<long>();
            foreach (var stat in step4Stats)
            {
                var sheet = workbook.Worksheet(stat.SheetName);
                Assert.Equal("傳票號碼", sheet.Cell("B11").GetString());
                Assert.Contains("母體#2", sheet.Cell("A12").GetString(), StringComparison.Ordinal);
                if (!string.Equals(stat.SheetName, Step4Sheet, StringComparison.Ordinal))
                {
                    Assert.True((sheet.LastRowUsed()?.RowNumber() ?? 0) <= continuationRowLimit);
                }
                for (var row = 13; row < 13 + stat.RowsWritten; row++)
                {
                    step4Ordinals.Add(sheet.Cell(row, 1).GetValue<long>());
                }
            }
            Assert.Equal(Enumerable.Range(1, 26).Select(value => (long)value), step4Ordinals);

            var step41Keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stat in step41Stats)
            {
                var sheet = workbook.Worksheet(stat.SheetName);
                Assert.Equal("傳票號碼_JE", sheet.Cell("A5").GetString());
                if (!string.Equals(stat.SheetName, Step41Sheet, StringComparison.Ordinal))
                {
                    Assert.True((sheet.LastRowUsed()?.RowNumber() ?? 0) <= continuationRowLimit);
                }
                for (var row = 6; row < 6 + stat.RowsWritten; row++)
                {
                    Assert.True(step41Keys.Add($"{sheet.Cell(row, 1).GetString()}\u001f{sheet.Cell(row, 2).GetString()}"),
                        "step4-1 續頁不得重複傳票行");
                }
            }
            Assert.Equal(52, step41Keys.Count);

            foreach (var baseName in new[] { CalendarInfoSheet, AccountMappingSheet })
            {
                var seriesWidths = stats.SheetStats
                    .Where(stat => IsSheetSeries(stat.SheetName, baseName))
                    .Select(stat => Enumerable.Range(1, 3)
                        .Select(column => workbook.Worksheet(stat.SheetName).Column(column).Width)
                        .ToArray())
                    .ToArray();
                var expectedWidths = seriesWidths[0];
                Assert.All(
                    seriesWidths.Skip(1),
                    actualWidths => Assert.Equal(expectedWidths, actualWidths));
            }
        }

        stream.Position = 0;
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        foreach (var stat in step4Stats.Concat(step41Stats))
        {
            var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>()
                .Single(item => item.Name?.Value == stat.SheetName);
            var part = Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!.Value!));
            var worksheet = part.Worksheet;
            Assert.NotNull(worksheet.Elements<Columns>().SingleOrDefault());
            if (!string.Equals(stat.SheetName, Step4Sheet, StringComparison.Ordinal)
                && !string.Equals(stat.SheetName, Step41Sheet, StringComparison.Ordinal))
            {
                Assert.Empty(worksheet.Elements<SheetViews>());
                Assert.Empty(worksheet.Descendants<Pane>());
                Assert.Empty(worksheet.Elements<PageMargins>());
                Assert.Empty(worksheet.Elements<PageSetup>());
            }
            Assert.NotNull(worksheet.Elements<SheetProtection>().SingleOrDefault());

            var validations = worksheet.Descendants<DataValidation>().ToArray();
            if (IsSheetSeries(stat.SheetName, Step4Sheet))
            {
                var validation = Assert.Single(validations);
                Assert.Equal(
                    $"P13:T{12 + stat.RowsWritten}",
                    validation.SequenceOfReferences?.InnerText);
                Assert.Equal("\"Y,N,N/A\"", validation.Formula1?.Text);
            }
            else
            {
                Assert.Empty(validations);
            }

            if (IsSheetSeries(stat.SheetName, Step4Sheet))
            {
                if (!string.Equals(stat.SheetName, Step4Sheet, StringComparison.Ordinal))
                {
                    AssertCellUnlocked(workbookPart, worksheet, "P13");
                    AssertCellNumberFormat(workbookPart, worksheet, "E13", "#,##0.0000");
                }
            }
            else
            {
                if (!string.Equals(stat.SheetName, Step41Sheet, StringComparison.Ordinal))
                {
                    var amountColumn = plan.Step41Columns
                        .Select((column, index) => (column, index))
                        .Single(item =>
                            item.column.ValueSource
                            == WorkpaperStep41ValueSource.SignedAmount)
                        .index + 1;
                    var amountReference = $"{ColumnLetters(amountColumn)}6";
                    AssertCellNumberFormat(
                        workbookPart,
                        worksheet,
                        amountReference,
                        "#,##0.0000");
                }
            }

            var headerRow = IsSheetSeries(stat.SheetName, Step4Sheet) ? 11U : 5U;
            var row = worksheet.GetFirstChild<SheetData>()!.Elements<Row>()
                .First(item => item.RowIndex?.Value == headerRow);
            if (!string.Equals(stat.SheetName, Step4Sheet, StringComparison.Ordinal)
                && !string.Equals(stat.SheetName, Step41Sheet, StringComparison.Ordinal))
            {
                Assert.Equal(42d, row.Height!.Value);
            }
            else
            {
                Assert.NotNull(row.Height);
            }
        }

        AssertEditableCells(workbookPart, Step12Sheet, ["C12", "F12", "G12", "H12"]);
        AssertEditableCells(workbookPart, Step13Sheet, ["E17", "F17", "G17"]);
    }

    [Fact]
    public void ExcelWorksheetConstraints_RejectOutOfRangeCells_AndKeepContinuationNamesSafe()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ExcelWorksheetConstraints.EnsureCell(ExcelWorksheetConstraints.MaxRows + 1, 1));
        Assert.Throws<InvalidOperationException>(() =>
            ExcelWorksheetConstraints.EnsureCell(1, ExcelWorksheetConstraints.MaxColumns + 1));

        var name = ExcelWorksheetConstraints.ContinuationSheetName(
            "step4-1 符合高風險條件傳票明細/不合法字元與過長名稱",
            12);
        Assert.InRange(name.Length, 1, ExcelWorksheetConstraints.MaxSheetNameLength);
        Assert.DoesNotContain('/', name);
        Assert.EndsWith(" (續12)", name, StringComparison.Ordinal);
        Assert.Equal(Step4Sheet, ExcelWorksheetConstraints.ContinuationSheetName(Step4Sheet, 1));
    }

    // ================= Task 2:封面 / 固定文字三表(母體不影響,沿用既有逐字 oracle)=================

    [Fact]
    public async Task WriteAsync_EmitsCoverIntroAndStep5Sheets()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var names = workbook.Worksheets.Select(ws => ws.Name).ToList();
        Assert.Contains(CoverSheet, names);
        Assert.Contains(IntroSheet, names);
        Assert.Contains(Step5Sheet, names);
    }

    [Fact]
    public async Task WriteAsync_CoverSheet_WritesCompanyPeriodAndCaatsFileName()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var cover = workbook.Worksheet(CoverSheet);
        Assert.Equal("公司名稱 : 示範科技股份有限公司", cover.Cell("A1").GetString());
        Assert.Equal("測試資料期間 :  2025-01-01 ~ 2025-12-31", cover.Cell("A2").GetString());
        Assert.Equal("篩選測試母體 : 查核期間", cover.Cell("A3").GetString());
        // A6:CAATs 文件檔名,yyyymmdd 取 PeriodEnd 去非數字字元(全形冒號逐字對齊樣本)
        Assert.Equal("請詳：示範科技股份有限公司_CAATS_JE_WP_20251231.docx", cover.Cell("A6").GetString());
    }

    [Fact]
    public async Task WriteAsync_CoverSheet_RemovesTemplateOleAttachmentClosure()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        var outputBytes = await WriteToBytesAsync(host, projectId);
        using var output = SpreadsheetDocument.Open(
            new MemoryStream(outputBytes, writable: false),
            false);
        var workbookPart = Assert.IsType<WorkbookPart>(output.WorkbookPart);
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>()
            .Single(item => string.Equals(
                item.Name?.Value,
                CoverSheet,
                StringComparison.Ordinal));
        var worksheetPart = Assert.IsType<WorksheetPart>(
            workbookPart.GetPartById(sheet.Id!.Value!));

        Assert.Empty(worksheetPart.Worksheet.Elements<Drawing>());
        Assert.Empty(worksheetPart.Worksheet.Elements<LegacyDrawing>());
        Assert.Empty(worksheetPart.Worksheet.Elements<OleObjects>());
        Assert.DoesNotContain(
            worksheetPart.Parts,
            relationship => relationship.OpenXmlPart is DrawingsPart
                or VmlDrawingPart
                or EmbeddedPackagePart
                or ImagePart);
    }

    [Fact]
    public async Task WriteAsync_CoverAndStep3_AuditPeriodScope_AreExplicitAndConsistent()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        await using var stream = new MemoryStream();

        await BuildWriter(host).WriteAsync(
            stream,
            ContextFor(projectId),
            CancellationToken.None);

        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        Assert.Equal(
            "篩選測試母體 : 查核期間",
            workbook.Worksheet(CoverSheet).Cell("A3").GetString());
        var step3 = workbook.Worksheet(Step3Sheet);
        Assert.Contains("2025-01-01 ~ 2025-12-31", step3.Cell("B7").GetString(), StringComparison.Ordinal);
        Assert.Contains("查核期間外與無有效總帳日期", step3.Cell("B9").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step1Header_UsesLastAccountingPeriodStart_AndNeverSubstitutesPeriodEnd()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);

        await using var datedStream = new MemoryStream();
        await BuildWriter(host).WriteAsync(
            datedStream,
            ContextFor(projectId) with { LastPeriodStart = "2023-01-01" },
            CancellationToken.None);
        datedStream.Position = 0;
        using (var workbook = new XLWorkbook(datedStream))
        {
            var header = workbook.Worksheet(Step1Sheet).Cell("A3").GetString();
            Assert.Contains("20230101", header, StringComparison.Ordinal);
            Assert.DoesNotContain("20251231", header, StringComparison.Ordinal);
        }

        await using var missingStream = new MemoryStream();
        await BuildWriter(host).WriteAsync(
            missingStream,
            ContextFor(projectId) with { LastPeriodStart = null },
            CancellationToken.None);
        missingStream.Position = 0;
        using var missingWorkbook = new XLWorkbook(missingStream);
        Assert.EndsWith(
            "N/A",
            missingWorkbook.Worksheet(Step1Sheet).Cell("A3").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_IntroSheet_HasLabelAndMergedBoilerplate()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var intro = workbook.Worksheet(IntroSheet);
        Assert.Equal("說明：", intro.Cell("A1").GetString());
        Assert.Contains("JE Testing Tool", intro.Cell("B1").GetString());
        Assert.Contains("B1:O1", intro.MergedRanges.Select(r => r.RangeAddress.ToString()));
    }

    [Fact]
    public async Task WriteAsync_Step5Sheet_HasYellowBannerMergedAcrossA1R1()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var step5 = workbook.Worksheet(Step5Sheet);
        Assert.Contains("Post-closing entries", step5.Cell("A1").GetString());
        Assert.Contains("A1:R1", step5.MergedRanges.Select(r => r.RangeAddress.ToString()));
    }

    // ================= Task 3:step1 完整性(全科目)=================

    [Fact]
    public async Task Step1_ListsEveryAccount_IncludingZeroDiff()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step1Sheet);

        // 全科目 recount(含 diff=0):CompletenessDiffCte 同語意,不加 tb_s<>gl_s 過濾。
        var allAccounts = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "WITH gl AS (SELECT account_code, SUM(amount_scaled) s FROM target_gl_entry GROUP BY account_code), " +
            "tb AS (SELECT account_code, SUM(change_amount_scaled) s FROM target_tb_balance GROUP BY account_code) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_code FROM tb t LEFT JOIN gl ON gl.account_code=t.account_code " +
            " UNION ALL " +
            " SELECT g.account_code FROM gl g LEFT JOIN tb ON tb.account_code=g.account_code " +
            "   WHERE tb.account_code IS NULL) x;");

        // 第 19 列為欄標,資料自第 20 列起。逐列讀科目編號(B 欄)直到空白。
        var codes = ReadColumnFrom(sheet, "B", 20);
        Assert.Equal(allAccounts, codes.Count);
        // 1101(diff=0)與 2201(diff≠0)都在 → 證實「全科目」(非僅差異)。
        Assert.Contains("1101", codes);
        Assert.Contains("2201", codes);

        // 2201 列:D=TB 變動(scaled→顯示=500)、E=GL 彙總(0)、F=差異(tb_s-gl_s=500)。
        var row2201 = FindRowByColumnValue(sheet, "B", "2201", 20);
        Assert.Equal(500d, sheet.Cell($"D{row2201}").GetDouble());
        Assert.Equal(0d, sheet.Cell($"E{row2201}").GetDouble());
        Assert.Equal(500d, sheet.Cell($"F{row2201}").GetDouble());
        Assert.All(Enumerable.Range(4, 3), column => AssertSafeAmountColumn(sheet, column));
        AssertSingleLine(sheet.Cell(row2201, 6));
    }

    [Fact]
    public async Task Step1Family_LateLongText_UsesFullDataWidthsAndSingleLineStyles()
    {
        using var host = new HandlerTestHost();
        var longAccountCode = "ZZZ-" + new string('A', 72);
        var longAccountName = string.Concat(Enumerable.Repeat("晚頁長科目名稱", 10));
        var longDocumentNumber = "ZZZ-DOC-" + new string('9', 64);
        var longCreator = "ZZZ-" + string.Concat(Enumerable.Repeat("晚頁編製人員", 8));
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns(
                    "傳票號碼",
                    "傳票日期",
                    "科目代號",
                    "科目名稱",
                    "摘要",
                    "建立人員",
                    "金額",
                    "借方旗標");
                gl.AddRow("A-BALANCED", "2025-03-01", "1000", "現金", "短值", "A-USER", 100m, 1);
                gl.AddRow("A-BALANCED", "2025-03-01", "1000", "現金", "短值", "A-USER", 100m, 0);
                gl.AddRow("B-UNBALANCED", "2025-03-02", "2000", "費用", "短值", "B-USER", 20m, 1);
                gl.AddRow("B-UNBALANCED", "2025-03-02", "2000", "費用", "短值", "B-USER", 10m, 0);
                gl.AddRow(
                    longDocumentNumber,
                    "2025-03-03",
                    longAccountCode,
                    longAccountName,
                    "晚頁長值",
                    longCreator,
                    300m,
                    1);
                gl.AddRow(
                    longDocumentNumber,
                    "2025-03-03",
                    longAccountCode,
                    longAccountName,
                    "晚頁長值",
                    longCreator,
                    100m,
                    0);
            },
            configureTb: tb =>
            {
                tb.AddRow("1000", "現金", 0m);
                tb.AddRow("2000", "費用", 10m);
                tb.AddRow(longAccountCode, longAccountName, 700m);
            });

        using var workbook = await WriteAndReadAsync(host, projectId);

        var step1 = workbook.Worksheet(Step1Sheet);
        var step1Row = FindRowByColumnValue(step1, "B", longAccountCode, 20);
        Assert.True(step1Row > 20, "晚頁長科目必須位於短值之後。");
        Assert.Equal(longAccountName, step1.Cell(step1Row, 3).GetString());
        AssertFullDataWidth(step1, 2, longAccountCode);
        AssertFullDataWidth(step1, 3, longAccountName);
        AssertSingleLine(step1.Cell(step1Row, 2));
        AssertSingleLine(step1.Cell(step1Row, 3));

        var step11 = workbook.Worksheet(Step11Sheet);
        var step11Row = FindRowByColumnValue(step11, "B", longDocumentNumber, 15);
        Assert.True(step11Row > 15, "晚頁長傳票必須位於較短的不平傳票之後。");
        AssertFullDataWidth(step11, 2, longDocumentNumber);
        AssertSingleLine(step11.Cell(step11Row, 2));

        var step12 = workbook.Worksheet(Step12Sheet);
        var step12Row = FindRowByColumnValue(step12, "B", longCreator, 12);
        Assert.True(step12Row > 12, "晚頁長編製者必須位於短值之後。");
        AssertFullDataWidth(step12, 2, longCreator);
        AssertSingleLine(step12.Cell(step12Row, 2));
        AssertSingleLine(step12.Cell(step12Row, 8));

        var step13 = workbook.Worksheet(Step13Sheet);
        var step13Row = FindRowByColumnValue(step13, "B", longAccountCode, 17);
        Assert.True(step13Row >= 17);
        Assert.Equal(longAccountName, step13.Cell(step13Row, 3).GetString());
        AssertFullDataWidth(step13, 2, longAccountCode);
        AssertFullDataWidth(step13, 3, longAccountName);
        AssertSingleLine(step13.Cell(step13Row, 2));
        AssertSingleLine(step13.Cell(step13Row, 3));
        AssertSingleLine(step13.Cell(step13Row, 5));
    }

    [Fact]
    public async Task Step1_AccountDiff_MatchesIndependentRecount()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step1Sheet);
        var row2201 = FindRowByColumnValue(sheet, "B", "2201", 20);

        // 獨立 recount:2201 的 tb_s - gl_s(scaled),再除 MoneyScale 還原顯示值。
        var diffScaled = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COALESCE((SELECT SUM(change_amount_scaled) FROM target_tb_balance WHERE account_code='2201'),0) " +
            "     - COALESCE((SELECT SUM(amount_scaled) FROM target_gl_entry WHERE account_code='2201'),0);");

        Assert.Equal((double)diffScaled / MoneyScale, sheet.Cell($"F{row2201}").GetDouble());
    }

    // ================= Task 3:step1-1 借貸不平(條件例外表)=================

    [Fact]
    public async Task Step11_UnbalancedPopulation_EmitsExceptionRow()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step11Sheet);

        // 結論文字恆在(固定區塊);例外表因有不平傳票而出現。JV3 借 300 貸 100 → diff 200。
        var jv3Row = FindRowByColumnValue(sheet, "B", "JV3", 1);
        Assert.True(jv3Row > 0, "借貸不平母體應 emit JV3 例外列");
        Assert.All(Enumerable.Range(3, 3), column => AssertSafeAmountColumn(sheet, column));
        AssertSingleLine(sheet.Cell(jv3Row, 5));
    }

    [Fact]
    public async Task Step11_UnbalancedPopulation_PreservesLegacyHeaderStyles()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        var outputBytes = await WriteToBytesAsync(host, projectId);
        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            "WorkingPaper.xlsx");

        using var template = SpreadsheetDocument.Open(templatePath, false);
        using var output = SpreadsheetDocument.Open(
            new MemoryStream(outputBytes, writable: false),
            false);
        var templateSheet = WorksheetFor(template, Step11Sheet);
        var outputSheet = WorksheetFor(output, Step11Sheet);

        foreach (var reference in new[] { "B14", "C14", "D14", "E14" })
        {
            var templateFormat = ResolveCellFormat(
                template.WorkbookPart!,
                CellFor(templateSheet, reference));
            var outputFormat = ResolveCellFormat(
                output.WorkbookPart!,
                CellFor(outputSheet, reference));
            Assert.Equal(
                templateFormat.NumberFormatId?.Value,
                outputFormat.NumberFormatId?.Value);
            Assert.Equal(
                templateFormat.ApplyNumberFormat?.Value,
                outputFormat.ApplyNumberFormat?.Value);
            Assert.Equal(templateFormat.FillId?.Value, outputFormat.FillId?.Value);
            Assert.Equal(templateFormat.ApplyFill?.Value, outputFormat.ApplyFill?.Value);
            Assert.Equal(templateFormat.BorderId?.Value, outputFormat.BorderId?.Value);
            Assert.Equal(templateFormat.ApplyBorder?.Value, outputFormat.ApplyBorder?.Value);
            Assert.Equal(templateFormat.Alignment?.OuterXml, outputFormat.Alignment?.OuterXml);
            Assert.Equal(
                templateFormat.ApplyAlignment?.Value,
                outputFormat.ApplyAlignment?.Value);
            Assert.Equal(templateFormat.Protection?.OuterXml, outputFormat.Protection?.OuterXml);
            Assert.Equal(
                templateFormat.ApplyProtection?.Value,
                outputFormat.ApplyProtection?.Value);
        }

        foreach (var reference in new[] { "B15", "C15", "D15", "E15" })
        {
            var format = ResolveCellFormat(
                output.WorkbookPart!,
                CellFor(outputSheet, reference));
            Assert.False(format.Alignment?.WrapText?.Value ?? false);
            Assert.Equal(0U, format.Alignment?.Indent?.Value ?? 0U);
            Assert.False(format.Alignment?.ShrinkToFit?.Value ?? false);
        }
    }

    [Fact]
    public async Task Step11_BalancedPopulation_NoExceptionRows_OnlyConclusion()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step11Sheet);

        // 平衡母體:結論文字在,但無任何例外傳票列(條件表不出)。
        Assert.Contains("結論", sheet.Cell("A12").GetString());
        var unbalanced = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COUNT(*) FROM (SELECT document_number FROM target_gl_entry " +
            "GROUP BY document_number HAVING SUM(amount_scaled) <> 0) x;");
        Assert.Equal(0, unbalanced); // 母體前置確認:確實無不平傳票
        Assert.Equal(0, CountColumnFrom(sheet, "B", 13)); // 例外表欄標之後無資料列
    }

    // ================= Task 3:step1-2 編製人員(全名單)=================

    [Fact]
    public async Task Step12_ListsEveryCreator_WithCountsAndAmounts_ManualColumnsBlank()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step12Sheet);

        // 名單筆數 == distinct created_by recount。第 11 列欄標,資料自第 12 列起。
        var distinct = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(DISTINCT created_by) FROM target_gl_entry;");
        var creators = ReadColumnFrom(sheet, "B", 12);
        Assert.Equal(distinct, creators.Count);
        Assert.Contains("甲", creators);
        Assert.Contains("乙", creators);

        // 甲列:D=傳票數、E=金額彙總(借方 scaled→顯示);獨立 recount。
        var jiaRow = FindRowByColumnValue(sheet, "B", "甲", 12);
        var jiaCount = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM target_gl_entry WHERE created_by='甲';");
        var jiaDebit = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COALESCE(SUM(debit_amount_scaled),0) FROM target_gl_entry WHERE created_by='甲';");
        Assert.Equal(jiaCount, (long)sheet.Cell($"D{jiaRow}").GetDouble());
        Assert.Equal((double)jiaDebit / MoneyScale, sheet.Cell($"E{jiaRow}").GetDouble());
        AssertSafeCountColumn(sheet, 4);
        AssertSafeAmountColumn(sheet, 5);
        AssertSingleLine(sheet.Cell(jiaRow, 5));

        // 手填欄(C 自動/人工、F 部門、G 職稱、H 說明)一律空白。
        Assert.True(sheet.Cell($"C{jiaRow}").IsEmpty());
        Assert.True(sheet.Cell($"F{jiaRow}").IsEmpty());
        Assert.True(sheet.Cell($"G{jiaRow}").IsEmpty());
        Assert.True(sheet.Cell($"H{jiaRow}").IsEmpty());
    }

    // ================= Task 3:step1-3 差異說明(僅 diff≠0)=================

    [Fact]
    public async Task Step13_ContainsOnlyNonZeroDiffAccounts_ManualColumnsBlank()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step13Sheet);

        // 僅 diff≠0:2201 在、1101/3301(diff=0)不在。第 16 列欄標,資料自第 17 列起。
        var codes = ReadColumnFrom(sheet, "B", 17);
        var diffCount = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "WITH gl AS (SELECT account_code, SUM(amount_scaled) s FROM target_gl_entry GROUP BY account_code), " +
            "tb AS (SELECT account_code, SUM(change_amount_scaled) s FROM target_tb_balance GROUP BY account_code) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_code FROM tb t LEFT JOIN gl ON gl.account_code=t.account_code " +
            "   WHERE COALESCE(gl.s,0) <> t.s " +
            " UNION ALL " +
            " SELECT g.account_code FROM gl g LEFT JOIN tb ON tb.account_code=g.account_code " +
            "   WHERE tb.account_code IS NULL AND g.s <> 0) x;");

        Assert.Equal(diffCount, codes.Count);
        Assert.Contains("2201", codes);
        Assert.DoesNotContain("1101", codes);
        Assert.DoesNotContain("3301", codes);

        // 手填欄(E 原因、F 調節、G 調節後差異)空白;D 差異金額自動。
        var row2201 = FindRowByColumnValue(sheet, "B", "2201", 17);
        Assert.Equal(500d, sheet.Cell($"D{row2201}").GetDouble());
        AssertSafeAmountColumn(sheet, 4);
        AssertSingleLine(sheet.Cell(row2201, 4));
        Assert.True(sheet.Cell($"E{row2201}").IsEmpty());
        Assert.True(sheet.Cell($"F{row2201}").IsEmpty());
        Assert.True(sheet.Cell($"G{row2201}").IsEmpty());
    }

    // ================= Task 3:step1-3 正準條件表 =================

    [Fact]
    public async Task Step13_ExistsWhenDiffPresent_WithoutRemovedStep131()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheetNames = workbook.Worksheets.Select(ws => ws.Name).ToList();
        Assert.Contains(Step13Sheet, sheetNames);
        Assert.DoesNotContain(RemovedStep131Sheet, sheetNames);

        var diffCount = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "WITH gl AS (SELECT account_code, SUM(amount_scaled) s FROM target_gl_entry GROUP BY account_code), " +
            "tb AS (SELECT account_code, SUM(change_amount_scaled) s FROM target_tb_balance GROUP BY account_code) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_code FROM tb t LEFT JOIN gl ON gl.account_code=t.account_code " +
            "   WHERE COALESCE(gl.s,0) <> t.s " +
            " UNION ALL " +
            " SELECT g.account_code FROM gl g LEFT JOIN tb ON tb.account_code=g.account_code " +
            "   WHERE tb.account_code IS NULL AND g.s <> 0) x;");

        Assert.Equal(diffCount, ReadColumnFrom(workbook.Worksheet(Step13Sheet), "B", 17).Count);

    }

    [Fact]
    public async Task Step13Family_OmittedWhenNoDiff()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        // 全平衡母體(無 diff≠0 科目)→ step1-3 不出現，已移除的工作表永不出現。
        Assert.DoesNotContain(Step13Sheet, workbook.Worksheets.Select(ws => ws.Name));
        Assert.DoesNotContain(RemovedStep131Sheet, workbook.Worksheets.Select(ws => ws.Name));
    }

    // ================= Task 5:自動化工具-檔案欄位資訊(欄位配對正準名)=================

    [Fact]
    public async Task FieldInfo_TypedPath_UsesCanonicalProjectionAndDynamicTwoRowGap()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        var context = await CurrentContextForAsync(host, ctx.ProjectId);
        var plan = await CurrentPlanForAsync(host, context);
        var projection = Assert.IsType<FieldInfoProjection>(plan.FieldInfo);
        await using var stream = new MemoryStream();
        await ((IWorkpaperPlanWriter)BuildWriter(host)).WriteAsync(
            stream,
            context,
            plan,
            CancellationToken.None);

        var firstBlankRow = checked((uint)(4 + projection.TbRows.Count));
        var secondBlankRow = checked(firstBlankRow + 1);
        stream.Position = 0;
        using (var package = SpreadsheetDocument.Open(stream, false))
        {
            var workbookPart = package.WorkbookPart!;
            var sheetRef = workbookPart.Workbook.Sheets!
                .Elements<Sheet>()
                .Single(sheet => string.Equals(
                    sheet.Name?.Value,
                    FieldInfoSheet,
                    StringComparison.Ordinal));
            var worksheet = ((WorksheetPart)workbookPart.GetPartById(sheetRef.Id!))
                .Worksheet;
            var rows = worksheet.GetFirstChild<SheetData>()!
                .Elements<Row>()
                .ToDictionary(row => row.RowIndex!.Value);
            Assert.Empty(rows[firstBlankRow].Elements<Cell>());
            Assert.Empty(rows[secondBlankRow].Elements<Cell>());
        }

        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(FieldInfoSheet);
        Assert.Equal("V.2019", sheet.Cell("A1").GetString());
        Assert.Equal("TB檔案配對前後欄位對照表", sheet.Cell("A2").GetString());

        AssertProjectionRows(sheet, 4, projection.TbRows);
        var glTitleRow = checked((int)secondBlankRow + 1);
        Assert.Equal("GL檔案配對前後欄位對照表", sheet.Cell(glTitleRow, 1).GetString());
        AssertProjectionRows(sheet, glTitleRow + 2, projection.GlRows);

        Assert.All(
            Enumerable.Range(1, 5),
            column => Assert.InRange(sheet.Column(column).Width, 8d, 255d));
        Assert.All(
            Enumerable.Range(6, 3),
            column => Assert.True(sheet.Column(column).IsHidden));
        Assert.DoesNotContain("K_R條件", ReadEntireColumn(sheet, "A"));
    }

    private static void AssertProjectionRows(
        IXLWorksheet sheet,
        int firstRow,
        IReadOnlyList<FieldInfoRow> rows)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            var expected = rows[index];
            var row = firstRow + index;
            Assert.Equal(expected.DisplayName, sheet.Cell(row, 1).GetString());
            Assert.Equal(expected.FieldType, sheet.Cell(row, 2).GetString());
            Assert.Equal(
                expected.TextLength?.ToString() ?? string.Empty,
                sheet.Cell(row, 3).GetString());
            Assert.Equal(
                expected.DecimalPlaces?.ToString() ?? string.Empty,
                sheet.Cell(row, 4).GetString());
            Assert.Equal(expected.ActualFieldName ?? string.Empty, sheet.Cell(row, 5).GetString());
        }
    }

    // ================= Task 5:自動化工具-假期假日資訊(週末固定 + 假日/補班)=================

    [Fact]
    public async Task CalendarInfo_FixedWeekendTable_AndHolidayMakeupCountsMatchStore()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host); // demo 匯入 holiday(13)+ makeup(1)
        var longHolidayName = string.Concat(Enumerable.Repeat("跨年度長假日名稱", 10));
        var holidayFile = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 2).Value = "Holiday Table";
            ws.Cell(2, 1).Value = "Date_of_Holiday";
            ws.Cell(2, 2).Value = "Holiday_Name";
            ws.Cell(2, 3).Value = "IS_Holiday";
            ws.Cell(3, 1).Value = new DateTime(2025, 8, 8);
            ws.Cell(3, 2).Value = longHolidayName;
            ws.Cell(3, 3).Value = "Y";
        });
        try
        {
            await host.DispatchAsync(
                "import.holiday.fromFile",
                JsonSerializer.Serialize(new { filePath = holidayFile }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(holidayFile);
        }

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(CalendarInfoSheet);

        // 週末表固定(資料化常數,非逐列特判):A1/B1 標頭 + 7 列;Mon-Fri=N、Sat/Sun=Y。
        Assert.Equal("DAYOFWEEK", sheet.Cell("A1").GetString());
        Assert.Equal("WORKDAY", sheet.Cell("B1").GetString());
        Assert.Equal("Monday", sheet.Cell("A2").GetString());
        Assert.Equal("N", sheet.Cell("B2").GetString());
        Assert.Equal("Friday", sheet.Cell("A6").GetString());
        Assert.Equal("N", sheet.Cell("B6").GetString());
        Assert.Equal("Saturday", sheet.Cell("A7").GetString());
        Assert.Equal("Y", sheet.Cell("B7").GetString());
        Assert.Equal("Sunday", sheet.Cell("A8").GetString());
        Assert.Equal("Y", sheet.Cell("B8").GetString());

        // 假日表標頭存在(逐字對齊樣本);假日筆數 == calendar store recount(獨立查 staging 表)。
        var holidayHeaderRow = FindRowByColumnValue(sheet, "A", "DATE_OF_HOLIDAY", 1);
        Assert.True(holidayHeaderRow > 0, "應有假日表標頭 DATE_OF_HOLIDAY");
        Assert.Equal("HOLIDAY_NAME", sheet.Cell($"B{holidayHeaderRow}").GetString());
        Assert.Equal("IS_HOLIDAY", sheet.Cell($"C{holidayHeaderRow}").GetString());

        var holidayCount = await DemoProjectPipeline.QueryScalarAsync(
            host, ctx.ProjectId,
            "SELECT COUNT(*) FROM staging_calendar_raw_day WHERE day_type = 'holiday';");
        Assert.True(holidayCount > 0, "demo 應有假日");
        var holidayRows = CountColumnFrom(sheet, "A", holidayHeaderRow + 1);
        // 假日列含補班標頭以下會中斷(補班標頭在空列之後),故先到首個空白即止——此處假日段連續。
        var isHolidayMarks = CountColumnFrom(sheet, "C", holidayHeaderRow + 1);
        Assert.Equal(holidayCount, (long)isHolidayMarks);
        Assert.True(holidayRows >= holidayCount);

        // 補班段標頭 + 筆數 == makeup recount。
        var makeupHeaderRow = FindRowByColumnValue(sheet, "A", "DATE_OF_MAKEUPDAY", 1);
        Assert.True(makeupHeaderRow > 0, "應有補班段標頭 DATE_OF_MAKEUPDAY");
        Assert.Equal("MAKEUPDAY_DESC", sheet.Cell($"B{makeupHeaderRow}").GetString());

        var makeupCount = await DemoProjectPipeline.QueryScalarAsync(
            host, ctx.ProjectId,
            "SELECT COUNT(*) FROM staging_calendar_raw_day WHERE day_type = 'makeup';");
        Assert.Equal(makeupCount, (long)CountColumnFrom(sheet, "A", makeupHeaderRow + 1));

        // 假日 IS_HOLIDAY 一律 Y(對齊樣本)。
        Assert.Equal("Y", sheet.Cell($"C{holidayHeaderRow + 1}").GetString());
        Assert.Equal(longHolidayName, sheet.Cell(holidayHeaderRow + 1, 2).GetString());
        var expectedNameWidth = ExcelDisplayWidth.ToColumnWidth(
            ExcelDisplayWidth.Measure(longHolidayName));
        Assert.InRange(sheet.Column(2).Width, expectedNameWidth - 1.5D, expectedNameWidth + 1.5D);
        AssertSingleLine(sheet.Cell(holidayHeaderRow + 1, 2));
    }

    // ================= Task 5:自動化工具-科目配對資訊(Not-in-TB 字面值)=================

    [Fact]
    public async Task AccountMapping_NotInTbAccount_WritesLiteral_OthersWriteAccountName()
    {
        using var host = new HandlerTestHost();
        var longAccountName = string.Concat(Enumerable.Repeat("長科目名稱", 12));

        // 母體:GL 含 1101(TB 有)與 5501(TB 無 → Not in TB);TB 只列 1101。
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                // 過帳日對齊本測試 WorkpaperContext 的期間(2025)——完整性 GL 彙總（含 not-in-tb 判定）
                // 母體限本期 post_date（§2），資料須落在 context.PeriodStart/End 內方可入母體。
                gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                gl.AddRow("JV1", "2025-03-01", "1101", longAccountName, "說明", "甲", "100.00", 1);
                gl.AddRow("JV1", "2025-03-01", "1101", longAccountName, "說明", "甲", "100.00", 0);
                gl.AddRow("JV2", "2025-03-02", "5501", "管理費用", "說明", "乙", "50.00", 1);
                gl.AddRow("JV2", "2025-03-02", "1101", longAccountName, "說明", "乙", "50.00", 0);
            },
            configureTb: tb =>
            {
                tb.AddRow("1101", longAccountName, 0);
                // 5501 刻意不入 TB → 完整性 not_in_tb 集合應含 5501
            });

        // 匯入科目配對檔(含 1101 與 5501,皆給標準分類)。
        var mappingFile = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "GL_NUMBER";
            ws.Cell(1, 2).Value = "GL_NAME";
            ws.Cell(1, 3).Value = "STANDARDIZED_ACCOUNT_NAME";
            ws.Cell(2, 1).Value = "1101"; ws.Cell(2, 2).Value = longAccountName; ws.Cell(2, 3).Value = "Cash";
            ws.Cell(3, 1).Value = "2101"; ws.Cell(3, 2).Value = "收入"; ws.Cell(3, 3).Value = "Revenue";
            ws.Cell(4, 1).Value = "2201"; ws.Cell(4, 2).Value = "應收款"; ws.Cell(4, 3).Value = "Receivables";
            ws.Cell(5, 1).Value = "2301"; ws.Cell(5, 2).Value = "預收款"; ws.Cell(5, 3).Value = "Receipt in advance";
            ws.Cell(6, 1).Value = "5501"; ws.Cell(6, 2).Value = "管理費用"; ws.Cell(6, 3).Value = "Others";
        });
        try
        {
            await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
            {
                filePath = mappingFile,
                fileName = "inline-account-mapping.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(mappingFile);
        }

        using var workbook = await WriteAndReadAsync(host, projectId);
        var sheet = workbook.Worksheet(AccountMappingSheet);

        // 第 1 列標頭(逐字對齊樣本)。
        Assert.Equal("GL_NUMBER", sheet.Cell("A1").GetString());
        Assert.Equal("GL_NAME", sheet.Cell("B1").GetString());
        Assert.Equal("STANDARDIZED_ACCOUNT_NAME", sheet.Cell("C1").GetString());

        // 1101 在 TB → GL_NAME 寫 account_name。
        var row1101 = FindRowByColumnValue(sheet, "A", "1101", 2);
        Assert.True(row1101 > 0, "科目配對應含 1101");
        Assert.Equal(longAccountName, sheet.Cell($"B{row1101}").GetString());
        Assert.Equal("Cash", sheet.Cell($"C{row1101}").GetString());
        var expectedAccountNameWidth = ExcelDisplayWidth.ToColumnWidth(
            ExcelDisplayWidth.Measure(longAccountName));
        Assert.InRange(
            sheet.Column(2).Width,
            expectedAccountNameWidth - 1.5D,
            expectedAccountNameWidth + 1.5D);
        AssertSingleLine(sheet.Cell(row1101, 2));

        // 5501 在 GL 不在 TB → GL_NAME 寫字面「Not in TB」(非 account_name);分類仍寫。
        var row5501 = FindRowByColumnValue(sheet, "A", "5501", 2);
        Assert.True(row5501 > 0, "科目配對應含 5501");
        Assert.Equal("Not in TB", sheet.Cell($"B{row5501}").GetString());
        Assert.Equal("Others", sheet.Cell($"C{row5501}").GetString());
        Assert.Equal(
            AccountMappingCategories.All.Order(StringComparer.Ordinal),
            ReadColumnFrom(sheet, "C", 2).Order(StringComparer.Ordinal));

        // 獨立 recount:5501 確實落在完整性 not-in-tb 集合(GL 有 TB 無)。
        var notInTb = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COUNT(*) FROM (SELECT account_code FROM target_gl_entry " +
            "EXCEPT SELECT account_code FROM target_tb_balance) x WHERE account_code = '5501';");
        Assert.Equal(1, notInTb);
    }


    [Fact]
    public async Task WriteAsync_FullWorkbook_InitializesFirstWorksheetPartWithSheetData()
    {
        using var host = new HandlerTestHost();
        var project = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        using var stream = new MemoryStream();
        var context = await CurrentContextForAsync(host, project.ProjectId);

        var stats = await BuildWriter(host).WriteAsync(stream, context, CancellationToken.None);

        var stat = Assert.IsType<SheetStat>(stats.SheetStats[0]);
        Assert.Equal(CoverSheet, stat.SheetName);
        Assert.Equal(0, stat.RowsWritten);

        stream.Position = 0;
        using var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(stream, false);
        Assert.NotNull(document.WorkbookPart);
        var workbookPart = document.WorkbookPart;
        Assert.NotNull(workbookPart.Workbook.Sheets);
        var sheet = workbookPart.Workbook.Sheets
            .Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
            .First();
        Assert.Equal(CoverSheet, sheet.Name?.Value);

        var relationshipId = sheet.Id?.Value;
        Assert.False(string.IsNullOrWhiteSpace(relationshipId));
        var worksheetPart = Assert.IsType<DocumentFormat.OpenXml.Packaging.WorksheetPart>(
            workbookPart.GetPartById(relationshipId!));
        Assert.Single(worksheetPart.Worksheet.Elements<DocumentFormat.OpenXml.Spreadsheet.SheetData>());
    }

    // ---- 讀回小工具(不含分支邏輯;掃描欄至首個空白)----

    /// <summary>自 startRow 起讀 column 欄連續非空字串值,遇空白即止(資料表自欄標下一列起為密集列,無內部空隙)。</summary>
    private static List<string> ReadColumnFrom(IXLWorksheet sheet, string column, int startRow)
    {
        var values = new List<string>();
        var row = startRow;
        while (!sheet.Cell($"{column}{row}").IsEmpty())
        {
            values.Add(sheet.Cell($"{column}{row}").GetString());
            row++;
        }

        return values;
    }

    private static int CountColumnFrom(IXLWorksheet sheet, string column, int startRow) =>
        ReadColumnFrom(sheet, column, startRow).Count;

    /// <summary>讀整欄(row 1 → LastRowUsed)的非空字串值;欄內可有空列(欄標/區段之間),不以首空白中止。</summary>
    private static List<string> ReadEntireColumn(IXLWorksheet sheet, string column)
    {
        var values = new List<string>();
        var last = sheet.LastRowUsed()?.RowNumber() ?? 0;
        for (var row = 1; row <= last; row++)
        {
            if (!sheet.Cell($"{column}{row}").IsEmpty())
            {
                values.Add(sheet.Cell($"{column}{row}").GetString());
            }
        }

        return values;
    }

    /// <summary>
    /// 回傳 column 欄等於 value 的列號(掃描整張表已用列,容忍表頭/資料間的空列);找不到回 0。
    /// 用 LastRowUsed 而非「連續非空」是因條件表上方有共同表頭(A 欄)造成 B 欄非連續。
    /// </summary>
    private static int FindRowByColumnValue(IXLWorksheet sheet, string column, string value, int startRow)
    {
        var last = sheet.LastRowUsed()?.RowNumber() ?? 0;
        for (var row = startRow; row <= last; row++)
        {
            if (!sheet.Cell($"{column}{row}").IsEmpty()
                && sheet.Cell($"{column}{row}").GetString() == value)
            {
                return row;
            }
        }

        return 0;
    }

    /// <summary>回傳兩欄同時相符的列號(step4-1 行以「傳票號+項次」唯一定位);找不到回 0。</summary>
    private static int FindRowByTwoColumns(
        IXLWorksheet sheet, string columnA, string valueA, string columnB, string? valueB, int startRow)
    {
        var last = sheet.LastRowUsed()?.RowNumber() ?? 0;
        for (var row = startRow; row <= last; row++)
        {
            if (sheet.Cell($"{columnA}{row}").GetString() == valueA
                && sheet.Cell($"{columnB}{row}").GetString() == (valueB ?? string.Empty))
            {
                return row;
            }
        }

        return 0;
    }

    private static bool IsSheetSeries(string candidate, string baseName) =>
        string.Equals(candidate, baseName, StringComparison.Ordinal)
        || candidate.StartsWith(baseName + " (續", StringComparison.Ordinal);

    private static string ColumnLetters(int column)
    {
        var letters = string.Empty;
        while (column > 0)
        {
            var remainder = (column - 1) % 26;
            letters = (char)('A' + remainder) + letters;
            column = (column - 1) / 26;
        }
        return letters;
    }

    private static long SumSeriesRows(ExportStats stats, string baseName) =>
        stats.SheetStats
            .Where(stat => IsSheetSeries(stat.SheetName, baseName))
            .Sum(stat => stat.RowsWritten);

    private static void AssertSafeAmountColumn(IXLWorksheet sheet, int column)
    {
        var expected = ExcelDisplayWidth.ToColumnWidth(
            ExcelDisplayWidth.Measure("-9,223,372,036,854,775,808.0000"));
        Assert.InRange(sheet.Column(column).Width, expected - 1.5D, expected + 1.5D);
    }

    private static void AssertSafeCountColumn(IXLWorksheet sheet, int column)
    {
        var expected = ExcelDisplayWidth.ToColumnWidth(
            ExcelDisplayWidth.Measure("9,223,372,036,854,775,807"));
        Assert.InRange(sheet.Column(column).Width, expected - 1.5D, expected + 1.5D);
    }

    private static void AssertFullDataWidth(
        IXLWorksheet sheet,
        int column,
        string renderedText)
    {
        var expected = ExcelDisplayWidth.ToColumnWidth(
            ExcelDisplayWidth.Measure(renderedText));
        Assert.InRange(
            sheet.Column(column).Width,
            expected - 1.5D,
            expected + 1.5D);
    }

    private static void AssertSingleLine(IXLCell cell)
    {
        Assert.False(cell.Style.Alignment.WrapText);
        Assert.Equal(0, cell.Style.Alignment.Indent);
        Assert.False(cell.Style.Alignment.ShrinkToFit);
    }

    private static void AssertIntermediateBeforeCompletion(
        IReadOnlyList<WorkpaperProgress> progress,
        ExportStats stats,
        string sheetName,
        long checkpointRows)
    {
        var completedIndex = stats.SheetStats
            .Select((stat, index) => (stat, index))
            .Single(item => item.stat.SheetName == sheetName);
        var completion = Assert.Single(progress, update =>
            update.SheetName == sheetName
            && update.SheetsCompleted == completedIndex.index + 1);
        Assert.Equal(completedIndex.stat.RowsWritten, completion.RowsWritten);
        var checkpoint = Assert.Single(progress, update =>
            update.SheetName == sheetName
            && update.SheetsCompleted == completedIndex.index
            && update.RowsWritten == checkpointRows);
        Assert.True(
            progress.ToList().IndexOf(checkpoint) < progress.ToList().IndexOf(completion),
            $"{sheetName} 的列數 checkpoint 必須發生在工作表關閉完成事件之前。");
    }

    private static void AssertEditableCells(
        WorkbookPart workbookPart,
        string sheetName,
        IReadOnlyList<string> cellReferences)
    {
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>()
            .Single(item => item.Name?.Value == sheetName);
        var worksheetPart = Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!.Value!));
        Assert.NotNull(worksheetPart.Worksheet.Elements<SheetProtection>().SingleOrDefault());

        foreach (var reference in cellReferences)
        {
            AssertCellUnlocked(workbookPart, worksheetPart.Worksheet, reference);
        }
    }

    private static void AssertCellUnlocked(
        WorkbookPart workbookPart,
        Worksheet worksheet,
        string cellReference)
    {
        var cell = worksheet.Descendants<Cell>()
            .Single(item => item.CellReference?.Value == cellReference);
        var format = ResolveCellFormat(workbookPart, cell);
        Assert.False(format.Protection?.Locked?.Value ?? true);
    }

    private static void AssertCellNumberFormat(
        WorkbookPart workbookPart,
        Worksheet worksheet,
        string cellReference,
        string expectedFormatCode)
    {
        var cell = worksheet.Descendants<Cell>()
            .Single(item => item.CellReference?.Value == cellReference);
        var format = ResolveCellFormat(workbookPart, cell);
        var numberFormatId = format.NumberFormatId?.Value;
        var numberFormat = workbookPart.WorkbookStylesPart!.Stylesheet.NumberingFormats?
            .Elements<NumberingFormat>()
            .SingleOrDefault(item => item.NumberFormatId?.Value == numberFormatId);
        Assert.Equal(expectedFormatCode, numberFormat?.FormatCode?.Value);
    }

    private static CellFormat ResolveCellFormat(WorkbookPart workbookPart, Cell cell)
    {
        var styleIndex = checked((int)(cell.StyleIndex?.Value ?? 0U));
        return Assert.IsType<CellFormat>(
            workbookPart.WorkbookStylesPart!.Stylesheet.CellFormats!.ElementAt(styleIndex));
    }

    private static void AssertTemplateFillAndBorder(
        SpreadsheetDocument template,
        SpreadsheetDocument output,
        string sheetName,
        IReadOnlyList<string> references,
        string? outputSheetName = null)
    {
        var templatePart = Assert.IsType<WorkbookPart>(template.WorkbookPart);
        var outputPart = Assert.IsType<WorkbookPart>(output.WorkbookPart);
        var templateSheet = WorksheetFor(template, sheetName);
        var outputSheet = WorksheetFor(output, outputSheetName ?? sheetName);

        foreach (var reference in references)
        {
            AssertTemplateFillAndBorderAt(
                templatePart,
                outputPart,
                templateSheet,
                outputSheet,
                reference,
                reference);
        }
    }

    private static void AssertTemplateFillAndBorderAt(
        SpreadsheetDocument template,
        SpreadsheetDocument output,
        string sheetName,
        string expectedReference,
        string outputReference)
    {
        var templatePart = Assert.IsType<WorkbookPart>(template.WorkbookPart);
        var outputPart = Assert.IsType<WorkbookPart>(output.WorkbookPart);
        AssertTemplateFillAndBorderAt(
            templatePart,
            outputPart,
            WorksheetFor(template, sheetName),
            WorksheetFor(output, sheetName),
            expectedReference,
            outputReference);
    }

    private static void AssertTemplateFillAndBorderAt(
        WorkbookPart templatePart,
        WorkbookPart outputPart,
        Worksheet templateSheet,
        Worksheet outputSheet,
        string expectedReference,
        string outputReference)
    {
        var expected = ResolveCellFormat(
            templatePart,
            CellFor(templateSheet, expectedReference));
        var actual = ResolveCellFormat(
            outputPart,
            CellFor(outputSheet, outputReference));
        Assert.Equal(expected.FillId?.Value, actual.FillId?.Value);
        Assert.Equal(expected.ApplyFill?.Value, actual.ApplyFill?.Value);
        Assert.Equal(expected.BorderId?.Value, actual.BorderId?.Value);
        Assert.Equal(expected.ApplyBorder?.Value, actual.ApplyBorder?.Value);
    }

    private static void AssertTemplateCellFormats(
        SpreadsheetDocument template,
        SpreadsheetDocument output,
        string sheetName,
        IReadOnlyList<string> references,
        string outputSheetName)
    {
        var templatePart = Assert.IsType<WorkbookPart>(template.WorkbookPart);
        var outputPart = Assert.IsType<WorkbookPart>(output.WorkbookPart);
        var templateSheet = WorksheetFor(template, sheetName);
        var outputSheet = WorksheetFor(output, outputSheetName);

        foreach (var reference in references)
        {
            var expected = ResolveCellFormat(templatePart, CellFor(templateSheet, reference));
            var actual = ResolveCellFormat(outputPart, CellFor(outputSheet, reference));
            Assert.Equal(expected.OuterXml, actual.OuterXml);
        }
    }

    private static Worksheet WorksheetFor(
        SpreadsheetDocument document,
        string sheetName)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>()
            .Single(item => string.Equals(
                item.Name?.Value,
                sheetName,
                StringComparison.Ordinal));
        return Assert.IsType<WorksheetPart>(
            workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet;
    }

    private static Cell CellFor(Worksheet worksheet, string reference) =>
        worksheet.Descendants<Cell>()
            .Single(cell => cell.CellReference?.Value == reference);

    private sealed class CapturingStep41PreparedRepository(
        LocalTagMatrixRowPageRepository inner)
        : ITagMatrixRowPageRepository, IWorkpaperStep41PreparedSessionFactory
    {
        internal int TypedPageCalls { get; private set; }

        internal WorkpaperStep41PreparedSessionMetrics? Metrics { get; private set; }

        public Task<(
            PageResult<RowTagRow> Page,
            IReadOnlyList<long> EntryIds,
            IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)>
            GetPageAsync(
                string projectId,
                GlPopulationContext context,
                PageRequest request,
                IReadOnlyList<int>? scenarioPositions,
                CancellationToken cancellationToken)
        {
            TypedPageCalls++;
            return inner.GetPageAsync(
                projectId,
                context,
                request,
                scenarioPositions,
                cancellationToken);
        }

        public async Task<IWorkpaperStep41PreparedSession> PrepareAsync(
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
            Metrics = session.Metrics;
            return session;
        }
    }

    private sealed class CapturingStep4StreamRepository(
        LocalTagMatrixVoucherPageRepository inner)
        : ITagMatrixVoucherPageRepository,
          IWorkpaperStep4StreamRepository
    {
        internal int PublicPageCalls { get; private set; }

        internal int StreamCalls { get; private set; }

        public Task<(
            PageResult<VoucherTagRow> Page,
            IReadOnlyDictionary<string, IReadOnlyList<int>> PositionsByDoc)>
            GetPageAsync(
                string projectId,
                GlPopulationContext context,
                PageRequest request,
                IReadOnlyList<int>? scenarioPositions,
                CancellationToken cancellationToken)
        {
            PublicPageCalls++;
            return inner.GetPageAsync(
                projectId,
                context,
                request,
                scenarioPositions,
                cancellationToken);
        }

        async IAsyncEnumerable<WorkpaperStep4VoucherRow>
            IWorkpaperStep4StreamRepository.StreamAsync(
                string projectId,
                GlPopulationContext context,
                IReadOnlyList<int> scenarioPositions,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken)
        {
            StreamCalls++;
            await foreach (var row in ((IWorkpaperStep4StreamRepository)inner)
                               .StreamAsync(
                                   projectId,
                                   context,
                                   scenarioPositions,
                                   cancellationToken))
            {
                yield return row;
            }
        }
    }
}
