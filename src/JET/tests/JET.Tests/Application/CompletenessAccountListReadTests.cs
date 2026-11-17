using System.Text.RegularExpressions;
using ClosedXML.Excel;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 完整性全科目表與差異表在正式輸出裡讀完整份科目清單時，每張表只算一次各科目的 GL 彙總，
/// 讀清單的資料庫工作量跟著科目數成比例增加，不隨頁數重複計算全部科目。
/// 用到這兩份清單的是資料驗證報告的 V_Report 5 與差異說明頁，以及底稿的 Step 1 與 Step 1-3。
///
/// 合成資料每個科目一張傳票：借方記在這個科目 100 元，貸方記在同一個沖轉科目。
/// 試算表每個科目的變動金額是 101 元，所以每個科目都差 1 元；沖轉科目和總帳相同，沒有差異。
/// 科目數多一倍時，總帳列數也多一倍。
/// </summary>
public sealed class CompletenessAccountListReadTests(ITestOutputHelper testOutput)
{
    private const string AccountList = "全科目表";
    private const string DifferenceList = "差異表";
    private const string ClearingAccount = "99999";

    private static readonly string[] OriginalHeaders =
    [
        "傳票號碼",
        "傳票日期",
        "核准日期",
        "科目代號",
        "科目名稱",
        "摘要",
        "金額",
        "借方旗標"
    ];

    // 讀科目清單的查詢：先算各科目的 GL 與 TB 彙總，再依固定順序編號；只列差異的那一份另有差異條件。
    private static readonly Regex AccountListStatement = new(
        @"\bgl_by_account\s+AS\s*\(.*\bordered\s+AS\s*\(",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    [Fact]
    public async Task ValidationReport_AccountAndDifferenceLists_ComputeGlTotalsOncePerSheet_AndReadWorkGrowsWithTheAccounts()
    {
        var small = await WriteValidationReportAsync(600);
        var large = await WriteValidationReportAsync(1_200);

        AssertReadsOncePerList("資料驗證報告", small, large);
    }

    [Fact]
    public async Task WorkingPaper_StepOneAndStepOneThree_ComputeGlTotalsOncePerSheet_AndReadWorkGrowsWithTheAccounts()
    {
        var small = await WriteWorkingPaperAsync(600);
        var large = await WriteWorkingPaperAsync(1_200);

        AssertReadsOncePerList("底稿", small, large);
    }

    private void AssertReadsOncePerList(string document, ListReadWork small, ListReadWork large)
    {
        var measured = $"{document}：全科目表的查詢 {small.AccountStatements} 次與 {large.AccountStatements} 次，"
            + $"差異表的查詢 {small.DifferenceStatements} 次與 {large.DifferenceStatements} 次；"
            + $"全科目表工作量 {small.AccountSteps} 與 {large.AccountSteps}，{Ratio(large.AccountSteps, small.AccountSteps):F2} 倍；"
            + $"差異表工作量 {small.DifferenceSteps} 與 {large.DifferenceSteps}，{Ratio(large.DifferenceSteps, small.DifferenceSteps):F2} 倍。";
        testOutput.WriteLine(measured);
        Assert.True(small.AccountSteps > 0 && small.DifferenceSteps > 0, $"量不到資料庫的工作量。{measured}");
        Assert.True(
            small.AccountStatements == 1 && large.AccountStatements == 1
            && small.DifferenceStatements == 1 && large.DifferenceStatements == 1,
            $"每張表的科目清單要只查一次。{measured}");
        Assert.True(
            large.AccountSteps <= small.AccountSteps * 2.5 && large.DifferenceSteps <= small.DifferenceSteps * 2.5,
            $"科目數多一倍時，讀科目清單的資料庫工作量不應超過 2.5 倍。{measured}");
    }

    private static double Ratio(long large, long small) => small == 0 ? double.NaN : (double)large / small;

    private static async Task<ListReadWork> WriteValidationReportAsync(int accounts)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host, accounts);
        var meter = new SqliteStatementWorkMeter(Classify);
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot), meter);
        var run = Assert.IsType<ResultRecord>(await new LocalResultStore(database).FindLatestAsync(
            projectId, ResultKind.Validation, CancellationToken.None));
        var context = new ValidationReportContext(
            new ReportDocumentContext(projectId, "測試自含案件", "2025-01-01", "2025-12-31", "2025-12-01", 10_000),
            run.ResultId,
            new DateOnly(2026, 11, 17),
            run.SummaryJson);
        var projection = ValidationReportProjectionParser.Parse(run.SummaryJson);
        Assert.Equal(accounts, projection.CompletenessDiffAccountCount);
        await using var output = new MemoryStream();

        await ((IValidationReportWriter)CreateLegacyReportWriter(database)).WriteAsync(
            output,
            context,
            projection,
            ReportWriterTestSupport.ValidationPlan(context, projection),
            new FieldInfoProjection([], []),
            ReportWriterTestSupport.SyntheticMetadata(),
            "tester",
            CancellationToken.None);

        using var workbook = new XLWorkbook(new MemoryStream(output.ToArray(), writable: false));
        // V_Report 5 列全部科目，依科目編號；最後是沒有差異的沖轉科目。
        var accountSheet = workbook.Worksheet("V_Report 5");
        Assert.Equal(accounts + 2, accountSheet.LastRowUsed()!.RowNumber());
        Assert.Equal(
            ExpectedAccounts(accounts).Append(ClearingAccount).ToArray(),
            Enumerable.Range(2, accounts + 1).Select(row => accountSheet.Cell(row, 1).GetString()).ToArray());
        // 差異說明頁從第 17 列起列出有差異的科目。
        var differenceSheet = workbook.Worksheet(WorkingPaperSheetCatalog.Step13);
        Assert.Equal(
            ExpectedAccounts(accounts).ToArray(),
            Enumerable.Range(17, accounts).Select(row => differenceSheet.Cell(row, 2).GetString()).ToArray());
        Assert.Equal(string.Empty, differenceSheet.Cell(17 + accounts, 2).GetString());
        return ListReadWork.From(meter);
    }

    private static async Task<ListReadWork> WriteWorkingPaperAsync(int accounts)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host, accounts);
        var context = await WorkingPaperWriterTestSupport.CurrentContextForAsync(host, projectId);
        var plan = await WorkingPaperWriterTestSupport.CurrentPlanForAsync(host, context);
        var meter = new SqliteStatementWorkMeter(Classify);
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot), meter);
        await using var output = new MemoryStream();

        var stats = await ((IWorkingPaperPlanWriter)CreateWorkingPaperWriter(database)).WriteAsync(
            output,
            context,
            plan,
            CancellationToken.None);

        Assert.Equal(accounts + 1, stats.SheetStats.Single(sheet => sheet.SheetName == WorkingPaperSheetCatalog.Step1).RowsWritten);
        Assert.Equal(accounts, stats.SheetStats.Single(sheet => sheet.SheetName == WorkingPaperSheetCatalog.Step13).RowsWritten);
        using var workbook = new XLWorkbook(new MemoryStream(output.ToArray(), writable: false));
        // Step 1 從第 20 列、Step 1-3 從第 17 列起，B 欄是科目編號。
        var step1 = workbook.Worksheet(WorkingPaperSheetCatalog.Step1);
        Assert.Equal(
            ExpectedAccounts(accounts).Append(ClearingAccount).ToArray(),
            Enumerable.Range(20, accounts + 1).Select(row => step1.Cell(row, 2).GetString()).ToArray());
        var step13 = workbook.Worksheet(WorkingPaperSheetCatalog.Step13);
        Assert.Equal(
            ExpectedAccounts(accounts).ToArray(),
            Enumerable.Range(17, accounts).Select(row => step13.Cell(row, 2).GetString()).ToArray());
        return ListReadWork.From(meter);
    }

    private static string? Classify(string sql) =>
        !AccountListStatement.IsMatch(sql)
            ? null
            : sql.Contains("tb_s <> gl_s", StringComparison.Ordinal)
                ? DifferenceList
                : AccountList;

    private static string Account(int index) => $"1{index:0000}";

    private static IEnumerable<string> ExpectedAccounts(int accounts) =>
        Enumerable.Range(1, accounts).Select(Account);

    private static async Task<string> SetupAsync(HandlerTestHost host, int accounts)
    {
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder =>
            {
                builder.WithColumns(OriginalHeaders);
                for (var index = 1; index <= accounts; index++)
                {
                    var voucher = $"JV-{index:0000}";
                    builder.AddRow(voucher, "2025-03-05", "2025-03-06", Account(index), "合成科目", "借方", "100.00", 1);
                    builder.AddRow(voucher, "2025-03-05", "2025-03-06", ClearingAccount, "沖轉科目", "貸方", "100.00", 0);
                }
            },
            prepStartDate: "2025-12-01",
            configureTb: tb =>
            {
                for (var index = 1; index <= accounts; index++)
                {
                    tb.AddRow(Account(index), "合成科目", 101);
                }

                tb.AddRow(ClearingAccount, "沖轉科目", -100 * accounts);
            });
        await host.DispatchAsync("validate.run");
        return projectId;
    }

    private static LegacyReportWriter CreateLegacyReportWriter(SqliteProjectDatabase database) =>
        new(
            new LocalCompletenessAccountPageRepository(database),
            new LocalCompletenessDiffPageRepository(database),
            new LocalUnbalancedVoucherPageRepository(database),
            new LocalBlankRecordsPageRepository(database),
            new LocalInfSamplePageRepository(database),
            new LocalPrescreenPageRepository(database),
            new LocalRawGlExportRepository(database),
            new LocalImportRepository(database),
            new LocalMappingStateStore(database),
            new LocalPreparerSummaryExportRepository(database),
            new LocalAccountUsageExportRepository(database),
            new LocalHighRiskSummaryScenariosRepository(database),
            new LocalSourceQualityPageRepository(database),
            new LocalHighRiskSummaryRowPageRepository(database));

    private static WorkingPaperWriter CreateWorkingPaperWriter(SqliteProjectDatabase database) =>
        new(
            new LocalCompletenessAccountPageRepository(database),
            new LocalCompletenessDiffPageRepository(database),
            new LocalVoucherBalancePageRepository(database),
            new LocalPreparerSummaryExportRepository(database),
            new LocalInfSamplePageRepository(database),
            new LocalHighRiskSummaryVoucherPageRepository(database),
            new LocalHighRiskSummaryRowPageRepository(database),
            new LocalMappingStateStore(database),
            new LocalCalendarExportRepository(database),
            new LocalAccountMappingExportRepository(database),
            new LocalAccountMappingRepository(database),
            new LocalRawGlExportRepository(database));

    private sealed record ListReadWork(int AccountStatements, long AccountSteps, int DifferenceStatements, long DifferenceSteps)
    {
        internal static ListReadWork From(SqliteStatementWorkMeter meter) =>
            new(
                meter.Statements(AccountList),
                meter.Steps(AccountList),
                meter.Statements(DifferenceList),
                meter.Steps(DifferenceList));
    }
}
