using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml;
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
internal static class WorkpaperWriterTestSupport
{
    internal const string CoverSheet = "資料預先整理之說明";
    internal const string IntroSheet = "JE WorkingPaper說明";
    internal const string Step5Sheet = "step5 財務報表關帳後調整之分錄";
    internal const string Step1Sheet = "step1 完整性測試";
    internal const string Step11Sheet = "step1-1 借貸不平測試";
    internal const string Step12Sheet = "step1-2 分錄編製人員說明";
    internal const string Step13Sheet = "step1-3 完整性測試之差異說明";
    internal const string RemovedStep131Sheet = "step1-3-1完整性差異調節";
    internal const string Step2Sheet = "step2 可靠性測試";
    internal const string Step3Sheet = "step3 高風險條件彙總";
    internal const string Step4Sheet = "step4 符合高風險條件傳票";
    internal const string Step41Sheet = "step4-1 符合高風險條件傳票明細";
    internal const string FieldInfoSheet = "自動化工具-檔案欄位資訊";
    internal const string CalendarInfoSheet = "自動化工具-假期假日資訊";
    internal const string AccountMappingSheet = "自動化工具-科目配對資訊";

    internal const int MoneyScale = 10_000;

    // ---- 寫出器組裝(真 SQLite repo;此 task 尚無 handler/DI 入口)----

    internal static WorkpaperWriter BuildWriter(
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

    internal static WorkpaperContext ContextFor(string projectId) => new(
        ProjectId: projectId,
        CompanyName: "示範科技股份有限公司",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastPeriodStart: "2023-01-01",
        MoneyScale: MoneyScale,
        ValidationRunId: string.Empty,
        ScenarioRevision: string.Empty,
        ScenarioPositions: Array.Empty<int>());

    internal static async Task<WorkpaperContext> CurrentContextForAsync(
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

    internal static async Task<WorkpaperPlan> CurrentPlanForAsync(
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
        var facts = await factsPort.ExecuteAsync(
            initial,
            CancellationToken.None);
        return JetAuditProgram.Finalize(initial, facts);
    }

    internal static async Task<XLWorkbook> WriteAndReadAsync(HandlerTestHost host, string projectId)
    {
        var bytes = await WriteToBytesAsync(host, projectId);
        return new XLWorkbook(new MemoryStream(bytes));
    }

    internal static async Task<byte[]> WriteToBytesAsync(HandlerTestHost host, string projectId)
    {
        await using var stream = new MemoryStream();
        var stats = await BuildWriter(host).WriteAsync(
            stream, await CurrentContextForAsync(host, projectId), CancellationToken.None);
        Assert.Equal(stream.Length, stats.BytesWritten);

        return stream.ToArray();
    }

    internal static async Task ExecuteProjectSqlAsync(
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

    // ---- 母體建構 ----

    /// <summary>
    /// 不平衡母體:刻意造一個完整性差異科目 + 一張借貸不平傳票,使條件表(step1-1 例外、step1-3)均出現。
    /// - 1101:JV1 借 100/貸 100(GL 淨 0),TB 變動 0 → diff=0(平衡科目,只在 step1 全科目列出)
    /// - 2201:JV2 借 80/貸 80(GL 淨 0),TB 變動 500 → diff=500≠0(step1-3 出)
    /// - 3301:JV3 借 300/貸 100(GL 淨 200,傳票借貸不平),TB 變動 200 → diff=0(只進 step1-1 例外)
    /// </summary>
    internal static Task<string> SetupUnbalancedAsync(HandlerTestHost host) =>
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
    internal static Task<string> SetupBalancedAsync(HandlerTestHost host) =>
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
    internal static string MatrixScenarioPayload() =>
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
    internal static Task<string> SetupSelectedScenarioMatrixAsync(HandlerTestHost host) =>
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

    internal static string SelectedScenarioPayload() =>
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

    internal static Task<string> SetupContinuationMatrixAsync(HandlerTestHost host, int voucherCount = 26) =>
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

    internal static async Task ImportContinuationReferencesAsync(
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

    internal static string ContinuationScenarioPayload(
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

    internal sealed class FixedCompletenessDiffPageRepository(
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

    internal static async Task<XLWorkbook> WriteDemoAndReadAsync(HandlerTestHost host, string projectId)
    {
        var stream = new MemoryStream();
        await BuildWriter(host).WriteAsync(
            stream, await CurrentContextForAsync(host, projectId), CancellationToken.None);
        stream.Position = 0;
        return new XLWorkbook(stream);
    }

    internal static string? FillArgb(WorkbookPart workbookPart, CellFormat format)
    {
        var fillId = checked((int)(format.FillId?.Value ?? 0U));
        var fill = workbookPart.WorkbookStylesPart!.Stylesheet.Fills!.Elements<Fill>().ElementAt(fillId);
        return fill.PatternFill?.ForegroundColor?.Rgb?.Value;
    }

    internal static void AssertProjectionRows(
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

    // ---- 讀回小工具(不含分支邏輯;掃描欄至首個空白)----

    /// <summary>自 startRow 起讀 column 欄連續非空字串值,遇空白即止(資料表自欄標下一列起為密集列,無內部空隙)。</summary>
    internal static List<string> ReadColumnFrom(IXLWorksheet sheet, string column, int startRow)
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

    internal static int CountColumnFrom(IXLWorksheet sheet, string column, int startRow) =>
        ReadColumnFrom(sheet, column, startRow).Count;

    /// <summary>讀整欄(row 1 → LastRowUsed)的非空字串值;欄內可有空列(欄標/區段之間),不以首空白中止。</summary>
    internal static List<string> ReadEntireColumn(IXLWorksheet sheet, string column)
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
    internal static int FindRowByColumnValue(IXLWorksheet sheet, string column, string value, int startRow)
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
    internal static int FindRowByTwoColumns(
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

    internal static bool IsSheetSeries(string candidate, string baseName) =>
        string.Equals(candidate, baseName, StringComparison.Ordinal)
        || candidate.StartsWith(baseName + " (續", StringComparison.Ordinal);

    internal static string ColumnLetters(int column)
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

    internal static long SumSeriesRows(ExportStats stats, string baseName) =>
        stats.SheetStats
            .Where(stat => IsSheetSeries(stat.SheetName, baseName))
            .Sum(stat => stat.RowsWritten);

    internal static void AssertSafeAmountColumn(IXLWorksheet sheet, int column)
    {
        var expected = ExcelDisplayWidth.ToColumnWidth(
            ExcelDisplayWidth.Measure("-9,223,372,036,854,775,808.0000"));
        Assert.InRange(sheet.Column(column).Width, expected - 1.5D, expected + 1.5D);
    }

    internal static void AssertSafeCountColumn(IXLWorksheet sheet, int column)
    {
        var expected = ExcelDisplayWidth.ToColumnWidth(
            ExcelDisplayWidth.Measure("9,223,372,036,854,775,807"));
        Assert.InRange(sheet.Column(column).Width, expected - 1.5D, expected + 1.5D);
    }

    internal static void AssertFullDataWidth(
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

    internal static void AssertSingleLine(IXLCell cell)
    {
        Assert.False(cell.Style.Alignment.WrapText);
        Assert.Equal(0, cell.Style.Alignment.Indent);
        Assert.False(cell.Style.Alignment.ShrinkToFit);
    }

    internal static void AssertIntermediateBeforeCompletion(
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

    internal static void AssertEditableCells(
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

    internal static void AssertCellUnlocked(
        WorkbookPart workbookPart,
        Worksheet worksheet,
        string cellReference)
    {
        var cell = worksheet.Descendants<Cell>()
            .Single(item => item.CellReference?.Value == cellReference);
        var format = ResolveCellFormat(workbookPart, cell);
        Assert.False(format.Protection?.Locked?.Value ?? true);
    }

    internal static void AssertCellNumberFormat(
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

    internal static CellFormat ResolveCellFormat(WorkbookPart workbookPart, Cell cell)
    {
        var styleIndex = checked((int)(cell.StyleIndex?.Value ?? 0U));
        return Assert.IsType<CellFormat>(
            workbookPart.WorkbookStylesPart!.Stylesheet.CellFormats!.ElementAt(styleIndex));
    }

    internal static void AssertTemplateFillAndBorder(
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

    internal static void AssertTemplateFillAndBorderAt(
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

    internal static void AssertTemplateFillAndBorderAt(
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

    internal static void AssertTemplateCellFormats(
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

    internal static Worksheet WorksheetFor(
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

    internal static Cell CellFor(Worksheet worksheet, string reference) =>
        worksheet.Descendants<Cell>()
            .Single(cell => cell.CellReference?.Value == reference);

    internal sealed class CapturingStep41PreparedRepository(
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

    internal sealed class CapturingStep4StreamRepository(
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

    internal static readonly string[] Stage8TrackWIds =
    [
        "workingpaper-step1-difference-bold",
        "workingpaper-step1-difference-font",
        "workingpaper-step1-difference-size",
        "workingpaper-step1-difference-font-color",
        "workingpaper-step1-balance-block-bold",
        "workingpaper-step1-balance-block-font",
        "workingpaper-step1-balance-block-size",
        "workingpaper-step1-balance-message-font-color",
        "workingpaper-step1-balance-header-font-color",
        "workingpaper-step1-balance-header-fill",
        "workingpaper-step1-preparer-warning-bold",
        "workingpaper-step1-preparer-warning-font",
        "workingpaper-step1-preparer-warning-size",
        "workingpaper-step1-preparer-warning-color",
        "workingpaper-field-info-version-bold",
        "workingpaper-field-info-version-font",
        "workingpaper-field-info-version-size",
        "workingpaper-field-info-version-color",
        "workingpaper-field-info-version-fill",
        "workingpaper-field-info-tb-header-bold",
        "workingpaper-field-info-tb-header-font",
        "workingpaper-field-info-tb-header-size",
        "workingpaper-field-info-tb-header-fill",
        "workingpaper-field-info-gl-header-bold",
        "workingpaper-field-info-gl-header-font",
        "workingpaper-field-info-gl-header-size",
        "workingpaper-field-info-gl-header-fill",
        "workingpaper-field-info-autofit",
        "workingpaper-weekend-header-bold",
        "workingpaper-weekend-header-font",
        "workingpaper-weekend-header-size",
        "workingpaper-holiday-header-bold",
        "workingpaper-holiday-header-font",
        "workingpaper-holiday-header-size",
        "workingpaper-holiday-autofit",
        "workingpaper-makeup-header-bold",
        "workingpaper-makeup-header-font",
        "workingpaper-makeup-header-size",
        "workingpaper-makeup-autofit",
        "workingpaper-account-mapping-header-bold",
        "workingpaper-account-mapping-header-font",
        "workingpaper-account-mapping-header-size",
        "workingpaper-account-mapping-autofit"
    ];

    internal static string[] CanonicalElements<T>(IEnumerable<T> elements)
        where T : OpenXmlElement =>
        elements.Select(CanonicalElement).ToArray();

    internal static string CanonicalElement(OpenXmlElement element)
    {
        var attributes = element.GetAttributes()
            .OrderBy(attribute => attribute.NamespaceUri, StringComparer.Ordinal)
            .ThenBy(attribute => attribute.LocalName, StringComparer.Ordinal)
            .Select(attribute =>
                $"{attribute.NamespaceUri}|{attribute.LocalName}={attribute.Value}");
        var children = element.ChildElements.Select(CanonicalElement);
        var attributeText = string.Join(";", attributes);
        var childText = string.Join(";", children);
        return $"{element.NamespaceUri}|{element.LocalName}[{attributeText}]({childText})";
    }

    internal static IReadOnlyList<LegacyAppearanceRegistryEntry> TrackWEntries() =>
        LegacyAppearanceRegistry.Load().Entries
            .Where(entry => string.Equals(
                entry.Procedure,
                "Step5_Export_Excel_TW",
                StringComparison.Ordinal))
            .ToArray();

    internal static FixedCompletenessDiffPageRepository DifferenceRows(int count) => new(
        Enumerable.Range(1, count)
            .Select(index => new CompletenessDiffAccount(
                $"SYN-{index:000}",
                "Synthetic account",
                110_000,
                100_000,
                10_000,
                false))
            .ToArray());

    internal static async Task<Stage8TrackWWriteResult> WriteStage8TrackWAsync(
        HandlerTestHost host,
        string projectId,
        ICompletenessDiffPageRepository completenessDiffs,
        ICalendarExportRepository calendar,
        IAccountMappingExportRepository accountMappings,
        WorkpaperWriterOptions? options = null)
    {
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context, completenessDiffs);
        var accountMappingStateStore = Assert.IsAssignableFrom<IAccountMappingStore>(accountMappings);
        await using var stream = new MemoryStream();
        var stats = await ((IWorkpaperPlanWriter)BuildWriter(
            host,
            options,
            completenessDiffsOverride: completenessDiffs,
            calendarDaysOverride: calendar,
            accountMappingsOverride: accountMappings,
            accountMappingStateStoreOverride: accountMappingStateStore)).WriteAsync(
                stream,
                context,
                plan,
                CancellationToken.None);
        Assert.Equal(stream.Length, stats.BytesWritten);
        return new Stage8TrackWWriteResult(stream.ToArray(), plan);
    }

    internal static Task<string> SetupStage8MissingPreparerMappingAsync(HandlerTestHost host) =>
        InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns(
                    "傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標");
                gl.AddRow("SYN-DOC", "2025-03-01", "SYN-D", "Synthetic debit", "Synthetic", 100m, 1);
                gl.AddRow("SYN-DOC", "2025-03-01", "SYN-C", "Synthetic credit", "Synthetic", 100m, 0);
            },
            validateForDownstream: true);

    internal static SpreadsheetDocument OpenStage8Document(byte[] bytes) =>
        SpreadsheetDocument.Open(new MemoryStream(bytes, writable: false), false);

    internal static Dictionary<string, Worksheet> WorksheetsByName(SpreadsheetDocument document)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        return workbookPart.Workbook.Sheets!.Elements<Sheet>().ToDictionary(
            sheet => sheet.Name!.Value!,
            sheet => Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet,
            StringComparer.Ordinal);
    }

    internal static IReadOnlyDictionary<string, Stage8AppearanceTarget> Stage8TrackWTargets(uint glHeaderRow)
    {
        var targets = new Dictionary<string, Stage8AppearanceTarget>(StringComparer.Ordinal);
        AddTargets(targets, Stage8TrackWIds.Take(4), Stage8TrackWOutput.Positive, Step1Sheet, Cells(15, 17, 2, 2));
        AddTargets(targets, Stage8TrackWIds.Skip(4).Take(3), Stage8TrackWOutput.Positive, Step11Sheet, Cells(12, 16, 2, 6));
        AddTargets(targets, [Stage8TrackWIds[7]], Stage8TrackWOutput.Positive, Step11Sheet, Cells(12, 15, 2, 2));
        AddTargets(targets, Stage8TrackWIds.Skip(8).Take(2), Stage8TrackWOutput.Positive, Step11Sheet, Cells(16, 16, 2, 6));
        AddTargets(targets, Stage8TrackWIds.Skip(10).Take(4), Stage8TrackWOutput.MissingPreparer, Step12Sheet, ["B13"]);
        AddTargets(targets, Stage8TrackWIds.Skip(14).Take(5), Stage8TrackWOutput.Positive, FieldInfoSheet, ["A1"]);
        AddTargets(targets, Stage8TrackWIds.Skip(19).Take(4), Stage8TrackWOutput.Positive, FieldInfoSheet, Cells(3, 3, 1, 5));
        AddTargets(targets, Stage8TrackWIds.Skip(23).Take(4), Stage8TrackWOutput.Positive, FieldInfoSheet, Cells(glHeaderRow, glHeaderRow, 1, 5));
        targets.Add(Stage8TrackWIds[27], Stage8AppearanceTarget.Columns(Stage8TrackWOutput.Positive, FieldInfoSheet, 1, 5));
        AddTargets(targets, Stage8TrackWIds.Skip(28).Take(3), Stage8TrackWOutput.Positive, CalendarInfoSheet, Cells(1, 1, 1, 2));
        AddTargets(targets, Stage8TrackWIds.Skip(31).Take(3), Stage8TrackWOutput.Positive, CalendarInfoSheet, Cells(10, 10, 1, 3));
        targets.Add(Stage8TrackWIds[34], Stage8AppearanceTarget.Columns(Stage8TrackWOutput.Positive, CalendarInfoSheet, 1, 3));
        AddTargets(targets, Stage8TrackWIds.Skip(35).Take(3), Stage8TrackWOutput.Positive, CalendarInfoSheet, Cells(13, 13, 1, 2));
        targets.Add(Stage8TrackWIds[38], Stage8AppearanceTarget.Columns(Stage8TrackWOutput.Positive, CalendarInfoSheet, 1, 3));
        AddTargets(targets, Stage8TrackWIds.Skip(39).Take(3), Stage8TrackWOutput.Positive, AccountMappingSheet, Cells(1, 1, 1, 3));
        targets.Add(Stage8TrackWIds[42], Stage8AppearanceTarget.Columns(Stage8TrackWOutput.Positive, AccountMappingSheet, 1, 3));
        Assert.Equal(43, targets.Count);
        return targets;
    }

    internal static void AddTargets(
        IDictionary<string, Stage8AppearanceTarget> targets,
        IEnumerable<string> ids,
        Stage8TrackWOutput output,
        string sheet,
        IReadOnlyList<string> cells)
    {
        foreach (var id in ids)
        {
            targets.Add(id, Stage8AppearanceTarget.Cells(output, sheet, cells));
        }
    }

    internal static string[] Cells(uint firstRow, uint lastRow, uint firstColumn, uint lastColumn) =>
        Enumerable.Range(checked((int)firstRow), checked((int)(lastRow - firstRow + 1)))
            .SelectMany(row => Enumerable.Range(
                checked((int)firstColumn),
                checked((int)(lastColumn - firstColumn + 1))),
                (row, column) => $"{ColumnLetters(column)}{row}")
            .ToArray();

    internal static IReadOnlyList<string> Stage8AppearanceMismatches(
        SpreadsheetDocument document,
        LegacyAppearanceRegistryEntry entry,
        Stage8AppearanceTarget target)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var worksheet = WorksheetsByName(document)[target.Sheet];
        var mismatches = new List<string>();
        foreach (var reference in target.CellReferences)
        {
            var actual = ReadCellAppearance(workbookPart, worksheet, reference, entry.Property);
            var expected = entry.Property == "font.name"
                ? "微軟正黑體"
                : entry.NormalizedValue;
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                mismatches.Add($"{entry.Id}|{target.Sheet}!{reference}|{entry.Property}|expected={expected}|actual={actual}");
            }
        }
        foreach (var column in target.ColumnIndexes)
        {
            var actual = ReadColumnAutoFit(worksheet, column);
            if (!string.Equals(entry.NormalizedValue, actual, StringComparison.Ordinal))
            {
                mismatches.Add($"{entry.Id}|{target.Sheet}!column:{column}|{entry.Property}|expected={entry.NormalizedValue}|actual={actual}");
            }
        }
        return mismatches;
    }

    internal static void AssertStage8Condition(
        SpreadsheetDocument document,
        SpreadsheetDocument template,
        IReadOnlyList<LegacyAppearanceRegistryEntry> entries,
        IReadOnlyDictionary<string, Stage8AppearanceTarget> targets,
        IReadOnlyList<string> ids,
        bool expectedApplied)
    {
        var mismatches = ids
            .Select(id => entries.Single(entry => entry.Id == id))
            .SelectMany(entry => Stage8AppearanceMismatches(document, entry, targets[entry.Id]))
            .ToArray();
        if (expectedApplied)
        {
            Assert.Empty(mismatches);
        }
        else
        {
            Assert.NotEmpty(mismatches);
            var actualWorkbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
            var templateWorkbookPart = Assert.IsType<WorkbookPart>(template.WorkbookPart);
            var actualSheets = WorksheetsByName(document);
            var templateSheets = WorksheetsByName(template);
            foreach (var id in ids)
            {
                var entry = entries.Single(item => item.Id == id);
                var target = targets[id];
                foreach (var reference in target.CellReferences)
                {
                    var actual = ReadCellAppearance(
                        actualWorkbookPart,
                        actualSheets[target.Sheet],
                        reference,
                        entry.Property);
                    if (entry.Property == "font.name")
                    {
                        Assert.Equal("微軟正黑體", actual);
                    }
                    else
                    {
                        Assert.Equal(
                            ReadCellAppearance(
                                templateWorkbookPart,
                                templateSheets[target.Sheet],
                                reference,
                                entry.Property),
                            actual);
                    }
                }
                foreach (var column in target.ColumnIndexes)
                {
                    Assert.Equal(
                        ReadColumnAutoFit(templateSheets[target.Sheet], column),
                        ReadColumnAutoFit(actualSheets[target.Sheet], column));
                }
            }
        }
    }

    internal static void AssertStage8Applied(
        SpreadsheetDocument document,
        IReadOnlyList<LegacyAppearanceRegistryEntry> entries,
        IReadOnlyDictionary<string, Stage8AppearanceTarget> targets,
        IEnumerable<string> ids) =>
        Assert.Empty(ids
            .Select(id => entries.Single(entry => entry.Id == id))
            .SelectMany(entry => Stage8AppearanceMismatches(document, entry, targets[entry.Id])));

    internal static void AssertStage8GeneratedCellMatchesBaseline(
        SpreadsheetDocument document,
        IReadOnlyList<LegacyAppearanceRegistryEntry> entries,
        IEnumerable<string> ids,
        string sheetName,
        string targetReference,
        string baselineReference)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var sheet = WorksheetsByName(document)[sheetName];
        foreach (var id in ids)
        {
            var entry = entries.Single(item => item.Id == id);
            Assert.NotEqual(
                entry.NormalizedValue,
                ReadCellAppearance(workbookPart, sheet, targetReference, entry.Property));
            Assert.Equal(
                ReadCellAppearance(workbookPart, sheet, baselineReference, entry.Property),
                ReadCellAppearance(workbookPart, sheet, targetReference, entry.Property));
        }
    }

    internal static void AssertReferenceBaseline(
        SpreadsheetDocument document,
        IReadOnlyList<LegacyAppearanceRegistryEntry> entries,
        IReadOnlyDictionary<string, Stage8AppearanceTarget> targets,
        IEnumerable<string> ids)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var sheets = WorksheetsByName(document);
        foreach (var id in ids)
        {
            var entry = entries.Single(item => item.Id == id);
            var target = targets[id];
            foreach (var reference in target.CellReferences)
            {
                var expected = entry.Property switch
                {
                    "font.bold" => "true",
                    "font.name" => "微軟正黑體",
                    "font.size" => "11",
                    _ => throw new InvalidOperationException(
                        $"Unsupported condition-off property '{entry.Property}'.")
                };
                Assert.Equal(
                    expected,
                    ReadCellAppearance(workbookPart, sheets[target.Sheet], reference, entry.Property));
            }
            foreach (var column in target.ColumnIndexes)
            {
                Assert.Equal("false", ReadColumnAutoFit(sheets[target.Sheet], column));
            }
        }
    }

    internal static void AssertReferenceConditionMatchesBaseline(
        SpreadsheetDocument document,
        SpreadsheetDocument baseline,
        IReadOnlyList<LegacyAppearanceRegistryEntry> entries,
        IReadOnlyDictionary<string, Stage8AppearanceTarget> targets,
        IEnumerable<string> ids,
        IReadOnlyDictionary<string, Stage8AppearanceTarget>? baselineTargets = null)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var baselineWorkbookPart = Assert.IsType<WorkbookPart>(baseline.WorkbookPart);
        var sheets = WorksheetsByName(document);
        var baselineSheets = WorksheetsByName(baseline);
        foreach (var id in ids)
        {
            var entry = entries.Single(item => item.Id == id);
            var target = targets[id];
            var baselineTarget = (baselineTargets ?? targets)[id];
            for (var referenceIndex = 0; referenceIndex < target.CellReferences.Count; referenceIndex++)
            {
                var reference = target.CellReferences[referenceIndex];
                var baselineReference = baselineTarget.CellReferences[referenceIndex];
                Assert.Equal(
                    ReadCellAppearance(
                        baselineWorkbookPart,
                        baselineSheets[baselineTarget.Sheet],
                        baselineReference,
                        entry.Property),
                    ReadCellAppearance(
                        workbookPart,
                        sheets[target.Sheet],
                        reference,
                        entry.Property));
            }
            for (var columnIndex = 0; columnIndex < target.ColumnIndexes.Count; columnIndex++)
            {
                var column = target.ColumnIndexes[columnIndex];
                var baselineColumn = baselineTarget.ColumnIndexes[columnIndex];
                Assert.Equal(
                    ReadColumnAutoFit(baselineSheets[baselineTarget.Sheet], baselineColumn),
                    ReadColumnAutoFit(sheets[target.Sheet], column));
            }
        }
    }

    internal static string ReadStage8CellText(
        SpreadsheetDocument document,
        string sheetName,
        string reference)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var cell = WorksheetsByName(document)[sheetName].Descendants<Cell>()
            .Single(item => string.Equals(item.CellReference?.Value, reference, StringComparison.Ordinal));
        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(cell.CellValue?.Text, out var sharedIndex))
        {
            return workbookPart.SharedStringTablePart!.SharedStringTable!
                .Elements<SharedStringItem>()
                .ElementAt(sharedIndex)
                .InnerText;
        }
        return cell.CellValue?.Text ?? string.Empty;
    }

    internal static string ReadCellAppearance(
        WorkbookPart workbookPart,
        Worksheet worksheet,
        string reference,
        string property)
    {
        var cell = worksheet.Descendants<Cell>()
            .SingleOrDefault(item => string.Equals(item.CellReference?.Value, reference, StringComparison.Ordinal));
        if (cell is null)
        {
            return "<missing-cell>";
        }
        var stylesheet = workbookPart.WorkbookStylesPart!.Stylesheet;
        var format = stylesheet.CellFormats!.Elements<CellFormat>()
            .ElementAt(checked((int)(cell.StyleIndex?.Value ?? 0U)));
        var font = stylesheet.Fonts!.Elements<DocumentFormat.OpenXml.Spreadsheet.Font>()
            .ElementAt(checked((int)(format.FontId?.Value ?? 0U)));
        return property switch
        {
            "font.bold" => (font.Bold is not null && (font.Bold.Val?.Value ?? true)) ? "true" : "false",
            "font.name" => font.FontName?.Val?.Value ?? "<missing-font-name>",
            "font.size" => font.FontSize?.Val?.Value.ToString("0.################", CultureInfo.InvariantCulture)
                ?? "<missing-font-size>",
            "font.colorArgb" => font.Color?.Rgb?.Value?.ToUpperInvariant() ?? "<missing-font-rgb>",
            "fill.foregroundArgb" => stylesheet.Fills!.Elements<Fill>()
                .ElementAt(checked((int)(format.FillId?.Value ?? 0U)))
                .PatternFill?.ForegroundColor?.Rgb?.Value?.ToUpperInvariant() ?? "<missing-fill-rgb>",
            _ => throw new InvalidOperationException($"Unsupported Stage 8 Track W property '{property}'.")
        };
    }

    internal static string ReadColumnAutoFit(Worksheet worksheet, uint index)
    {
        var column = worksheet.GetFirstChild<Columns>()?.Elements<Column>()
            .LastOrDefault(item => item.Min?.Value <= index && item.Max?.Value >= index);
        return column?.BestFit?.Value == true && column.CustomWidth?.Value == true
            ? "true"
            : "false";
    }

    internal sealed record Stage8TrackWWriteResult(byte[] Bytes, WorkpaperPlan Plan);

    internal enum Stage8TrackWOutput
    {
        Positive,
        MissingPreparer
    }

    internal sealed record Stage8AppearanceTarget(
        Stage8TrackWOutput Output,
        string Sheet,
        IReadOnlyList<string> CellReferences,
        IReadOnlyList<uint> ColumnIndexes)
    {
        internal static Stage8AppearanceTarget Cells(
            Stage8TrackWOutput output,
            string sheet,
            IReadOnlyList<string> cells) => new(output, sheet, cells, []);

        internal static Stage8AppearanceTarget Columns(
            Stage8TrackWOutput output,
            string sheet,
            uint first,
            uint last) => new(
                output,
                sheet,
                [],
                Enumerable.Range(checked((int)first), checked((int)(last - first + 1)))
                    .Select(value => checked((uint)value))
                    .ToArray());
    }

    internal sealed class FixedStage8CalendarRepository(
        IReadOnlyList<CalendarDayEntry> holidays,
        IReadOnlyList<CalendarDayEntry> makeups) : ICalendarExportRepository
    {
        internal static FixedStage8CalendarRepository Empty { get; } = new([], []);

        public Task<IReadOnlyList<CalendarDayEntry>> FetchDaysAsync(
            string projectId,
            CalendarDayType type,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(type == CalendarDayType.Holiday ? holidays : makeups);
        }
    }

    internal sealed class FixedStage8AccountMappingRepository(
        IReadOnlyList<AccountMappingExportRow> rows,
        bool sourceImported = true) : IAccountMappingExportRepository, IAccountMappingStore
    {
        internal static FixedStage8AccountMappingRepository Empty { get; } = new([], false);
        internal static FixedStage8AccountMappingRepository ImportedEmpty { get; } = new([], true);

        public Task<IReadOnlyList<AccountMappingExportRow>> FetchAllAsync(
            string projectId,
            string periodStart,
            string periodEnd,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyList<AccountMappingTemplateRow>> FetchTemplateRowsAsync(
            string projectId,
            string periodStart,
            string periodEnd,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AccountMappingTemplateRow> projected = rows
                .Select(row => new AccountMappingTemplateRow(row.AccountCode, row.AccountName))
                .ToArray();
            return Task.FromResult(projected);
        }

        public Task<AccountMappingImportResult> ImportAsync(
            string projectId,
            ImportSourceDescriptor source,
            IReadOnlyList<string> columns,
            IAsyncEnumerable<StagingRow> stagingRows,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Stage 8 fixed account-mapping source is read-only.");

        public Task<AccountMappingState?> FindStateAsync(
            string projectId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<AccountMappingState?>(sourceImported
                ? new AccountMappingState(
                    BatchId: "stage8-synthetic-account-mapping",
                    RowCount: rows.Count,
                    FileName: "stage8-synthetic-account-mapping.xlsx",
                    ImportedUtc: DateTimeOffset.UnixEpoch,
                    HasAnyCategory: rows.Count > 0,
                    HasRevenue: rows.Any(row => string.Equals(
                        row.Category,
                        AccountMappingCategories.Revenue,
                        StringComparison.Ordinal)),
                    HasCounterpart: rows.Any(row => AccountMappingCategories.CounterpartCategories.Contains(
                        row.Category)))
                : null);
        }
    }
}
