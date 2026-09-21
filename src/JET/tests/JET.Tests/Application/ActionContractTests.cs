using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Architecture;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 回應契約鎖（harness TDD 強化,做法一:手寫語意斷言)。
/// 目的:把 manifest 描述的回應形狀變成可執行的測試——欄位被改名／拿掉／改型別／悄悄新增時,
/// 這裡會紅,逼人確認「故意還是改壞」(Linus「不破壞 userspace」用在 JS↔C# 介面)。
///
/// 鎖的是「欄位集合＋型別＋巢狀結構」,不鎖屬性順序、不鎖具體數值(jet-testing §3)。
/// 巢狀的 run 形狀(latestRuns 內)由各自 action 的契約鎖負責,project.load 只鎖外層信封。
/// 陣列元素形狀「有資料才檢查」;新欄位(unbalancedDocuments／nullRows)另以固定 seed 保證有資料、確實鎖到。
/// </summary>
public sealed class ActionContractTests(DemoProjectFixture fixture) : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task ValidateRun_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync("validate.run");

        JsonShape.HasExactKeys(data,
            "stats", "amountDistribution", "completenessTest", "docBalanceTest",
            "infSamplingTest", "nullRecordsTest", "populationSummary", "sourceQuality", "resultRef");

        var stats = JsonShape.Obj(data, "stats");
        JsonShape.HasExactKeys(stats,
            "glRowCount", "voucherCount", "totalDebit", "totalCredit", "net", "periodStart", "periodEnd");
        JsonShape.Number(stats, "glRowCount");
        JsonShape.Number(stats, "voucherCount");
        JsonShape.Number(stats, "totalDebit");
        JsonShape.Number(stats, "totalCredit");
        JsonShape.Number(stats, "net");
        JsonShape.Str(stats, "periodStart");
        JsonShape.Str(stats, "periodEnd");

        var populationSummary = JsonShape.Obj(data, "populationSummary");
        JsonShape.HasExactKeys(populationSummary, "raw", "effective", "excluded");
        var rawPopulation = JsonShape.Obj(populationSummary, "raw");
        JsonShape.HasExactKeys(rawPopulation, "rowCount", "totalDebit", "totalCredit");
        JsonShape.Number(rawPopulation, "rowCount");
        JsonShape.Number(rawPopulation, "totalDebit");
        JsonShape.Number(rawPopulation, "totalCredit");
        var effectivePopulation = JsonShape.Obj(populationSummary, "effective");
        JsonShape.HasExactKeys(
            effectivePopulation,
            "rowCount", "voucherCount", "totalDebit", "totalCredit", "net");
        JsonShape.Number(effectivePopulation, "rowCount");
        JsonShape.Number(effectivePopulation, "voucherCount");
        JsonShape.Number(effectivePopulation, "totalDebit");
        JsonShape.Number(effectivePopulation, "totalCredit");
        JsonShape.Number(effectivePopulation, "net");
        var excludedPopulation = JsonShape.Obj(populationSummary, "excluded");
        JsonShape.HasExactKeys(
            excludedPopulation,
            "rowCount", "byPeriodCount", "byPostingStatusCount");
        JsonShape.Number(excludedPopulation, "rowCount");
        JsonShape.Number(excludedPopulation, "byPeriodCount");
        JsonShape.Number(excludedPopulation, "byPostingStatusCount");

        var amountDistribution = JsonShape.Obj(data, "amountDistribution");
        JsonShape.HasExactKeys(amountDistribution, AmountDistributionWireKeys.Block);
        var amountBins = JsonShape.Arr(amountDistribution, "bins");
        Assert.Equal(AmountDistributionWireKeys.BinKeys.Length, amountBins.GetArrayLength());
        Assert.Equal(
            AmountDistributionWireKeys.BinKeys,
            amountBins.EnumerateArray()
                .Select(bin => bin.GetProperty("key").GetString())
                .ToArray());
        JsonShape.Element(amountBins, bin =>
        {
            JsonShape.HasExactKeys(bin, AmountDistributionWireKeys.BinRow);
            JsonShape.Str(bin, "key");
            JsonShape.Number(bin, "count");
            JsonShape.Number(bin, "ecdfPct", nullable: true);
        });

        var completeness = JsonShape.Obj(data, "completenessTest");
        JsonShape.HasExactKeys(
            completeness,
            "status", "naReason", "diffAccountCount", "diffAccounts", "partA", "eligibility");
        JsonShape.Str(completeness, "status");
        JsonShape.Str(completeness, "naReason", nullable: true);
        JsonShape.Number(completeness, "diffAccountCount");
        JsonShape.Element(JsonShape.Arr(completeness, "diffAccounts"), e =>
        {
            JsonShape.HasExactKeys(e, "accountCode", "accountName", "tbAmount", "glAmount", "diff", "notInTb");
            JsonShape.Str(e, "accountCode");
            JsonShape.Str(e, "accountName", nullable: true);
            JsonShape.Number(e, "tbAmount");
            JsonShape.Number(e, "glAmount");
            JsonShape.Number(e, "diff");
            Assert.Contains(e.GetProperty("notInTb").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        });

        // part(a) controls 與有效目標母體都維持固定子物件；無 controls 時全部欄位（含 match）為 null。
        var partA = JsonShape.Obj(completeness, "partA");
        JsonShape.HasExactKeys(
            partA,
            "eligibleSource", "effectiveTarget", "rowCountMatch", "amountMatch");
        foreach (var population in new[]
                 {
                     JsonShape.Obj(partA, "eligibleSource"),
                     JsonShape.Obj(partA, "effectiveTarget")
                 })
        {
            JsonShape.HasExactKeys(population, "rowCount", "totalDebit", "totalCredit");
            JsonShape.Number(population, "rowCount", nullable: true);
            JsonShape.Number(population, "totalDebit", nullable: true);
            JsonShape.Number(population, "totalCredit", nullable: true);
        }
        Assert.Contains(
            partA.GetProperty("rowCountMatch").ValueKind,
            new[] { JsonValueKind.True, JsonValueKind.False, JsonValueKind.Null });
        Assert.Contains(
            partA.GetProperty("amountMatch").ValueKind,
            new[] { JsonValueKind.True, JsonValueKind.False, JsonValueKind.Null });

        var eligibility = JsonShape.Obj(completeness, "eligibility");
        JsonShape.HasExactKeys(eligibility, "isEligible", "reason", "warning");
        Assert.Contains(
            eligibility.GetProperty("isEligible").ValueKind,
            new[] { JsonValueKind.True, JsonValueKind.False });
        JsonShape.Str(eligibility, "reason", nullable: true);
        JsonShape.Str(eligibility, "warning", nullable: true);

        var docBalance = JsonShape.Obj(data, "docBalanceTest");
        JsonShape.HasExactKeys(docBalance, "status", "unbalancedDocumentCount", "unbalancedDocuments");
        JsonShape.Str(docBalance, "status");
        JsonShape.Number(docBalance, "unbalancedDocumentCount");
        JsonShape.Arr(docBalance, "unbalancedDocuments"); // 元素形狀見 ValidateRun_DetailArrays

        var inf = JsonShape.Obj(data, "infSamplingTest");
        JsonShape.HasExactKeys(inf, "status", "sampleSize", "seed");
        JsonShape.Str(inf, "status");
        JsonShape.Number(inf, "sampleSize");
        JsonShape.Number(inf, "seed");

        var nullRecords = JsonShape.Obj(data, "nullRecordsTest");
        JsonShape.HasExactKeys(nullRecords,
            "status", "nullAccountCount", "nullDocumentCount", "nullDescriptionCount", "outOfRangeDateCount",
            "nullRows");
        JsonShape.Str(nullRecords, "status");
        JsonShape.Number(nullRecords, "nullAccountCount");
        JsonShape.Number(nullRecords, "nullDocumentCount");
        JsonShape.Number(nullRecords, "nullDescriptionCount");
        JsonShape.Number(nullRecords, "outOfRangeDateCount");
        JsonShape.Arr(nullRecords, "nullRows"); // 元素形狀見 ValidateRun_DetailArrays

        var sourceQuality = JsonShape.Obj(data, "sourceQuality");
        JsonShape.HasExactKeys(sourceQuality, "findingCount", "sampleRows");
        JsonShape.Number(sourceQuality, "findingCount");
        JsonShape.Arr(sourceQuality, "sampleRows");

        var resultRef = JsonShape.Obj(data, "resultRef");
        JsonShape.HasExactKeys(resultRef, "runId", "generatedUtc", "logicVersion");
        JsonShape.Str(resultRef, "runId");
        JsonShape.Str(resultRef, "generatedUtc");
        JsonShape.Str(resultRef, "logicVersion");
    }

    /// <summary>新明細欄位以固定 seed 保證非空,確實鎖住元素形狀(借貸不平 + 空值)。</summary>
    [Fact]
    public async Task ValidateRun_DetailArrays_ElementShapeIsLocked()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "借方", "300.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方", "100.00", 0)
            .AddRow("JV-003", "2025-03-07", null, null, "空科目", "10.00", 1)
            .AddRow("JV-003", "2025-03-07", "4101", "銷貨收入", "對方", "10.00", 0));

        var data = await host.DispatchAsync("validate.run");

        var unbalanced = JsonShape.Arr(data.GetProperty("docBalanceTest"), "unbalancedDocuments");
        Assert.True(unbalanced.GetArrayLength() >= 1, "seed 應產生不平傳票");
        JsonShape.Element(unbalanced, e =>
        {
            JsonShape.HasExactKeys(e, "documentNumber", "debit", "credit", "diff");
            JsonShape.Str(e, "documentNumber", nullable: true);
            JsonShape.Number(e, "debit");
            JsonShape.Number(e, "credit");
            JsonShape.Number(e, "diff");
        });

        var nullRows = JsonShape.Arr(data.GetProperty("nullRecordsTest"), "nullRows");
        Assert.True(nullRows.GetArrayLength() >= 1, "seed 應產生空值列");
        JsonShape.Element(nullRows, e =>
        {
            JsonShape.HasExactKeys(e, "documentNumber", "accountCode", "postDate", "description", "issues");
            JsonShape.Str(e, "documentNumber", nullable: true);
            JsonShape.Str(e, "accountCode", nullable: true);
            JsonShape.Str(e, "postDate", nullable: true);
            JsonShape.Str(e, "description", nullable: true);
            var issues = JsonShape.Arr(e, "issues");
            JsonShape.Element(issues, i => Assert.Equal(JsonValueKind.String, i.ValueKind));
        });
    }

    [Fact]
    public async Task PrescreenRun_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        JsonShape.HasExactKeys(data,
            "postPeriodApproval", "suspiciousKeywords", "unexpectedAccountPair", "trailingZeros",
            "creatorSummary", "rareAccounts", "weekendActivity", "holidayActivity", "blankDescription",
            "backdatedPosting", "nonAuthorizedPreparer", "lowFrequencyPreparer", "lowFrequencyAccount",
            "rulePeriod", "concentration", "positioning", "resultRef");

        foreach (var key in new[] { "postPeriodApproval", "unexpectedAccountPair", "nonAuthorizedPreparer", "backdatedPosting", "lowFrequencyPreparer" })
        {
            var rule = JsonShape.Obj(data, key);
            JsonShape.HasExactKeys(rule, "status", "naReason", "count");
            JsonShape.Str(rule, "status");
            JsonShape.Str(rule, "naReason", nullable: true);
            JsonShape.Number(rule, "count");
        }

        foreach (var key in new[] { "suspiciousKeywords", "blankDescription", "lowFrequencyAccount" })
        {
            var rule = JsonShape.Obj(data, key);
            JsonShape.HasExactKeys(rule, "status", "count");
            JsonShape.Str(rule, "status");
            JsonShape.Number(rule, "count");
        }

        var trailing = JsonShape.Obj(data, "trailingZeros");
        JsonShape.HasExactKeys(trailing, "status", "count", "zerosThreshold");
        JsonShape.Str(trailing, "status");
        JsonShape.Number(trailing, "count");
        JsonShape.Number(trailing, "zerosThreshold");

        var creatorSummary = JsonShape.Obj(data, "creatorSummary");
        JsonShape.HasExactKeys(creatorSummary, "status", "naReason", "creators");
        JsonShape.Str(creatorSummary, "status");
        JsonShape.Str(creatorSummary, "naReason", nullable: true);
        JsonShape.Element(JsonShape.Arr(creatorSummary, "creators"), e =>
        {
            JsonShape.HasExactKeys(e, "createdBy", "entryCount", "debitTotal", "creditTotal", "manualCount");
            JsonShape.Str(e, "createdBy", nullable: true);
            JsonShape.Number(e, "entryCount");
            JsonShape.Number(e, "debitTotal");
            JsonShape.Number(e, "creditTotal");
            JsonShape.Number(e, "manualCount");
        });

        var rareAccounts = JsonShape.Obj(data, "rareAccounts");
        JsonShape.HasExactKeys(rareAccounts, "status", "distinctAccountCount", "accounts");
        JsonShape.Str(rareAccounts, "status");
        JsonShape.Number(rareAccounts, "distinctAccountCount");
        JsonShape.Element(JsonShape.Arr(rareAccounts, "accounts"), e =>
        {
            JsonShape.HasExactKeys(e, "accountCode", "accountName", "entryCount", "debitTotal", "creditTotal");
            JsonShape.Str(e, "accountCode", nullable: true);
            JsonShape.Str(e, "accountName", nullable: true);
            JsonShape.Number(e, "entryCount");
            JsonShape.Number(e, "debitTotal");
            JsonShape.Number(e, "creditTotal");
        });

        foreach (var key in new[] { "weekendActivity", "holidayActivity" })
        {
            var rule = JsonShape.Obj(data, key);
            JsonShape.HasExactKeys(rule, "status", "naReason", "postingCount", "approvalCount");
            JsonShape.Str(rule, "status");
            JsonShape.Str(rule, "naReason", nullable: true);
            JsonShape.Number(rule, "postingCount");
            JsonShape.Number(rule, "approvalCount", nullable: true);
        }

        // S2b 降級形狀：13 條規則的「規則 × 全查核期間」分布。
        // N/A 由 naReason 與 nullable 數值表達，不另增會與既有 status='na' 混淆的欄位。
        var rulePeriod = JsonShape.Obj(data, "rulePeriod");
        JsonShape.HasExactKeys(rulePeriod, RulePeriodWireKeys.Block);
        JsonShape.Number(rulePeriod, "population");
        JsonShape.Element(JsonShape.Arr(rulePeriod, "rules"), rule =>
        {
            JsonShape.HasExactKeys(rule, RulePeriodWireKeys.RuleRow);
            JsonShape.Str(rule, "key");
            JsonShape.Str(rule, "naReason", nullable: true);
            JsonShape.Number(rule, "hitLines", nullable: true);
            JsonShape.Number(rule, "hitVouchers", nullable: true);
            JsonShape.Number(rule, "ratePct", nullable: true);
        });

        // 集中度分析（總覽區塊⑤）：不適用時 preparers／rareAccounts／distinctAccountCount 為 null，
        // 因此三者在型別上都可為空；demo 案件有配對編製人員，故此處實測非空分支。
        var concentration = JsonShape.Obj(data, "concentration");
        JsonShape.HasExactKeys(
            concentration, "status", "naReason", "preparers", "rareAccounts", "distinctAccountCount");
        JsonShape.Str(concentration, "status");
        JsonShape.Str(concentration, "naReason", nullable: true);
        JsonShape.Number(concentration, "distinctAccountCount", nullable: true);

        var preparers = JsonShape.Obj(concentration, "preparers", nullable: true);
        JsonShape.HasExactKeys(
            preparers, "top", "othersEntryCount", "totalPreparerCount", "totalEntryCount", "top5SharePct");
        JsonShape.Number(preparers, "othersEntryCount");
        JsonShape.Number(preparers, "totalPreparerCount");
        JsonShape.Number(preparers, "totalEntryCount");
        JsonShape.Number(preparers, "top5SharePct", nullable: true);
        JsonShape.Element(JsonShape.Arr(preparers, "top"), e =>
        {
            JsonShape.HasExactKeys(e, "createdBy", "entryCount", "manualCount", "cumulativePct");
            JsonShape.Str(e, "createdBy", nullable: true);
            JsonShape.Number(e, "entryCount");
            JsonShape.Number(e, "manualCount");
            JsonShape.Number(e, "cumulativePct", nullable: true);
        });
        JsonShape.Element(JsonShape.Arr(concentration, "rareAccounts"), e =>
        {
            JsonShape.HasExactKeys(e, "accountCode", "accountName", "entryCount");
            JsonShape.Str(e, "accountCode", nullable: true);
            JsonShape.Str(e, "accountName", nullable: true);
            JsonShape.Number(e, "entryCount");
        });

        var positioning = JsonShape.Obj(data, "positioning");
        JsonShape.HasExactKeys(
            positioning,
            "aggregateGuidance",
            "signalGuidance",
            "reportGuidance",
            "overviewGuidance",
            "exportDefaultGuidance",
            "exportPendingRunGuidance");
        Assert.Equal(
            "先看依分錄編製者與較少使用科目的全期彙總；這兩項是常用的母體判讀面。",
            positioning.GetProperty("aggregateGuidance").GetString());
        Assert.Equal(
            "逐筆命中只供初步判讀，不是高風險裁定；要形成測試範圍，請到「進階條件篩選」組合 KCT 與其他條件。",
            positioning.GetProperty("signalGuidance").GetString());
        Assert.Equal(
            "Pre-screening Report 預設隨匯出底稿一併產出，這裡可以先單獨產生；不產生也不影響進階條件篩選、Criteria Selection Report 或 Working Paper。",
            positioning.GetProperty("reportGuidance").GetString());
        Assert.Equal(
            "彙總只描述母體分布；逐筆命中不等於錯誤，也不是高風險裁定；兩者都不代替審計判斷。",
            positioning.GetProperty("overviewGuidance").GetString());
        Assert.Equal(
            "匯出底稿時預設一併產出 Pre-screening Report；取消勾選只會少這一份，其餘報告與底稿內容都不受影響。",
            positioning.GetProperty("exportDefaultGuidance").GetString());
        Assert.Equal(
            "目前沒有可用的預篩選結果。維持勾選並按下產生，系統會先執行一次預篩選再產出這份報告；大型案件的預篩選可能需要數分鐘到十餘分鐘。",
            positioning.GetProperty("exportPendingRunGuidance").GetString());

        var resultRef = JsonShape.Obj(data, "resultRef");
        JsonShape.HasExactKeys(resultRef, "runId", "generatedUtc", "logicVersion");
        JsonShape.Str(resultRef, "runId");
        JsonShape.Str(resultRef, "generatedUtc");
        JsonShape.Str(resultRef, "logicVersion");
    }

    [Fact]
    public async Task FilterPreview_ResponseShape_IsLocked()
    {
        // 廣域條件(|金額| ≥ 0)保證命中,使 previewRows 非空、可鎖元素形狀。
        var payload = JsonSerializer.Serialize(new
        {
            scenario = new
            {
                name = "契約鎖預覽",
                rationale = "契約鎖測試用情境",
                groups = new[]
                {
                    new { join = "AND", rules = new[] { new { join = "AND", type = "numRange", field = "amount", from = "0" } } }
                }
            }
        });

        var data = await fixture.Host.DispatchAsync("filter.preview", payload);

        JsonShape.HasExactKeys(data, "scenario");
        var scenario = JsonShape.Obj(data, "scenario");
        JsonShape.HasExactKeys(scenario, "name", "populationScope", "count", "voucherCount", "previewRows");
        JsonShape.Str(scenario, "name");
        JsonShape.Str(scenario, "populationScope");
        Assert.Equal(GlPopulationScopeValues.AuditPeriod, scenario.GetProperty("populationScope").GetString());
        JsonShape.Number(scenario, "count");
        JsonShape.Number(scenario, "voucherCount");
        var previewRows = JsonShape.Arr(scenario, "previewRows");
        Assert.True(previewRows.GetArrayLength() >= 1, "廣域條件應命中至少一列");
        JsonShape.Element(previewRows, e =>
        {
            JsonShape.HasExactKeys(e,
                "documentNumber", "lineItem", "postDate", "accountCode", "accountName",
                "documentDescription", "amount", "drCr");
            JsonShape.Str(e, "documentNumber", nullable: true);
            JsonShape.Str(e, "lineItem", nullable: true);
            JsonShape.Str(e, "postDate", nullable: true);
            JsonShape.Str(e, "accountCode", nullable: true);
            JsonShape.Str(e, "accountName", nullable: true);
            JsonShape.Str(e, "documentDescription", nullable: true);
            JsonShape.Number(e, "amount");
            JsonShape.Str(e, "drCr");
        });
    }

    [Fact]
    public async Task FilterCommit_ResponseShape_IsLocked()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        var data = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[]
            {
                new
                {
                    name = "契約鎖情境",
                    rationale = "確認 revision 回應契約",
                    groups = new[]
                    {
                        new
                        {
                            join = "AND",
                            rules = new[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } }
                        }
                    }
                }
            }
        }));

        JsonShape.HasExactKeys(data, "ok", "savedCount", "scenarios", "resultRef");
        Assert.Equal(JsonValueKind.True, data.GetProperty("ok").ValueKind);
        JsonShape.Number(data, "savedCount");
        JsonShape.Element(JsonShape.Arr(data, "scenarios"), scenario =>
        {
            JsonShape.HasExactKeys(
                scenario,
                "source", "name", "rationale", "groups", "populationScope", "savedUtc");
            JsonShape.Str(scenario, "source", nullable: true);
            JsonShape.Str(scenario, "name");
            JsonShape.Str(scenario, "rationale");
            JsonShape.Arr(scenario, "groups");
            JsonShape.Str(scenario, "populationScope");
            JsonShape.Str(scenario, "savedUtc");
        });
        var resultRef = JsonShape.Obj(data, "resultRef");
        JsonShape.HasExactKeys(resultRef, "revision", "generatedUtc", "logicVersion", "populationScope");
        JsonShape.Str(resultRef, "revision");
        JsonShape.Str(resultRef, "generatedUtc");
        JsonShape.Str(resultRef, "logicVersion");
        JsonShape.Str(resultRef, "populationScope");
        Assert.Equal(GlPopulationScopeValues.AuditPeriod, resultRef.GetProperty("populationScope").GetString());
    }

    [Fact]
    public async Task QueryDataPreview_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync(
            "query.dataPreview", JsonSerializer.Serialize(new { dataset = "glEntries" }));

        JsonShape.HasExactKeys(data, "dataset", "columns", "rows", "totalCount", "stats");
        JsonShape.Str(data, "dataset");
        JsonShape.Number(data, "totalCount");
        JsonShape.Element(JsonShape.Arr(data, "columns"), c => Assert.Equal(JsonValueKind.String, c.ValueKind));
        JsonShape.Element(JsonShape.Arr(data, "rows"), r => Assert.Equal(JsonValueKind.Array, r.ValueKind));

        // glEntries 一律帶 stats(非 null)。
        var stats = JsonShape.Obj(data, "stats");
        JsonShape.HasExactKeys(stats, "amountAbsMin", "amountAbsMax", "postDateMin", "postDateMax", "voucherCount");
        JsonShape.Number(stats, "amountAbsMin");
        JsonShape.Number(stats, "amountAbsMax");
        JsonShape.Str(stats, "postDateMin", nullable: true);
        JsonShape.Str(stats, "postDateMax", nullable: true);
        JsonShape.Number(stats, "voucherCount");
    }

    [Fact]
    public async Task QueryDataPreview_GlExcludedEntriesResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync(
            "query.dataPreview", JsonSerializer.Serialize(new { dataset = "glExcludedEntries", limit = 1 }));

        JsonShape.HasExactKeys(data, "dataset", "columns", "rows", "totalCount", "stats");
        Assert.Equal(
            [
                "documentNumber", "lineItem", "postDate", "postingStatus", "accountCode", "accountName",
                "documentDescription", "amount", "drCr", "exclusionReason"
            ],
            data.GetProperty("columns").EnumerateArray().Select(column => column.GetString()!).ToArray());
        JsonShape.Number(data, "totalCount");
        JsonShape.Element(JsonShape.Arr(data, "rows"), row =>
        {
            Assert.Equal(JsonValueKind.Array, row.ValueKind);
            Assert.Equal(10, row.GetArrayLength());
            Assert.All(
                row.EnumerateArray(),
                cell => Assert.True(cell.ValueKind is JsonValueKind.String or JsonValueKind.Null));
        });

        var stats = JsonShape.Obj(data, "stats");
        JsonShape.HasExactKeys(stats, "excludedByPeriodCount", "excludedByPostingStatusCount");
        JsonShape.Number(stats, "excludedByPeriodCount");
        JsonShape.Number(stats, "excludedByPostingStatusCount");
    }

    [Fact]
    public async Task ProjectLoad_EnvelopeShape_IsLocked()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        // 先跑一次 validate/prescreen,讓 latestRuns 兩格都有值(物件而非 null)。
        var validation = await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        // 至少建立一筆正式報告 artifact，讓 project.load 的元素形狀（含 fileState）確實被鎖住。
        // 科目配對範本自 2026-09-02 起是工作檔，不會出現在 reportArtifacts。
        await host.DispatchAsync(
            "export.validationArtifacts",
            JsonSerializer.Serialize(new
            {
                runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()
            }));
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[]
            {
                new
                {
                    name = "project.load 契約鎖情境",
                    rationale = "鎖定情境摘要元素形狀",
                    groups = new[]
                    {
                        new
                        {
                            join = "AND",
                            rules = new[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } }
                        }
                    }
                }
            }
        }));

        var data = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        JsonShape.HasExactKeys(data,
            "project", "mapping", "taxonomy", "mappingReviewRequired", "staleState",
            "importState", "latestRuns", "filterScenarios",
            "filterResultRef", "reportArtifacts", "heartbeatSeconds");
        JsonShape.Obj(data, "project");
        var mapping = JsonShape.Obj(data, "mapping");
        var glMapping = JsonShape.Obj(mapping, "gl");
        JsonShape.HasExactKeys(
            glMapping,
            "mapping", "amountMode", "approvalDateMode", "postingStatusPolicy",
            "manualAutoPolicy", "rdeFields", "formatVersion", "sourceBatchId", "committedUtc");
        var tbMapping = JsonShape.Obj(mapping, "tb");
        JsonShape.HasExactKeys(
            tbMapping, "mapping", "changeMode", "formatVersion", "sourceBatchId", "committedUtc");
        Assert.Equal(2, glMapping.GetProperty("formatVersion").GetInt32());
        Assert.Equal(2, tbMapping.GetProperty("formatVersion").GetInt32());

        var taxonomy = JsonShape.Obj(data, "taxonomy");
        JsonShape.HasExactKeys(taxonomy, "revision", "categories");
        Assert.Equal(1, taxonomy.GetProperty("revision").GetInt32());
        JsonShape.Element(JsonShape.Arr(taxonomy, "categories"), category =>
            JsonShape.HasExactKeys(
                category, "categoryId", "label", "ordinal", "semanticRole", "isBuiltIn", "parentCategoryId"));
        Assert.Equal(JsonValueKind.False, data.GetProperty("mappingReviewRequired").ValueKind);
        var staleState = JsonShape.Obj(data, "staleState");
        JsonShape.HasExactKeys(staleState, "validation", "prescreen", "filter");
        Assert.Equal(JsonValueKind.False, staleState.GetProperty("validation").ValueKind);
        Assert.Equal(JsonValueKind.False, staleState.GetProperty("prescreen").ValueKind);
        Assert.Equal(JsonValueKind.False, staleState.GetProperty("filter").ValueKind);
        var importState = JsonShape.Obj(data, "importState");
        var calendar = JsonShape.Obj(importState, "calendar");
        JsonShape.HasExactKeys(
            calendar,
            "holidayCount", "makeupDayCount", "calendarImported",
            "nonWorkingDays", "nonWorkingDaysConfigured");
        JsonShape.Number(calendar, "holidayCount");
        JsonShape.Number(calendar, "makeupDayCount");
        JsonShape.Arr(calendar, "nonWorkingDays");
        Assert.Equal(JsonValueKind.True, calendar.GetProperty("calendarImported").ValueKind);
        Assert.Equal(JsonValueKind.False, calendar.GetProperty("nonWorkingDaysConfigured").ValueKind);
        JsonShape.Element(JsonShape.Arr(data, "filterScenarios"), scenario =>
        {
            JsonShape.HasExactKeys(
                scenario,
                "source", "name", "rationale", "groups", "populationScope", "savedUtc");
            JsonShape.Str(scenario, "source", nullable: true);
            JsonShape.Str(scenario, "name");
            JsonShape.Str(scenario, "rationale");
            JsonShape.Arr(scenario, "groups");
            JsonShape.Str(scenario, "populationScope");
            JsonShape.Str(scenario, "savedUtc");
        });
        var reportArtifacts = JsonShape.Arr(data, "reportArtifacts");
        Assert.True(reportArtifacts.GetArrayLength() >= 1, "契約鎖 seed 應有正式報告 artifact");
        JsonShape.Element(reportArtifacts, artifact =>
        {
            JsonShape.HasExactKeys(
                artifact,
                "artifactId", "kind", "fileName", "fullPath", "generatedUtc", "bytes", "fileState", "sourceRef", "stale");
            JsonShape.Str(artifact, "fileState");
            var sourceRef = JsonShape.Obj(artifact, "sourceRef");
            JsonShape.HasExactKeys(
                sourceRef, "validationRunId", "prescreenRunId", "scenarioRevision", "scenarioPositions");
        });
        var filterResultRef = data.GetProperty("filterResultRef");
        Assert.Contains(filterResultRef.ValueKind, new[] { JsonValueKind.Null, JsonValueKind.Object });
        // 租約鎖心跳間隔（控制面第六輪）：sqlServer 讀 app_config、缺鍵回預設；本地案件為程式常數（此 demo 為 sqlite）。
        JsonShape.Number(data, "heartbeatSeconds");

        // latestRuns 是信封:validate / prescreen 兩格,各為 null 或物件(巢狀 run 形狀由各自契約鎖負責)。
        var latestRuns = JsonShape.Obj(data, "latestRuns");
        JsonShape.HasExactKeys(latestRuns, "validate", "prescreen");
        JsonShape.Obj(latestRuns, "validate", nullable: true);
        JsonShape.Obj(latestRuns, "prescreen", nullable: true);
    }

    [Fact]
    public async Task ProjectLoad_MappingReviewRequired_RemainsUntilEveryExistingLegacyMappingIsRecommitted()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(
            host,
            importCalendar: false,
            importAccountMapping: false,
            importAuthorizedPreparer: false,
            runValidation: false);
        var databasePath = Path.Combine(host.ProjectsRoot, context.ProjectId, "jet.db");
        var directConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();

        async Task<JsonElement> LoadAsync() => await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        static int MappingVersion(JsonElement loaded, string dataset) =>
            loaded.GetProperty("mapping").GetProperty(dataset).GetProperty("formatVersion").GetInt32();

        static bool ReviewRequired(JsonElement loaded) =>
            loaded.GetProperty("mappingReviewRequired").GetBoolean();

        async Task MarkLegacyAsync(params string[] datasets)
        {
            await using var connection = new SqliteConnection(directConnectionString);
            await connection.OpenAsync();
            foreach (var dataset in datasets)
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE config_field_mapping " +
                    "SET format_version = 1, options_json = NULL " +
                    "WHERE dataset_kind = @dataset;";
                command.Parameters.AddWithValue("@dataset", dataset);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
        }

        async Task DeleteMappingAsync(string dataset)
        {
            await using var connection = new SqliteConnection(directConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM config_field_mapping WHERE dataset_kind = @dataset;";
            command.Parameters.AddWithValue("@dataset", dataset);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        async Task RecommitGlAsync() => await host.DispatchAsync(
            "mapping.commit.gl",
            JsonSerializer.Serialize(new
            {
                mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    context.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
                amountMode = context.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
            }));

        async Task RecommitTbAsync() => await host.DispatchAsync(
            "mapping.commit.tb",
            JsonSerializer.Serialize(new
            {
                mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    context.Demo.GetProperty("tb").GetProperty("mapping").GetRawText()),
                changeMode = context.Demo.GetProperty("tb").GetProperty("changeMode").GetString()
            }));

        var loaded = await LoadAsync();
        Assert.False(ReviewRequired(loaded));

        await DeleteMappingAsync("tb");
        await MarkLegacyAsync("gl");
        loaded = await LoadAsync();
        Assert.True(ReviewRequired(loaded));
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("mapping").GetProperty("tb").ValueKind);
        await RecommitGlAsync();
        Assert.False(ReviewRequired(await LoadAsync()));
        await RecommitTbAsync();

        await MarkLegacyAsync("gl");
        loaded = await LoadAsync();
        Assert.True(ReviewRequired(loaded));
        Assert.Equal(1, MappingVersion(loaded, "gl"));
        Assert.Equal(2, MappingVersion(loaded, "tb"));
        await RecommitGlAsync();
        Assert.False(ReviewRequired(await LoadAsync()));

        await MarkLegacyAsync("tb");
        loaded = await LoadAsync();
        Assert.True(ReviewRequired(loaded));
        Assert.Equal(2, MappingVersion(loaded, "gl"));
        Assert.Equal(1, MappingVersion(loaded, "tb"));
        await RecommitTbAsync();
        Assert.False(ReviewRequired(await LoadAsync()));

        await MarkLegacyAsync("gl", "tb");
        Assert.True(ReviewRequired(await LoadAsync()));
        await RecommitGlAsync();
        loaded = await LoadAsync();
        Assert.True(ReviewRequired(loaded));
        Assert.Equal(2, MappingVersion(loaded, "gl"));
        Assert.Equal(1, MappingVersion(loaded, "tb"));
        await RecommitTbAsync();
        Assert.False(ReviewRequired(await LoadAsync()));
    }

    [Fact]
    public async Task ProjectList_ResponseShape_IsLocked()
    {
        // 雙來源清單雛形（2026-07-07）：頂層新增 online 塊，sqlServer 條目新增 syncStatus。
        // 用獨立 host + 單一 sqlite 案，鎖信封與本地條目形狀（syncStatus 僅 sqlServer 條目有 → sqlite 條目不含）。
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", """
            { "projectCode": "CONTRACT-1", "entityName": "E", "operatorId": "op",
              "periodStart": "2024-01-01", "periodEnd": "2024-12-31" }
            """);

        var data = await host.DispatchAsync("project.list");

        JsonShape.HasExactKeys(data, "projects", "online");

        // online：reachable(bool) + principal(str) + message(str|null)。不鎖 reachable 的值（依環境 SQL 是否可達而定）。
        var online = JsonShape.Obj(data, "online");
        JsonShape.HasExactKeys(online, "reachable", "principal", "message");
        Assert.Contains(online.GetProperty("reachable").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        JsonShape.Str(online, "principal");
        JsonShape.Str(online, "message", nullable: true);

        // sqlite 條目：基礎九鍵，且**不含** syncStatus（syncStatus 僅 sqlServer 條目有）。
        var projects = JsonShape.Arr(data, "projects");
        Assert.True(projects.GetArrayLength() >= 1);
        JsonShape.Element(projects, e => JsonShape.HasExactKeys(e,
            "projectId", "projectCode", "entityName", "periodStart", "periodEnd",
            "createdUtc", "currentStep", "databaseProvider", "lastOpenedUtc"));
    }

    [Fact]
    public async Task SystemWhoAmI_ResponseShape_IsLocked()
    {
        // 身分徽章契約：恰四鍵＋型別（principal/shortName 字串、userNumber 數字或 null、numberSource 字串）。
        // numberSource 的「值」env-相依（SQL 是否可達：online/cached/unavailable），故只鎖列舉成員、不硬鎖單一值
        // （比照 ProjectList 對 online.reachable 的處理；確定性的「未設定→unavailable」由 SystemWhoAmIHandlerTests 單元鎖）。
        using var host = new HandlerTestHost(sqlServerConnectionString: "");

        var data = await host.DispatchAsync("system.whoAmI");

        JsonShape.HasExactKeys(data, "principal", "shortName", "userNumber", "numberSource");
        JsonShape.Str(data, "principal");
        JsonShape.Str(data, "shortName");
        JsonShape.Number(data, "userNumber", nullable: true);
        JsonShape.Str(data, "numberSource");
        Assert.Contains(
            data.GetProperty("numberSource").GetString(),
            new[] { "online", "cached", "unavailable" });
    }

    [Fact]
    public async Task QueryCompletenessDiffPage_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync("query.completenessDiffPage",
            JsonSerializer.Serialize(new { pageSize = 200 }));
        JsonShape.HasExactKeys(data, "rows", "nextCursor");
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(e, "accountCode", "accountName", "tbAmount", "glAmount", "diff", "notInTb"));
    }

    [Fact]
    public async Task QueryDocBalancePage_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync("query.docBalancePage",
            JsonSerializer.Serialize(new { pageSize = 200 }));
        JsonShape.HasExactKeys(data, "rows", "nextCursor");
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(e, "documentNumber", "debit", "credit", "diff"));
    }

    [Fact]
    public async Task QueryNullRecordsPage_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync("query.nullRecordsPage",
            JsonSerializer.Serialize(new { category = "nullDescription", pageSize = 200 }));
        JsonShape.HasExactKeys(data, "rows", "nextCursor");
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(e, "documentNumber", "accountCode", "postDate", "description"));
    }

    [Fact]
    public async Task QueryFilterHitsPage_ResponseShape_IsLocked()
    {
        // 先 commit 一個 demo 母體確有命中的情境(backdatedPosting),保證 rows 非空、元素鍵集合確實鎖到。
        await fixture.Host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "提前過帳", rationale = "contract",
                groups = new[] { new { join = "and", rules = new[] {
                    new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } } }
        }));

        var data = await fixture.Host.DispatchAsync("query.filterHitsPage",
            JsonSerializer.Serialize(new { scenarioPosition = 1, pageSize = 200 }));
        JsonShape.HasExactKeys(data, "columns", "rows", "nextCursor");
        var columns = JsonShape.Arr(data, "columns");
        Assert.Equal(
            ["documentNumber", "lineItem", "postDate", "accountCode", "accountName", "amount", "drCr", "description"],
            columns.EnumerateArray().Select(column => column.GetProperty("key").GetString()!).ToArray());
        JsonShape.Element(columns, column =>
        {
            // 2026-09-07 工作包 G：固定欄多帶 sortable（第一次失敗：收據 20260907-082210786）。
            JsonShape.HasExactKeys(column, "key", "label", "valueType", "isCustom", "sortable");
            JsonShape.Str(column, "key");
            JsonShape.Str(column, "label");
            JsonShape.Str(column, "valueType");
            Assert.False(column.GetProperty("isCustom").GetBoolean());
            Assert.True(column.GetProperty("sortable").GetBoolean());
        });
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(
                e, "documentNumber", "lineItem", "postDate", "accountCode",
                "accountName", "amount", "drCr", "description", "customValues"));
    }

    [Fact]
    public async Task QueryTagMatrixScenarios_ResponseShape_IsLocked()
    {
        // 先 commit 一個 demo 母體確有命中的情境(backdatedPosting),保證 scenarios 非空、元素鍵集合確實鎖到。
        await fixture.Host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "提前過帳", rationale = "contract",
                groups = new[] { new { join = "and", rules = new[] {
                    new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } } }
        }));

        var data = await fixture.Host.DispatchAsync("query.tagMatrixScenarios");
        JsonShape.HasExactKeys(data, "scenarios");
        var scenarios = JsonShape.Arr(data, "scenarios");
        Assert.True(scenarios.GetArrayLength() >= 1, "已 commit 情境,scenarios 應非空");
        JsonShape.Element(scenarios, e =>
        {
            JsonShape.HasExactKeys(e, "position", "name", "voucherHitCount", "rowHitCount");
            JsonShape.Number(e, "position");
            JsonShape.Str(e, "name");
            JsonShape.Number(e, "voucherHitCount");
            JsonShape.Number(e, "rowHitCount");
        });
    }

    [Fact]
    public async Task QueryTagMatrixVoucherPage_ResponseShape_IsLocked()
    {
        // 先 commit 一個 demo 母體確有命中的情境(backdatedPosting),保證 rows 非空、元素鍵集合確實鎖到。
        await fixture.Host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "提前過帳", rationale = "contract",
                groups = new[] { new { join = "and", rules = new[] {
                    new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } } }
        }));

        var data = await fixture.Host.DispatchAsync("query.tagMatrixVoucherPage",
            JsonSerializer.Serialize(new { pageSize = 200 }));
        JsonShape.HasExactKeys(data, "rows", "nextCursor");
        var rows = JsonShape.Arr(data, "rows");
        Assert.True(rows.GetArrayLength() >= 1, "已 commit 命中情境,rows 應非空");
        JsonShape.Element(rows, e =>
        {
            JsonShape.HasExactKeys(
                e, "documentNumber", "postDate", "createdBy", "voucherTotal", "matchedPositions");
            JsonShape.Number(e, "voucherTotal");
            var positions = JsonShape.Arr(e, "matchedPositions");
            JsonShape.Element(positions, p => Assert.Equal(JsonValueKind.Number, p.ValueKind));
        });
    }

    [Fact]
    public async Task QueryInfSamplePage_ResponseShape_IsLocked()
    {
        // 先跑 validate.run 落地 INF 樣本,保證 rows 非空、元素鍵集合確實鎖到。
        await fixture.Host.DispatchAsync("validate.run");

        var data = await fixture.Host.DispatchAsync("query.infSamplePage",
            JsonSerializer.Serialize(new { pageSize = 200 }));
        JsonShape.HasExactKeys(data, "columns", "rows", "nextCursor");
        var columns = JsonShape.Arr(data, "columns");
        Assert.Equal(
            ["documentNumber", "accountCode", "accountName", "debit", "credit", "postDate",
                "approvalDate", "createdBy", "approvedBy", "description"],
            columns.EnumerateArray().Select(column => column.GetProperty("key").GetString()!).ToArray());
        JsonShape.Element(columns, column =>
        {
            // 2026-09-07 工作包 G：固定欄多帶 sortable（第一次失敗：收據 20260907-082210786）。
            JsonShape.HasExactKeys(column, "key", "label", "valueType", "isCustom", "sortable");
            JsonShape.Str(column, "key");
            JsonShape.Str(column, "label");
            JsonShape.Str(column, "valueType");
            Assert.False(column.GetProperty("isCustom").GetBoolean());
            Assert.True(column.GetProperty("sortable").GetBoolean());
        });
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(
                e, "documentNumber", "accountCode", "accountName", "debit", "credit",
                "postDate", "approvalDate", "createdBy", "approvedBy", "description", "customValues"));
    }

    [Fact]
    public async Task ExportWorkpaperStream_ResponseShape_IsLocked()
    {
        // export 會寫實體檔且 materialize result_filter_run,故用獨立 host(不污染共用 fixture)。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var filter = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "契約情境", rationale = "contract",
                groups = new[] { new { join = "and", rules = new[] {
                    new { join = "and", type = "drCrOnly", drCr = "debit" } } } } } }
        }));
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString();
        var scenarioRevision = filter.GetProperty("resultRef").GetProperty("revision").GetString();
        await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationRunId,
                revision = scenarioRevision
            }));
        var data = await host.DispatchAsync(
            "export.workpaperStream", JsonSerializer.Serialize(new
            {
                validationRunId,
                scenarioRevision,
                scenarioPositions = new[] { 1 }
            }));

            JsonShape.HasExactKeys(data, "ok", "artifact", "sheetStats", "reportArtifacts");
            Assert.Equal(JsonValueKind.True, data.GetProperty("ok").ValueKind);
            var artifact = JsonShape.Obj(data, "artifact");
            JsonShape.HasExactKeys(artifact,
                "artifactId", "kind", "fileName", "fullPath", "generatedUtc", "bytes", "fileState", "sourceRef", "stale");
            JsonShape.Str(artifact, "fileState");
            JsonShape.Str(artifact, "artifactId");
            JsonShape.Str(artifact, "kind");
            JsonShape.Str(artifact, "fileName");
            JsonShape.Number(artifact, "bytes");
            var catalog = JsonShape.Arr(data, "reportArtifacts");
            Assert.Equal(new[] { "criteriaSelectionReport", "workingPaper" }, catalog.EnumerateArray()
                .Select(item => item.GetProperty("kind").GetString()).Order(StringComparer.Ordinal));
            JsonShape.Element(catalog, item => JsonShape.HasExactKeys(item,
                "artifactId", "kind", "fileName", "fullPath", "generatedUtc", "bytes", "fileState", "sourceRef", "stale"));
            var published = Assert.Single(catalog.EnumerateArray(), item =>
                item.GetProperty("artifactId").GetString() == artifact.GetProperty("artifactId").GetString());
            Assert.Equal(artifact.GetRawText(), published.GetRawText());
            var sheetStats = JsonShape.Arr(data, "sheetStats");
            Assert.True(sheetStats.GetArrayLength() >= 1, "至少應有封面等工作表的統計");
            JsonShape.Element(sheetStats, e =>
            {
                JsonShape.HasExactKeys(e, "sheetName", "rowsWritten");
                JsonShape.Str(e, "sheetName");
                JsonShape.Number(e, "rowsWritten");
            });
    }

    [Fact]
    public async Task DemoExportAuthorizedPreparerFile_ResponseShape_IsLocked()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        var data = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAuthorizedPreparerFile");

        JsonShape.HasExactKeys(data, "filePath", "fileName");
        JsonShape.Str(data, "filePath");
        JsonShape.Str(data, "fileName");
    }

    [Fact]
    public async Task ExportAccountMappingTemplate_ResponseShape_IsLocked()
    {
        // 範本是工作檔：回傳完整路徑讓前端顯示，不再回 artifact 物件、不進報告清單。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var data = await host.DispatchAsync(
            "export.accountMappingTemplate",
            JsonSerializer.Serialize(new
            {
                runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()
            }));

        JsonShape.HasExactKeys(data, "ok", "filePath", "fileName", "rowCount", "validationRunId", "disposition");
        Assert.Equal(JsonValueKind.True, data.GetProperty("ok").ValueKind);
        JsonShape.Str(data, "filePath");
        JsonShape.Str(data, "fileName");
        JsonShape.Number(data, "rowCount");
        JsonShape.Str(data, "validationRunId");
        Assert.Equal("created", data.GetProperty("disposition").GetString());
        Assert.Equal(validation.GetProperty("resultRef").GetProperty("runId").GetString(),
            data.GetProperty("validationRunId").GetString());
        Assert.True(Path.IsPathFullyQualified(data.GetProperty("filePath").GetString()!));
        Assert.Equal(
            Path.GetFileName(data.GetProperty("filePath").GetString()!),
            data.GetProperty("fileName").GetString());
        var original = await File.ReadAllBytesAsync(data.GetProperty("filePath").GetString()!);
        var kept = await host.DispatchAsync("export.accountMappingTemplate", JsonSerializer.Serialize(new
        {
            runId = data.GetProperty("validationRunId").GetString(),
            onlyIfMissing = true
        }));
        JsonShape.HasExactKeys(kept, "ok", "filePath", "fileName", "rowCount", "validationRunId", "disposition");
        Assert.Equal(JsonValueKind.True, kept.GetProperty("ok").ValueKind);
        Assert.Equal("kept", kept.GetProperty("disposition").GetString());
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("rowCount").ValueKind);
        foreach (var field in new[] { "filePath", "fileName", "validationRunId" })
        {
            JsonShape.Str(kept, field);
            Assert.Equal(data.GetProperty(field).GetString(), kept.GetProperty(field).GetString());
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(kept.GetProperty("filePath").GetString()!));
    }

    [Fact]
    public async Task HostOpenFolder_ResponseShape_IsLocked()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var exported = await host.DispatchAsync(
            "export.validationArtifacts",
            JsonSerializer.Serialize(new
            {
                runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()
            }));
        var data = await host.DispatchAsync(
            "host.openFolder",
            JsonSerializer.Serialize(new
            {
                artifactId = exported.GetProperty("artifacts")[0].GetProperty("artifactId").GetString()
            }));

        JsonShape.HasExactKeys(data, "ok");
        Assert.Equal(JsonValueKind.True, data.GetProperty("ok").ValueKind);
    }

    [Fact]
    public async Task QueryTagMatrixRowPage_ResponseShape_IsLocked()
    {
        // 先 commit 一個 demo 母體確有命中的情境(backdatedPosting),保證命中傳票存在、
        // 其所有行被列出、元素鍵集合(含 matchedPositions)確實鎖到。
        await fixture.Host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "提前過帳", rationale = "contract",
                groups = new[] { new { join = "and", rules = new[] {
                    new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } } }
        }));

        var data = await fixture.Host.DispatchAsync("query.tagMatrixRowPage",
            JsonSerializer.Serialize(new { pageSize = 200 }));
        JsonShape.HasExactKeys(data, "rows", "nextCursor");
        var rows = JsonShape.Arr(data, "rows");
        Assert.True(rows.GetArrayLength() >= 1, "已 commit 命中情境,命中傳票之所有行 rows 應非空");
        JsonShape.Element(rows, e =>
        {
            JsonShape.HasExactKeys(
                e, "documentNumber", "lineItem", "postDate", "approvalDate", "createdBy",
                "approvedBy", "accountCode", "accountName", "amount", "matchedPositions", "description");
            JsonShape.Number(e, "amount");
            var positions = JsonShape.Arr(e, "matchedPositions");
            JsonShape.Element(positions, p => Assert.Equal(JsonValueKind.Number, p.ValueKind));
        });
    }
}

/// <summary>
/// JSON 形狀斷言小工具:鎖「鍵集合(順序無關)＋型別」,不鎖順序或數值。
/// 一致的寫法套用到每個 action,不為單一 action 另搞特例(Linus 好品味:消除特例)。
/// </summary>
internal static class JsonShape
{
    /// <summary>斷言 obj 是物件,且鍵集合「恰等於」expectedKeys——同時抓到漏欄位與悄悄多出的欄位。</summary>
    public static void HasExactKeys(JsonElement obj, params string[] expectedKeys)
    {
        Assert.Equal(JsonValueKind.Object, obj.ValueKind);
        var actual = obj.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var expected = expectedKeys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    public static void Number(JsonElement obj, string key, bool nullable = false) => Kind(obj, key, JsonValueKind.Number, nullable);

    public static void Str(JsonElement obj, string key, bool nullable = false) => Kind(obj, key, JsonValueKind.String, nullable);

    /// <summary>斷言鍵為物件(可空)並回傳之,供進一步檢查;null 時直接回傳該 null 元素。</summary>
    public static JsonElement Obj(JsonElement obj, string key, bool nullable = false)
    {
        var v = obj.GetProperty(key);
        if (nullable && v.ValueKind == JsonValueKind.Null) { return v; }
        Assert.Equal(JsonValueKind.Object, v.ValueKind);
        return v;
    }

    /// <summary>斷言鍵為陣列並回傳之。</summary>
    public static JsonElement Arr(JsonElement obj, string key)
    {
        var v = obj.GetProperty(key);
        Assert.Equal(JsonValueKind.Array, v.ValueKind);
        return v;
    }

    /// <summary>有資料才檢查第一個元素的形狀(空陣列不阻擋,但形狀鎖在有資料時生效)。</summary>
    public static void Element(JsonElement array, Action<JsonElement> assertElement)
    {
        if (array.GetArrayLength() > 0) { assertElement(array[0]); }
    }

    private static void Kind(JsonElement obj, string key, JsonValueKind kind, bool nullable)
    {
        var v = obj.GetProperty(key);
        if (nullable && v.ValueKind == JsonValueKind.Null) { return; }
        Assert.Equal(kind, v.ValueKind);
    }
}
