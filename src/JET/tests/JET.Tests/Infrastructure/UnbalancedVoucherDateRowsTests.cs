using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 底稿 Step 1-1 明細的資料來源：不平傳票依傳票號碼與總帳入帳日彙總（legacy idea-tool.bas:6686-6691）。
/// 兩個本機資料庫都要得到同一份結果；平衡傳票與查核期間外的分錄不列入。
/// </summary>
public sealed class UnbalancedVoucherDateRowsTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task StreamVoucherDateRows_GroupsUnbalancedVouchersByPostDate(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                gl.AddRow("JV1", "2025-03-01", "1101", "現金", "說明", "甲", "100.00", 1);
                gl.AddRow("JV1", "2025-03-01", "6101", "費用", "說明", "甲", "100.00", 0);
                gl.AddRow("JV9", "2025-03-03", "1101", "現金", "說明", "甲", "300.00", 1);
                gl.AddRow("JV9", "2025-03-03", "6101", "費用", "說明", "甲", "40.00", 0);
                gl.AddRow("JV9", "2025-03-04", "6101", "費用", "說明", "甲", "100.00", 0);
                gl.AddRow("JV9", "2026-01-02", "6101", "費用", "期外", "甲", "500.00", 0);
            },
            databaseProvider: provider,
            configureTb: tb =>
            {
                tb.AddRow("1101", "現金", 400);
                tb.AddRow("6101", "費用", -240);
            });

        var folder = new JetProjectFolder(host.ProjectsRoot);
        ILocalProjectDatabase database = provider == "sqlite"
            ? new SqliteProjectDatabase(folder)
            : new DuckDbProjectDatabase(folder);
        var repository = new LocalDocBalancePageRepository(database);

        var rows = new List<UnbalancedVoucherDateRow>();
        await foreach (var row in repository.StreamVoucherDateRowsAsync(projectId, CancellationToken.None))
        {
            rows.Add(row);
        }

        // MoneyScale 為 10000：300.00 → 3,000,000。
        Assert.Equal(
            new[]
            {
                new UnbalancedVoucherDateRow("JV9", "2025-03-03", 3_000_000, 400_000),
                new UnbalancedVoucherDateRow("JV9", "2025-03-04", 0, 1_000_000)
            },
            rows);
    }
}
