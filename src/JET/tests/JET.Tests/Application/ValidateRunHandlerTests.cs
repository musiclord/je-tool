using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// validate.run 黑箱測試（走 dispatcher，斷言 wire shape）。
/// 共用 DemoProjectFixture 的測試只斷言「自身呼叫的 response」或穩定母體事實；
/// 前置條件變體各自建 host。
/// </summary>
public sealed class ValidateRunHandlerTests(DemoProjectFixture fixture) : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task ValidateRun_WithoutProject_ThrowsNoActiveProject()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("validate.run"));

        Assert.Equal("no_active_project", ex.Code);
    }

    [Fact]
    public async Task ValidateRun_BeforeMappingCommit_ThrowsNoTargetData()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, commitGl: false, commitTb: false);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("validate.run"));

        Assert.Equal("no_target_data", ex.Code);
        // 2026-10-02 整體複審 T4：畫面不再說「提交」欄位配對，改用「完成」；斷言改鎖新句子。
        // 2026-10-04 第 8 批 Q8 再統一為「確認配對」；錯誤碼與完整訊息斷言保留。
        // 第一次失敗：20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d。
        Assert.Equal(
            "尚未確認 GL 欄位配對，請先到第三步按「確認配對」。",
            ex.Message);
    }

    [Fact]
    public async Task ValidateRun_DemoFixture_StatsAreBalanced()
    {
        var data = await fixture.Host.DispatchAsync("validate.run");

        var rawRowCount = (long)DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher;
        var excludedByPeriod = (long)DemoDataFactory.OutOfPeriodVouchers * DemoDataFactory.LinesPerVoucher;
        var effectiveRowCount = rawRowCount - excludedByPeriod;
        var stats = data.GetProperty("stats");
        Assert.Equal(effectiveRowCount, stats.GetProperty("glRowCount").GetInt64());
        Assert.Equal(0m, stats.GetProperty("net").GetDecimal());

        // Demo 的四筆排除列全是刻意設計的兩張期外傳票；沒有空白過帳日或
        // posting-status 排除。raw = effective + excluded 必須由 wire 摘要直接鎖住。
        var population = data.GetProperty("populationSummary");
        var raw = population.GetProperty("raw");
        var effective = population.GetProperty("effective");
        var excluded = population.GetProperty("excluded");
        Assert.Equal(rawRowCount, raw.GetProperty("rowCount").GetInt64());
        Assert.Equal(effectiveRowCount, effective.GetProperty("rowCount").GetInt64());
        Assert.Equal(excludedByPeriod, excluded.GetProperty("rowCount").GetInt64());
        Assert.Equal(excludedByPeriod, excluded.GetProperty("byPeriodCount").GetInt64());
        Assert.Equal(0, excluded.GetProperty("byPostingStatusCount").GetInt64());
        Assert.Equal(
            raw.GetProperty("rowCount").GetInt64(),
            effective.GetProperty("rowCount").GetInt64() + excluded.GetProperty("rowCount").GetInt64());
        Assert.Equal(0, data.GetProperty("sourceQuality").GetProperty("findingCount").GetInt64());
    }

    [Fact]
    public async Task ValidateRun_DemoFixture_VoucherCountMatchesDistinctDocuments()
    {
        var data = await fixture.Host.DispatchAsync("validate.run");

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(DISTINCT document_number) FROM target_gl_entry WHERE is_effective = 1;");

        Assert.Equal(recount, data.GetProperty("stats").GetProperty("voucherCount").GetInt64());
    }

    [Fact]
    public async Task ValidateRun_DemoFixture_CompletenessHasNoDifferences()
    {
        var data = await fixture.Host.DispatchAsync("validate.run");

        Assert.Equal(0, data.GetProperty("completenessTest").GetProperty("diffAccountCount").GetInt64());
    }

    [Fact]
    public async Task ValidateRun_WithoutTbMapping_CompletenessIsNa()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, commitTb: false);

        var data = await host.DispatchAsync("validate.run");

        Assert.Equal("na", data.GetProperty("completenessTest").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ValidateRun_DemoFixture_DocBalanceHasNoUnbalancedDocuments()
    {
        var data = await fixture.Host.DispatchAsync("validate.run");

        Assert.Equal(0, data.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());
    }

    [Fact]
    public async Task ValidateRun_UnbalancedWorkbook_DocBalanceCountsDocument()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "正常分錄", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "正常分錄", "99.99", 0)
            .AddRow("JV-002", "2025-03-06", "1101", "現金", "平衡分錄", "50.00", 1)
            .AddRow("JV-002", "2025-03-06", "4101", "銷貨收入", "平衡分錄", "50.00", 0));

        var data = await host.DispatchAsync("validate.run");

        Assert.Equal(1, data.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());
    }

    /// <summary>
    /// part(a) 控制總數核對：投影為無損（每列皆成功插入）時,落地的控制總數
    /// （來源列數、母體借/貸總額）對上 target 現值,rowCountMatch / amountMatch 皆為 true。
    /// oracle：手算小母體——2 列皆成功投影,故來源列數 == 母體列數;金額亦逐筆落地。
    /// </summary>
    [Fact]
    public async Task ValidateRun_PartA_MatchesWhenProjectionLossless()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "借方", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方", "100.00", 0));

        var data = await host.DispatchAsync("validate.run");

        var partA = data.GetProperty("completenessTest").GetProperty("partA");
        Assert.True(partA.GetProperty("rowCountMatch").GetBoolean());
        Assert.True(partA.GetProperty("amountMatch").GetBoolean());
        // 兩列皆無損投影：來源列數 == 母體列數 == 2。
        Assert.Equal(2, partA.GetProperty("eligibleSource").GetProperty("rowCount").GetInt64());
        Assert.Equal(2, partA.GetProperty("effectiveTarget").GetProperty("rowCount").GetInt64());
    }

    /// <summary>
    /// Not-in-TB 具名化：GL 含科目 9999、TB 不含 → 該差異列 notInTb == true;
    /// TB 與 GL 皆有但金額不符的科目 notInTb == false。
    /// oracle：手算——GL 的 9999 在 TB CTE 的 LEFT JOIN 無對應（UNION 第二支標 1）;
    /// 1101 兩側皆有但 TB 變動 50 ≠ GL 100,屬金額差異（標 0）。
    /// </summary>
    [Fact]
    public async Task ValidateRun_NotInTb_FlagsGlAccountAbsentFromTb()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                // 1101：GL 借方淨額 +100（TB 也有,但金額不符 → 差異、非 not-in-tb）
                .AddRow("JV-001", "2025-03-05", "1101", "現金", "借方", "100.00", 1)
                // 9999：GL 借方淨額 +30,TB 不含此科目 → not-in-tb
                .AddRow("JV-001", "2025-03-05", "9999", "暫付款", "借方", "30.00", 1)
                .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方", "130.00", 0),
            configureTb: tb => tb
                // TB 只列 1101（金額 50 ≠ GL 100）與 4101,不含 9999。
                .AddRow("1101", "現金", "50.00")
                .AddRow("4101", "銷貨收入", "-130.00"));

        var data = await host.DispatchAsync("validate.run");

        var diffs = data.GetProperty("completenessTest").GetProperty("diffAccounts");

        var notInTb = diffs.EnumerateArray()
            .First(e => e.GetProperty("accountCode").GetString() == "9999");
        Assert.True(notInTb.GetProperty("notInTb").GetBoolean());

        var amountDiff = diffs.EnumerateArray()
            .First(e => e.GetProperty("accountCode").GetString() == "1101");
        Assert.False(amountDiff.GetProperty("notInTb").GetBoolean());
    }

    /// <summary>
    /// part(a) 在「已投影、TB 未配對」時仍出現且核對通過——part(a) 是 GL 控制總數對 GL 母體現值,
    /// 與 TB 是否存在無關（completeness 整段雖 na,partA 子物件仍在 na 形狀內回報）。
    /// oracle：demo 母體無損投影,故 rowCountMatch / amountMatch 皆 true。
    /// </summary>
    [Fact]
    public async Task ValidateRun_PartA_PresentEvenWhenCompletenessNa()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, commitTb: false);

        var data = await host.DispatchAsync("validate.run");
        var completeness = data.GetProperty("completenessTest");

        Assert.Equal("na", completeness.GetProperty("status").GetString());
        var partA = completeness.GetProperty("partA");
        Assert.True(partA.GetProperty("rowCountMatch").GetBoolean());
        Assert.True(partA.GetProperty("amountMatch").GetBoolean());
    }

    /// <summary>
    /// 失效範圍收斂（2026-06-22 實務稽核）：gl_control_total（part(a) 控制總數）的上游只有 GL target，
    /// 由 GL 投影（mapping.commit.gl）隨 target 一起 upsert，與 target_gl_entry 恆一致。與 GL 無關的
    /// 資料變動（行事曆／授權清單／TB 投影／科目配對匯入）雖同交易走 RuleRunResultReset 清規則結果，
    /// 但**不得**連帶清掉 gl_control_total——否則完整性 part(a) 會在常見的「先 commit GL、後做其他匯入」
    /// 順序下變全 null（控制總數核對形同沒跑），這是稽核發現的失效範圍過廣（已收斂）。
    /// 此處用 import.holiday（與 GL 無關）走清除路徑，驗證 gl_control_total 存活。
    /// oracle：規格（gl_control_total 上游只有 GL target；收斂後 RuleRunResultReset 不再清它）。
    /// </summary>
    [Fact]
    public async Task GlControlTotal_SurvivesGlUnrelatedDataChange()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-001", "2025-03-05", "1101", "現金", "借方", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方", "100.00", 0));

        // GL 投影已 upsert 控制總數 → 恰 1 列(GL-only,setup 末步即 mapping.commit.gl)。
        var before = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM gl_control_total;");
        Assert.Equal(1, before);

        // 與 GL 無關的匯入(import.holiday → LocalCalendarStore 內呼叫 ClearWithinAsync 清規則結果)。
        await host.DispatchAsync("import.holiday", JsonSerializer.Serialize(new
        {
            dates = new[] { "2025-03-10" }
        }));

        // 收斂後不變量:gl_control_total 不受行事曆匯入影響,仍存活(part(a) 控制總數仍可核對)。
        var after = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM gl_control_total;");
        Assert.Equal(1, after);
    }

    [Fact]
    public async Task ValidateRun_DemoFixture_InfSamplingPersistsFiftyNineSampleRows()
    {
        var data = await fixture.Host.DispatchAsync("validate.run");
        var runId = data.GetProperty("resultRef").GetProperty("runId").GetString()!;

        var persisted = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM result_inf_sampling_test_sample WHERE run_id = @runId;",
            ("@runId", runId));

        Assert.Equal(59, persisted);
    }

    /// <summary>
    /// per-project 種子：validate.run 回應的 seed == 該專案 project.json 落地的 sampleSeed
    /// （非全域常數）；且同專案兩次 run 的 seed 相等（種子終身固定 → 可重現）。
    /// oracle：規格（種子來源＝專案種子）＋ project.json 物證。
    /// </summary>
    [Fact]
    public async Task ValidateRun_SeedMatchesPersistedProjectSeed_AndIsStableAcrossRuns()
    {
        var first = await fixture.Host.DispatchAsync("validate.run");
        var second = await fixture.Host.DispatchAsync("validate.run");

        var persistedSeed = ReadSampleSeed(fixture.Host.ProjectsRoot, fixture.ProjectId);

        Assert.Equal(persistedSeed, first.GetProperty("infSamplingTest").GetProperty("seed").GetInt64());
        Assert.Equal(persistedSeed, second.GetProperty("infSamplingTest").GetProperty("seed").GetInt64());
    }

    /// <summary>
    /// sampleSeed 欄位問世前建立的舊專案（project.json 缺該欄位）→ validate.run 明確拒絕，
    /// 不再回退固定種子。fixture：建正常 inline 專案後移除 project.json 的 sampleSeed 欄位。
    /// </summary>
    [Fact]
    public async Task ValidateRun_LegacyProjectWithoutSampleSeed_RejectsAsOldProject()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "借方", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方", "100.00", 0));

        // 模擬舊專案：把 project.json 的 sampleSeed 與版本欄位一併移除（欄位問世前的形狀）。
        var path = Path.Combine(host.ProjectsRoot, projectId, "project.json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        node.Remove("sampleSeed");
        node.Remove("sampleSeedVersion");
        await File.WriteAllTextAsync(path, node.ToJsonString());

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync("validate.run"));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, exception.Code);
        Assert.Equal(
            $"專案『{projectId}』的 project.json 是舊版 JET 建立的案件（缺少 sampleSeedVersion），"
            + "目前版本無法讀取。請用目前版本重新建立案件，再重新匯入資料。",
            exception.Message);
    }

    [Theory]
    [InlineData(0L, 2)]
    [InlineData(10_000_000_000L, 2)]
    [InlineData(123_456L, 99)]
    public async Task ValidateRun_CorruptSeedValueOrVersionFailsWithoutRegeneration(
        long seed,
        int version)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "借方", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方", "100.00", 0));
        var path = Path.Combine(host.ProjectsRoot, projectId, "project.json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        node["sampleSeed"] = seed;
        node["sampleSeedVersion"] = version;
        await File.WriteAllTextAsync(path, node.ToJsonString());

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync("validate.run"));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.Contains("INF 抽樣種子", exception.Message, StringComparison.Ordinal);
        Assert.Contains("不會重新產生", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateRun_VersionMarkerWithoutSeedFailsWithoutRegeneration()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "借方", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方", "100.00", 0));
        var path = Path.Combine(host.ProjectsRoot, projectId, "project.json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        node.Remove("sampleSeed");
        node["sampleSeedVersion"] = JetAuditProgram.CurrentInfSamplingAlgorithmVersion;
        await File.WriteAllTextAsync(path, node.ToJsonString());

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync("validate.run"));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.Contains("INF 抽樣種子", exception.Message, StringComparison.Ordinal);
        Assert.Contains("不會重新產生", exception.Message, StringComparison.Ordinal);
    }

    private static long ReadSampleSeed(string projectsRoot, string projectId)
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(projectsRoot, projectId, "project.json")));
        return doc.RootElement.GetProperty("sampleSeed").GetInt64();
    }

    [Fact]
    public async Task ValidateRun_RunTwice_SamplesIdenticalKeys()
    {
        var first = await fixture.Host.DispatchAsync("validate.run");
        var second = await fixture.Host.DispatchAsync("validate.run");

        var firstRunId = first.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var secondRunId = second.GetProperty("resultRef").GetProperty("runId").GetString()!;
        Assert.NotEqual(firstRunId, secondRunId);

        // 兩次 run 的抽中 (document_number, line_item) 集合必須完全一致（可重現性）。
        var differing = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            """
            SELECT COUNT(*) FROM (
                SELECT document_number, line_item FROM result_inf_sampling_test_sample WHERE run_id = @first
                EXCEPT
                SELECT document_number, line_item FROM result_inf_sampling_test_sample WHERE run_id = @second
            );
            """,
            ("@first", firstRunId), ("@second", secondRunId));

        Assert.Equal(0, differing);
    }

    [Fact]
    public async Task ValidateRun_DemoFixture_NullRecordsBlankDescriptionsAndOutOfPeriodApproval()
    {
        // demo 母體刻意埋兩種「空值/期外」:(1)「摘要空白」種子(借方行空白)→ nullDescription 命中數
        // 恰等於該種子張數;(2)「期末後核准」種子(20 張、核准日 2026-01-15 在期末 2025-12-31 之後),
        // 每張 2 列 → 核准日離期 40 列。科目/傳票號皆完整 → 其餘兩類為 0。
        // 「日期區間外」第四旗標以**核准日**判定(2026-06-23 決策,對齊舊 JET 工具)。
        var data = await fixture.Host.DispatchAsync("validate.run");

        var nullRecords = data.GetProperty("nullRecordsTest");
        Assert.Equal(DemoDataFactory.BlankDescriptionVouchers, nullRecords.GetProperty("nullDescriptionCount").GetInt64());
        Assert.Equal(0, nullRecords.GetProperty("nullAccountCount").GetInt64()
            + nullRecords.GetProperty("nullDocumentCount").GetInt64());
        // 核准日離期 = 期末後核准種子張數 × 每張 2 列。
        Assert.Equal(
            2L * DemoDataFactory.PostPeriodApprovalVouchers,
            nullRecords.GetProperty("outOfRangeDateCount").GetInt64());
    }

    [Fact]
    public async Task ValidateRun_PersistsLatestRunForResume()
    {
        await fixture.Host.DispatchAsync("validate.run");

        var loaded = await fixture.Host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = fixture.ProjectId }));

        var resumed = loaded.GetProperty("latestRuns").GetProperty("validate");
        var effectiveRowCount =
            ((long)DemoDataFactory.GlVoucherCount - DemoDataFactory.OutOfPeriodVouchers)
            * DemoDataFactory.LinesPerVoucher;
        Assert.Equal(effectiveRowCount, resumed.GetProperty("stats").GetProperty("glRowCount").GetInt64());
    }

    /// <summary>
    /// 回應的 docBalanceTest.unbalancedDocuments 與 nullRecordsTest.nullRows 要出現在 wire JSON，
    /// 且內容與母體一致；既有純量欄位（counts）數值不能改變。
    /// </summary>
    [Fact]
    public async Task ValidateRun_WithUnbalancedAndNullRows_ResponseContainsDetailLists()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            // 欄：傳票號碼、傳票日期、科目代號、科目名稱、摘要、金額、借方旗標
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            // JV-001：借 300、貸 100 → 不平，差額 200（正值）
            .AddRow("JV-001", "2025-03-05", "1101", "現金",     "借方",   "300.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方",   "100.00", 0)
            // JV-002：借 50、貸 50 → 平衡（不應出現在 unbalancedDocuments）
            .AddRow("JV-002", "2025-03-06", "1101", "現金",     "借方",   "50.00",  1)
            .AddRow("JV-002", "2025-03-06", "4101", "銷貨收入", "貸方",   "50.00",  0)
            // JV-003：空科目 → 應出現在 nullRows，NullAccount=true
            .AddRow("JV-003", "2025-03-07", null,   null,       "空科目分錄", "10.00", 1)
            .AddRow("JV-003", "2025-03-07", "4101", "銷貨收入", "對方",   "10.00",  0));

        var data = await host.DispatchAsync("validate.run");

        // --- docBalanceTest ---
        var docBalance = data.GetProperty("docBalanceTest");
        // 純量欄位不變
        Assert.Equal(1, docBalance.GetProperty("unbalancedDocumentCount").GetInt64());
        // 新明細清單：JV-001 出現在第 0 筆
        var unbalancedDocs = docBalance.GetProperty("unbalancedDocuments");
        Assert.True(unbalancedDocs.GetArrayLength() >= 1, "unbalancedDocuments 應至少有 1 筆");
        var firstDoc = unbalancedDocs[0];
        Assert.Equal("JV-001", firstDoc.GetProperty("documentNumber").GetString());
        // debit=300.00, credit=100.00 → scaled: 3_000_000 / 100_000 (DefaultMoneyScale=10_000)
        // ToDisplay: 3_000_000/10_000=300.00, 1_000_000/10_000=100.00, diff=2_000_000/10_000=200.00
        Assert.Equal(300.00m, firstDoc.GetProperty("debit").GetDecimal());
        Assert.Equal(100.00m, firstDoc.GetProperty("credit").GetDecimal());
        Assert.Equal(200.00m, firstDoc.GetProperty("diff").GetDecimal());

        // --- nullRecordsTest ---
        var nullRecords = data.GetProperty("nullRecordsTest");
        // 純量欄位：空科目 ≥ 1（JV-003 那列）
        Assert.True(nullRecords.GetProperty("nullAccountCount").GetInt64() >= 1);
        // 新明細清單：至少 1 筆
        var nullRows = nullRecords.GetProperty("nullRows");
        Assert.True(nullRows.GetArrayLength() >= 1, "nullRows 應至少有 1 筆");
        var firstRow = nullRows[0];
        // nullRows 依 source_row_number, entry_id 排序；seed 中 JV-003 的 null-account 列
        // 是工作簿第 5 列（header 後第 4 筆）,排在 JV-003 第 6 列之前,故第一筆為 JV-003。
        Assert.Equal("JV-003", firstRow.GetProperty("documentNumber").GetString());
        Assert.True(firstRow.TryGetProperty("accountCode", out _),    "應有 accountCode");
        Assert.True(firstRow.TryGetProperty("postDate", out _),       "應有 postDate");
        Assert.True(firstRow.TryGetProperty("description", out _),    "應有 description");
        // issues 是陣列，且包含 "account"（因為科目是空的）
        var issues = firstRow.GetProperty("issues");
        Assert.Equal(JsonValueKind.Array, issues.ValueKind);
        var issueValues = Enumerable.Range(0, issues.GetArrayLength())
            .Select(i => issues[i].GetString()!)
            .ToArray();
        Assert.Contains("account", issueValues);
    }
}
