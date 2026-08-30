using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 守門測試：預篩選命中預覽重用 filter.preview，
/// 任一預篩選鍵的 filter.preview 筆數必須等於 prescreen.run 同鍵筆數。
/// 這是「共用述詞 seam」的行為不變式（見 jet-guide §共用述詞 / filter engine）。
/// 若任何鍵出現不等，代表述詞分歧（如門檻值、context 不對齊），請勿弱化斷言。
/// </summary>
public sealed class PrescreenPreviewParityTests
{
    /// <summary>
    /// 對單個 prescreenKey 組成「單條件情境」的 filter.preview payload。
    /// 形狀與 filter-step.js prescreenScenario(key) 完全相同。
    /// </summary>
    private static string PreviewPayload(string prescreenKey) =>
        JsonSerializer.Serialize(new
        {
            scenario = new
            {
                name = "(預覽)",
                rationale = "預篩選命中預覽",
                groups = new object[]
                {
                    new
                    {
                        join = "AND",
                        rules = new object[]
                        {
                            new { join = "AND", type = "prescreen", prescreenKey }
                        }
                    }
                }
            }
        });

    /// <summary>
    /// 涵蓋：suspiciousKeywords、trailingZeros、blankDescription、weekendPosting、
    /// weekendApproval、postPeriodApproval、holidayPosting。
    ///
    /// 播種內容（lastPeriodStart = 2025-12-31）：
    ///   JV-001  過帳 2025-01-01（週三）  核准 2026-01-10（期末後）  摘要＝調整分錄（關鍵字）  金額 5000000（6 個尾數 0 = 固定預設門檻）
    ///   JV-002  過帳 2025-01-04（週六）  核准 2025-01-06（週一）  摘要＝進貨            金額 999
    ///   JV-003  過帳 2025-01-01（週三）  核准 2025-01-04（週六週末核准）  摘要＝（空白）
    ///   JV-004  過帳 2025-01-27（假日）  核准 2025-01-28（假日）  摘要＝假日過帳測試  金額 500
    ///
    /// 傳票各雙列（借貸配對）確保傳票平衡。
    /// </summary>
    [Fact]
    public async Task PrescreenPreviewParity_SeededPopulation_AllKeysMatch()
    {
        using var host = new HandlerTestHost();

        // 播種方式一：InlineWorkbookProject（lastPeriodStart、holidays）
        // 使用「核准日期」欄位（InlineGlWorkbookBuilder 的 "核准日期" → docDate）。
        // 借方旗標欄 1 = 借方。
        await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(
                    "傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                // JV-001：週三過帳、期末後核准（2026-01-10）、摘要含關鍵字「調整分錄」、金額 6 個尾數 0（5000000 = 固定預設門檻）
                .AddRow("JV-001", "2025-01-01", "2026-01-10", "1101", "現金",     "調整分錄",   "5000000.00", 1)
                .AddRow("JV-001", "2025-01-01", "2026-01-10", "4101", "銷貨收入", "調整分錄",   "5000000.00", 0)
                // JV-002：週六過帳（weekendPosting）、週一核准、金額 999（非整零）
                .AddRow("JV-002", "2025-01-04", "2025-01-06", "1101", "現金",     "進貨",       "999.00",  1)
                .AddRow("JV-002", "2025-01-04", "2025-01-06", "4101", "銷貨收入", "進貨",       "999.00",  0)
                // JV-003：週三過帳、週六核准（weekendApproval）、摘要空白（blankDescription）、金額 500
                .AddRow("JV-003", "2025-01-08", "2025-01-11", "1101", "現金",     "",           "500.00",  1)
                .AddRow("JV-003", "2025-01-08", "2025-01-11", "4101", "銷貨收入", "",           "500.00",  0)
                // JV-004：假日過帳（2025-01-27 台灣農曆年前假日）、假日核准（2025-01-28）、金額 200
                .AddRow("JV-004", "2025-01-27", "2025-01-28", "1101", "現金",     "假日過帳測試", "200.00", 1)
                .AddRow("JV-004", "2025-01-27", "2025-01-28", "4101", "銷貨收入", "假日過帳測試", "200.00", 0),
            lastPeriodStart: "2025-12-31",
            holidays: ["2025-01-27", "2025-01-28"],
            validateForDownstream: true);

        // 跑一次 prescreen.run，取各規則命中數。
        var prescreen = await host.DispatchAsync("prescreen.run");

        // --- 1. suspiciousKeywords ---
        var skCount = prescreen.GetProperty("suspiciousKeywords").GetProperty("count").GetInt64();
        var skPreview = await host.DispatchAsync("filter.preview", PreviewPayload("suspiciousKeywords"));
        Assert.Equal(skCount, skPreview.GetProperty("scenario").GetProperty("count").GetInt64());

        // --- 2. trailingZeros ---
        // 門檻為固定預設 6;prescreen.run 與 filter.preview 同源,count 應相等。
        var tzCount = prescreen.GetProperty("trailingZeros").GetProperty("count").GetInt64();
        var tzPreview = await host.DispatchAsync("filter.preview", PreviewPayload("trailingZeros"));
        Assert.Equal(tzCount, tzPreview.GetProperty("scenario").GetProperty("count").GetInt64());

        // --- 3. blankDescription ---
        var bdCount = prescreen.GetProperty("blankDescription").GetProperty("count").GetInt64();
        var bdPreview = await host.DispatchAsync("filter.preview", PreviewPayload("blankDescription"));
        Assert.Equal(bdCount, bdPreview.GetProperty("scenario").GetProperty("count").GetInt64());

        // --- 4. weekendPosting ---
        var wpCount = prescreen.GetProperty("weekendActivity").GetProperty("postingCount").GetInt64();
        var wpPreview = await host.DispatchAsync("filter.preview", PreviewPayload("weekendPosting"));
        Assert.Equal(wpCount, wpPreview.GetProperty("scenario").GetProperty("count").GetInt64());

        // --- 5. weekendApproval ---
        // weekendApproval 需有核准日欄位（"核准日期" 已配對）。
        var waCount = prescreen.GetProperty("weekendActivity").GetProperty("approvalCount").GetInt64();
        var waPreview = await host.DispatchAsync("filter.preview", PreviewPayload("weekendApproval"));
        Assert.Equal(waCount, waPreview.GetProperty("scenario").GetProperty("count").GetInt64());

        // --- 6. postPeriodApproval ---
        // 需 lastPeriodStart 已設定（已於 project.create 傳入 "2025-12-31"）。
        var ppCount = prescreen.GetProperty("postPeriodApproval").GetProperty("count").GetInt64();
        var ppPreview = await host.DispatchAsync("filter.preview", PreviewPayload("postPeriodApproval"));
        Assert.Equal(ppCount, ppPreview.GetProperty("scenario").GetProperty("count").GetInt64());

        // --- 7. holidayPosting ---
        var hpCount = prescreen.GetProperty("holidayActivity").GetProperty("postingCount").GetInt64();
        var hpPreview = await host.DispatchAsync("filter.preview", PreviewPayload("holidayPosting"));
        Assert.Equal(hpCount, hpPreview.GetProperty("scenario").GetProperty("count").GetInt64());

        // 額外 sanity guards：確保命中數非零，避免「0 == 0」誤判通過。
        Assert.True(skCount > 0, "suspiciousKeywords 應有命中（「調整分錄」是關鍵字）");
        Assert.True(tzCount > 0, "trailingZeros 應有命中，否則等式為 0==0 無意義（JV-001 金額 5000000 = 6 個尾數 0）");
        Assert.True(bdCount > 0, "blankDescription 應有命中（JV-003 摘要為空白）");
        Assert.True(wpCount > 0, "weekendPosting 應有命中（JV-002 過帳日 2025-01-04 週六）");
        Assert.True(waCount > 0, "weekendApproval 應有命中，否則等式為 0==0 無意義（JV-003 核准日 2025-01-11 週六）");
        Assert.True(ppCount > 0, "postPeriodApproval 應有命中（JV-001 核准日 2026-01-10）");
        Assert.True(hpCount > 0, "holidayPosting 應有命中（JV-004 過帳日 2025-01-27 假日）");
    }

    /// <summary>
    /// 涵蓋：unexpectedAccountPair。
    /// 需要科目配對（Revenue + Receivables/Cash 對方分類），才能解鎖此規則。
    /// 使用 demo 管線（含科目配對匯入）。
    /// </summary>
    [Fact]
    public async Task PrescreenPreviewParity_UnexpectedAccountPair_MatchesPrescreenRunCount()
    {
        using var host = new HandlerTestHost();

        // demo 管線有 Revenue（4101/4201）+ Cash（1101/1102）+ Receivables，滿足前置條件。
        await DemoProjectPipeline.SetupAsync(host);
        var accountFile = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAccountMappingFile");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = accountFile.GetProperty("filePath").GetString()
        }));

        var prescreen = await host.DispatchAsync("prescreen.run");

        var uapCount = prescreen.GetProperty("unexpectedAccountPair").GetProperty("count").GetInt64();
        var uapPreview = await host.DispatchAsync("filter.preview", PreviewPayload("unexpectedAccountPair"));

        Assert.Equal(uapCount, uapPreview.GetProperty("scenario").GetProperty("count").GetInt64());
        Assert.True(uapCount > 0, "unexpectedAccountPair 應有命中（demo 含 Revenue 對方分類科目組合）");
    }
}
