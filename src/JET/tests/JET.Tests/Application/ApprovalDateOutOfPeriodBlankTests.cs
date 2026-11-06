using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 裁定 2026-11-05-38：資料驗證的「核准日不在期間」照舊版把核准日空白的分錄也算進來；
/// 傳票核准日沒有配對時這一項照裁定 2026-10-23-01 不執行，明細樣本也不能因為核准日全是空白而列出每一筆。
/// 合成總帳的總帳日期都在查核期間內，資料由程式逐列宣告。
/// </summary>
public sealed class ApprovalDateOutOfPeriodBlankTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task BlankApprovalDate_CountsAsOutOfPeriod_InTheResultAndTheDetailPage(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
                .WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-IN", "2025-03-03", "2025-03-04", "1000", "合成現金", "合成摘要", "10", 1)
                .AddRow("JV-IN", "2025-03-03", "2025-03-04", "4000", "合成收入", "合成摘要", "10", 0)
                .AddRow("JV-BLANK", "2025-03-05", null, "1000", "合成現金", "合成摘要", "20", 1)
                .AddRow("JV-BLANK", "2025-03-05", null, "4000", "合成收入", "合成摘要", "20", 0)
                .AddRow("JV-LATE", "2025-03-06", "2026-01-05", "1000", "合成現金", "合成摘要", "30", 1)
                .AddRow("JV-LATE", "2025-03-06", "2026-01-05", "4000", "合成收入", "合成摘要", "30", 0),
            databaseProvider: provider, validateForDownstream: true);

        var validation = await host.DispatchAsync("validate.run");

        // 核准日空白的兩列和核准日在期間之後的兩列都算不在期間；核准日在期間內的兩列不算。
        var blankRecords = validation.GetProperty("blankRecordsTest");
        Assert.Equal(4, blankRecords.GetProperty("approvalDateNotInPeriodCount").GetInt64());
        Assert.Equal(JsonValueKind.Null, blankRecords.GetProperty("approvalDateNotInPeriodNaReason").ValueKind);
        string[] expected = ["JV-BLANK", "JV-BLANK", "JV-LATE", "JV-LATE"];
        Assert.Equal(expected, ApprovalFlaggedVouchers(blankRecords));

        // 畫面的明細頁照同一個判斷列出同樣四筆。
        var page = await host.DispatchAsync("query.blankRecordsPage",
            JsonSerializer.Serialize(new { category = "approvalDateNotInPeriod", pageSize = 50 }));
        Assert.Equal(4, page.GetProperty("totalCount").GetInt64());
        Assert.Equal(
            expected,
            page.GetProperty("rows").EnumerateArray()
                .Select(static row => row.GetProperty("voucherNumber").GetString()!)
                .ToArray());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task UnmappedApprovalDate_DoesNotFlagEveryEntryInTheDetailSample(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-1", "2025-03-03", "1000", "合成現金", "合成摘要", "10", 1)
                .AddRow("JV-1", "2025-03-03", "4000", "合成收入", "合成摘要", "10", 0),
            databaseProvider: provider, validateForDownstream: true);

        var validation = await host.DispatchAsync("validate.run");

        // 沒有傳票核准日時核准日全是空白；這一項不執行，筆數寫原因，明細樣本不標任何一筆（合成資料也沒有其他三類空白）。
        var blankRecords = validation.GetProperty("blankRecordsTest");
        Assert.Equal(JsonValueKind.Null, blankRecords.GetProperty("approvalDateNotInPeriodCount").ValueKind);
        Assert.NotNull(blankRecords.GetProperty("approvalDateNotInPeriodNaReason").GetString());
        Assert.Empty(ApprovalFlaggedVouchers(blankRecords));
        Assert.Empty(blankRecords.GetProperty("blankRows").EnumerateArray());
    }

    private static string[] ApprovalFlaggedVouchers(JsonElement blankRecords) =>
        blankRecords.GetProperty("blankRows").EnumerateArray()
            .Where(static row => row.GetProperty("issues").EnumerateArray()
                .Any(static issue => issue.GetString() == "approvalDate"))
            .Select(static row => row.GetProperty("voucherNumber").GetString()!)
            .ToArray();
}
