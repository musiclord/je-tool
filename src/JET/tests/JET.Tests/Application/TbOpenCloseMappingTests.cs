using System.Linq;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// TB legacy 金額模式 OpenClose（legacy SA=2）與 OpenCloseBySide（legacy SA=4）的窄流程驗收：
/// 匯入 → mapping.commit.tb（新模式）→ 讀回 target_tb_balance.change_amount_scaled。
/// oracle：規格（legacy idea-script.bas:11242/:11283 換算式，guide §2.2）＋手算固定資料集。
/// 斷言鎖值＋身分（科目碼 → 變動 scaled）。scale=10000（HandlerTestHost 預設）。
/// </summary>
public sealed class TbOpenCloseMappingTests
{
    private const string CreatePayload =
        """
        {
          "projectCode": "ENG-2025-TB",
          "entityName": "範例股份有限公司",
          "operatorId": "auditor01",
          "periodStart": "2025-01-01",
          "periodEnd": "2025-12-31"
        }
        """;

    /// <summary>期初/期末兩欄寬表（legacy 當年於 IDEA GUI 以科目碼 join 後的單一寬表；guide §2.2 操作指引）。</summary>
    private static string WriteOpenCloseWorkbook()
    {
        return TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            string[] headers = ["會計科目編號", "會計科目名稱", "期初餘額", "期末餘額"];
            for (var i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
            }

            // 1101：期末 1500 − 期初 1000 = +500 → scaled +5,000,000
            ws.Cell(2, 1).Value = "1101";
            ws.Cell(2, 2).Value = "現金";
            ws.Cell(2, 3).Value = 1000;
            ws.Cell(2, 4).Value = 1500;

            // 1102：期末 300 − 期初 800 = −500 → scaled −5,000,000（負變動）
            ws.Cell(3, 1).Value = "1102";
            ws.Cell(3, 2).Value = "銀行存款";
            ws.Cell(3, 3).Value = 800;
            ws.Cell(3, 4).Value = 300;
        });
    }

    /// <summary>期初借貸＋期末借貸四欄寬表（legacy SA=4）。</summary>
    private static string WriteOpenCloseBySideWorkbook()
    {
        return TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            string[] headers = ["會計科目編號", "會計科目名稱", "期初借", "期初貸", "期末借", "期末貸"];
            for (var i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
            }

            // 1101（借方科目）：(1500−0) − (1000−0) = +500 → scaled +5,000,000
            ws.Cell(2, 1).Value = "1101";
            ws.Cell(2, 2).Value = "現金";
            ws.Cell(2, 3).Value = 1000; // 期初借
            ws.Cell(2, 4).Value = 0;    // 期初貸
            ws.Cell(2, 5).Value = 1500; // 期末借
            ws.Cell(2, 6).Value = 0;    // 期末貸

            // 2101（貸方科目）：(0−300) − (0−800) = +500 → scaled +5,000,000（借貸交叉）
            ws.Cell(3, 1).Value = "2101";
            ws.Cell(3, 2).Value = "應付帳款";
            ws.Cell(3, 3).Value = 0;    // 期初借
            ws.Cell(3, 4).Value = 800;  // 期初貸
            ws.Cell(3, 5).Value = 0;    // 期末借
            ws.Cell(3, 6).Value = 300;  // 期末貸
        });
    }

    private static async Task<long> ChangeScaledAsync(HandlerTestHost host, string accountCode)
    {
        var table = await host.DispatchAsync(
            "dev.db.tableData",
            """{ "tableName": "target_tb_balance", "limit": 50 }""");

        var columns = table.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
        var accIndex = columns.IndexOf("account_code");
        var changeIndex = columns.IndexOf("change_amount_scaled");

        var row = table.GetProperty("rows").EnumerateArray()
            .Single(r => r[accIndex].GetString() == accountCode);
        return long.Parse(row[changeIndex].GetString()!);
    }

    [Fact]
    public async Task CommitTb_OpenCloseMode_ProjectsClosingMinusOpening()
    {
        using var host = new HandlerTestHost();
        var tbPath = WriteOpenCloseWorkbook();

        try
        {
            await host.DispatchAsync("project.create", CreatePayload);
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tbPath }));

            var commit = await host.DispatchAsync(
                "mapping.commit.tb",
                """
                {
                  "mapping": {
                    "accNum": "會計科目編號", "accName": "會計科目名稱",
                    "openingBalance": "期初餘額", "closingBalance": "期末餘額"
                  },
                  "changeMode": "openClose"
                }
                """);

            Assert.Equal(2, commit.GetProperty("projectedRowCount").GetInt32());
            Assert.Equal("openClose", commit.GetProperty("changeMode").GetString());

            Assert.Equal(5_000_000L, await ChangeScaledAsync(host, "1101"));   // +500
            Assert.Equal(-5_000_000L, await ChangeScaledAsync(host, "1102"));  // −500
        }
        finally
        {
            TestWorkbookBuilder.Delete(tbPath);
        }
    }

    [Fact]
    public async Task CommitTb_OpenCloseBySideMode_ProjectsNetSideChange()
    {
        using var host = new HandlerTestHost();
        var tbPath = WriteOpenCloseBySideWorkbook();

        try
        {
            await host.DispatchAsync("project.create", CreatePayload);
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tbPath }));

            var commit = await host.DispatchAsync(
                "mapping.commit.tb",
                """
                {
                  "mapping": {
                    "accNum": "會計科目編號", "accName": "會計科目名稱",
                    "openingDebit": "期初借", "openingCredit": "期初貸",
                    "closingDebit": "期末借", "closingCredit": "期末貸"
                  },
                  "changeMode": "openCloseBySide"
                }
                """);

            Assert.Equal(2, commit.GetProperty("projectedRowCount").GetInt32());
            Assert.Equal("openCloseBySide", commit.GetProperty("changeMode").GetString());

            Assert.Equal(5_000_000L, await ChangeScaledAsync(host, "1101"));  // 借方科目 +500
            Assert.Equal(5_000_000L, await ChangeScaledAsync(host, "2101"));  // 貸方科目 +500
        }
        finally
        {
            TestWorkbookBuilder.Delete(tbPath);
        }
    }

    /// <summary>
    /// 寬表等價性（metamorphic oracle）：同一科目用 DirectChange（直接給 +500 變動）與 OpenClose
    /// （期初 1000／期末 1500）算出相同 target_tb_balance.change_amount_scaled。兩案各自獨立專案。
    /// </summary>
    [Fact]
    public async Task OpenClose_IsEquivalentToDirectChange_ForSameNetMovement()
    {
        using var directHost = new HandlerTestHost();
        using var openCloseHost = new HandlerTestHost();

        var directPath = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            string[] headers = ["會計科目編號", "會計科目名稱", "本期變動"];
            for (var i = 0; i < headers.Length; i++) { ws.Cell(1, i + 1).Value = headers[i]; }
            ws.Cell(2, 1).Value = "1101";
            ws.Cell(2, 2).Value = "現金";
            ws.Cell(2, 3).Value = 500; // 直接給本期變動
        });
        var openClosePath = WriteOpenCloseWorkbook();

        try
        {
            await directHost.DispatchAsync("project.create", CreatePayload);
            await directHost.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = directPath }));
            await directHost.DispatchAsync(
                "mapping.commit.tb",
                """
                { "mapping": { "accNum": "會計科目編號", "accName": "會計科目名稱", "amount": "本期變動" },
                  "changeMode": "direct" }
                """);

            await openCloseHost.DispatchAsync("project.create", CreatePayload);
            await openCloseHost.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = openClosePath }));
            await openCloseHost.DispatchAsync(
                "mapping.commit.tb",
                """
                { "mapping": { "accNum": "會計科目編號", "accName": "會計科目名稱",
                  "openingBalance": "期初餘額", "closingBalance": "期末餘額" },
                  "changeMode": "openClose" }
                """);

            var directValue = await ChangeScaledAsync(directHost, "1101");
            var openCloseValue = await ChangeScaledAsync(openCloseHost, "1101");

            Assert.Equal(directValue, openCloseValue);
            Assert.Equal(5_000_000L, openCloseValue); // 兩路皆 +500 × 10000
        }
        finally
        {
            TestWorkbookBuilder.Delete(directPath);
            TestWorkbookBuilder.Delete(openClosePath);
        }
    }
}
