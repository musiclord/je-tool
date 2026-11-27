using System.Text.RegularExpressions;
using ClosedXML.Excel;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 資料驗證報告 V_Report 6 列出借貸不平傳票的全部有效分錄，明細跨很多批時：
/// 寫出的分錄依 entry_id 升冪，和分頁查詢逐頁讀完相同；哪些傳票不平只算一次，
/// 讀明細的資料庫工作量跟著明細列數成比例增加，不隨批數重複計算全部傳票的借貸。
///
/// 合成資料有兩種傳票，各一借一貸：不平的傳票借 100、貸 90；平衡的傳票借 100、貸 100。
/// 先依傳票號碼由大到小寫完全部借方列，再由小到大寫貸方列，所以同一張傳票的分錄散在各處。
/// 傳票數多一倍時，明細列數與總帳列數也多一倍。
/// </summary>
public sealed class UnbalancedVoucherDetailReadTests(ITestOutputHelper testOutput)
{
    private const string UnbalancedVoucherSets = "不平傳票";

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

    // 依傳票號碼彙總、留下借貸淨額不是 0 的傳票。
    private static readonly Regex UnbalancedVoucherStatement = new(
        @"GROUP\s+BY\s+voucher_number\s+HAVING\s+SUM\(amount_scaled\)\s*<>\s*0",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public async Task ValidationReportSix_ComputesTheUnbalancedVouchersOnce_AndReadWorkGrowsWithTheRows()
    {
        var small = await WriteReportAsync(300);
        var large = await WriteReportAsync(600);

        var measured = $"不平傳票的查詢：{small.Statements} 次與 {large.Statements} 次；"
            + $"資料庫工作量 {small.Steps} 與 {large.Steps}，{(double)large.Steps / small.Steps:F2} 倍。";
        testOutput.WriteLine(measured);
        Assert.True(small.Steps > 0, $"量不到資料庫的工作量。{measured}");
        Assert.True(small.Statements == 1 && large.Statements == 1, $"V_Report 6 的不平傳票要只算一次。{measured}");
        Assert.True(large.Steps <= small.Steps * 2.5, $"明細列數多一倍時，讀明細的資料庫工作量不應超過 2.5 倍。{measured}");
    }

    private static async Task<(int Statements, long Steps)> WriteReportAsync(int vouchers)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder =>
            {
                builder.WithColumns(OriginalHeaders);
                for (var index = vouchers; index >= 1; index--)
                {
                    builder.AddRow(UnbalancedVoucher(index), "2025-03-05", "2025-03-06", "1101", "現金", DebitDescription(index), "100.00", 1);
                    builder.AddRow($"B-{index:0000}", "2025-03-05", "2025-03-06", "1101", "現金", "平衡借方", "100.00", 1);
                }

                for (var index = 1; index <= vouchers; index++)
                {
                    builder.AddRow(UnbalancedVoucher(index), "2025-03-05", "2025-03-06", "4101", "收入", CreditDescription(index), "90.00", 0);
                    builder.AddRow($"B-{index:0000}", "2025-03-05", "2025-03-06", "4101", "收入", "平衡貸方", "100.00", 0);
                }
            },
            prepStartDate: "2025-12-01",
            validateForDownstream: true);

        var meter = new SqliteStatementWorkMeter(sql => UnbalancedVoucherStatement.IsMatch(sql) ? UnbalancedVoucherSets : null);
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot), meter);
        var run = Assert.IsType<ResultRecord>(await new LocalResultStore(database).FindLatestAsync(
            projectId, ResultKind.Validation, CancellationToken.None));
        var context = new ValidationReportContext(
            new ReportDocumentContext(projectId, "測試自含案件", "2025-01-01", "2025-12-31", "2025-12-01", 10_000),
            run.ResultId,
            new DateOnly(2026, 11, 17),
            run.SummaryJson);
        var projection = ValidationReportProjectionParser.Parse(run.SummaryJson);
        Assert.Equal(vouchers, projection.UnbalancedVoucherCount);
        await using var output = new MemoryStream();

        await ((IValidationReportWriter)CreateWriter(database)).WriteAsync(
            output,
            context,
            projection,
            ReportWriterTestSupport.ValidationPlan(context, projection),
            new FieldInfoProjection([], []),
            ReportWriterTestSupport.SyntheticMetadata(),
            "tester",
            CancellationToken.None);

        // V_Report 6 照寫入順序列出不平傳票的每一列：借方列（傳票號碼由大到小）在前，貸方列（由小到大）在後。
        using var workbook = new XLWorkbook(new MemoryStream(output.ToArray(), writable: false));
        var detail = workbook.Worksheet("V_Report 6");
        var expected = Enumerable.Range(1, vouchers).Reverse().Select(index => (UnbalancedVoucher(index), DebitDescription(index)))
            .Concat(Enumerable.Range(1, vouchers).Select(index => (UnbalancedVoucher(index), CreditDescription(index))))
            .ToArray();
        Assert.Equal(expected.Length + 1, detail.LastRowUsed()!.RowNumber());
        Assert.Equal(
            expected,
            Enumerable.Range(2, expected.Length)
                .Select(row => (detail.Cell(row, 1).GetString(), detail.Cell(row, 6).GetString()))
                .ToArray());
        return (meter.Statements(UnbalancedVoucherSets), meter.Steps(UnbalancedVoucherSets));
    }

    private static string UnbalancedVoucher(int index) => $"U-{index:0000}";

    private static string DebitDescription(int index) => $"不平借方 {index:0000}";

    private static string CreditDescription(int index) => $"不平貸方 {index:0000}";

    private static LegacyReportWriter CreateWriter(SqliteProjectDatabase database) =>
        new(
            new LocalCompletenessAccountPageRepository(database),
            new LocalCompletenessDiffPageRepository(database),
            new LocalUnbalancedVoucherEntryStreamRepository(database),
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
}
