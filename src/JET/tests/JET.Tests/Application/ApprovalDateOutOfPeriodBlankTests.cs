using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 資料驗證的「核准日不在期間」照舊版把核准日空白的分錄也算進來；
/// 傳票核准日沒有配對時這一項不執行，明細樣本也不能因為核准日全是空白而列出每一筆。
/// 合成總帳的總帳日期都在查核期間內，資料由程式逐列宣告。
/// </summary>
public sealed class ApprovalDateOutOfPeriodBlankTests
{
    [Theory]
    [InlineData("sqlite")]
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

    [Fact]
    public async Task ApprovalDateNotInPeriod_ShowsEachRowsApprovalDate_AndSortsByIt()
    {
        // 「核准日不在期間」的預覽帶傳票核准日一欄，審計員才看得到是哪一天不在期間。
        // 驗證結果與明細頁都帶每一列的核准日，明細頁也能依核准日排序。
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
                .WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-IN", "2025-03-03", "2025-03-04", "1000", "合成現金", "合成摘要", "10", 1)
                .AddRow("JV-IN", "2025-03-03", "2025-03-04", "4000", "合成收入", "合成摘要", "10", 0)
                .AddRow("JV-BLANK", "2025-03-05", null, "1000", "合成現金", "合成摘要", "20", 1)
                .AddRow("JV-BLANK", "2025-03-05", null, "4000", "合成收入", "合成摘要", "20", 0)
                .AddRow("JV-LATE", "2025-03-06", "2026-01-05", "1000", "合成現金", "合成摘要", "30", 1)
                .AddRow("JV-LATE", "2025-03-06", "2026-01-05", "4000", "合成收入", "合成摘要", "30", 0)
                .AddRow("JV-EARLY", "2025-03-07", "2024-12-20", "1000", "合成現金", "合成摘要", "40", 1)
                .AddRow("JV-EARLY", "2025-03-07", "2024-12-20", "4000", "合成收入", "合成摘要", "40", 0),
            databaseProvider: "sqlite", validateForDownstream: true);

        var validation = await host.DispatchAsync("validate.run");
        var flagged = validation.GetProperty("blankRecordsTest").GetProperty("blankRows").EnumerateArray()
            .Where(static row => row.GetProperty("issues").EnumerateArray()
                .Any(static issue => issue.GetString() == "approvalDate"))
            .Select(static row => (row.GetProperty("voucherNumber").GetString(), ApprovalDate(row)))
            .Distinct()
            .OrderBy(static item => item.Item1, StringComparer.Ordinal)
            .ToArray();
        (string?, string?)[] expectedRows = [("JV-BLANK", null), ("JV-EARLY", "2024-12-20"), ("JV-LATE", "2026-01-05")];
        Assert.Equal(expectedRows, flagged);

        // 明細頁依核准日由早到晚排，核准日空白的排在最後；由晚到早時空白一樣排在最後。
        Assert.Equal(
            ["2024-12-20", "2024-12-20", "2026-01-05", "2026-01-05", null, null],
            await SortedApprovalDatesAsync(host, "asc"));
        Assert.Equal(
            ["2026-01-05", "2026-01-05", "2024-12-20", "2024-12-20", null, null],
            await SortedApprovalDatesAsync(host, "desc"));
    }

    [Fact]
    public async Task UnrecognizedApprovalDate_DetailPageShowsTheSourceTextApartFromBlank()
    {
        // 核准日讀不成日期的分錄和核准日空白的一樣算不在期間，筆數不變；明細頁另帶來源的原文，
        // 畫面才分得出是「讀不成」還是「空白」。空白與讀得成日期的列沒有原文。
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
                .WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-IN", "2025-03-03", "2025-03-04", "1000", "合成現金", "合成摘要", "10", 1)
                .AddRow("JV-IN", "2025-03-03", "2025-03-04", "4000", "合成收入", "合成摘要", "10", 0)
                .AddRow("JV-BLANK", "2025-03-05", null, "1000", "合成現金", "合成摘要", "20", 1)
                .AddRow("JV-BLANK", "2025-03-05", "   ", "4000", "合成收入", "合成摘要", "20", 0)
                .AddRow("JV-BAD", "2025-03-06", "2025-13-45", "1000", "合成現金", "合成摘要", "30", 1)
                .AddRow("JV-BAD", "2025-03-06", " 2025-13-45 ", "4000", "合成收入", "合成摘要", "30", 0)
                .AddRow("JV-LATE", "2025-03-07", "2026-01-05", "1000", "合成現金", "合成摘要", "40", 1)
                .AddRow("JV-LATE", "2025-03-07", "2026-01-05", "4000", "合成收入", "合成摘要", "40", 0),
            databaseProvider: "sqlite", validateForDownstream: true);

        var validation = await host.DispatchAsync("validate.run");
        Assert.Equal(6, validation.GetProperty("blankRecordsTest").GetProperty("approvalDateNotInPeriodCount").GetInt64());

        var page = await host.DispatchAsync("query.blankRecordsPage",
            JsonSerializer.Serialize(new { category = "approvalDateNotInPeriod", pageSize = 50 }));
        Assert.Equal(6, page.GetProperty("totalCount").GetInt64());
        Assert.Equal(
            ["JV-BLANK||", "JV-BLANK||", "JV-BAD||2025-13-45", "JV-BAD||2025-13-45", "JV-LATE|2026-01-05|", "JV-LATE|2026-01-05|"],
            page.GetProperty("rows").EnumerateArray().Select(RawValueRow).ToArray());
    }

    [Fact]
    public async Task UnrecognizedApprovalDate_LongSourceText_ShowsTheSameBeginningAsTheDateNotice()
    {
        // 太長的原文只留開頭 50 個字，後面接「…」；明細頁和重開案件後的日期提醒寫的是同一段。
        const string source = "NOT-A-DATE-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
        const string shown = "NOT-A-DATE-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx…";
        Assert.Equal(60, source.Length);
        Assert.Equal(51, shown.Length);
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder => builder
                .WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-LONG", "2025-03-06", source, "1000", "合成現金", "合成摘要", "30", 1)
                .AddRow("JV-LONG", "2025-03-06", source, "4000", "合成收入", "合成摘要", "30", 0),
            databaseProvider: "sqlite", validateForDownstream: true);

        var page = await host.DispatchAsync("query.blankRecordsPage",
            JsonSerializer.Serialize(new { category = "approvalDateNotInPeriod", pageSize = 50 }));
        Assert.Equal(["JV-LONG||" + shown, "JV-LONG||" + shown], page.GetProperty("rows").EnumerateArray().Select(RawValueRow).ToArray());

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var notice = Assert.Single(loaded.GetProperty("mapping").GetProperty("gl").GetProperty("dateNotices").EnumerateArray());
        Assert.Equal(shown, Assert.Single(notice.GetProperty("values").EnumerateArray()).GetProperty("value").GetString());
    }

    [Fact]
    public async Task UnmappedApprovalDate_DetailPageHasNoSourceText()
    {
        // 沒有配對傳票核准日時核准日全是空白，明細頁每一列都沒有來源原文。
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-1", "2025-03-03", "1000", "合成現金", "合成摘要", "10", 1)
                .AddRow("JV-1", "2025-03-03", "4000", "合成收入", "合成摘要", "10", 0),
            databaseProvider: "sqlite", validateForDownstream: true);

        var page = await host.DispatchAsync("query.blankRecordsPage",
            JsonSerializer.Serialize(new { category = "approvalDateNotInPeriod", pageSize = 50 }));
        Assert.Equal(["JV-1||", "JV-1||"], page.GetProperty("rows").EnumerateArray().Select(RawValueRow).ToArray());
    }

    // 傳票號碼|核准日|來源原文；欄位不存在時斷言失敗並說明。
    private static string RawValueRow(JsonElement row)
    {
        Assert.True(row.TryGetProperty("approvalDateRawValue", out var raw), "明細頁的列沒有 approvalDateRawValue");
        return row.GetProperty("voucherNumber").GetString() + "|" + ApprovalDate(row) + "|"
            + (raw.ValueKind == JsonValueKind.Null ? "" : raw.GetString());
    }

    private static async Task<string?[]> SortedApprovalDatesAsync(HandlerTestHost host, string direction)
    {
        var page = await host.DispatchAsync("query.blankRecordsPage", JsonSerializer.Serialize(new
        {
            category = "approvalDateNotInPeriod",
            pageSize = 50,
            sort = new { key = "approvalDate", direction },
        }));
        return page.GetProperty("rows").EnumerateArray().Select(ApprovalDate).ToArray();
    }

    private static string? ApprovalDate(JsonElement row) =>
        row.GetProperty("approvalDate").ValueKind == JsonValueKind.Null ? null : row.GetProperty("approvalDate").GetString();

    private static string[] ApprovalFlaggedVouchers(JsonElement blankRecords) =>
        blankRecords.GetProperty("blankRows").EnumerateArray()
            .Where(static row => row.GetProperty("issues").EnumerateArray()
                .Any(static issue => issue.GetString() == "approvalDate"))
            .Select(static row => row.GetProperty("voucherNumber").GetString()!)
            .ToArray();
}
