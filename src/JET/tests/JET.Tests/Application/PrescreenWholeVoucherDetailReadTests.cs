using System.Text.Json;
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
/// 預篩選報告 R3「未預期借貸組合」列出整張命中傳票的分錄，明細跨很多批時：
/// 寫出的分錄依 entry_id 升冪，和畫面的分頁查詢相同；命中傳票只算一次，
/// 讀明細的資料庫工作量跟著明細列數成比例增加，不隨批數重複計算全部命中傳票。
///
/// 合成資料每張命中傳票四列：費用借 1,000、收入貸 600、應付費用貸 300 與 100，收入沒有正常的對方科目；
/// 每張一般傳票兩列：應收帳款借 200、收入貸 200，有正常的對方科目，不會命中。
/// 寫入時先寫每張傳票的第 1 列，再寫每張的第 2 列，依此類推，所以同一張傳票的分錄散在各處。
/// 傳票數多一倍時，明細列數與總帳列數也多一倍。
/// </summary>
public sealed class PrescreenWholeVoucherDetailReadTests(ITestOutputHelper testOutput)
{
    private const string HitVoucherSets = "命中傳票";

    private static readonly string[] OriginalHeaders =
    [
        "傳票號碼",
        "傳票日期",
        "核准日期",
        "科目代號",
        "科目名稱",
        "摘要",
        "金額",
        "借方旗標",
        "建立人員"
    ];

    // 命中傳票的列：分錄的傳票號碼在「有直接命中分錄的傳票」裡。
    private static readonly Regex HitVoucherStatement = new(
        @"voucher_number\s+IN\s*\(\s*SELECT\s+g\.voucher_number\s+FROM\s+\w*gl\s+g\s+WHERE\s+g\.voucher_number\s+IS\s+NOT\s+NULL",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly (string Account, string Name, string Amount, int Debit)[] HitVoucherRows =
    [
        ("5101", "費用", "1000.00", 1),
        ("4101", "銷貨收入", "600.00", 0),
        ("2209", "應付費用", "300.00", 0),
        ("2209", "應付費用", "100.00", 0)
    ];

    private static readonly (string Account, string Name, string Amount, int Debit)[] NormalVoucherRows =
    [
        ("1170", "應收帳款", "200.00", 1),
        ("4101", "銷貨收入", "200.00", 0)
    ];

    [Fact]
    public async Task UnexpectedAccountPairDetail_ComputesTheHitVouchersOnce_AndReadWorkGrowsWithTheRows()
    {
        var small = await WriteReportAsync(150);
        var large = await WriteReportAsync(300);

        var measured = $"命中傳票的查詢：{small.Statements} 次與 {large.Statements} 次；"
            + $"資料庫工作量 {small.Steps} 與 {large.Steps}，{(double)large.Steps / small.Steps:F2} 倍。";
        testOutput.WriteLine(measured);
        Assert.True(small.Steps > 0, $"量不到資料庫的工作量。{measured}");
        Assert.True(small.Statements == 1 && large.Statements == 1, $"R3 的命中傳票要只算一次。{measured}");
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
                for (var line = 0; line < HitVoucherRows.Length; line++)
                {
                    for (var index = 1; index <= vouchers; index++)
                    {
                        var hit = HitVoucherRows[line];
                        builder.AddRow(HitVoucher(index), "2025-03-04", "2025-03-04", hit.Account, hit.Name, Description(index, line), hit.Amount, hit.Debit, "E01");
                        if (line < NormalVoucherRows.Length)
                        {
                            var normal = NormalVoucherRows[line];
                            builder.AddRow($"N-{index:0000}", "2025-03-05", "2025-03-05", normal.Account, normal.Name, "一般", normal.Amount, normal.Debit, "E01");
                        }
                    }
                }
            },
            prepStartDate: "2025-12-01",
            validateForDownstream: true);
        await ImportAccountMappingAsync(host);
        await host.DispatchAsync("prescreen.run");

        // 和正式 handler 一樣先算計畫：明細列數由規劃查詢數出，R4 用那次預篩選的 0 的個數。規劃查詢不算進量到的工作量。
        var planningDatabase = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var run = Assert.IsType<ResultRecord>(await new LocalResultStore(planningDatabase).FindLatestAsync(
            projectId, ResultKind.Prescreen, CancellationToken.None));
        var context = new PrescreenReportContext(
            new ReportDocumentContext(projectId, "測試自含案件", "2025-01-01", "2025-12-31", "2025-12-01", ProjectDocument.DefaultMoneyScale),
            run.ResultId,
            new DateOnly(2026, 11, 17),
            run.SummaryJson);
        var projection = PrescreenReportProjectionParser.Parse(run.SummaryJson);
        Assert.Equal(vouchers * HitVoucherRows.Length, projection.UnexpectedAccountPair.ItemCount);
        var unfinalized = JetAuditProgram.Plan(new PrescreenReportRequest(
            projectId,
            new FilterConditionContext(ProjectDocument.DefaultMoneyScale, "2025-12-01", "2025-01-01", "2025-12-31")
            {
                ZerosThreshold = projection.ZerosThreshold
            },
            projection.PrepPeriodApproval.NaReason,
            projection.SuspiciousKeywords.NaReason,
            projection.UnexpectedAccountPair.NaReason,
            projection.TrailingZeros.NaReason,
            projection.BlankDescription.NaReason));
        var plan = JetAuditProgram.Finalize(
            unfinalized,
            await new PrescreenReportPlanningFactsPort(new LocalPrescreenPageRepository(planningDatabase))
                .ExecuteAsync(unfinalized, CancellationToken.None));
        Assert.True(plan.RequireDetail(PrescreenReportDetailKind.UnexpectedAccountPair).Emit);

        var meter = new SqliteStatementWorkMeter(sql => HitVoucherStatement.IsMatch(sql) ? HitVoucherSets : null);
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot), meter);
        await using var output = new MemoryStream();

        await ((IPrescreenReportWriter)CreateWriter(database)).WriteAsync(
            output,
            context,
            projection,
            plan,
            ReportWriterTestSupport.SyntheticMetadata(),
            "tester",
            CancellationToken.None);

        // R3 照寫入順序列出命中傳票的每一列：先是每張的第 1 列，再是每張的第 2 列，一般傳票不列。
        using var workbook = new XLWorkbook(new MemoryStream(output.ToArray(), writable: false));
        var detail = workbook.Worksheet("R3");
        var expected = Enumerable.Range(0, HitVoucherRows.Length)
            .SelectMany(line => Enumerable.Range(1, vouchers).Select(index => (HitVoucher(index), Description(index, line))))
            .ToArray();
        Assert.Equal(expected.Length + 1, detail.LastRowUsed()!.RowNumber());
        Assert.Equal(
            expected,
            Enumerable.Range(2, expected.Length)
                .Select(row => (detail.Cell(row, 1).GetString(), detail.Cell(row, 6).GetString()))
                .ToArray());
        return (meter.Statements(HitVoucherSets), meter.Steps(HitVoucherSets));
    }

    private static string HitVoucher(int index) => $"H-{index:0000}";

    private static string Description(int index, int line) => $"合成 {index:0000} 第 {line + 1} 列";

    private static async Task ImportAccountMappingAsync(HandlerTestHost host)
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "科目代號";
            sheet.Cell(1, 2).Value = "科目名稱";
            sheet.Cell(1, 3).Value = "標準化分類";
            var rows = new (string Code, string Name, string Category)[]
            {
                ("1170", "應收帳款", "Receivables"),
                ("4101", "銷貨收入", "Revenue"),
                ("5101", "費用", ""),
                ("2209", "應付費用", "")
            };
            for (var index = 0; index < rows.Length; index++)
            {
                sheet.Cell(index + 2, 1).Value = rows[index].Code;
                sheet.Cell(index + 2, 2).Value = rows[index].Name;
                sheet.Cell(index + 2, 3).Value = rows[index].Category;
            }
        });
        try
        {
            await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    private static LegacyReportWriter CreateWriter(SqliteProjectDatabase database) =>
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
}
