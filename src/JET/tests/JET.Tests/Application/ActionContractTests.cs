using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Architecture;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 回應契約鎖（harness TDD 強化,做法一:手寫語意斷言)。
/// 目的:把 manifest 描述的回應形狀變成可執行的測試——欄位被改名／拿掉／改型別／悄悄新增時,
/// 這裡會紅,逼人確認「故意還是改壞」(Linus「不破壞 userspace」用在 JS↔C# 介面)。
///
/// 鎖的是「欄位集合＋型別＋巢狀結構」,不鎖屬性順序、不鎖具體數值(jet-testing §3)。
/// 巢狀的 run 形狀(validationResult、prescreenResult 內)由各自 action 的契約鎖負責,project.load 只鎖外層信封。
/// 陣列元素形狀「有資料才檢查」;新欄位(unbalancedVouchers／nullRows)另以固定 seed 保證有資料、確實鎖到。
/// </summary>
public sealed class ActionContractTests(DemoProjectFixture fixture) : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task ValidateRun_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync("validate.run");

        // 新增有界的同號多日期摘要，保留根層及巢狀精確形狀檢查；Public首敗100911120。
        JsonShape.HasExactKeys(data,
            "stats", "amountDistribution", "completenessTest", "voucherBalanceTest",
            "infSamplingTest", "blankRecordsTest", "populationSummary", "sourceQuality", "voucherNumberAcrossGlDates", "resultRef");

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
        JsonShape.HasExactKeys(populationSummary, "raw", "gl", "excluded");
        var rawPopulation = JsonShape.Obj(populationSummary, "raw");
        JsonShape.HasExactKeys(rawPopulation, "rowCount", "totalDebit", "totalCredit");
        JsonShape.Number(rawPopulation, "rowCount");
        JsonShape.Number(rawPopulation, "totalDebit");
        JsonShape.Number(rawPopulation, "totalCredit");
        var glPopulation = JsonShape.Obj(populationSummary, "gl");
        JsonShape.HasExactKeys(
            glPopulation,
            "rowCount", "voucherCount", "totalDebit", "totalCredit", "net");
        JsonShape.Number(glPopulation, "rowCount");
        JsonShape.Number(glPopulation, "voucherCount");
        JsonShape.Number(glPopulation, "totalDebit");
        JsonShape.Number(glPopulation, "totalCredit");
        JsonShape.Number(glPopulation, "net");
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
            "status", "naReason", "diffAccountCount", "diffAccounts", "storedTotals", "eligibility");
        JsonShape.Str(completeness, "status");
        JsonShape.Str(completeness, "naReason", nullable: true);
        JsonShape.Number(completeness, "diffAccountCount");
        JsonShape.Element(JsonShape.Arr(completeness, "diffAccounts"), e =>
        {
            JsonShape.HasExactKeys(e, "accountNumber", "accountName", "tbAmount", "glAmount", "diff", "notInTb");
            JsonShape.Str(e, "accountNumber");
            JsonShape.Str(e, "accountName", nullable: true);
            JsonShape.Number(e, "tbAmount");
            JsonShape.Number(e, "glAmount");
            JsonShape.Number(e, "diff");
            Assert.Contains(e.GetProperty("notInTb").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        });

        // 存入筆數與合計核對的 eligibleSource 與 gl 都維持固定子物件；沒有這組數字時全部欄位（含 match）為 null。
        var storedTotals = JsonShape.Obj(completeness, "storedTotals");
        JsonShape.HasExactKeys(
            storedTotals,
            "eligibleSource", "gl", "rowCountMatch", "amountMatch");
        foreach (var population in new[]
                 {
                     JsonShape.Obj(storedTotals, "eligibleSource"),
                     JsonShape.Obj(storedTotals, "gl")
                 })
        {
            JsonShape.HasExactKeys(population, "rowCount", "totalDebit", "totalCredit");
            JsonShape.Number(population, "rowCount", nullable: true);
            JsonShape.Number(population, "totalDebit", nullable: true);
            JsonShape.Number(population, "totalCredit", nullable: true);
        }
        Assert.Contains(
            storedTotals.GetProperty("rowCountMatch").ValueKind,
            new[] { JsonValueKind.True, JsonValueKind.False, JsonValueKind.Null });
        Assert.Contains(
            storedTotals.GetProperty("amountMatch").ValueKind,
            new[] { JsonValueKind.True, JsonValueKind.False, JsonValueKind.Null });

        var eligibility = JsonShape.Obj(completeness, "eligibility");
        JsonShape.HasExactKeys(eligibility, "isEligible", "reason", "warning");
        Assert.Contains(
            eligibility.GetProperty("isEligible").ValueKind,
            new[] { JsonValueKind.True, JsonValueKind.False });
        JsonShape.Str(eligibility, "reason", nullable: true);
        JsonShape.Str(eligibility, "warning", nullable: true);

        var voucherBalance = JsonShape.Obj(data, "voucherBalanceTest");
        JsonShape.HasExactKeys(voucherBalance, "status", "unbalancedVoucherCount", "unbalancedVouchers");
        JsonShape.Str(voucherBalance, "status");
        JsonShape.Number(voucherBalance, "unbalancedVoucherCount");
        JsonShape.Arr(voucherBalance, "unbalancedVouchers"); // 元素形狀見 ValidateRun_DetailArrays

        var inf = JsonShape.Obj(data, "infSamplingTest");
        JsonShape.HasExactKeys(inf, "status", "sampleSize", "seed");
        JsonShape.Str(inf, "status");
        JsonShape.Number(inf, "sampleSize");
        JsonShape.Number(inf, "seed");

        var blankRecords = JsonShape.Obj(data, "blankRecordsTest");
        // 裁定 2026-10-23-01：傳票核准日不可用時「核准日不在期間」改成無法執行，回應多一個原因欄，所以預期的欄位多列一項。
        // 這個示範案件有配對傳票核准日，筆數仍是數字、原因是 null。第一次失敗收據 20261023-101645559-0f1f5a2c47644dfd9f035d23db690993。
        JsonShape.HasExactKeys(blankRecords,
            "status", "nullAccountCount", "nullDocumentCount", "nullDescriptionCount", "approvalDateNotInPeriodCount",
            "approvalDateNotInPeriodNaReason", "nullRows");
        JsonShape.Str(blankRecords, "status");
        JsonShape.Number(blankRecords, "nullAccountCount");
        JsonShape.Number(blankRecords, "nullDocumentCount");
        JsonShape.Number(blankRecords, "nullDescriptionCount");
        JsonShape.Number(blankRecords, "approvalDateNotInPeriodCount");
        Assert.Equal(JsonValueKind.Null, blankRecords.GetProperty("approvalDateNotInPeriodNaReason").ValueKind);
        JsonShape.Arr(blankRecords, "nullRows"); // 元素形狀見 ValidateRun_DetailArrays

        var sourceQuality = JsonShape.Obj(data, "sourceQuality");
        JsonShape.HasExactKeys(sourceQuality, "findingCount", "sampleRows");
        JsonShape.Number(sourceQuality, "findingCount");
        JsonShape.Arr(sourceQuality, "sampleRows");

        var voucherNumberAcrossGlDates = JsonShape.Obj(data, "voucherNumberAcrossGlDates");
        JsonShape.HasExactKeys(voucherNumberAcrossGlDates, "voucherNumberCount", "entryCount");
        JsonShape.Number(voucherNumberAcrossGlDates, "voucherNumberCount");
        JsonShape.Number(voucherNumberAcrossGlDates, "entryCount");

        var resultRef = JsonShape.Obj(data, "resultRef");
        JsonShape.HasExactKeys(resultRef, "resultId", "generatedUtc", "logicVersion");
        JsonShape.Str(resultRef, "resultId");
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

        var unbalanced = JsonShape.Arr(data.GetProperty("voucherBalanceTest"), "unbalancedVouchers");
        Assert.True(unbalanced.GetArrayLength() >= 1, "seed 應產生不平傳票");
        JsonShape.Element(unbalanced, e =>
        {
            JsonShape.HasExactKeys(e, "voucherNumber", "debit", "credit", "diff");
            JsonShape.Str(e, "voucherNumber", nullable: true);
            JsonShape.Number(e, "debit");
            JsonShape.Number(e, "credit");
            JsonShape.Number(e, "diff");
        });

        var nullRows = JsonShape.Arr(data.GetProperty("blankRecordsTest"), "nullRows");
        Assert.True(nullRows.GetArrayLength() >= 1, "seed 應產生空值列");
        JsonShape.Element(nullRows, e =>
        {
            JsonShape.HasExactKeys(e, "voucherNumber", "accountNumber", "glDate", "description", "issues");
            JsonShape.Str(e, "voucherNumber", nullable: true);
            JsonShape.Str(e, "accountNumber", nullable: true);
            JsonShape.Str(e, "glDate", nullable: true);
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
            "prepPeriodApproval", "suspiciousKeywords", "unexpectedAccountPair", "trailingZeros",
            "preparerSummary", "rareAccounts", "weekendActivity", "holidayActivity", "blankDescription",
            // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，回應不再有 backdatedPosting。
            // 第四步照舊版拿掉編製分錄較少的人員與使用較少的科目（裁定 2026-10-29-27），回應不再有 rarePreparer、rareAccount 與 rareAccounts.rareAccountCount；第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad。
            "nonAuthorizedPreparer",
            "rulePeriod", "concentration", "positioning", "resultRef");

        foreach (var key in new[] { "prepPeriodApproval", "unexpectedAccountPair", "nonAuthorizedPreparer" })
        {
            var rule = JsonShape.Obj(data, key);
            JsonShape.HasExactKeys(rule, "status", "naReason", "count");
            JsonShape.Str(rule, "status");
            JsonShape.Str(rule, "naReason", nullable: true);
            JsonShape.Number(rule, "count");
        }

        foreach (var key in new[] { "suspiciousKeywords", "blankDescription" })
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

        var preparerSummary = JsonShape.Obj(data, "preparerSummary");
        // 裁定 2026-10-05-01：preparers 最多 50 列，畫面與報告的人數改用完整人數，回應因此多一個 totalPreparerCount。
        // 第一次失敗收據 20261005-044601454-1ab8711bd18d4134843046dbe6777340；原有三個鍵與型別檢查不變。
        JsonShape.HasExactKeys(preparerSummary, "status", "naReason", "totalPreparerCount", "preparers");
        JsonShape.Str(preparerSummary, "status");
        JsonShape.Str(preparerSummary, "naReason", nullable: true);
        JsonShape.Number(preparerSummary, "totalPreparerCount", nullable: true);
        JsonShape.Element(JsonShape.Arr(preparerSummary, "preparers"), e =>
        {
            JsonShape.HasExactKeys(e, "createdBy", "entryCount", "debitTotal", "creditTotal", "manualCount");
            JsonShape.Str(e, "createdBy", nullable: true);
            JsonShape.Number(e, "entryCount");
            JsonShape.Number(e, "debitTotal");
            JsonShape.Number(e, "creditTotal");
            JsonShape.Number(e, "manualCount");
        });

        var rareAccounts = JsonShape.Obj(data, "rareAccounts");
        // 原本另有完整母體的較少使用科目數，已隨第四步拿掉兩條規則移除；全科目數與命中分錄列數維持原義。
        // 首次失敗：Public 20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d；仍鎖定完整欄位集合。
        JsonShape.HasExactKeys(rareAccounts, "status", "distinctAccountCount", "accounts");
        JsonShape.Str(rareAccounts, "status");
        JsonShape.Number(rareAccounts, "distinctAccountCount");
        JsonShape.Element(JsonShape.Arr(rareAccounts, "accounts"), e =>
        {
            JsonShape.HasExactKeys(e, "accountNumber", "accountName", "entryCount", "debitTotal", "creditTotal");
            JsonShape.Str(e, "accountNumber", nullable: true);
            JsonShape.Str(e, "accountName", nullable: true);
            JsonShape.Number(e, "entryCount");
            JsonShape.Number(e, "debitTotal");
            JsonShape.Number(e, "creditTotal");
        });

        // 週末與假日卡分開「整張卡無法執行」與「只檢查了總帳日期」，
        // 回應多一個 approvalNaReason；原有四個鍵與型別檢查不變。第一次失敗：Focused 收據
        // 20261023-033416473-0f25ea4f0cd94c14b96c7607c741f7e7。
        foreach (var key in new[] { "weekendActivity", "holidayActivity" })
        {
            var rule = JsonShape.Obj(data, key);
            JsonShape.HasExactKeys(rule, "status", "naReason", "approvalNaReason", "postingCount", "approvalCount");
            JsonShape.Str(rule, "status");
            JsonShape.Str(rule, "naReason", nullable: true);
            JsonShape.Str(rule, "approvalNaReason", nullable: true);
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
            JsonShape.HasExactKeys(e, "accountNumber", "accountName", "entryCount");
            JsonShape.Str(e, "accountNumber", nullable: true);
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
            "查看編製人員與較少使用科目的分錄筆數及金額。",
            positioning.GetProperty("aggregateGuidance").GetString());
        Assert.Equal(
            "符合條件的分錄供初步查核；請在「進階條件篩選」設定本案的測試範圍。",
            positioning.GetProperty("signalGuidance").GetString());
        Assert.Equal(
            "可在此產生預篩選報告，或在匯出底稿時一併產生。不產生也可繼續篩選與匯出底稿。",
            positioning.GetProperty("reportGuidance").GetString());
        Assert.Equal(
            // 用語統一後，畫面不再使用「母體」（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
            "預篩選呈現查核期間分錄的分布與符合條件的分錄，是否需進一步查核由審計員判斷。",
            positioning.GetProperty("overviewGuidance").GetString());
        Assert.Equal(
            "預設一併產出預篩選報告；取消勾選不影響其他報告及底稿內容。",
            positioning.GetProperty("exportDefaultGuidance").GetString());
        // 裁定 2026-10-14-01 確認了這句的用字（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。
        Assert.Equal(
            "尚無預篩選結果，匯出時會先執行預篩選；資料量大時需要數分鐘到十餘分鐘。",
            positioning.GetProperty("exportPendingRunGuidance").GetString());

        var resultRef = JsonShape.Obj(data, "resultRef");
        JsonShape.HasExactKeys(resultRef, "resultId", "generatedUtc", "logicVersion");
        JsonShape.Str(resultRef, "resultId");
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
                    new { join = "AND", conditions = new[] { new { join = "AND", type = "numRange", field = "amount", from = "0" } } }
                }
            }
        });

        var data = await fixture.Host.DispatchAsync("filter.preview", payload);

        JsonShape.HasExactKeys(data, "scenario");
        var scenario = JsonShape.Obj(data, "scenario");
        // 畫面要分開顯示傳票數量與分錄筆數，並和條件篩選報告同一個口徑（裁定 2026-10-16-08），所以每個情境另回
        // summaryRowCount 與 countsWholeVoucherRows（第一次失敗：Public 收據 20261016-051726257-ff7a19590e9c4c30a7f61b5bec0bc527）。
        JsonShape.HasExactKeys(scenario, "name", "populationScope", "count", "voucherCount", "summaryRowCount", "countsWholeVoucherRows", "previewRows");
        JsonShape.Str(scenario, "name");
        JsonShape.Str(scenario, "populationScope");
        Assert.Equal(GlPopulationScopeValues.AuditPeriod, scenario.GetProperty("populationScope").GetString());
        JsonShape.Number(scenario, "count");
        JsonShape.Number(scenario, "voucherCount");
        JsonShape.Number(scenario, "summaryRowCount");
        // 這個情境不是只用「未預期借貸組合」，分錄筆數就是直接符合條件的列數。
        Assert.Equal(JsonValueKind.False, scenario.GetProperty("countsWholeVoucherRows").ValueKind);
        Assert.Equal(scenario.GetProperty("count").GetInt64(), scenario.GetProperty("summaryRowCount").GetInt64());
        var previewRows = JsonShape.Arr(scenario, "previewRows");
        Assert.True(previewRows.GetArrayLength() >= 1, "廣域條件應命中至少一列");
        JsonShape.Element(previewRows, e =>
        {
            JsonShape.HasExactKeys(e,
                "voucherNumber", "lineItem", "glDate", "accountNumber", "accountName",
                "description", "amount", "drCr");
            JsonShape.Str(e, "voucherNumber", nullable: true);
            JsonShape.Str(e, "lineItem", nullable: true);
            JsonShape.Str(e, "glDate", nullable: true);
            JsonShape.Str(e, "accountNumber", nullable: true);
            JsonShape.Str(e, "accountName", nullable: true);
            JsonShape.Str(e, "description", nullable: true);
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
                            conditions = new[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } }
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
        // 裁定 2026-10-08-04 統一全系統的資料名稱：資料預覽 wire key 與型別名稱同步改名。
        var data = await fixture.Host.DispatchAsync(
            "query.dataPreview", JsonSerializer.Serialize(new { dataset = "gl" }));

        // 資料預覽依配對列欄位（裁定 2026-10-22-09），欄名和欄位配對畫面相同：回應多帶 columnDetails，
        // 和 columns 同順序寫出每欄的名稱與型別。原本的五個鍵不變；第一次失敗：Focused 收據 20261023-112004700-2e000bb010d54098b6c123ef7f06faf0。
        JsonShape.HasExactKeys(data, "dataset", "columns", "columnDetails", "rows", "totalCount", "stats");
        JsonShape.Str(data, "dataset");
        JsonShape.Number(data, "totalCount");
        JsonShape.Element(JsonShape.Arr(data, "columns"), c => Assert.Equal(JsonValueKind.String, c.ValueKind));
        JsonShape.Element(JsonShape.Arr(data, "columnDetails"), detail =>
        {
            JsonShape.HasExactKeys(detail, "key", "label", "valueType");
            JsonShape.Str(detail, "key");
            JsonShape.Str(detail, "label");
            Assert.Contains(detail.GetProperty("valueType").GetString(), new[] { "text", "date", "amount" });
        });
        Assert.Equal(
            data.GetProperty("columns").EnumerateArray().Select(column => column.GetString()),
            data.GetProperty("columnDetails").EnumerateArray().Select(detail => detail.GetProperty("key").GetString()));
        JsonShape.Element(JsonShape.Arr(data, "rows"), r => Assert.Equal(JsonValueKind.Array, r.ValueKind));

        // gl（GL）一律帶 stats(非 null)。
        // 資料預覽的金額固定顯示四位小數（裁定 2026-10-22-06）：統計列的金額改用字串照存下的值傳，畫面補四位時不經過浮點數。
        // 原本斷言數字；第一次失敗同上。
        var stats = JsonShape.Obj(data, "stats");
        JsonShape.HasExactKeys(stats, "amountAbsMin", "amountAbsMax", "glDateMin", "glDateMax", "voucherCount");
        JsonShape.Str(stats, "amountAbsMin");
        JsonShape.Str(stats, "amountAbsMax");
        JsonShape.Str(stats, "glDateMin", nullable: true);
        JsonShape.Str(stats, "glDateMax", nullable: true);
        JsonShape.Number(stats, "voucherCount");
    }

    [Fact]
    public async Task QueryDataPreview_GlExcludedResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync(
            "query.dataPreview", JsonSerializer.Serialize(new { dataset = "glExcluded", limit = 1 }));

        // 資料預覽依配對列欄位（裁定 2026-10-22-09）：示範案件配對了傳票核准日、傳票建立人員與
        // 人工或自動分錄，沒有配對過帳狀態，所以列核心八欄、這三欄與未納入原因，共十二欄；回應多帶 columnDetails。
        // 原本斷言固定十欄；第一次失敗：Focused 收據 20261023-112004700-2e000bb010d54098b6c123ef7f06faf0。
        JsonShape.HasExactKeys(data, "dataset", "columns", "columnDetails", "rows", "totalCount", "stats");
        Assert.Equal(
            [
                "voucherNumber", "lineItem", "glDate", "accountNumber", "accountName",
                "description", "amount", "drCr", "approvalDate", "createdBy", "manualAuto", "exclusionReason"
            ],
            data.GetProperty("columns").EnumerateArray().Select(column => column.GetString()!).ToArray());
        JsonShape.Number(data, "totalCount");
        JsonShape.Element(JsonShape.Arr(data, "rows"), row =>
        {
            Assert.Equal(JsonValueKind.Array, row.ValueKind);
            Assert.Equal(12, row.GetArrayLength());
            Assert.All(
                row.EnumerateArray(),
                cell => Assert.True(cell.ValueKind is JsonValueKind.String or JsonValueKind.Null));
        });

        // 裁定 2026-10-21-04 把日期不符分成三種原因，兩個計數改成四個；第一次失敗收據 20261021-080930829-9ee6cdc9021744a0bf40903ea77f833a。
        var stats = JsonShape.Obj(data, "stats");
        JsonShape.HasExactKeys(stats, "blankGlDateCount", "beforePeriodCount", "afterPeriodCount", "postingStatusCount");
        JsonShape.Number(stats, "blankGlDateCount");
        JsonShape.Number(stats, "beforePeriodCount");
        JsonShape.Number(stats, "afterPeriodCount");
        JsonShape.Number(stats, "postingStatusCount");
    }

    [Fact]
    public async Task ProjectLoad_EnvelopeShape_IsLocked()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        // 先跑一次 validate/prescreen,讓 validationResult、prescreenResult 都有值(物件而非 null)。
        var validation = await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        // 至少建立一筆正式報告，讓 project.load 的元素形狀（含 fileState）確實被鎖住。
        // 科目配對範本自 2026-09-02 起是工作檔，不會出現在 reports。
        await host.DispatchAsync(
            "export.validationReports",
            JsonSerializer.Serialize(new
            {
                resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()
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
                            conditions = new[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } }
                        }
                    }
                }
            }
        }));

        var data = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        // 舊版配對確認旗標與每側 formatVersion 已隨舊案件相容程式移除，回應不再帶這些欄位。
        // 新增 previousMapping，重新匯入後還沒重新確認時帶回上次確認的配對當草稿
        // （第一次失敗：收據 20261003-065529947-9b08f413915748fe812bdead0a6d6d89）。
        // 裁定 2026-10-14-01 確認了這句的用字（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。新增 retiredGlFieldLabels，
        // 帶已移除的 GL 欄位與畫面名稱，第五步舊情境的條件標題與讀回用它寫出名稱。
        // 裁定 2026-10-30-25：讀回已儲存情境舊寫法的程式拿掉，retiredGlFieldLabels 跟著從回應拿掉
        // （拿掉後這裡第一次編譯失敗的收據 20261030-072053096-b7f05aee757c4a11a86f01fe840d74a1，同一批的名稱表一起拿掉）。
        JsonShape.HasExactKeys(data,
            "project", "mapping", "previousMapping", "accountCategory", "staleState",
            "importState", "validationResult", "prescreenResult", "filterScenarios",
            "filterResultRef", "filterScenarioCheck", "reports", "heartbeatSeconds");
        JsonShape.Obj(data, "project");
        // 兩側都有有效配對時不帶上次的配對。
        var previousMapping = JsonShape.Obj(data, "previousMapping");
        JsonShape.HasExactKeys(previousMapping, "gl", "tb");
        Assert.Equal(JsonValueKind.Null, previousMapping.GetProperty("gl").ValueKind);
        Assert.Equal(JsonValueKind.Null, previousMapping.GetProperty("tb").ValueKind);
        var mapping = JsonShape.Obj(data, "mapping");
        var glMapping = JsonShape.Obj(mapping, "gl");
        // 前端依後端算好的可用欄位停用第五步的入口，GL 配對多帶 availableGlFields；
        // 原有八個鍵不變。第一次失敗：Focused 收據 20261023-034722896-edea6c2e6490491690174ff6552e60f8。
        // 確認配對時往下填補空白的欄位跟著配對保存，GL 配對多帶 fillDownFields；
        // 第一次失敗：Focused 收據 20261023-194740297-3ae629d46df04e06861cacc3aaccf1e9。
        // 往下填補的格數隨配對保存（裁定 2026-10-28-05），GL 配對多帶 fillDownCount，第三步摘要在重開案件後也列得出來；
        // 第一次失敗：Focused 收據 20261028-030052458-976ecec7163e46888ef1157effd56baf。
        JsonShape.HasExactKeys(
            glMapping,
            "mapping", "amountMode", "approvalDateMode", "postingStatusPolicy",
            "manualAutoPolicy", "rdeFields", "fillDownFields", "fillDownCount", "sourceBatchId", "committedUtc", "availableGlFields");
        JsonShape.Element(JsonShape.Arr(glMapping, "fillDownFields"), field =>
            Assert.Equal(JsonValueKind.String, field.ValueKind));
        Assert.Equal(JsonValueKind.Number, glMapping.GetProperty("fillDownCount").ValueKind);
        JsonShape.Element(JsonShape.Arr(glMapping, "availableGlFields"), field =>
            Assert.Equal(JsonValueKind.String, field.ValueKind));
        var tbMapping = JsonShape.Obj(mapping, "tb");
        JsonShape.HasExactKeys(
            tbMapping, "mapping", "changeMode", "sourceBatchId", "committedUtc");

        var categorySnapshot = JsonShape.Obj(data, "accountCategory");
        JsonShape.HasExactKeys(categorySnapshot, "revision", "categories");
        Assert.Equal(1, categorySnapshot.GetProperty("revision").GetInt32());
        JsonShape.Element(JsonShape.Arr(categorySnapshot, "categories"), category =>
            JsonShape.HasExactKeys(
                category, "categoryId", "label", "ordinal", "semanticRole", "isBuiltIn", "parentCategoryId"));
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
        var reports = JsonShape.Arr(data, "reports");
        Assert.True(reports.GetArrayLength() >= 1, "契約鎖 seed 應有正式報告");
        JsonShape.Element(reports, report =>
        {
            JsonShape.HasExactKeys(
                report,
                "reportId", "kind", "fileName", "fullPath", "generatedUtc", "bytes", "fileState", "sourceRef", "stale");
            JsonShape.Str(report, "fileState");
            var sourceRef = JsonShape.Obj(report, "sourceRef");
            JsonShape.HasExactKeys(
                sourceRef, "validationResultId", "prescreenResultId", "scenarioRevision", "scenarioPositions", "filterDataRevision");
        });
        var filterResultRef = data.GetProperty("filterResultRef");
        Assert.Contains(filterResultRef.ValueKind, new[] { JsonValueKind.Null, JsonValueKind.Object });
        // 2026-10-02：開案時用目前規則檢查已儲存情境的結果。這個 seed 已是目前版本，所以是 current。
        var scenarioCheck = JsonShape.Obj(data, "filterScenarioCheck");
        JsonShape.HasExactKeys(scenarioCheck, "status", "recalculatedCount", "problems");
        Assert.Equal("current", scenarioCheck.GetProperty("status").GetString());
        Assert.Equal(0, scenarioCheck.GetProperty("recalculatedCount").GetInt32());
        Assert.Equal(0, JsonShape.Arr(scenarioCheck, "problems").GetArrayLength());
        // 租約鎖心跳間隔：sqlServer 讀 app_config、缺鍵回預設；本地案件為程式常數（此 demo 為 sqlite）。
        JsonShape.Number(data, "heartbeatSeconds");

        // validationResult、prescreenResult 各為 null 或物件(巢狀 run 形狀由各自契約鎖負責)。
        JsonShape.Obj(data, "validationResult", nullable: true);
        JsonShape.Obj(data, "prescreenResult", nullable: true);
    }

    [Fact]
    public async Task ProjectList_ResponseShape_IsLocked()
    {
        // 雙來源清單雛形（2026-07-07）：頂層新增 online 塊，sqlServer 條目新增 syncStatus。
        // 用獨立 host + 單一 sqlite 案，鎖信封與本地條目形狀（syncStatus 僅 sqlServer 條目有 → sqlite 條目不含）。
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", """
            { "projectCode": "CONTRACT-1", "clientName": "E", "operatorId": "op",
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
            "projectId", "projectCode", "clientName", "periodStart", "periodEnd",
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
        // 2026-10-16 每頁須回完整查詢總數以計算頁數；第一次失敗：20261016-094952219-d2e1f780f92649c4a72a3f03c4b46396。
        JsonShape.HasExactKeys(data, "rows", "nextCursor", "totalCount");
        JsonShape.Number(data, "totalCount");
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(e, "accountNumber", "accountName", "tbAmount", "glAmount", "diff", "notInTb"));
    }

    [Fact]
    public async Task QueryVoucherBalancePage_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync("query.voucherBalancePage",
            JsonSerializer.Serialize(new { pageSize = 200 }));
        // 2026-10-16 每頁須回完整查詢總數以計算頁數；第一次失敗：20261016-094952219-d2e1f780f92649c4a72a3f03c4b46396。
        JsonShape.HasExactKeys(data, "rows", "nextCursor", "totalCount");
        JsonShape.Number(data, "totalCount");
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(e, "voucherNumber", "debit", "credit", "diff"));
    }

    [Fact]
    public async Task QueryBlankRecordsPage_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync("query.blankRecordsPage",
            JsonSerializer.Serialize(new { category = "nullDescription", pageSize = 200 }));
        // 2026-10-16 每頁須回完整查詢總數以計算頁數；第一次失敗：20261016-094952219-d2e1f780f92649c4a72a3f03c4b46396。
        JsonShape.HasExactKeys(data, "rows", "nextCursor", "totalCount");
        JsonShape.Number(data, "totalCount");
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(e, "voucherNumber", "accountNumber", "glDate", "description"));
    }

    [Fact]
    public async Task QueryFilterHitsPage_ResponseShape_IsLocked()
    {
        // 先 commit 一個 demo 母體確有命中的情境(nonAuthorizedPreparer),保證 rows 非空、元素鍵集合確實鎖到。
        await fixture.Host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "非授權編製人員", rationale = "contract",
                groups = new[] { new { join = "and", conditions = new[] {
                    // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，改用示範資料同樣整張傳票兩列皆命中的非授權編製人員。
                    new { join = "and", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" } } } } } }
        }));

        var data = await fixture.Host.DispatchAsync("query.filterHitsPage",
            JsonSerializer.Serialize(new { scenarioPosition = 1, pageSize = 200 }));
        // 2026-10-16 每頁須回完整查詢總數以計算頁數；第一次失敗：20261016-094952219-d2e1f780f92649c4a72a3f03c4b46396。
        JsonShape.HasExactKeys(data, "columns", "rows", "nextCursor", "totalCount");
        JsonShape.Number(data, "totalCount");
        var columns = JsonShape.Arr(data, "columns");
        Assert.Equal(
            ["voucherNumber", "lineItem", "glDate", "accountNumber", "accountName", "amount", "drCr", "description"],
            columns.EnumerateArray().Select(column => column.GetProperty("key").GetString()!).ToArray());
        JsonShape.Element(columns, column =>
        {
            // 固定欄多帶 sortable（第一次失敗：收據 20260907-082210786）。
            JsonShape.HasExactKeys(column, "key", "label", "valueType", "isCustom", "sortable");
            JsonShape.Str(column, "key");
            JsonShape.Str(column, "label");
            JsonShape.Str(column, "valueType");
            Assert.False(column.GetProperty("isCustom").GetBoolean());
            Assert.True(column.GetProperty("sortable").GetBoolean());
        });
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(
                e, "voucherNumber", "lineItem", "glDate", "accountNumber",
                "accountName", "amount", "drCr", "description", "customValues"));
    }

    [Fact]
    public async Task QueryHighRiskSummaryScenarios_ResponseShape_IsLocked()
    {
        // 先 commit 一個 demo 母體確有命中的情境(nonAuthorizedPreparer),保證 scenarios 非空、元素鍵集合確實鎖到。
        await fixture.Host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "非授權編製人員", rationale = "contract",
                groups = new[] { new { join = "and", conditions = new[] {
                    // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，改用示範資料同樣整張傳票兩列皆命中的非授權編製人員。
                    new { join = "and", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" } } } } } }
        }));

        var data = await fixture.Host.DispatchAsync("query.highRiskSummaryScenarios");
        JsonShape.HasExactKeys(data, "scenarios");
        var scenarios = JsonShape.Arr(data, "scenarios");
        Assert.True(scenarios.GetArrayLength() >= 1, "已 commit 情境,scenarios 應非空");
        JsonShape.Element(scenarios, e =>
        {
            // 每個情境另回 summaryRowCount 與 countsWholeVoucherRows，原因同 filter.preview
            // （第一次失敗：Public 收據 20261016-051726257-ff7a19590e9c4c30a7f61b5bec0bc527）。
            JsonShape.HasExactKeys(e, "position", "name", "voucherHitCount", "rowHitCount", "summaryRowCount", "countsWholeVoucherRows");
            JsonShape.Number(e, "position");
            JsonShape.Str(e, "name");
            JsonShape.Number(e, "voucherHitCount");
            JsonShape.Number(e, "rowHitCount");
            JsonShape.Number(e, "summaryRowCount");
            // 非授權編製人員不是只用「未預期借貸組合」的情境，分錄筆數就是直接命中列數。
            Assert.Equal(JsonValueKind.False, e.GetProperty("countsWholeVoucherRows").ValueKind);
            Assert.Equal(e.GetProperty("rowHitCount").GetInt64(), e.GetProperty("summaryRowCount").GetInt64());
        });
    }

    [Fact]
    public async Task QueryHighRiskSummaryVoucherPage_ResponseShape_IsLocked()
    {
        // 先 commit 一個 demo 母體確有命中的情境(nonAuthorizedPreparer),保證 rows 非空、元素鍵集合確實鎖到。
        await fixture.Host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "非授權編製人員", rationale = "contract",
                groups = new[] { new { join = "and", conditions = new[] {
                    // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，改用示範資料同樣整張傳票兩列皆命中的非授權編製人員。
                    new { join = "and", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" } } } } } }
        }));

        var data = await fixture.Host.DispatchAsync("query.highRiskSummaryVoucherPage",
            JsonSerializer.Serialize(new { pageSize = 200 }));
        // 2026-10-16 每頁須回完整查詢總數以計算頁數；第一次失敗：20261016-094952219-d2e1f780f92649c4a72a3f03c4b46396。
        JsonShape.HasExactKeys(data, "rows", "nextCursor", "totalCount");
        JsonShape.Number(data, "totalCount");
        var rows = JsonShape.Arr(data, "rows");
        Assert.True(rows.GetArrayLength() >= 1, "已 commit 命中情境,rows 應非空");
        JsonShape.Element(rows, e =>
        {
            JsonShape.HasExactKeys(
                e, "voucherNumber", "glDate", "createdBy", "voucherTotal", "matchedPositions");
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
        // 2026-10-16 每頁須回完整查詢總數以計算頁數；第一次失敗：20261016-094952219-d2e1f780f92649c4a72a3f03c4b46396。
        JsonShape.HasExactKeys(data, "columns", "rows", "nextCursor", "totalCount");
        JsonShape.Number(data, "totalCount");
        var columns = JsonShape.Arr(data, "columns");
        Assert.Equal(
            ["voucherNumber", "accountNumber", "accountName", "debit", "credit", "glDate",
                "approvalDate", "createdBy", "approvedBy", "description"],
            columns.EnumerateArray().Select(column => column.GetProperty("key").GetString()!).ToArray());
        JsonShape.Element(columns, column =>
        {
            // 固定欄多帶 sortable（第一次失敗：收據 20260907-082210786）。
            JsonShape.HasExactKeys(column, "key", "label", "valueType", "isCustom", "sortable");
            JsonShape.Str(column, "key");
            JsonShape.Str(column, "label");
            JsonShape.Str(column, "valueType");
            Assert.False(column.GetProperty("isCustom").GetBoolean());
            Assert.True(column.GetProperty("sortable").GetBoolean());
        });
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(
                e, "voucherNumber", "accountNumber", "accountName", "debit", "credit",
                "glDate", "approvalDate", "createdBy", "approvedBy", "description", "customValues"));
    }

    [Fact]
    public async Task ExportWorkingPaperStream_ResponseShape_IsLocked()
    {
        // export 會寫實體檔且 materialize filter_hit,故用獨立 host(不污染共用 fixture)。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var filter = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "契約情境", rationale = "contract",
                groups = new[] { new { join = "and", conditions = new[] {
                    new { join = "and", type = "drCrOnly", drCr = "debit" } } } } } }
        }));
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString();
        var scenarioRevision = filter.GetProperty("resultRef").GetProperty("revision").GetString();
        await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationResultId,
                revision = scenarioRevision
            }));
        var data = await host.DispatchAsync(
            "export.workingPaperStream", JsonSerializer.Serialize(new
            {
                validationResultId,
                scenarioRevision,
                scenarioPositions = new[] { 1 }
            }));

            // 2026-09-23：已記入 manifest 的加法契約，分開所選底稿成功、全案結果狀態及清單刷新提醒。
            // 原欄位與產物來源檢查保留；此處選全部情境且清單可讀，兩個新欄位有固定答案。
            // 產生報告寫不進操作紀錄時回報成功並提醒（裁定 2026-10-19-20），回應多了 warnings；這裡寫得進操作紀錄，
            // 所以是空陣列（加上這個欄位前的失敗收據 20261019-153611395-da8287ebb31a47dbac7419f7421eeae2）。
            JsonShape.HasExactKeys(data, "ok", "publishedReport", "sheetStats", "reports", "filterResultsCurrent", "reportWarning", "warnings");
            Assert.True(data.GetProperty("filterResultsCurrent").GetBoolean());
            Assert.Equal(JsonValueKind.Null, data.GetProperty("reportWarning").ValueKind);
            Assert.Empty(JsonShape.Arr(data, "warnings").EnumerateArray());
            Assert.Equal(JsonValueKind.True, data.GetProperty("ok").ValueKind);
            var report = JsonShape.Obj(data, "publishedReport");
            JsonShape.HasExactKeys(report,
                "reportId", "kind", "fileName", "fullPath", "generatedUtc", "bytes", "fileState", "sourceRef", "stale");
            JsonShape.Str(report, "fileState");
            JsonShape.Str(report, "reportId");
            JsonShape.Str(report, "kind");
            JsonShape.Str(report, "fileName");
            JsonShape.Number(report, "bytes");
            var catalog = JsonShape.Arr(data, "reports");
            Assert.Equal(new[] { "criteriaSelectionReport", "workingPaper" }, catalog.EnumerateArray()
                .Select(item => item.GetProperty("kind").GetString()).Order(StringComparer.Ordinal));
            JsonShape.Element(catalog, item => JsonShape.HasExactKeys(item,
                "reportId", "kind", "fileName", "fullPath", "generatedUtc", "bytes", "fileState", "sourceRef", "stale"));
            var published = Assert.Single(catalog.EnumerateArray(), item =>
                item.GetProperty("reportId").GetString() == report.GetProperty("reportId").GetString());
            Assert.Equal(report.GetRawText(), published.GetRawText());
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
        // 範本是工作檔：回傳完整路徑讓前端顯示，不再回報告物件、不進報告清單。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var data = await host.DispatchAsync(
            "export.accountMappingTemplate",
            JsonSerializer.Serialize(new
            {
                resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()
            }));

        JsonShape.HasExactKeys(data, "ok", "filePath", "fileName", "rowCount", "validationResultId", "disposition");
        Assert.Equal(JsonValueKind.True, data.GetProperty("ok").ValueKind);
        JsonShape.Str(data, "filePath");
        JsonShape.Str(data, "fileName");
        JsonShape.Number(data, "rowCount");
        JsonShape.Str(data, "validationResultId");
        Assert.Equal("created", data.GetProperty("disposition").GetString());
        Assert.Equal(validation.GetProperty("resultRef").GetProperty("resultId").GetString(),
            data.GetProperty("validationResultId").GetString());
        Assert.True(Path.IsPathFullyQualified(data.GetProperty("filePath").GetString()!));
        Assert.Equal(
            Path.GetFileName(data.GetProperty("filePath").GetString()!),
            data.GetProperty("fileName").GetString());
        var original = await File.ReadAllBytesAsync(data.GetProperty("filePath").GetString()!);
        var kept = await host.DispatchAsync("export.accountMappingTemplate", JsonSerializer.Serialize(new
        {
            resultId = data.GetProperty("validationResultId").GetString(),
            onlyIfMissing = true
        }));
        JsonShape.HasExactKeys(kept, "ok", "filePath", "fileName", "rowCount", "validationResultId", "disposition");
        Assert.Equal(JsonValueKind.True, kept.GetProperty("ok").ValueKind);
        Assert.Equal("kept", kept.GetProperty("disposition").GetString());
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("rowCount").ValueKind);
        foreach (var field in new[] { "filePath", "fileName", "validationResultId" })
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
            "export.validationReports",
            JsonSerializer.Serialize(new
            {
                resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()
            }));
        var data = await host.DispatchAsync(
            "host.openFolder",
            JsonSerializer.Serialize(new
            {
                reportId = exported.GetProperty("publishedReports")[0].GetProperty("reportId").GetString()
            }));

        JsonShape.HasExactKeys(data, "ok");
        Assert.Equal(JsonValueKind.True, data.GetProperty("ok").ValueKind);
    }

    [Fact]
    public async Task QueryHighRiskSummaryRowPage_ResponseShape_IsLocked()
    {
        // 先 commit 一個 demo 母體確有命中的情境(nonAuthorizedPreparer),保證命中傳票存在、
        // 其所有行被列出、元素鍵集合(含 matchedPositions)確實鎖到。
        await fixture.Host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "非授權編製人員", rationale = "contract",
                groups = new[] { new { join = "and", conditions = new[] {
                    // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，改用示範資料同樣整張傳票兩列皆命中的非授權編製人員。
                    new { join = "and", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" } } } } } }
        }));

        var data = await fixture.Host.DispatchAsync("query.highRiskSummaryRowPage",
            JsonSerializer.Serialize(new { pageSize = 200 }));
        // 2026-10-16 每頁須回完整查詢總數以計算頁數；第一次失敗：20261016-094952219-d2e1f780f92649c4a72a3f03c4b46396。
        JsonShape.HasExactKeys(data, "rows", "nextCursor", "totalCount");
        JsonShape.Number(data, "totalCount");
        var rows = JsonShape.Arr(data, "rows");
        Assert.True(rows.GetArrayLength() >= 1, "已 commit 命中情境,命中傳票之所有行 rows 應非空");
        JsonShape.Element(rows, e =>
        {
            JsonShape.HasExactKeys(
                e, "voucherNumber", "lineItem", "glDate", "approvalDate", "createdBy",
                "approvedBy", "accountNumber", "accountName", "amount", "matchedPositions", "description");
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
