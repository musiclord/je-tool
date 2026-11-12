using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// prescreen.run 黑箱測試。可定錨值取自 DemoDataFactory 的 seed 常數
/// (摘要關鍵字、期末後核准、零尾數門檻、週末過帳、摘要空白等),規則命中數一律引用常數;
/// 其餘以「handler 計數 == 獨立參數化 SQL recount」斷言。
/// </summary>
public sealed class PrescreenRunHandlerTests(DemoProjectFixture fixture) : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task PrescreenRun_BeforeMappingCommit_ThrowsCompletenessPrerequisiteFailed()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, commitGl: false, commitTb: false);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("prescreen.run"));

        Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, ex.Code);
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_SuspiciousKeywordsCountsKeywordSeed()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        // R2 種子:每張借方行摘要「調整分錄」命中(僅借方行),共 SuspiciousKeywordVouchers 列;
        // baseline 安全摘要不含任何預設關鍵字。
        Assert.Equal(DemoDataFactory.SuspiciousKeywordVouchers, data.GetProperty("suspiciousKeywords").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_PrepPeriodApprovalMatchesSetBasedRecount()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        // recount 與 prescreen 母體同口徑（限本期 gl_date）——期外對照組核准日雖 ≥ 期末，
        // 但總帳日期在查核期間外，不入母體，故不計。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM gl WHERE approval_date >= @prepStartDate " +
            "AND gl_date >= '2025-01-01' AND gl_date <= '2025-12-31';",
            ("@prepStartDate", "2025-12-31"));

        Assert.Equal(recount, data.GetProperty("prepPeriodApproval").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_PrepPeriodApprovalHitsSeedVouchers()
    {
        // R1 種子:核准日 2026-01-15(期末後)共 PrepPeriodApprovalVouchers 張,過帳日在期內;
        // baseline 核准日 = 過帳日(平日、< 期末),不可能跨入期末日。母體限本期 gl_date：
        // R1 過帳期內入母體；期外對照組（過帳 2026-01-05）核准日雖 ≥ 期末，但不入母體 → 不計。
        var distinctVouchers = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(DISTINCT voucher_number) FROM gl WHERE approval_date >= '2025-12-31' " +
            "AND gl_date >= '2025-01-01' AND gl_date <= '2025-12-31';");

        Assert.Equal(DemoDataFactory.PrepPeriodApprovalVouchers, distinctVouchers);
    }

    // 0 的個數照 IDEA 版 R4 由 GL 算出，
    // 預期值由範例資料逐列獨立計算：查核期間內的借方金額合計除以分錄筆數，
    // 平均的整數部分位數減一。
    [Fact]
    public async Task PrescreenRun_DemoFixture_TrailingZerosThresholdFollowsTheDebitAverage()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        Assert.Equal(DemoTrailingZeroDigits(), data.GetProperty("trailingZeros").GetProperty("zerosThreshold").GetInt32());
        // 範例金額多在 1 到 5 萬元，每張傳票一借一貸，借方平均約一萬多元，是 5 位數、4 個 0。
        Assert.Equal(4, DemoTrailingZeroDigits());
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_TrailingZerosMatchesSetBasedRecount()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        // scale 10000：先向零取得主單位整數，再檢查 10 的 N 次方倍數，N 是由範例資料獨立算出的 0 的個數。
        // 小數尾數不參與 legacy 尾零判定；0 排除，母體固定查核期間。範例的一般分錄沒有整數部分是一萬倍數的金額，
        // 所以選到的都是 30 筆種子分錄，這裡只證 handler 與獨立 SQL 同源。
        var modulus = (long)Math.Pow(10, DemoTrailingZeroDigits());
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM gl " +
            "WHERE gl_date >= '2025-01-01' AND gl_date <= '2025-12-31' " +
            "AND CAST(ABS(amount_scaled) / 10000 AS INTEGER) <> 0 " +
            $"AND CAST(ABS(amount_scaled) / 10000 AS INTEGER) % {modulus} = 0;");

        Assert.Equal(recount, data.GetProperty("trailingZeros").GetProperty("count").GetInt64());
        Assert.Equal(DemoDataFactory.TrailingZerosVouchers * DemoDataFactory.LinesPerVoucher, recount);
    }

    /// <summary>範例資料查核期間內的借方金額合計除以分錄筆數，平均整數部分的位數減一，至少 1 個、最多 12 個；不經過被測程式。</summary>
    private static int DemoTrailingZeroDigits()
    {
        var demo = DemoDataFactory.Create();
        var start = DateOnly.Parse(demo.PeriodStart, System.Globalization.CultureInfo.InvariantCulture);
        var end = DateOnly.Parse(demo.PeriodEnd, System.Globalization.CultureInfo.InvariantCulture);
        var rows = demo.GlRows.Where(row => row.GlDate >= start && row.GlDate <= end).ToList();
        var average = decimal.Truncate(rows.Where(row => row.IsDebit).Sum(row => row.Amount) / rows.Count);
        return Math.Clamp(average.ToString(System.Globalization.CultureInfo.InvariantCulture).Length - 1, 1, 12);
    }

    [Fact]
    public async Task PrescreenRun_ApplicableZeroHit_UsesNaZeroAndNullReasonOnWire()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(
                    "傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-ZERO", "2025-02-03", "2025-02-03", "1101", "現金", "一般分錄", "12.34", 1)
                .AddRow("JV-ZERO", "2025-02-03", "2025-02-03", "4101", "銷貨收入", "一般分錄", "12.34", 0),
            prepStartDate: "2025-12-31",
            configureTb: tb => tb
                .AddRow("1101", "現金", "12.34")
                .AddRow("4101", "銷貨收入", "-12.34"));
        await host.DispatchAsync("validate.run");

        var data = await host.DispatchAsync("prescreen.run");
        var prepPeriodApproval = data.GetProperty("prepPeriodApproval");

        Assert.Equal("na", prepPeriodApproval.GetProperty("status").GetString());
        Assert.Equal(0, prepPeriodApproval.GetProperty("count").GetInt64());
        Assert.Equal(JsonValueKind.Null, prepPeriodApproval.GetProperty("naReason").ValueKind);
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_WeekendPostingsMatchSeed()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        // R7 種子:過帳日為週六(非補班)共 WeekendPostingVouchers 張、兩行皆命中;
        // baseline 與其餘種子過帳日皆走平日池,不命中週末過帳。
        Assert.Equal(
            DemoDataFactory.WeekendPostingVouchers * DemoDataFactory.LinesPerVoucher,
            data.GetProperty("weekendActivity").GetProperty("postingCount").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_WeekendApprovalsMatchRecount()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        // 週五假日過帳 + 1/2 天核准 → 落在週六/週日（包含補班日，種子保證非空）。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            """
            SELECT COUNT(*) FROM gl g
            WHERE strftime('%w', g.approval_date) IN ('0','6');
            """);

        Assert.True(recount > 0);
        Assert.Equal(recount, data.GetProperty("weekendActivity").GetProperty("approvalCount").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_MakeupSaturday_IncludedInWeekendPostings()
    {
        // 週末規則依星期判斷；補班週六與一般週六都應納入。
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-001", "2025-02-08", "1101", "現金", "補班日傳票", "100.00", 1)
                .AddRow("JV-001", "2025-02-08", "4101", "銷貨收入", "補班日傳票", "100.00", 0)
                .AddRow("JV-002", "2025-02-15", "1101", "現金", "一般週六傳票", "80.00", 1)
                .AddRow("JV-002", "2025-02-15", "4101", "銷貨收入", "一般週六傳票", "80.00", 0),
            makeupDays: ["2025-02-08"],
            configureTb: tb => tb
                .AddRow("1101", "現金", "180.00")
                .AddRow("4101", "銷貨收入", "-180.00"));
        await host.DispatchAsync("validate.run");

        var data = await host.DispatchAsync("prescreen.run");

        Assert.Equal(4, data.GetProperty("weekendActivity").GetProperty("postingCount").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_HolidayPostingsMatchRecount()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            """
            SELECT COUNT(*) FROM gl g
            WHERE EXISTS (
                SELECT 1 FROM calendar_day d
                WHERE d.day_type = 'holiday' AND d.date = g.gl_date);
            """);

        Assert.True(recount > 0);
        Assert.Equal(recount, data.GetProperty("holidayActivity").GetProperty("postingCount").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_HolidayApprovalsMatchRecount()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            """
            SELECT COUNT(*) FROM gl g
            WHERE EXISTS (
                SELECT 1 FROM calendar_day d
                WHERE d.day_type = 'holiday' AND d.date = g.approval_date);
            """);

        Assert.True(recount > 0);
        Assert.Equal(recount, data.GetProperty("holidayActivity").GetProperty("approvalCount").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_WithoutHolidays_HolidayActivityIsNa()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-001", "2025-03-05", "1101", "現金", "一般分錄", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "一般分錄", "100.00", 0),
            configureTb: tb => tb
                .AddRow("1101", "現金", "100.00")
                .AddRow("4101", "銷貨收入", "-100.00"));
        await host.DispatchAsync("validate.run");

        var data = await host.DispatchAsync("prescreen.run");

        var holidayActivity = data.GetProperty("holidayActivity");
        Assert.Equal("na", holidayActivity.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, holidayActivity.GetProperty("naReason").ValueKind);
    }

    [Fact]
    public async Task PrescreenRun_MissingApprovalDateWithHolidayCalendar_PreservesCompositeWirePrecedence()
    {
        // 決策表：缺 approvalDate＋有假日曆，週末核准保留 N/A，假日過帳命中則主導複合結果。
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-001", "2025-03-05", "1101", "現金", "一般分錄", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "一般分錄", "100.00", 0),
            holidays: ["2025-03-05"],
            configureTb: tb => tb
                .AddRow("1101", "現金", "100.00")
                .AddRow("4101", "銷貨收入", "-100.00"));
        await host.DispatchAsync("validate.run");

        var data = await host.DispatchAsync("prescreen.run");

        // 兩張卡用同一個方法合併兩側的原因。缺傳票核准日的配對時，
        // 兩張卡都照常顯示總帳日期那一側，「只檢查了總帳日期」放在 approvalNaReason，naReason 只在整張卡無法執行時才有。
        const string onlyGlDateChecked = "只檢查了總帳日期。「傳票核准日」尚未選擇來源欄，請回第三步欄位配對補上。";
        var weekendActivity = data.GetProperty("weekendActivity");
        Assert.Equal("na", weekendActivity.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, weekendActivity.GetProperty("naReason").ValueKind);
        Assert.Equal(onlyGlDateChecked, weekendActivity.GetProperty("approvalNaReason").GetString());
        Assert.Equal(0, weekendActivity.GetProperty("postingCount").GetInt64());
        Assert.Equal(JsonValueKind.Null, weekendActivity.GetProperty("approvalCount").ValueKind);

        var holidayActivity = data.GetProperty("holidayActivity");
        Assert.Equal("V", holidayActivity.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, holidayActivity.GetProperty("naReason").ValueKind);
        Assert.Equal(onlyGlDateChecked, holidayActivity.GetProperty("approvalNaReason").GetString());
        Assert.Equal(2, holidayActivity.GetProperty("postingCount").GetInt64());
        Assert.Equal(JsonValueKind.Null, holidayActivity.GetProperty("approvalCount").ValueKind);
    }

    // 整張卡無法執行時，原因列出兩側缺的每一項，一項一句、依表列順序，
    // 假日檔排在最前面。
    [Fact]
    public async Task PrescreenRun_MissingApprovalDateWithoutHolidayCalendar_HolidayCalendarReasonComesFirst()
    {
        // 決策表：缺 approvalDate＋缺假日曆，複合 wire 的 N/A 原因以假日曆前置為準。
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-001", "2025-03-05", "1101", "現金", "一般分錄", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "一般分錄", "100.00", 0),
            configureTb: tb => tb
                .AddRow("1101", "現金", "100.00")
                .AddRow("4101", "銷貨收入", "-100.00"));
        await host.DispatchAsync("validate.run");

        var data = await host.DispatchAsync("prescreen.run");

        var holidayActivity = data.GetProperty("holidayActivity");
        Assert.Equal("na", holidayActivity.GetProperty("status").GetString());
        Assert.Equal(
            // 這句的用字已定案。
            "尚未上傳國定假日清單，請先在第二步「國定假日與補班日」上傳。「傳票核准日」尚未選擇來源欄，請回第三步欄位配對補上。",
            holidayActivity.GetProperty("naReason").GetString());
        Assert.Equal(JsonValueKind.Null, holidayActivity.GetProperty("approvalNaReason").ValueKind);
        Assert.Equal(0, holidayActivity.GetProperty("postingCount").GetInt64());
        Assert.Equal(JsonValueKind.Null, holidayActivity.GetProperty("approvalCount").ValueKind);
    }

    [Fact]
    public async Task PrescreenRun_UnexpectedAccountPairIsNaWithoutAccountMapping()
    {
        // 前置不足:未匯入科目配對 → unexpectedAccountPair 為 na。
        // 自建 host 並關閉科目配對匯入以維持原意(共用 fixture 預設已匯入)。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);

        var data = await host.DispatchAsync("prescreen.run");

        Assert.Equal("na", data.GetProperty("unexpectedAccountPair").GetProperty("status").GetString());
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_PreparerSummaryCoversAllPreparers()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        // demo 編製者數遠少於彙總上限(50),故全員列出;名單基數 == 本期 distinct recount,
        // 各人筆數合計 == 本期母體列數(編製者彙總限本期 gl_date；期外對照組 4 列不計)。
        var distinctPreparers = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(DISTINCT created_by) FROM gl " +
            "WHERE gl_date >= '2025-01-01' AND gl_date <= '2025-12-31';");

        var preparers = data.GetProperty("preparerSummary").GetProperty("preparers").EnumerateArray().ToList();
        Assert.Equal(distinctPreparers, preparers.Count);
        Assert.Equal(
            (DemoDataFactory.GlVoucherCount - DemoDataFactory.OutOfPeriodVouchers) * DemoDataFactory.LinesPerVoucher,
            preparers.Sum(c => c.GetProperty("entryCount").GetInt64()));
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_RareAccountsDistinctMatchesRecount()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(DISTINCT account_number) FROM gl;");

        Assert.Equal(recount, data.GetProperty("rareAccounts").GetProperty("distinctAccountCount").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_RareAccountsAscendingByUsageAndCapped()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        var accounts = data.GetProperty("rareAccounts").GetProperty("accounts").EnumerateArray().ToList();
        Assert.True(accounts.Count <= 50);

        var counts = accounts.Select(a => a.GetProperty("entryCount").GetInt64()).ToList();
        Assert.Equal(counts.OrderBy(c => c).ToList(), counts);
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_BlankDescriptionMatchesSeed()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        // descNull 種子:每張借方行摘要空白共 BlankDescriptionVouchers 列;baseline 摘要皆非空。
        Assert.Equal(DemoDataFactory.BlankDescriptionVouchers, data.GetProperty("blankDescription").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_NonAuthorizedPreparerIsNaWithoutList()
    {
        // 非授權編製人員閘控（鏡射 unexpectedAccountPair）：授權編製人員清單未匯入 → status na、count 0。
        // 自建 host 並關閉授權清單匯入以維持原意(共用 fixture 預設已匯入)。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, importAuthorizedPreparer: false);

        var data = await host.DispatchAsync("prescreen.run");

        var nonAuthorized = data.GetProperty("nonAuthorizedPreparer");
        Assert.Equal("na", nonAuthorized.GetProperty("status").GetString());
        Assert.Equal(0, nonAuthorized.GetProperty("count").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, nonAuthorized.GetProperty("naReason").ValueKind);
    }

    [Fact]
    public async Task PrescreenRun_WithAuthorizedList_NonAuthorizedMatchesRecount()
    {
        // 名單匯入後閘控放行:handler 計數須等於同述詞的獨立 recount,且 status 不再 na。
        // 自建 host 以隔離狀態變更(匯入授權清單)。
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        // demo 高頻編製者有多位;只授權其中三人,其餘高頻編製者(及未授權者種子)→ 非授權命中。
        var listPath = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "AUTHORIZED_PREPARER";
            ws.Cell(2, 1).Value = "王小明";
            ws.Cell(3, 1).Value = "李美麗";
            ws.Cell(4, 1).Value = "陳大文";
        });

        try
        {
            // 匯入人員清單必須指定 sourceColumn。
            await host.DispatchAsync("import.authorizedPreparer.fromFile",
                JsonSerializer.Serialize(new { filePath = listPath, fileName = "ap.xlsx", sourceColumn = "AUTHORIZED_PREPARER" }));

            var data = await host.DispatchAsync("prescreen.run");

            var recount = await DemoProjectPipeline.QueryScalarAsync(host, context.ProjectId,
                "SELECT COUNT(*) FROM gl g WHERE g.created_by IS NOT NULL " +
                "AND TRIM(g.created_by) <> '' " +
                "AND TRIM(g.created_by) NOT IN (SELECT name FROM authorized_preparer);");

            var nonAuthorized = data.GetProperty("nonAuthorizedPreparer");
            Assert.True(recount > 0, "只授權部分編製者 → 應有非授權命中");
            Assert.Equal(recount, nonAuthorized.GetProperty("count").GetInt64());
            Assert.Equal("V", nonAuthorized.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, nonAuthorized.GetProperty("naReason").ValueKind);
        }
        finally
        {
            TestWorkbookBuilder.Delete(listPath);
        }
    }

    [Fact]
    public async Task FilterPreview_CustomPreparerEntryCount_MatchesRecount()
    {
        // 編製人員分錄筆數 200 以下：filter.preview 的命中數須等於同述詞的獨立 recount（handler 與獨立 SQL 同源，不另設命中量級假設）。
        // 舊式 customPreparerEntryCount 是停用寫法，情境用等價的「科目與人員統計」。
        const int maxEntries = 200;
        var payload = JsonSerializer.Serialize(new
        {
            scenario = new
            {
                name = "較少使用編製者(自訂)",
                rationale = "自訂門檻軌驗收",
                groups = new[]
                {
                    new { join = "AND", conditions = new[]
                    {
                        new { join = "AND", type = "entityFrequency", field = "createdBy", countUnit = "entries", countOperator = "lessThanOrEqual", countFrom = maxEntries }
                    } }
                }
            }
        });

        var data = await fixture.Host.DispatchAsync("filter.preview", payload);

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM gl g WHERE g.created_by IN " +
            $"(SELECT created_by FROM gl GROUP BY created_by HAVING COUNT(*) <= {maxEntries});");

        Assert.Equal(recount, data.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_CustomAccountEntryCount_MatchesRecount()
    {
        // 科目分錄筆數 11 以下：filter.preview 的命中數須等於同述詞的獨立 recount（和第五步依審計目的「較少使用的科目」的預設條件相同）。
        // 舊式 customAccountEntryCount 是停用寫法，情境用等價的「科目與人員統計」。
        const int maxEntries = 11;
        var payload = JsonSerializer.Serialize(new
        {
            scenario = new
            {
                name = "較少使用科目(自訂)",
                rationale = "自訂門檻軌驗收",
                groups = new[]
                {
                    new { join = "AND", conditions = new[]
                    {
                        new { join = "AND", type = "entityFrequency", field = "accountNumber", countUnit = "entries", countOperator = "lessThanOrEqual", countFrom = maxEntries }
                    } }
                }
            }
        });

        var data = await fixture.Host.DispatchAsync("filter.preview", payload);

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM gl g WHERE g.account_number IN " +
            $"(SELECT account_number FROM gl GROUP BY account_number HAVING COUNT(*) <= {maxEntries});");

        Assert.Equal(recount, data.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_PersistsLatestResultForResume()
    {
        await fixture.Host.DispatchAsync("prescreen.run");

        var loaded = await fixture.Host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = fixture.ProjectId }));

        var resumed = loaded.GetProperty("prescreenResult");
        Assert.Equal(DemoDataFactory.SuspiciousKeywordVouchers, resumed.GetProperty("suspiciousKeywords").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_TypedPlanIdentity_MatchesPersistedRunIdentity()
    {
        var folder = new JetProjectFolder(fixture.Host.ProjectsRoot);
        var database = new SqliteProjectDatabase(folder);
        var resultStore = new LocalResultStore(database);
        var factsPort = new RecordingPrescreenFactsPort(
            new LocalPrescreenRunRepository(database));
        // handler 從作用中案件的資料庫組取 repository，把這些物件放進 SQLite 資料庫組後再進入 session。
        // 預篩選現在多讀科目分類設定，取原因裡的分類名稱，資料庫組一併放進去。
        // （處理器讀分類設定時遇到空參考）。
        var session = new ProjectSession();
        session.Enter(
            fixture.ProjectId,
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                PrescreenFacts = factsPort,
                MappingStates = new LocalMappingStateStore(database),
                Calendar = new LocalCalendarStore(database),
                AccountMappings = new LocalAccountMappingRepository(database),
                AccountCategories = new LocalAccountCategoryStore(database),
                AuthorizedPreparers = new LocalAuthorizedPreparerRepository(database),
                Results = resultStore,
            });
        var handler = new PrescreenRunHandler(new JsonFileProjectStore(folder), session);

        await handler.HandleAsync(default, CancellationToken.None);

        var executedPlan = Assert.IsType<PrescreenPlan>(factsPort.ExecutedPlan);
        var saved = await resultStore.FindLatestAsync(
            fixture.ProjectId,
            ResultKind.Prescreen,
            CancellationToken.None);
        Assert.NotNull(saved);
        Assert.False(string.IsNullOrWhiteSpace(executedPlan.Request.ResultId));
        Assert.NotEqual(default, executedPlan.Request.GeneratedUtc);
        Assert.Equal(
            new AuditUserParameters(
                saved!.ResultId,
                saved.GeneratedUtc,
                SampleSize: 0,
                ActionName: "prescreen.run"),
            executedPlan.ReviewPlan.UserParameters);

        using var summary = JsonDocument.Parse(saved.SummaryJson);
        var resultRef = summary.RootElement.GetProperty("resultRef");
        Assert.Equal(saved.ResultId, resultRef.GetProperty("resultId").GetString());
        Assert.Equal(
            saved.GeneratedUtc,
            resultRef.GetProperty("generatedUtc").GetDateTimeOffset());
    }

    private sealed class RecordingPrescreenFactsPort(IPrescreenFactsPort inner) : IPrescreenFactsPort
    {
        public PrescreenPlan? ExecutedPlan { get; private set; }

        public Task<PrescreenFacts> ExecuteAsync(
            PrescreenPlan plan,
            CancellationToken cancellationToken)
        {
            ExecutedPlan = plan;
            return inner.ExecuteAsync(plan, cancellationToken);
        }
    }
}
