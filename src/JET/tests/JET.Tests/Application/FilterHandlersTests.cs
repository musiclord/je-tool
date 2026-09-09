using System.Globalization;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// filter.preview / filter.commit 黑箱測試。
/// preview 計數一律與獨立參數化 SQL recount 比對；prescreen 條件與
/// prescreen.run 的相等是共用述詞 seam 的行為證明。
/// </summary>
public sealed class FilterHandlersTests(DemoProjectFixture fixture) : IClassFixture<DemoProjectFixture>
{
    private static string PreviewPayload(object scenario) =>
        JsonSerializer.Serialize(new
        {
            populationScope = GlPopulationScopeValues.AuditPeriod,
            scenario
        });

    private static string PreviewPayloadWithOmittedScope(object scenario) =>
        JsonSerializer.Serialize(new { scenario });

    private static string PreviewPayload(string populationScope, object scenario) =>
        JsonSerializer.Serialize(new { populationScope, scenario });

    private static object ValidScenario(params object[] rules) => new
    {
        name = "測試情境",
        rationale = "測試動機",
        groups = new object[] { new { join = "AND", rules } }
    };

    /* ---- 驗證與安全 ------------------------------------------------------ */

    [Fact]
    public async Task FilterPreview_MissingName_PreviewsButCannotSave()
    {
        var payload = PreviewPayload(new
        {
            name = "",
            rationale = "動機",
            groups = new object[]
            {
                new { join = "AND", rules = new object[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } } }
            }
        });

        var preview = await fixture.Host.DispatchAsync("filter.preview", payload);
        Assert.True(preview.GetProperty("scenario").GetProperty("count").GetInt64() > 0);
        using var definition = JsonDocument.Parse(payload);
        var error = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync("filter.commit",
            JsonSerializer.Serialize(new { scenarios = new[] { definition.RootElement.GetProperty("scenario") } })));
        Assert.Equal("invalid_scenario", error.Code);
    }

    [Fact]
    public async Task FilterPreview_UnknownField_ThrowsInvalidScenario()
    {
        // 注入形欄位字串必須被白名單擋下。
        var payload = PreviewPayload(ValidScenario(
            new { join = "AND", type = "text", field = "document_number; DROP TABLE target_gl_entry", keywords = "x", mode = "contains" }));

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", payload));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_InvalidRule_ReportsGroupAndRulePosition()
    {
        // 第二組第一條沒填分類，錯誤細節要指到那一條（從 1 起算），前端才能把該列標紅。
        var payload = PreviewPayload(new
        {
            name = "測試情境",
            rationale = "動機",
            groups = new object[]
            {
                new { join = "AND", rules = new object[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } } },
                new { join = "AND", rules = new object[] { new { join = "AND", type = "accountSide", drCr = "credit", categoryMode = "is", categoryIds = Array.Empty<string>() } } }
            }
        });

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", payload));

        Assert.Equal("invalid_scenario", ex.Code);
        Assert.NotNull(ex.Details);
        var positioned = Assert.Single(ex.Details!, detail => detail.Group == 2 && detail.Rule == 1);
        Assert.False(string.IsNullOrWhiteSpace(positioned.Message));
        Assert.DoesNotContain("條件群組", positioned.Message, StringComparison.Ordinal);
        Assert.Contains(positioned.Message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilterPreview_CategoryIdArrays_MatchLegacyScalarForTheSameCategory()
    {
        // scalar 是「單元素集合」的相容輸入：同一分類的兩種寫法必須得到同一個命中母體。
        var scalar = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(new
        {
            join = "AND",
            type = "accountPair",
            pairMode = "exact",
            debitCategory = "Receivables",
            creditCategory = "Revenue"
        })));
        var ids = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(new
        {
            join = "AND",
            type = "accountPair",
            pairMode = "exact",
            debitCategoryIds = new[] { AccountTaxonomyBuiltIns.ReceivablesId },
            creditCategoryIds = new[] { AccountTaxonomyBuiltIns.RevenueId }
        })));

        var scalarCount = scalar.GetProperty("scenario").GetProperty("count").GetInt64();
        Assert.True(scalarCount > 0, "demo 資料應含賒銷（借應收／貸收入）傳票");
        Assert.Equal(scalarCount, ids.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_CategoryIdArrays_TakePrecedenceOverLegacyScalars()
    {
        // 同時帶陣列與 scalar 時（migration 產生的舊定義即為此形狀），一律以陣列為準。
        var idsOnly = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(new
        {
            join = "AND",
            type = "accountPair",
            pairMode = "exact",
            debitCategoryIds = new[] { AccountTaxonomyBuiltIns.ReceivablesId },
            creditCategoryIds = new[] { AccountTaxonomyBuiltIns.RevenueId }
        })));
        var mixed = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(new
        {
            join = "AND",
            type = "accountPair",
            pairMode = "exact",
            debitCategory = "Cash",
            creditCategory = "Others",
            debitCategoryIds = new[] { AccountTaxonomyBuiltIns.ReceivablesId },
            creditCategoryIds = new[] { AccountTaxonomyBuiltIns.RevenueId }
        })));

        Assert.Equal(
            idsOnly.GetProperty("scenario").GetProperty("count").GetInt64(),
            mixed.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_MultiSelectCategoryIds_CoverTheUnionWithoutDuplicatingRows()
    {
        long CountOf(JsonElement response) =>
            response.GetProperty("scenario").GetProperty("count").GetInt64();

        var receivables = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(new
        {
            join = "AND",
            type = "accountPair",
            pairMode = "exact",
            debitCategoryIds = new[] { AccountTaxonomyBuiltIns.ReceivablesId },
            creditCategoryIds = new[] { AccountTaxonomyBuiltIns.RevenueId }
        })));
        var others = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(new
        {
            join = "AND",
            type = "accountPair",
            pairMode = "exact",
            debitCategoryIds = new[] { AccountTaxonomyBuiltIns.OthersId },
            creditCategoryIds = new[] { AccountTaxonomyBuiltIns.RevenueId }
        })));
        var both = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(new
        {
            join = "AND",
            type = "accountPair",
            pairMode = "exact",
            debitCategoryIds = new[]
            {
                AccountTaxonomyBuiltIns.OthersId,
                AccountTaxonomyBuiltIns.ReceivablesId,
                AccountTaxonomyBuiltIns.OthersId
            },
            creditCategoryIds = new[] { AccountTaxonomyBuiltIns.RevenueId, AccountTaxonomyBuiltIns.RevenueId }
        })));

        Assert.True(CountOf(receivables) > 0 && CountOf(others) > 0, "demo 資料應同時含賒銷與費用類對收入的傳票");
        // 借方兩類的傳票互斥，因此多選正好是兩個單選的聯集；重複身分不會放大命中列。
        Assert.Equal(CountOf(receivables) + CountOf(others), CountOf(both));
    }

    [Fact]
    public async Task FilterPreview_EmptyCategoryIdArray_ThrowsInvalidScenarioWithoutMutation()
    {
        var before = await LoadFilterStateAsync();
        var payload = PreviewPayload(ValidScenario(new
        {
            join = "AND",
            type = "accountPair",
            pairMode = "exact",
            debitCategoryIds = Array.Empty<string>(),
            creditCategoryIds = new[] { AccountTaxonomyBuiltIns.RevenueId }
        }));

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", payload));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Equal(before, await LoadFilterStateAsync());
    }

    [Fact]
    public async Task FilterPreview_UnknownCategoryId_ThrowsInvalidScenario()
    {
        var payload = PreviewPayload(ValidScenario(new
        {
            join = "AND",
            type = "accountPair",
            pairMode = "exact",
            debitCategoryIds = new[] { "custom.0123456789abcdef0123456789abcdef" },
            creditCategoryIds = new[] { AccountTaxonomyBuiltIns.RevenueId }
        }));

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", payload));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
    }

    [Fact]
    public async Task FilterCommit_CategoryIdArrays_ArePersistedVerbatim()
    {
        var scenario = ValidScenario(new
        {
            join = "AND",
            type = "specialAccountCategoryPair",
            pairMode = "drAndCr",
            debitCategoryIds = new[] { AccountTaxonomyBuiltIns.ReceivablesId },
            creditCategoryIds = new[] { AccountTaxonomyBuiltIns.RevenueId }
        });

        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        var scenarios = loaded.GetProperty("filterScenarios").GetRawText();

        Assert.Contains($"\"debitCategoryIds\":[\"{AccountTaxonomyBuiltIns.ReceivablesId}\"]", scenarios, StringComparison.Ordinal);
        Assert.Contains($"\"creditCategoryIds\":[\"{AccountTaxonomyBuiltIns.RevenueId}\"]", scenarios, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilterPreview_AccountPairWithoutAccountMapping_ThrowsInvalidScenario()
    {
        // 閘控:科目配對未匯入時 accountPair 述詞必須被擋下(invalid_scenario)。
        // 自建 host 並關閉科目配對匯入以維持「前置不足」原意(共用 fixture 預設已匯入)。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);

        var payload = PreviewPayload(ValidScenario(
            new { join = "AND", type = "accountPair", debitCategory = "Cash", creditCategory = "Revenue" }));

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("filter.preview", payload));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_NonAuthorizedPreparerWithoutAuthorizedList_ThrowsInvalidScenario()
    {
        // 非授權編製人員的 filter 端閘控（鏡射 unexpectedAccountPair）：授權編製人員清單未匯入時，
        // 空名單會讓 NOT IN 述詞反轉成全命中，validator 必須先擋下（invalid_scenario）。
        // 自建 host 並關閉授權清單匯入以維持「未匯入」原意(共用 fixture 預設已匯入)。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, importAuthorizedPreparer: false);

        var payload = PreviewPayload(ValidScenario(
            new { join = "AND", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" }));

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("filter.preview", payload));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterCommit_NonAuthorizedPreparerWithoutAuthorizedList_ThrowsInvalidScenario()
    {
        // commit 端同樣閘控（與 preview 共用 FilterCommitShared.EnsureValid）。
        // 自建 host 並關閉授權清單匯入以維持「未匯入」原意(共用 fixture 預設已匯入)。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, importAuthorizedPreparer: false);

        var scenarios = new[]
        {
            ValidScenario(new { join = "AND", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" })
        };

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "filter.commit", JsonSerializer.Serialize(new { scenarios })));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_NonAuthorizedPreparerWithAuthorizedList_MatchesRecount()
    {
        // 名單匯入後放行：filter 命中數須等於同述詞的獨立 recount（正常路徑仍對）。
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        // demo 編製者五人；只授權三人 → 另兩人為非授權命中。
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

            var preview = await host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" })));

            // EXISTS 自保前綴使名單非空時不改變命中集合 → 等於原始 NOT IN recount。
            var recount = await DemoProjectPipeline.QueryScalarAsync(host, context.ProjectId,
                "SELECT COUNT(*) FROM target_gl_entry g WHERE g.post_date BETWEEN '2025-01-01' AND '2025-12-31' "
                + "AND g.created_by IS NOT NULL "
                + "AND TRIM(g.created_by) <> '' "
                + "AND TRIM(g.created_by) NOT IN (SELECT name FROM target_authorized_preparer);");

            Assert.True(recount > 0, "只授權部分編製者 → 應有非授權命中");
            Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
        }
        finally
        {
            TestWorkbookBuilder.Delete(listPath);
        }
    }

    [Fact]
    public async Task FilterPreview_SummaryPrescreenKey_ThrowsInvalidScenario()
    {
        var payload = PreviewPayload(ValidScenario(
            new { join = "AND", type = "prescreen", prescreenKey = "creatorSummary" }));

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", payload));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    /* ---- 條件型別語意（== 獨立 recount） ---------------------------------- */

    [Fact]
    public async Task FilterPreview_OmittedScope_DefaultsToAuditPeriodAndMatchesPrescreenPopulation()
    {
        var prescreen = await fixture.Host.DispatchAsync("prescreen.run");
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayloadWithOmittedScope(ValidScenario(
            new { join = "AND", type = "prescreen", prescreenKey = "suspiciousKeywords" })));

        var prescreenCount = prescreen.GetProperty("suspiciousKeywords").GetProperty("count").GetInt64();
        var previewCount = preview.GetProperty("scenario").GetProperty("count").GetInt64();

        Assert.Equal("auditPeriod", preview.GetProperty("scenario").GetProperty("populationScope").GetString());
        Assert.Equal(prescreenCount, previewCount);
    }

    [Fact]
    public async Task FilterPreview_ExplicitAllProjected_ThrowsInvalidPayload()
    {
        var exception = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview",
            PreviewPayload(
                "allProjected",
                ValidScenario(new { join = "AND", type = "prescreen", prescreenKey = "suspiciousKeywords" }))));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("AuditPeriod")]
    [InlineData(" auditPeriod ")]
    public async Task FilterPreview_UnknownPopulationScope_ThrowsInvalidPayload(string populationScope)
    {
        var exception = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview",
            PreviewPayload(populationScope, ValidScenario(
                new { join = "AND", type = "drCrOnly", drCr = "debit" }))));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
    }

    [Fact]
    public async Task FilterPreview_NonStringPopulationScope_ThrowsInvalidPayload()
    {
        var exception = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview",
            JsonSerializer.Serialize(new
            {
                populationScope = 1,
                scenario = ValidScenario(new { join = "AND", type = "drCrOnly", drCr = "debit" })
            })));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
    }

    [Fact]
    public async Task FilterCommit_UnknownPopulationScope_ThrowsInvalidPayloadWithoutReplacingRevision()
    {
        var before = await fixture.Host.DispatchAsync("project.load", JsonSerializer.Serialize(new
        {
            projectId = fixture.ProjectId
        }));
        var previousRef = before.GetProperty("filterResultRef").ValueKind == JsonValueKind.Null
            ? null
            : before.GetProperty("filterResultRef").GetRawText();

        var exception = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                populationScope = "unknown",
                scenarios = new[]
                {
                    ValidScenario(new { join = "AND", type = "drCrOnly", drCr = "debit" })
                }
            })));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
        var after = await fixture.Host.DispatchAsync("project.load", JsonSerializer.Serialize(new
        {
            projectId = fixture.ProjectId
        }));
        var currentRef = after.GetProperty("filterResultRef").ValueKind == JsonValueKind.Null
            ? null
            : after.GetProperty("filterResultRef").GetRawText();
        Assert.Equal(previousRef, currentRef);
    }

    [Fact]
    public async Task FilterCommit_ExplicitAllProjected_ThrowsInvalidPayloadWithoutReplacingRevision()
    {
        var before = await fixture.Host.DispatchAsync("project.load", JsonSerializer.Serialize(new
        {
            projectId = fixture.ProjectId
        }));
        var previousScenarios = before.GetProperty("filterScenarios").GetRawText();
        var previousRef = before.GetProperty("filterResultRef").GetRawText();

        var exception = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                populationScope = "allProjected",
                scenarios = new[]
                {
                    ValidScenario(new { join = "AND", type = "drCrOnly", drCr = "debit" })
                }
            })));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
        var after = await fixture.Host.DispatchAsync("project.load", JsonSerializer.Serialize(new
        {
            projectId = fixture.ProjectId
        }));
        Assert.Equal(previousScenarios, after.GetProperty("filterScenarios").GetRawText());
        Assert.Equal(previousRef, after.GetProperty("filterResultRef").GetRawText());
    }

    [Fact]
    public async Task FilterPreview_TextContains_MatchesRecount()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "text", field = "description", keywords = "調整", mode = "contains" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE post_date BETWEEN '2025-01-01' AND '2025-12-31' "
            + "AND document_description LIKE '%調整%';");

        Assert.True(recount > 0);
        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_TextNotContains_TreatsNullAsEmpty()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "調整分錄", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", null, "100.00", 0)
            .AddRow("JV-002", "2025-03-06", "1101", "現金", "一般進貨", "80.00", 1)
            .AddRow("JV-002", "2025-03-06", "4101", "銷貨收入", "一般進貨", "80.00", 0));

        var preview = await host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "text", field = "description", keywords = "調整", mode = "notContains" })));

        // NULL 摘要視為空字串 → notContains 成立；4 列中只排除「調整分錄」1 列。
        Assert.Equal(3, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_NumRangeFiltersOnAbsoluteScaledAmount()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "numRange", field = "amount", from = "500000", to = "" })));

        // ABS：貸方（負值）列同樣納入。500000 × 10000 = 5e9。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE post_date BETWEEN '2025-01-01' AND '2025-12-31' "
            + "AND ABS(amount_scaled) >= 5000000000;");

        Assert.True(recount > 0);
        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_DateRangeBoundsPostDate()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "dateRange", field = "postDate", from = "2025-01-01", to = "2025-01-31" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE post_date >= '2025-01-01' AND post_date <= '2025-01-31';");

        Assert.True(recount > 0);
        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_DuckDb_AuditPeriodIncludesInPeriodAndExcludesOutOfPeriodMatches()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("IN-001", "2025-03-05", "1101", "現金", "期間測試命中", "100.00", 1)
                .AddRow("IN-001", "2025-03-05", "4101", "收入", "期間測試命中", "100.00", 0)
                .AddRow("OUT-001", "2024-03-05", "1101", "現金", "期間測試命中", "80.00", 1)
                .AddRow("OUT-001", "2024-03-05", "4101", "收入", "期間測試命中", "80.00", 0),
            databaseProvider: "duckdb");

        var filterPreview = await host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new
            {
                join = "AND",
                type = "text",
                field = "description",
                keywords = "期間測試命中",
                mode = "contains"
            })));

        var scenario = filterPreview.GetProperty("scenario");
        Assert.Equal(2, scenario.GetProperty("count").GetInt64());
        Assert.Equal(1, scenario.GetProperty("voucherCount").GetInt64());
        Assert.Equal(2, scenario.GetProperty("previewRows").GetArrayLength());
        Assert.All(
            scenario.GetProperty("previewRows").EnumerateArray(),
            row => Assert.Equal("IN-001", row.GetProperty("documentNumber").GetString()));
    }

    [Fact]
    public async Task FilterPreview_DrCrOnlyDebit_MatchesRecount()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "drCrOnly", drCr = "debit" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE post_date BETWEEN '2025-01-01' AND '2025-12-31' "
            + "AND dr_cr = 'DEBIT';");

        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_ManualOnly_MatchesRecount()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "manualAuto", isManual = "true" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE post_date BETWEEN '2025-01-01' AND '2025-12-31' "
            + "AND is_manual = 1;");

        Assert.True(recount > 0);
        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_OrJoinFoldsLeftToRight()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "prescreen", prescreenKey = "suspiciousKeywords" },
            new { join = "OR", type = "prescreen", prescreenKey = "holidayPosting" })));

        // 獨立 recount：demo fixture 的摘要關鍵字命中 ⟺ 摘要為 5 個關鍵字種子之一。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            """
            SELECT COUNT(*) FROM target_gl_entry g
            WHERE g.post_date BETWEEN '2025-01-01' AND '2025-12-31'
              AND (g.document_description IN ('調整分錄','沖銷暫付款','迴轉前期應計','重分類科目','錯誤更正調整')
               OR EXISTS (
                   SELECT 1 FROM staging_calendar_raw_day d
                   WHERE d.day_type = 'holiday' AND d.date = g.post_date));
            """);

        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    /* ---- join 結合方式：正規化與 fail-loud（manifest 結合律段，2026-07-06 收緊） ---- */

    private const string SuspiciousOrHolidayRecountSql =
        """
        SELECT COUNT(*) FROM target_gl_entry g
        WHERE g.post_date BETWEEN '2025-01-01' AND '2025-12-31'
          AND (g.document_description IN ('調整分錄','沖銷暫付款','迴轉前期應計','重分類科目','錯誤更正調整')
           OR EXISTS (
               SELECT 1 FROM staging_calendar_raw_day d
               WHERE d.day_type = 'holiday' AND d.date = g.post_date));
        """;

    private const string SuspiciousAndHolidayRecountSql =
        """
        SELECT COUNT(*) FROM target_gl_entry g
        WHERE g.post_date BETWEEN '2025-01-01' AND '2025-12-31'
          AND g.document_description IN ('調整分錄','沖銷暫付款','迴轉前期應計','重分類科目','錯誤更正調整')
          AND EXISTS (
              SELECT 1 FROM staging_calendar_raw_day d
              WHERE d.day_type = 'holiday' AND d.date = g.post_date);
        """;

    // 等價分割：join 的合法值不分大小寫（"or"/"Or"/"oR" 都屬 OR 等價類）。
    // oracle：獨立參數化 SQL recount（OR 語意）——證明 normalize 後語意正確，不只是不報錯。
    [Theory]
    [InlineData("or")]
    [InlineData("Or")]
    [InlineData("oR")]
    public async Task FilterPreview_LowercaseOrJoinVariants_NormalizeToOrSemantics(string joinVariant)
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "prescreen", prescreenKey = "suspiciousKeywords" },
            new { join = joinVariant, type = "prescreen", prescreenKey = "holidayPosting" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId, SuspiciousOrHolidayRecountSql);

        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    // 等價分割：AND 等價類的大小寫變體。oracle：獨立 recount（AND 語意）。
    // 前提防呆：本 fixture 的 AND/OR 兩種結合命中數必須不同，否則本測試與
    // OR 變體測試都失去鑑別力（等於什麼都沒驗）。
    [Theory]
    [InlineData("and")]
    [InlineData("And")]
    public async Task FilterPreview_LowercaseAndJoinVariants_NormalizeToAndSemantics(string joinVariant)
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "prescreen", prescreenKey = "suspiciousKeywords" },
            new { join = joinVariant, type = "prescreen", prescreenKey = "holidayPosting" })));

        var andRecount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId, SuspiciousAndHolidayRecountSql);
        var orRecount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId, SuspiciousOrHolidayRecountSql);

        Assert.NotEqual(orRecount, andRecount);
        Assert.Equal(andRecount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    // fail-loud（rule 層）：join 有值但不是 AND/OR → invalid_scenario，訊息指明收到的值。
    // 刻意放在第一條規則（左折疊時會被忽略的位置）——契約明訂被忽略的位置同樣要驗。
    [Theory]
    [InlineData("XOR")]
    [InlineData("ANDD")]
    [InlineData("聯集")]
    public async Task FilterPreview_UnknownRuleJoin_ThrowsInvalidScenario(string badJoin)
    {
        var payload = PreviewPayload(ValidScenario(
            new { join = badJoin, type = "drCrOnly", drCr = "debit" }));

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", payload));

        Assert.Equal("invalid_scenario", ex.Code);
        Assert.Contains($"join 結合方式「{badJoin}」無效", ex.Message, StringComparison.Ordinal);
    }

    // fail-loud（group 層）：第一個群組的 join 在左折疊時被忽略，但未知值仍須擋下。
    [Theory]
    [InlineData("XOR")]
    [InlineData("聯集")]
    public async Task FilterPreview_UnknownGroupJoin_ThrowsInvalidScenario(string badJoin)
    {
        var payload = PreviewPayload(new
        {
            name = "測試情境",
            rationale = "測試動機",
            groups = new object[]
            {
                new { join = badJoin, rules = new object[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } } }
            }
        });

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", payload));

        Assert.Equal("invalid_scenario", ex.Code);
        Assert.Contains($"join 結合方式「{badJoin}」無效", ex.Message, StringComparison.Ordinal);
    }

    // 向後相容（manifest 結合律段）：join 缺欄 / null / 空白 → 維持預設 AND，不報錯。
    // 語意證明：第二條規則的 join 缺席時，命中數等於 AND 語意的獨立 recount。
    [Theory]
    [InlineData("")]
    [InlineData("\"join\": null, ")]
    [InlineData("\"join\": \"  \", ")]
    public async Task FilterPreview_AbsentJoin_DefaultsToAnd(string joinFragment)
    {
        var payload = """
            {"scenario":{"name":"測試情境","rationale":"測試動機","groups":[{"join":"AND","rules":[
              {"join":"AND","type":"prescreen","prescreenKey":"suspiciousKeywords"},
              {JOIN_FRAGMENT"type":"prescreen","prescreenKey":"holidayPosting"}]}]}}
            """.Replace("JOIN_FRAGMENT", joinFragment);

        var preview = await fixture.Host.DispatchAsync("filter.preview", payload);

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId, SuspiciousAndHolidayRecountSql);

        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    /* ---- demo 情境與預覽上限 ---------------------------------------------- */

    [Fact]
    public async Task FilterPreview_DemoScenario_CountsMatchSetBasedRecount()
    {
        var demo = await DemoProjectPipeline.DispatchDemoFixtureAsync("project.loadDemo");
        var scenarioJson = demo.GetProperty("demoScenario").GetRawText();

        var preview = await fixture.Host.DispatchAsync(
            "filter.preview", $"{{\"populationScope\":\"auditPeriod\",\"scenario\":{scenarioJson}}}");

        const string demoWhere =
            """
            (g.document_description IN ('調整分錄','沖銷暫付款','迴轉前期應計','重分類科目','錯誤更正調整')
             OR EXISTS (
                 SELECT 1 FROM staging_calendar_raw_day d
                 WHERE d.day_type = 'holiday' AND d.date = g.post_date))
            AND ABS(g.amount_scaled) >= 100000000
            """;

        var rowRecount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            $"SELECT COUNT(*) FROM target_gl_entry g WHERE g.post_date BETWEEN '2025-01-01' AND '2025-12-31' AND {demoWhere};");
        var voucherRecount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            $"SELECT COUNT(DISTINCT g.document_number) FROM target_gl_entry g WHERE g.post_date BETWEEN '2025-01-01' AND '2025-12-31' AND {demoWhere};");

        var scenario = preview.GetProperty("scenario");
        Assert.True(rowRecount > 0);
        Assert.Equal(rowRecount, scenario.GetProperty("count").GetInt64());
        Assert.Equal(voucherRecount, scenario.GetProperty("voucherCount").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_PreviewRowsCappedAtFifty()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "drCrOnly", drCr = "debit" })));

        var scenario = preview.GetProperty("scenario");
        Assert.True(scenario.GetProperty("count").GetInt64() > 50);
        Assert.Equal(50, scenario.GetProperty("previewRows").GetArrayLength());
    }

    /* ---- filter.commit ---------------------------------------------------- */

    [Fact]
    public async Task FilterCommit_WithoutValidation_ThrowsCompletenessPrerequisiteFailed()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, runValidation: false);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = new object[]
                {
                    ValidScenario(new { join = "AND", type = "drCrOnly", drCr = "debit" })
                }
            })));

        Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, exception.Code);
    }

    [Fact]
    public async Task FilterCommit_SavesScenariosForResume()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new object[] { ValidScenario(new { join = "AND", type = "drCrOnly", drCr = "debit" }) }
        }));

        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        var scenarios = loaded.GetProperty("filterScenarios");
        Assert.Equal(1, scenarios.GetArrayLength());
        Assert.Equal("測試情境", scenarios[0].GetProperty("name").GetString());
        Assert.Equal("auditPeriod", scenarios[0].GetProperty("populationScope").GetString());
        Assert.Equal(
            RuleLogicVersions.Filter,
            loaded.GetProperty("filterResultRef").GetProperty("logicVersion").GetString());
        Assert.Equal(
            "auditPeriod",
            loaded.GetProperty("filterResultRef").GetProperty("populationScope").GetString());
    }

    [Fact]
    public async Task FilterCommit_PreservesUnknownPropertiesAndNumberLexemesInPersistenceAndResumeAst()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        await host.DispatchAsync(
            "filter.commit",
            """
            {
              "scenarios": [
                {
                  "name": "測試情境",
                  "rationale": "測試動機",
                  "unknownTop": 1e2,
                  "groups": [
                    {
                      "join": "AND",
                      "unknownGroup": 1.2300,
                      "rules": [
                        {
                          "join": "AND",
                          "type": "drCrOnly",
                          "drCr": "debit",
                          "unknownRule": 1E+02
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var definitions = await DemoProjectPipeline.QueryStringListAsync(
            host,
            context.ProjectId,
            "SELECT definition_json FROM config_filter_scenario WHERE position = 1;");
        using var stored = JsonDocument.Parse(Assert.Single(definitions)!);
        var storedScenario = stored.RootElement;
        var storedGroup = storedScenario.GetProperty("groups")[0];
        var storedRule = storedGroup.GetProperty("rules")[0];
        Assert.Equal("1e2", storedScenario.GetProperty("unknownTop").GetRawText());
        Assert.Equal("1.2300", storedGroup.GetProperty("unknownGroup").GetRawText());
        Assert.Equal("1E+02", storedRule.GetProperty("unknownRule").GetRawText());

        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        var scenario = loaded.GetProperty("filterScenarios")[0];
        var group = scenario.GetProperty("groups")[0];
        var rule = group.GetProperty("rules")[0];
        Assert.Equal("1.2300", group.GetProperty("unknownGroup").GetRawText());
        Assert.Equal("1E+02", rule.GetProperty("unknownRule").GetRawText());
        Assert.False(scenario.TryGetProperty("unknownTop", out _));
    }

    [Fact]
    public async Task FilterPreview_AndCommit_ProduceTheSameHitSet()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("IN-001", "2025-03-05", "1101", "現金", "parity-hit", "100.00", 1)
                .AddRow("IN-001", "2025-03-05", "4101", "收入", "parity-hit", "100.00", 0)
                .AddRow("OUT-001", "2025-04-05", "1101", "現金", "no-match", "80.00", 1)
                .AddRow("OUT-001", "2025-04-05", "4101", "收入", "no-match", "80.00", 0),
            configureTb: tb => tb
                .AddRow("1101", "現金", "180.00")
                .AddRow("4101", "收入", "-180.00"));
        await host.DispatchAsync("validate.run");
        var scenario = ValidScenario(new
        {
            join = "AND",
            type = "text",
            field = "description",
            keywords = "parity-hit",
            mode = "contains"
        });

        var preview = await host.DispatchAsync("filter.preview", PreviewPayload(scenario));
        var previewScenario = preview.GetProperty("scenario");

        await host.DispatchAsync(
            "filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));

        var committed = await host.DispatchAsync(
            "query.filterHitsPage",
            JsonSerializer.Serialize(new { scenarioPosition = 1, pageSize = 500 }));

        static string Identity(JsonElement row) =>
            string.Join(
                "|",
                row.GetProperty("documentNumber").GetString(),
                row.GetProperty("lineItem").GetString(),
                row.GetProperty("accountCode").GetString());

        var previewIdentities = previewScenario.GetProperty("previewRows")
            .EnumerateArray()
            .Select(Identity)
            .ToArray();
        var committedIdentities = committed.GetProperty("rows")
            .EnumerateArray()
            .Select(Identity)
            .ToArray();

        Assert.Equal(2, previewScenario.GetProperty("count").GetInt64());
        Assert.Equal(1, previewScenario.GetProperty("voucherCount").GetInt64());
        Assert.Equal(previewIdentities, committedIdentities);
        Assert.Equal(JsonValueKind.Null, committed.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task ProjectLoad_PreEffectivePopulationFilterLogic_RejectsReuseAndLazyRematerialization()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new object[]
            {
                ValidScenario(new { join = "AND", type = "drCrOnly", drCr = "debit" })
            }
        }));

        await DemoProjectPipeline.QueryScalarAsync(
            host,
            context.ProjectId,
            """
            UPDATE config_filter_scenario
            SET definition_json = json_set(
                definition_json,
                '$.logicVersion',
                'filter-2026-08-04-v6');
            DELETE FROM result_filter_run;
            SELECT 0;
            """);

        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        Assert.Equal(1, loaded.GetProperty("filterScenarios").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("filterResultRef").ValueKind);
        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync(
                "query.filterHitsPage",
                JsonSerializer.Serialize(new { scenarioPosition = 1 })));
        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
            host,
            context.ProjectId,
            "SELECT COUNT(*) FROM result_filter_run;"));
    }

    [Fact]
    public async Task ProjectLoad_LegacyAllProjectedDefinition_ReplaysAsStaleAndResavesToAuditPeriod()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new object[]
            {
                ValidScenario(new { join = "AND", type = "drCrOnly", drCr = "debit" })
            }
        }));

        var retainedHitCount = await DemoProjectPipeline.QueryScalarAsync(
            host,
            context.ProjectId,
            """
            UPDATE config_filter_scenario
            SET definition_json = json_set(definition_json, '$.populationScope', 'allProjected');
            SELECT COUNT(*) FROM result_filter_run;
            """);
        Assert.True(retainedHitCount > 0);

        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        var replayed = loaded.GetProperty("filterScenarios")[0];
        Assert.Equal("auditPeriod", replayed.GetProperty("populationScope").GetString());
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("filterResultRef").ValueKind);

        var stale = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "query.filterHitsPage",
            JsonSerializer.Serialize(new { scenarioPosition = 1 })));
        Assert.Equal(JetErrorCodes.StaleResult, stale.Code);

        var recommit = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            populationScope = "auditPeriod",
            scenarios = new[]
            {
                new
                {
                    name = replayed.GetProperty("name").GetString(),
                    rationale = replayed.GetProperty("rationale").GetString(),
                    groups = replayed.GetProperty("groups")
                }
            }
        }));
        Assert.Equal(
            "auditPeriod",
            recommit.GetProperty("resultRef").GetProperty("populationScope").GetString());

        var page = await host.DispatchAsync(
            "query.filterHitsPage",
            JsonSerializer.Serialize(new { scenarioPosition = 1 }));
        Assert.NotEmpty(page.GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task FilterCommit_NonEmptyBatch_ReturnsPersistedUtcRevision()
    {
        // 等價分割（非空批次）＋持久化 oracle：revision 必須逐字等於同批每列的 saved_utc。
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var scenarios = new[]
        {
            new
            {
                name = "借方情境",
                rationale = "revision 測試",
                groups = new[] { new { join = "AND", rules = new[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } } } }
            },
            new
            {
                name = "貸方情境",
                rationale = "revision 測試",
                groups = new[] { new { join = "AND", rules = new[] { new { join = "AND", type = "drCrOnly", drCr = "credit" } } } }
            }
        };

        var response = await host.DispatchAsync(
            "filter.commit", JsonSerializer.Serialize(new { scenarios }));

        var resultRef = response.GetProperty("resultRef");
        var revision = resultRef.GetProperty("revision").GetString()!;
        Assert.Equal(revision, resultRef.GetProperty("generatedUtc").GetString());
        Assert.Equal(RuleLogicVersions.Filter, resultRef.GetProperty("logicVersion").GetString());
        Assert.Equal("auditPeriod", resultRef.GetProperty("populationScope").GetString());
        var parsed = DateTimeOffset.ParseExact(
            revision, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);

        var persisted = await DemoProjectPipeline.QueryStringListAsync(
            host,
            context.ProjectId,
            "SELECT saved_utc FROM config_filter_scenario ORDER BY position;");
        Assert.Equal(2, persisted.Count);
        Assert.All(persisted, savedUtc => Assert.Equal(revision, savedUtc));
    }

    [Fact]
    public async Task FilterCommit_EmptyBatch_StillReturnsRevision()
    {
        // 等價分割（空 replace-all 批次）：即使沒有 SavedFilterScenario 列，這次提交仍有可綁定的 revision。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        var response = await host.DispatchAsync(
            "filter.commit", JsonSerializer.Serialize(new { scenarios = Array.Empty<object>() }));

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal(0, response.GetProperty("savedCount").GetInt32());
        var resultRef = response.GetProperty("resultRef");
        var revision = resultRef.GetProperty("revision").GetString()!;
        Assert.Equal(revision, resultRef.GetProperty("generatedUtc").GetString());
        Assert.Equal(RuleLogicVersions.Filter, resultRef.GetProperty("logicVersion").GetString());
        var parsed = DateTimeOffset.ParseExact(
            revision, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Fact]
    public async Task FilterCommit_ElevenScenarios_ThrowsScenarioLimitReached()
    {
        var scenarios = Enumerable.Range(1, 11).Select(i => (object)new
        {
            name = $"情境 {i}",
            rationale = "動機",
            groups = new object[]
            {
                new { join = "AND", rules = new object[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } } }
            }
        }).ToArray();

        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.commit", JsonSerializer.Serialize(new { scenarios })));

        Assert.Equal("scenario_limit_reached", ex.Code);
    }

    [Fact]
    public async Task FilterCommit_TenScenarios_Succeeds()
    {
        var scenarios = Enumerable.Range(1, 10).Select(i => (object)new
        {
            name = $"情境 {i}",
            rationale = "動機",
            groups = new object[]
            {
                new { join = "AND", rules = new object[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } } }
            }
        }).ToArray();

        var result = await fixture.Host.DispatchAsync(
            "filter.commit", JsonSerializer.Serialize(new { scenarios }));

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(10, result.GetProperty("savedCount").GetInt32());
    }

    [Fact]
    public async Task FilterCommit_DuplicateNames_ThrowsInvalidScenario()
    {
        var duplicated = ValidScenario(new { join = "AND", type = "drCrOnly", drCr = "debit" });

        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { duplicated, duplicated } })));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterCommit_AdvancesProjectToExportStep()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new object[] { ValidScenario(new { join = "AND", type = "drCrOnly", drCr = "debit" }) }
        }));

        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        // 6 步模型：index 5 = 匯出底稿。
        Assert.Equal(5, loaded.GetProperty("project").GetProperty("currentStep").GetInt32());
    }

    [Fact]
    public async Task LoadDemo_ExposesDemoScenarioAst()
    {
        using var host = new HandlerTestHost();

        var demo = await DemoProjectPipeline.DispatchDemoFixtureAsync("project.loadDemo");

        var scenario = demo.GetProperty("demoScenario");
        Assert.False(string.IsNullOrWhiteSpace(scenario.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(scenario.GetProperty("rationale").GetString()));
        Assert.Equal(2, scenario.GetProperty("groups").GetArrayLength());
    }

    /* ---- 新條件型別（自訂關鍵字、自訂尾數、科目配對分析） ------------------ */

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task FilterPreview_PeriodInOut_ThrowsInvalidScenario(string inPeriod)
    {
        var exception = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview",
            PreviewPayloadWithOmittedScope(ValidScenario(
                new { join = "AND", type = "periodInOut", inPeriod }))));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
    }

    [Fact]
    public async Task FilterCommit_PeriodInOut_ThrowsInvalidScenarioWithoutReplacingRevision()
    {
        var before = await fixture.Host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = fixture.ProjectId }));
        var previousScenarios = before.GetProperty("filterScenarios").GetRawText();

        var exception = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = new[]
                {
                    ValidScenario(new { join = "AND", type = "periodInOut", inPeriod = "true" })
                }
            })));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        var after = await fixture.Host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = fixture.ProjectId }));
        Assert.Equal(previousScenarios, after.GetProperty("filterScenarios").GetRawText());
    }

    [Fact]
    public async Task FilterPreview_CustomKeywords_MatchesDescriptionRecount()
    {
        // demo R2 種子的借方行摘要含「調整」(KeywordDescription = 調整分錄);以此關鍵字
        // 保證母體非空,filter 命中數須等於同述詞的獨立 description recount。
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "customKeywords", keywords = "調整" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            """
            SELECT COUNT(*) FROM target_gl_entry
            WHERE post_date BETWEEN '2025-01-01' AND '2025-12-31'
              AND document_description LIKE '%調整%';
            """);

        Assert.True(recount > 0);
        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_CustomTrailingZeros_FixedDigitsMatchesRecount()
    {
        // 固定 4 位；先取主單位整數，再對 10^4 取模。
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "customTrailingZeros", digits = "4" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry "
            + "WHERE post_date BETWEEN '2025-01-01' AND '2025-12-31' "
            + "AND CAST(ABS(amount_scaled) / 10000 AS INTEGER) <> 0 "
            + "AND CAST(ABS(amount_scaled) / 10000 AS INTEGER) % 10000 = 0;");

        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_CustomTrailingZeros_UsesMajorUnitIntegerAndExcludesSubunitZero()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("ROUND-1", "2025-03-05", "1101", "現金", "小數尾零", "1000000.25", 1)
            .AddRow("ROUND-1", "2025-03-05", "4101", "收入", "小數尾零", "1000000.25", 0)
            .AddRow("SUB-1", "2025-03-06", "1101", "現金", "次單位", "0.25", 1)
            .AddRow("SUB-1", "2025-03-06", "4101", "收入", "次單位", "0.25", 0)
            .AddRow("ZERO", "2025-03-06", "1101", "現金", "零值", "0", 1)
            .AddRow("ZERO", "2025-03-06", "4101", "收入", "零值", "0", 0)
            .AddRow("ROUND-2", "2025-03-07", "1101", "現金", "小數尾零", "2000000.99", 1)
            .AddRow("ROUND-2", "2025-03-07", "4101", "收入", "小數尾零", "2000000.99", 0));

        var preview = await host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "customTrailingZeros", digits = "6" })));

        Assert.Equal(4, preview.GetProperty("scenario").GetProperty("count").GetInt64());
        var docs = preview.GetProperty("scenario").GetProperty("previewRows")
            .EnumerateArray()
            .Select(row => row.GetProperty("documentNumber").GetString()!)
            .Distinct()
            .Order()
            .ToArray();
        Assert.Equal(["ROUND-1", "ROUND-2"], docs);
    }

    [Fact]
    public async Task FilterPreview_TrailingZerosAndAmountThreshold_ComposesAuthorizationGate()
    {
        // 方法學授權閘 = 圓整數 AND 金額≥授權門檻(customTrailingZeros AND numRange)。
        // 驗證組合與獨立 recount 相等(catches AND/述詞 wiring 錯誤)。
        var preview = await fixture.Host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new
        {
            populationScope = GlPopulationScopeValues.AuditPeriod,
            scenario = new
            {
                name = "授權閘",
                rationale = "圓整數且超過授權門檻",
                groups = new object[]
                {
                    new
                    {
                        join = "AND",
                        rules = new object[]
                        {
                            new { join = "AND", type = "customTrailingZeros", digits = "6" },
                            new { join = "AND", type = "numRange", field = "amount", from = "5000000", to = "" }
                        }
                    }
                }
            }
        }));

        // customTrailingZeros(6)：主單位整數 % 10^6；numRange ABS ≥ 5,000,000×10000。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry "
            + "WHERE post_date BETWEEN '2025-01-01' AND '2025-12-31' "
            + "AND CAST(ABS(amount_scaled) / 10000 AS INTEGER) <> 0 "
            + "AND CAST(ABS(amount_scaled) / 10000 AS INTEGER) % 1000000 = 0 "
            + "AND ABS(amount_scaled) >= 50000000000;");

        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_CustomTrailingZerosOutOfRange_ThrowsInvalidScenario()
    {
        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "customTrailingZeros", digits = "13" }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_AccountPairCreditAnchor_MatchesRecount()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var file = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAccountMappingFile");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = file.GetProperty("filePath").GetString()
        }));

        var preview = await host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "accountPair", pairMode = "creditAnchor", creditCategory = "Revenue" })));

        // 貸方錨定（guide §6.1 C）：輸出貸方錨定列＋同傳票借方列（>= 0）。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            """
            SELECT COUNT(*) FROM target_gl_entry g
            WHERE g.post_date BETWEEN '2025-01-01' AND '2025-12-31'
              AND EXISTS (SELECT 1 FROM target_gl_entry c
                          JOIN target_account_mapping mc ON mc.account_code = c.account_code
                          WHERE c.document_number = g.document_number
                            AND c.post_date BETWEEN '2025-01-01' AND '2025-12-31'
                            AND mc.standardized_category = 'Revenue' AND c.amount_scaled < 0)
              AND ((EXISTS (SELECT 1 FROM target_account_mapping m
                            WHERE m.account_code = g.account_code AND m.standardized_category = 'Revenue')
                    AND g.amount_scaled < 0)
                   OR g.amount_scaled >= 0);
            """);

        Assert.True(recount > 0);
        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    /* ---- 考量特殊科目類別配對（specialAccountCategoryPair）的 wire 端到端 --- */

    [Fact]
    public async Task FilterPreview_SpecialAccountCategoryPairDrAndCr_WithMapping_MatchesRecount()
    {
        // 科目配對已匯入（共用 fixture）→ 放行。A = Receivables 借、B = Revenue 貸：demo 的
        // 賒銷種子傳票即「應收借（1131）＋ 收入貸（4101）」同傳票（DemoDataFactory.CreditSaleVouchers），保證母體非空。
        // drAndCr 命中數須等於同述詞的獨立 recount（標記 Receivables 借「或」Revenue 貸的列）。
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "specialAccountCategoryPair", pairMode = "drAndCr", debitCategory = "Receivables", creditCategory = "Revenue" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            """
            SELECT COUNT(*) FROM target_gl_entry g
            WHERE g.post_date BETWEEN '2025-01-01' AND '2025-12-31'
              AND EXISTS (SELECT 1 FROM target_gl_entry d
                          JOIN target_account_mapping md ON md.account_code = d.account_code
                          WHERE d.document_number = g.document_number
                            AND d.post_date BETWEEN '2025-01-01' AND '2025-12-31'
                            AND md.standardized_category = 'Receivables' AND d.amount_scaled >= 0)
              AND EXISTS (SELECT 1 FROM target_gl_entry c
                          JOIN target_account_mapping mc ON mc.account_code = c.account_code
                          WHERE c.document_number = g.document_number
                            AND c.post_date BETWEEN '2025-01-01' AND '2025-12-31'
                            AND mc.standardized_category = 'Revenue' AND c.amount_scaled < 0)
              AND ((EXISTS (SELECT 1 FROM target_account_mapping m
                            WHERE m.account_code = g.account_code AND m.standardized_category = 'Receivables')
                    AND g.amount_scaled >= 0)
                   OR (EXISTS (SELECT 1 FROM target_account_mapping m
                              WHERE m.account_code = g.account_code AND m.standardized_category = 'Revenue')
                       AND g.amount_scaled < 0));
            """);

        Assert.True(recount > 0, "demo R3 種子應有 Receivables 借 + Revenue 貸的傳票");
        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_SpecialAccountCategoryPairWithoutAccountMapping_ThrowsInvalidScenario()
    {
        // 閘控：科目配對未匯入時三模式皆須被擋下（invalid_scenario）。自建 host 關閉科目配對匯入。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "specialAccountCategoryPair", pairMode = "drAndCr", debitCategory = "Cash", creditCategory = "Revenue" }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_SpecialAccountCategoryPairNonWhitelistCategory_ThrowsInvalidScenario()
    {
        // 分類不在白名單（共用 fixture 已匯入科目配對 → 僅分類越界觸發）。
        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "specialAccountCategoryPair", pairMode = "drAndCr", debitCategory = "NotACategory", creditCategory = "Revenue" }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_SpecialAccountCategoryPairIllegalPairMode_ThrowsInvalidScenario()
    {
        // 非法 pairMode（沿用 accountPair 的模式名 exact 也屬非法——兩條件模式集合刻意分離）。
        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "specialAccountCategoryPair", pairMode = "exact", debitCategory = "Cash", creditCategory = "Revenue" }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_MissingScenario_ThrowsInvalidPayload()
    {
        // 等價分割:payload 為物件但缺少必填 scenario 欄位。
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", "{}"));

        Assert.Equal("invalid_payload", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_NonObjectPayload_ThrowsInvalidPayload()
    {
        // 等價分割:payload 不是物件時同樣缺少可讀取的 scenario 欄位。
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", "[]"));

        Assert.Equal("invalid_payload", ex.Code);
    }

    [Fact]
    public async Task FilterCommit_MissingScenarios_ThrowsInvalidPayload()
    {
        // 等價分割:payload 為物件但缺少必填 scenarios 陣列欄位。
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.commit", "{}"));

        Assert.Equal("invalid_payload", ex.Code);
    }

    [Fact]
    public async Task FilterCommit_ScenariosNotArray_ThrowsInvalidPayload()
    {
        // 等價分割:scenarios 存在但不是陣列時必須拒絕。
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.commit", "{\"scenarios\":{}}"));

        Assert.Equal("invalid_payload", ex.Code);
    }

    /* ---- KCT 小組條件（清單 A/C/D/H/J）的 wire 端到端 --------------------- */

    [Fact]
    public async Task FilterPreview_TrailingDigits_MatchesControlledHits()
    {
        // 清單 H 端到端（型別字串解析 + keywords 承載尾數樣態 + 述詞執行）；尾數無需科目配對。
        // MoneyScale 預設 10000：1,999,999.00 主單位整數 1,999,999 尾數 999999；500.00 不符。
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-1", "2025-03-05", "5101", "其他", "尾數九", "1999999.00", 1)
            .AddRow("JV-1", "2025-03-05", "1101", "現金", "尾數九", "1999999.00", 0)
            .AddRow("JV-2", "2025-03-06", "5101", "其他", "一般金額", "500.00", 1)
            .AddRow("JV-2", "2025-03-06", "1101", "現金", "一般金額", "500.00", 0));

        var preview = await host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "trailingDigits", keywords = "999999" })));

        var scenario = preview.GetProperty("scenario");
        Assert.Equal(2, scenario.GetProperty("count").GetInt64());        // JV-1 兩列（借/貸絕對值皆 1,999,999）
        Assert.Equal(1, scenario.GetProperty("voucherCount").GetInt64()); // 一張傳票
    }

    [Theory]
    [InlineData("revenueWithoutNormalCounterpart")]
    [InlineData("manualRevenueEntry")]
    public async Task FilterPreview_KctParameterlessType_RunsEndToEnd(string type)
    {
        // 證明型別字串解析 → 驗證放行 → 述詞執行整條 wire（共用 fixture 已匯入科目配對）。
        // 正確性由 KctFilterPredicateTests 的固定 fixture 身分斷言把關；此處只驗 wire 不丟例外。
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type })));

        Assert.True(preview.GetProperty("scenario").GetProperty("count").GetInt64() >= 0);
    }

    [Fact]
    public async Task FilterPreview_RevenueDebitNearQuarterEnd_WithoutAccountMapping_ThrowsInvalidScenario()
    {
        // 清單 A 閘控：科目配對未匯入時 validator 必須擋下（自建 host 關閉科目配對匯入）。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "revenueDebitNearQuarterEnd", windowDays = 5 }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_RevenueDebitNearQuarterEnd_WindowDaysOutOfRange_ThrowsInvalidScenario()
    {
        // 科目配對已匯入（共用 fixture）→ 僅 windowDays=0 越界觸發 invalid_scenario。
        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "revenueDebitNearQuarterEnd", windowDays = 0 }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_TrailingDigits_NonDigitPattern_ThrowsInvalidScenario()
    {
        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "trailingDigits", keywords = "12ab" }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    /* ---- KCT 來源情境豁免名稱/動機必填（manifest scenario.source）-------- */

    // KCT 來源、名稱與動機皆留空；測 metadata 時用不需 mapping 前置的清單 H，
    // D/E/G/J 前置與命中另由 provider matrix 精確驗證。
    private static object KctScenario(params object[] rules) => new
    {
        source = "kct",
        name = "",
        rationale = "",
        groups = new object[] { new { join = "AND", rules } }
    };

    private async Task<(string Scenarios, string ResultRef)> LoadFilterStateAsync()
    {
        var loaded = await fixture.Host.DispatchAsync("project.load", JsonSerializer.Serialize(new
        {
            projectId = fixture.ProjectId
        }));
        return (
            loaded.GetProperty("filterScenarios").GetRawText(),
            loaded.GetProperty("filterResultRef").GetRawText());
    }

    [Fact]
    public async Task FilterPreview_KctSourceWithEmptyNameAndRationale_IsAccepted()
    {
        // source:"kct" 豁免名稱/動機必填——即便兩者皆空也不應擲 invalid_scenario。
        // trailingDigits 不需額外 mapping 前置，能把本測試焦點收斂在 source metadata。
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(KctScenario(
            new { join = "AND", type = "trailingDigits", keywords = "000000" })));

        // 放行的證明：拿到 scenario response 且 count 可算（≥ 0），未走 invalid_scenario。
        Assert.True(preview.GetProperty("scenario").GetProperty("count").GetInt64() >= 0);
    }

    [Fact]
    public async Task FilterPreview_NonKctSourceWithEmptyNameAndRationale_PreviewsButCannotSave()
    {
        // 回歸：未標 source（查核員自擬）且名稱/動機皆空時，必填檢查仍須擋下。
        var payload = PreviewPayload(new
        {
            name = "",
            rationale = "",
            groups = new object[]
            {
                new { join = "AND", rules = new object[] { new { join = "AND", type = "trailingDigits", keywords = "000000" } } }
            }
        });

        var preview = await fixture.Host.DispatchAsync("filter.preview", payload);
        Assert.True(preview.GetProperty("scenario").GetProperty("count").GetInt64() > 0);
        using var definition = JsonDocument.Parse(payload);
        var error = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync("filter.commit",
            JsonSerializer.Serialize(new { scenarios = new[] { definition.RootElement.GetProperty("scenario") } })));
        Assert.Equal("invalid_scenario", error.Code);
    }

    [Fact]
    public async Task FilterCommit_KctSourceWithEmptyNameAndRationale_PersistsNonEmptyAuditTrail()
    {
        // 落地替補：KCT 來源、名稱/動機留空 → commit 後 project.load 回傳的情境
        // 名稱與動機皆為非空（config_filter_scenario.name/.rationale NOT NULL 的留痕不變量）。
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new object[] { KctScenario(new { join = "AND", type = "trailingDigits", keywords = "000000" }) }
        }));

        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        var scenarios = loaded.GetProperty("filterScenarios");
        Assert.Equal(1, scenarios.GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(scenarios[0].GetProperty("name").GetString()),
            "KCT 留痕名稱不得為空");
        Assert.False(string.IsNullOrWhiteSpace(scenarios[0].GetProperty("rationale").GetString()),
            "KCT 留痕動機不得為空");
        Assert.Equal("kct", scenarios[0].GetProperty("source").GetString());

        // project.load summary 經 wire 重存後 source 仍須保留，不能退化成一般情境。
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new object[]
            {
                new
                {
                    source = scenarios[0].GetProperty("source").GetString(),
                    name = scenarios[0].GetProperty("name").GetString(),
                    rationale = scenarios[0].GetProperty("rationale").GetString(),
                    groups = scenarios[0].GetProperty("groups")
                }
            }
        }));
        var reloaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        Assert.Equal(
            "kct",
            reloaded.GetProperty("filterScenarios")[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task FilterCommit_PaddedKctSource_ReturnsAndLoadsCanonicalSource()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var scenario = new
        {
            source = " kct ",
            name = "",
            rationale = "",
            groups = new object[]
            {
                new
                {
                    join = "AND",
                    rules = new object[]
                    {
                        new { join = "AND", type = "trailingDigits", keywords = "000000" }
                    }
                }
            }
        };

        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
        Assert.Equal(
            FilterScenarioSources.Kct,
            committed.GetProperty("scenarios")[0].GetProperty("source").GetString());

        var loaded = await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        Assert.Equal(
            FilterScenarioSources.Kct,
            loaded.GetProperty("filterScenarios")[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task FilterCommit_TwoKctScenariosWithEmptyMetadata_UsesPositionUniqueStableNames()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new object[]
            {
                KctScenario(new { join = "AND", type = "trailingDigits", keywords = "999999" }),
                KctScenario(new { join = "AND", type = "trailingDigits", keywords = "000000" })
            }
        }));

        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        var scenarios = loaded.GetProperty("filterScenarios");

        Assert.Equal(2, scenarios.GetArrayLength());
        Assert.Equal("KCT 小組方法論檢核條件（第 1 項）", scenarios[0].GetProperty("name").GetString());
        Assert.Equal("KCT 小組方法論檢核條件（第 2 項）", scenarios[1].GetProperty("name").GetString());
        Assert.All(
            scenarios.EnumerateArray(),
            scenario => Assert.Equal(
                FilterScenarioSources.KctDefaultRationale,
                scenario.GetProperty("rationale").GetString()));
    }
}
