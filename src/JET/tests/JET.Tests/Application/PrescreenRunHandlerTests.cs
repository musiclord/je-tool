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
    public async Task PrescreenRun_DemoFixture_PostPeriodApprovalMatchesSetBasedRecount()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        // recount 與 prescreen 母體同口徑（§2：限本期 post_date）——期外對照組核准日雖 ≥ 期末，
        // 但過帳日期外不入母體，故不計。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE approval_date >= @lastPeriodStart " +
            "AND post_date >= '2025-01-01' AND post_date <= '2025-12-31';",
            ("@lastPeriodStart", "2025-12-31"));

        Assert.Equal(recount, data.GetProperty("postPeriodApproval").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_PostPeriodApprovalHitsSeedVouchers()
    {
        // R1 種子:核准日 2026-01-15(期末後)共 PostPeriodApprovalVouchers 張,過帳日在期內;
        // baseline 核准日 = 過帳日(平日、< 期末),不可能跨入期末日。母體限本期 post_date（§2）：
        // R1 過帳期內入母體；期外對照組（過帳 2026-01-05）核准日雖 ≥ 期末，但不入母體 → 不計。
        var distinctVouchers = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(DISTINCT document_number) FROM target_gl_entry WHERE approval_date >= '2025-12-31' " +
            "AND post_date >= '2025-01-01' AND post_date <= '2025-12-31';");

        Assert.Equal(DemoDataFactory.PostPeriodApprovalVouchers, distinctVouchers);
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_TrailingZerosThresholdIsSix()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        // 校正後為方法學固定預設 6(取代原動態 max(3, floor(log10(avg))−1))。
        Assert.Equal(6, data.GetProperty("trailingZeros").GetProperty("zerosThreshold").GetInt32());
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_TrailingZerosMatchesSetBasedRecount()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        // 固定預設 6、scale 10000：先向零取得主單位整數，再檢查 10^6 倍數。
        // 小數尾數不參與 legacy 尾零判定；0 排除，母體固定查核期間。
        // demo 金額多在 10^4 量級、無 10^6 倍數,此 recount 可能為 0(方法學正確);
        // 因此不加 > 0 守門,只證 handler 與獨立 SQL 同源。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry " +
            "WHERE post_date >= '2025-01-01' AND post_date <= '2025-12-31' " +
            "AND CAST(ABS(amount_scaled) / 10000 AS INTEGER) <> 0 " +
            "AND CAST(ABS(amount_scaled) / 10000 AS INTEGER) % 1000000 = 0;");

        Assert.Equal(recount, data.GetProperty("trailingZeros").GetProperty("count").GetInt64());
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
            lastPeriodStart: "2025-12-31",
            configureTb: tb => tb
                .AddRow("1101", "現金", "12.34")
                .AddRow("4101", "銷貨收入", "-12.34"));
        await host.DispatchAsync("validate.run");

        var data = await host.DispatchAsync("prescreen.run");
        var postPeriodApproval = data.GetProperty("postPeriodApproval");

        Assert.Equal("na", postPeriodApproval.GetProperty("status").GetString());
        Assert.Equal(0, postPeriodApproval.GetProperty("count").GetInt64());
        Assert.Equal(JsonValueKind.Null, postPeriodApproval.GetProperty("naReason").ValueKind);
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
            SELECT COUNT(*) FROM target_gl_entry g
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
            SELECT COUNT(*) FROM target_gl_entry g
            WHERE EXISTS (
                SELECT 1 FROM staging_calendar_raw_day d
                WHERE d.day_type = 'holiday' AND d.date = g.post_date);
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
            SELECT COUNT(*) FROM target_gl_entry g
            WHERE EXISTS (
                SELECT 1 FROM staging_calendar_raw_day d
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
        // 決策表：缺 docDate＋有假日曆，週末核准保留 N/A，假日過帳命中則主導複合結果。
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

        var weekendActivity = data.GetProperty("weekendActivity");
        Assert.Equal("na", weekendActivity.GetProperty("status").GetString());
        Assert.Equal(
            "尚未完成 GL「傳票核准日」欄位配對，因此僅檢查過帳日。",
            weekendActivity.GetProperty("naReason").GetString());
        Assert.Equal(0, weekendActivity.GetProperty("postingCount").GetInt64());
        Assert.Equal(JsonValueKind.Null, weekendActivity.GetProperty("approvalCount").ValueKind);

        var holidayActivity = data.GetProperty("holidayActivity");
        Assert.Equal("V", holidayActivity.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, holidayActivity.GetProperty("naReason").ValueKind);
        Assert.Equal(2, holidayActivity.GetProperty("postingCount").GetInt64());
        Assert.Equal(JsonValueKind.Null, holidayActivity.GetProperty("approvalCount").ValueKind);
    }

    [Fact]
    public async Task PrescreenRun_MissingApprovalDateWithoutHolidayCalendar_HolidayCalendarReasonWins()
    {
        // 決策表：缺 docDate＋缺假日曆，複合 wire 的 N/A 原因以假日曆前置為準。
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
            "請先上傳事務所假日檔。",
            holidayActivity.GetProperty("naReason").GetString());
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
    public async Task PrescreenRun_DemoFixture_CreatorSummaryCoversAllCreators()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        // demo 編製者數遠少於彙總上限(50),故全員列出;名單基數 == 本期 distinct recount,
        // 各人筆數合計 == 本期母體列數(§2：編製者彙總限本期 post_date；期外對照組 4 列不計)。
        var distinctCreators = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(DISTINCT created_by) FROM target_gl_entry " +
            "WHERE post_date >= '2025-01-01' AND post_date <= '2025-12-31';");

        var creators = data.GetProperty("creatorSummary").GetProperty("creators").EnumerateArray().ToList();
        Assert.Equal(distinctCreators, creators.Count);
        Assert.Equal(
            (DemoDataFactory.GlVoucherCount - DemoDataFactory.OutOfPeriodVouchers) * DemoDataFactory.LinesPerVoucher,
            creators.Sum(c => c.GetProperty("entryCount").GetInt64()));
    }

    [Fact]
    public async Task PrescreenRun_DemoFixture_RareAccountsDistinctMatchesRecount()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(DISTINCT account_code) FROM target_gl_entry;");

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
    public async Task PrescreenRun_BackdatedPosting_CountMatchesIndependentRecount()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry g WHERE g.voucher_date IS NOT NULL AND g.post_date < g.voucher_date;");

        Assert.True(recount > 0, "demo fixture 應埋至少一筆回溯命中");
        Assert.Equal(
            recount,
            data.GetProperty("backdatedPosting").GetProperty("count").GetInt64());
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
            await host.DispatchAsync("import.authorizedPreparer.fromFile",
                JsonSerializer.Serialize(new { filePath = listPath, fileName = "ap.xlsx" }));

            var data = await host.DispatchAsync("prescreen.run");

            var recount = await DemoProjectPipeline.QueryScalarAsync(host, context.ProjectId,
                "SELECT COUNT(*) FROM target_gl_entry g WHERE g.created_by IS NOT NULL " +
                "AND TRIM(g.created_by) <> '' " +
                "AND TRIM(g.created_by) NOT IN (SELECT name FROM target_authorized_preparer);");

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
    public async Task PrescreenRun_DemoFixture_LowFrequencyPreparerMatchesRecount()
    {
        // 低頻編製者無閘控、永遠跑：handler 計數須等於固定門檻 11 的獨立 recount。
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry g WHERE g.created_by IN " +
            "(SELECT created_by FROM target_gl_entry GROUP BY created_by HAVING COUNT(*) <= 11);");

        Assert.Equal(recount, data.GetProperty("lowFrequencyPreparer").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_LowFrequencyAccount_MatchesIndependentRecount()
    {
        // C9 無閘控、永遠跑:handler 計數須等於固定門檻 11 的獨立 recount。
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry g WHERE g.account_code IN " +
            "(SELECT account_code FROM target_gl_entry GROUP BY account_code HAVING COUNT(*) <= 11);");

        Assert.Equal(recount, data.GetProperty("lowFrequencyAccount").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_CustomPreparerEntryCount_MatchesRecount()
    {
        // 低頻編製者自訂門檻軌：filter.preview 的 customPreparerEntryCount(maxEntries=200)命中數
        // 須等於同述詞的獨立 recount(handler 與獨立 SQL 同源,不另設命中量級假設)。
        const int maxEntries = 200;
        var payload = JsonSerializer.Serialize(new
        {
            scenario = new
            {
                name = "低頻編製者(自訂)",
                rationale = "自訂門檻軌驗收",
                groups = new[]
                {
                    new { join = "AND", rules = new[]
                    {
                        new { join = "AND", type = "customPreparerEntryCount", maxEntries }
                    } }
                }
            }
        });

        var data = await fixture.Host.DispatchAsync("filter.preview", payload);

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry g WHERE g.created_by IN " +
            $"(SELECT created_by FROM target_gl_entry GROUP BY created_by HAVING COUNT(*) <= {maxEntries});");

        Assert.Equal(recount, data.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_CustomAccountEntryCount_MatchesRecount()
    {
        // C9 自訂門檻軌:filter.preview 的 customAccountEntryCount(maxEntries=11)命中數
        // 須等於同述詞的獨立 recount(述詞同 lowFrequencyAccount,門檻取代固定預設 11)。
        const int maxEntries = 11;
        var payload = JsonSerializer.Serialize(new
        {
            scenario = new
            {
                name = "低頻科目(自訂)",
                rationale = "自訂門檻軌驗收",
                groups = new[]
                {
                    new { join = "AND", rules = new[]
                    {
                        new { join = "AND", type = "customAccountEntryCount", maxEntries }
                    } }
                }
            }
        });

        var data = await fixture.Host.DispatchAsync("filter.preview", payload);

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry g WHERE g.account_code IN " +
            $"(SELECT account_code FROM target_gl_entry GROUP BY account_code HAVING COUNT(*) <= {maxEntries});");

        Assert.Equal(recount, data.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_PersistsLatestRunForResume()
    {
        await fixture.Host.DispatchAsync("prescreen.run");

        var loaded = await fixture.Host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = fixture.ProjectId }));

        var resumed = loaded.GetProperty("latestRuns").GetProperty("prescreen");
        Assert.Equal(DemoDataFactory.SuspiciousKeywordVouchers, resumed.GetProperty("suspiciousKeywords").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task PrescreenRun_TypedPlanIdentity_MatchesPersistedRunIdentity()
    {
        var folder = new JetProjectFolder(fixture.Host.ProjectsRoot);
        var database = new SqliteProjectDatabase(folder);
        var runStore = new LocalRuleRunStore(database);
        var session = new ProjectSession();
        session.Enter(fixture.ProjectId);
        var factsPort = new RecordingPrescreenFactsPort(
            new LocalPrescreenRunRepository(database));
        var handler = new PrescreenRunHandler(
            factsPort,
            new LocalMappingStateStore(database),
            new LocalCalendarStore(database),
            new LocalAccountMappingRepository(database),
            new LocalAuthorizedPreparerRepository(database),
            runStore,
            new JsonFileProjectStore(folder),
            session);

        await handler.HandleAsync(default, CancellationToken.None);

        var executedPlan = Assert.IsType<PrescreenPlan>(factsPort.ExecutedPlan);
        var saved = await runStore.FindLatestAsync(
            fixture.ProjectId,
            RuleRunKinds.Prescreen,
            CancellationToken.None);
        Assert.NotNull(saved);
        Assert.False(string.IsNullOrWhiteSpace(executedPlan.Request.RunId));
        Assert.NotEqual(default, executedPlan.Request.GeneratedUtc);
        Assert.Equal(
            new AuditUserParameters(
                saved!.RunId,
                saved.GeneratedUtc,
                SampleSize: 0,
                ActionName: "prescreen.run"),
            executedPlan.ReviewPlan.UserParameters);

        using var summary = JsonDocument.Parse(saved.SummaryJson);
        var resultRef = summary.RootElement.GetProperty("resultRef");
        Assert.Equal(saved.RunId, resultRef.GetProperty("runId").GetString());
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
