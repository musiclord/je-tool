using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 條件篩選報告「#Criteria Select n」的整張傳票明細跨很多批時：
/// 寫出的分錄依傳票號碼排列、同一張傳票照原本的列順序，Final_TAG 只標直接符合的分錄；每個情境的命中傳票只算一次，
/// 讀明細的資料庫工作量跟著明細列數成比例增加，不隨批數重複計算整個情境。
///
/// 合成資料每張傳票一借一貸。先依傳票號碼由大到小寫完全部借方列，再由小到大寫貸方列，
/// 所以 entry_id 的順序和傳票號碼的順序不同。前一半傳票的借方摘要含「調整」，每一筆貸方摘要都含「結轉」：
/// 情境 1 命中前一半傳票，情境 2 命中全部傳票，整張傳票的明細列數剛好差一倍。
/// </summary>
public sealed class CriteriaSelectionReportDetailReadTests(ITestOutputHelper testOutput)
{
    /// <summary>算出一個情境命中傳票的子查詢。</summary>
    private static readonly Regex HitVoucherSet = new(
        @"SELECT\s+DISTINCT\s+\w+\.voucher_number\s+FROM\s+\w*filter_hit\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const string HitVoucherSetCategory = "hit-voucher-set";

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

    [Fact]
    public async Task WholeVoucherDetail_AcrossSeveralBatches_ListsVouchersByNumberAndMarksOnlyMatchedRows()
    {
        const int vouchers = 300;
        using var host = new HandlerTestHost();
        var (projectId, scenarios, revision) = await SetupAsync(host, vouchers);
        var writer = CreateWriter(new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot)));
        await using var output = new MemoryStream();

        await ((ICriteriaSelectionReportWriter)writer).WriteAsync(
            output,
            Context(projectId, scenarios, revision),
            ReportWriterTestSupport.SyntheticMetadata(),
            [],
            "tester",
            CancellationToken.None);

        using var workbook = new XLWorkbook(new MemoryStream(output.ToArray(), writable: false));
        // 摘要的 C 欄是符合條件的傳票數，D 欄是直接符合的分錄筆數。
        var summary = workbook.Worksheet("Summary Inforamtion");
        Assert.Equal(vouchers / 2, summary.Cell("C5").GetValue<long>());
        Assert.Equal(vouchers / 2, summary.Cell("D5").GetValue<long>());
        Assert.Equal(vouchers, summary.Cell("C6").GetValue<long>());
        Assert.Equal(vouchers, summary.Cell("D6").GetValue<long>());

        // 情境 1：前一半傳票依傳票號碼由小到大列出；同一張傳票照寫入順序，借方列在前、貸方列在後。只有借方列直接符合。
        var half = vouchers / 2;
        AssertDetail(
            workbook.Worksheet("#Criteria Select 1"),
            Enumerable.Range(1, half)
                .SelectMany(v => new[] { (Voucher(v), DebitDescription(v, vouchers), "Y"), (Voucher(v), CreditDescription(v), "") })
                .ToArray());
        // 情境 2：全部傳票依傳票號碼由小到大列出；每張傳票的借方列不符合，貸方列符合。
        AssertDetail(
            workbook.Worksheet("#Criteria Select 2"),
            Enumerable.Range(1, vouchers)
                .SelectMany(v => new[] { (Voucher(v), DebitDescription(v, vouchers), ""), (Voucher(v), CreditDescription(v), "Y") })
                .ToArray());
    }

    [Fact]
    public async Task WholeVoucherDetail_ComputesEachScenarioHitVouchersOnce_AndReadWorkGrowsWithTheRows()
    {
        const int vouchers = 1_200;
        using var host = new HandlerTestHost();
        var (projectId, scenarios, revision) = await SetupAsync(host, vouchers);
        var meter = new SqliteStatementWorkMeter(sql => HitVoucherSet.IsMatch(sql) ? HitVoucherSetCategory : null);
        var writer = CreateWriter(new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot), meter));
        var sheetStarts = new Dictionary<string, (long Steps, int HitVoucherSets)>(StringComparer.Ordinal);
        await using var output = new MemoryStream();

        // 每張工作表開始時記下到目前為止的工作量；兩個情境的明細各自是相鄰兩次紀錄之間的差。
        var stats = await ((ICriteriaSelectionReportWriter)writer).WriteAsync(
            output,
            Context(projectId, scenarios, revision),
            ReportWriterTestSupport.SyntheticMetadata(),
            [],
            "tester",
            CancellationToken.None,
            progress => sheetStarts.TryAdd(progress.SheetName, (meter.TotalSteps, meter.Statements(HitVoucherSetCategory))));
        var end = (Steps: meter.TotalSteps, HitVoucherSets: meter.Statements(HitVoucherSetCategory));

        Assert.Equal(vouchers, stats.SheetStats.Single(sheet => sheet.SheetName == "#Criteria Select 1").RowsWritten);
        Assert.Equal(vouchers * 2, stats.SheetStats.Single(sheet => sheet.SheetName == "#Criteria Select 2").RowsWritten);
        var first = sheetStarts["#Criteria Select 1"];
        var second = sheetStarts["#Criteria Select 2"];
        var firstSets = second.HitVoucherSets - first.HitVoucherSets;
        var secondSets = end.HitVoucherSets - second.HitVoucherSets;
        var firstSteps = second.Steps - first.Steps;
        var secondSteps = end.Steps - second.Steps;
        Assert.True(firstSteps > 0, "量不到資料庫的工作量。");
        var measured = $"命中傳票計算次數：情境 1 {firstSets} 次、情境 2 {secondSets} 次；"
            + $"資料庫工作量：情境 1 {firstSteps}、情境 2 {secondSteps}，{(double)secondSteps / firstSteps:F2} 倍。";
        testOutput.WriteLine(measured);
        Assert.True(firstSets == 1 && secondSets == 1, $"每個情境的命中傳票要剛好算一次。{measured}");
        Assert.True(secondSteps <= firstSteps * 2.5, $"明細列數多一倍時，讀明細的資料庫工作量不應超過 2.5 倍。{measured}");
    }

    private static string Voucher(int v) => $"JV-{v:0000}";

    private static string DebitDescription(int v, int vouchers) => v <= vouchers / 2 ? $"調整 借方 {v}" : $"借方 {v}";

    private static string CreditDescription(int v) => $"結轉 貸方 {v}";

    private static async Task<(string ProjectId, IReadOnlyList<SavedFilterScenario> Scenarios, string Revision)> SetupAsync(
        HandlerTestHost host,
        int vouchers)
    {
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder =>
            {
                builder.WithColumns(OriginalHeaders);
                for (var v = vouchers; v >= 1; v--)
                {
                    builder.AddRow(Voucher(v), "2025-03-05", "2025-03-06", "1101", "現金", DebitDescription(v, vouchers), "100.00", 1);
                }

                for (var v = 1; v <= vouchers; v++)
                {
                    builder.AddRow(Voucher(v), "2025-03-05", "2025-03-06", "4101", "收入", CreditDescription(v), "100.00", 0);
                }
            },
            validateForDownstream: true);
        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = new object[]
                {
                    KeywordScenario("調整情境", "調整"),
                    KeywordScenario("結轉情境", "結轉")
                }
            }));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var scenarios = await new LocalFilterScenarioStore(new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot)))
            .ListAsync(projectId, CancellationToken.None);
        return (projectId, scenarios, revision);
    }

    private static CriteriaSelectionReportContext Context(
        string projectId,
        IReadOnlyList<SavedFilterScenario> scenarios,
        string revision) =>
        new(
            new ReportDocumentContext(projectId, "合成公司", "2025-01-01", "2025-12-31", null, 10_000),
            revision,
            new DateOnly(2026, 11, 17),
            scenarios,
            new Dictionary<int, string>
            {
                [1] = "摘要包含「調整」",
                [2] = "摘要包含「結轉」"
            });

    private static void AssertDetail(IXLWorksheet sheet, IReadOnlyList<(string Voucher, string Description, string Tag)> expected)
    {
        Assert.Equal(OriginalHeaders, Enumerable.Range(1, OriginalHeaders.Length).Select(column => sheet.Cell(1, column).GetString()).ToArray());
        Assert.Equal("Final_TAG", sheet.Cell(1, OriginalHeaders.Length + 1).GetString());
        Assert.Equal(expected.Count + 1, sheet.LastRowUsed()!.RowNumber());
        var actual = Enumerable.Range(2, expected.Count)
            .Select(row => (
                sheet.Cell(row, 1).GetString(),
                sheet.Cell(row, 6).GetString(),
                sheet.Cell(row, OriginalHeaders.Length + 1).GetString()))
            .ToArray();
        Assert.Equal(expected, actual);
    }

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

    private static object KeywordScenario(string name, string keyword) => new
    {
        name,
        rationale = $"測試 {name}",
        groups = new[]
        {
            new
            {
                join = "AND",
                conditions = new[]
                {
                    new
                    {
                        join = "AND",
                        type = "fieldValue",
                        field = "description",
                        @operator = "contains",
                        values = new[] { keyword }
                    }
                }
            }
        }
    };
}
