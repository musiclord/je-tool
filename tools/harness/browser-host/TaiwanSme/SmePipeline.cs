using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Bridge;
using JET.Domain;

namespace Jet.Harness.TaiwanSme;

/// <summary>以正式 action 名稱與 payload 呼叫 JET；瀏覽器主機接 ActionDispatcher，測試接 HandlerTestHost。</summary>
internal delegate Task<JsonElement> SmeDispatch(string action, JsonElement payload);

/// <summary>
/// 台灣中小企業案件的建案流程與比對：建案、匯入、配對、分類、驗證、預篩選、篩選、匯出。
/// 每一步只呼叫正式 action；完成後把 JET 的驗證摘要、預篩選與情境命中，和 <see cref="SmeOracle"/>
/// 獨立算出的答案逐筆比對。瀏覽器主機與自動測試共用這一份流程，結果寫在 <see cref="SmeCaseLog"/>。
/// </summary>
internal static class SmePipeline
{
    public const string EntityName = "合成精密工業股份有限公司";

    public static async Task RunCompleteAsync(
        SmeDispatch dispatch,
        string caseName,
        string projectCode,
        string provider,
        SmeSourceFiles files,
        SmeOracle oracle,
        Func<IReadOnlyDictionary<string, string>, IReadOnlyList<SmeScenario>> scenarios,
        SmeCaseLog log)
    {
        await CreateAndImportAsync(dispatch, caseName, projectCode, provider, files, log).ConfigureAwait(false);
        await log.DispatchAsync(dispatch, "import.authorizedPreparer.fromFile", new
        {
            filePath = files.AuthorizedPreparerFile,
            fileName = Path.GetFileName(files.AuthorizedPreparerFile),
            sourceColumn = SmeWorkbookWriter.PreparerNameColumn,
        }).ConfigureAwait(false);
        await log.DispatchAsync(dispatch, "import.holiday", new { dates = SmeCalendar.Holidays.Select(SmeCalendar.Iso).ToArray() })
            .ConfigureAwait(false);
        await log.DispatchAsync(dispatch, "import.makeupDay", new { dates = SmeCalendar.MakeupDays.Select(SmeCalendar.Iso).ToArray() })
            .ConfigureAwait(false);
        await CommitMappingsAsync(dispatch, log).ConfigureAwait(false);
        var categoryIds = await SaveTaxonomyAsync(dispatch, log).ConfigureAwait(false);
        await log.DispatchAsync(dispatch, "import.accountMapping.fromFile", new
        {
            filePath = files.AccountMappingFile,
            fileName = Path.GetFileName(files.AccountMappingFile),
        }).ConfigureAwait(false);

        var validation = await log.DispatchAsync(dispatch, "validate.run", new { }).ConfigureAwait(false);
        CompareValidation(validation, files.Case, oracle, log);
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString();
        await log.DispatchAsync(dispatch, "export.validationArtifacts", new { runId = validationRunId }).ConfigureAwait(false);
        var prescreen = await log.DispatchAsync(dispatch, "prescreen.run", new { }).ConfigureAwait(false);
        var prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString();
        await log.DispatchAsync(dispatch, "export.prescreenReport", new { runId = prescreenRunId }).ConfigureAwait(false);
        foreach (var (ruleKey, expected) in oracle.Prescreen())
        {
            var actual = await ReadAllKeysAsync(dispatch, "query.prescreenPage", new JsonObject { ["ruleKey"] = ruleKey })
                .ConfigureAwait(false);
            log.Compare($"預篩選 {ruleKey}", expected, actual);
        }

        var definitions = scenarios(categoryIds);
        var committed = await log.DispatchAsync(dispatch, "filter.commit", new JsonObject
        {
            ["scenarios"] = new JsonArray(definitions.Select(scenario => (JsonNode)scenario.Wire.DeepClone()).ToArray()),
        }).ConfigureAwait(false);
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString();
        for (var i = 0; i < definitions.Count; i++)
        {
            var actual = await ReadAllKeysAsync(dispatch, "query.filterHitsPage", new JsonObject
            {
                ["scenarioPosition"] = i + 1,
                ["scenarioRevision"] = revision,
            }).ConfigureAwait(false);
            log.Compare($"情境 {i + 1} {definitions[i].Name}", definitions[i].Expected(oracle), actual);
        }

        await log.DispatchAsync(dispatch, "export.criteriaSelectionReport", new
        {
            validationRunId,
            prescreenRunId,
            revision,
        }).ConfigureAwait(false);
        await log.DispatchAsync(dispatch, "export.workpaperStream", new
        {
            validationRunId,
            prescreenRunId,
            scenarioRevision = revision,
            scenarioPositions = Enumerable.Range(1, definitions.Count).ToArray(),
        }).ConfigureAwait(false);

        // currentStep 是 0 起算的步驟索引：5 是匯出底稿。
        await log.DispatchAsync(dispatch, "project.saveProgress", new { currentStep = 5 }).ConfigureAwait(false);
        await log.DispatchAsync(dispatch, "project.releaseLock", new { }).ConfigureAwait(false);
    }

    /// <summary>只建案與匯入兩個檔案，停在欄位配對，留給審計員自己操作第三步。</summary>
    public static async Task RunMappingPendingAsync(
        SmeDispatch dispatch, string caseName, string projectCode, SmeSourceFiles files, SmeCaseLog log)
    {
        await CreateAndImportAsync(dispatch, caseName, projectCode, "sqlite", files, log).ConfigureAwait(false);
        // currentStep 2 是欄位配對。
        await log.DispatchAsync(dispatch, "project.saveProgress", new { currentStep = 2 }).ConfigureAwait(false);
        await log.DispatchAsync(dispatch, "project.releaseLock", new { }).ConfigureAwait(false);
    }

    private static async Task CreateAndImportAsync(
        SmeDispatch dispatch, string caseName, string projectCode, string provider, SmeSourceFiles files, SmeCaseLog log)
    {
        await log.DispatchAsync(dispatch, "project.create", new
        {
            caseName,
            projectCode,
            entityName = EntityName,
            periodStart = SmeCalendar.Iso(SmeCalendar.PeriodStart),
            periodEnd = SmeCalendar.Iso(SmeCalendar.PeriodEnd),
            lastPeriodStart = SmeCalendar.Iso(SmeCalendar.PreparationDate),
            databaseProvider = provider,
        }).ConfigureAwait(false);
        // 上半年與下半年兩個 ERP 匯出檔一起匯入同一個 GL 資料集。
        await log.DispatchAsync(dispatch, "import.gl.fromFile", new
        {
            sources = files.GlFiles.Select(file => new { filePath = file.FilePath, fileName = file.FileName }).ToArray(),
        }).ConfigureAwait(false);
        await log.DispatchAsync(dispatch, "import.tb.fromFile", new
        {
            filePath = files.TbFile,
            fileName = Path.GetFileName(files.TbFile),
        }).ConfigureAwait(false);
    }

    private static async Task CommitMappingsAsync(SmeDispatch dispatch, SmeCaseLog log)
    {
        var columns = SmeWorkbookWriter.GlColumns;
        await log.DispatchAsync(dispatch, "mapping.commit.gl", new
        {
            mapping = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.PostDate] = columns[0],
                [GlMappingKeys.DocNum] = columns[1],
                [GlMappingKeys.LineId] = columns[2],
                [GlMappingKeys.AccNum] = columns[3],
                [GlMappingKeys.AccName] = columns[4],
                [GlMappingKeys.Description] = columns[5],
                [GlMappingKeys.DebitAmount] = columns[6],
                [GlMappingKeys.CreditAmount] = columns[7],
                [GlMappingKeys.JeSource] = columns[9],
                [GlMappingKeys.CreateBy] = columns[10],
                [GlMappingKeys.ApproveBy] = columns[11],
                [GlMappingKeys.DocDate] = columns[12],
                [GlMappingKeys.Manual] = columns[13],
                [GlMappingKeys.PostingStatus] = columns[14],
            },
            amountMode = GlAmountModeNames.Dual,
            approvalDateMode = "mapped",
            manualAutoPolicy = new { manualValues = new[] { "Y" }, automaticValues = new[] { "N" } },
            postingStatusPolicy = new { acceptedValues = new[] { "已過帳" }, includeBlank = false },
            rdeFields = new[] { new { sourceColumn = columns[8], label = "部門", valueType = "text" } },
        }).ConfigureAwait(false);

        var tb = SmeWorkbookWriter.TbColumns;
        await log.DispatchAsync(dispatch, "mapping.commit.tb", new
        {
            mapping = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TbMappingKeys.AccNum] = tb[0],
                [TbMappingKeys.AccName] = tb[1],
                [TbMappingKeys.OpeningDebit] = tb[2],
                [TbMappingKeys.OpeningCredit] = tb[3],
                [TbMappingKeys.ClosingDebit] = tb[6],
                [TbMappingKeys.ClosingCredit] = tb[7],
            },
            changeMode = TbChangeModeNames.OpenCloseBySide,
        }).ConfigureAwait(false);
    }

    /// <summary>新增案件分類，回傳分類名稱對應的正式分類 ID。新案件的分類設定版本是 1。</summary>
    private static async Task<IReadOnlyDictionary<string, string>> SaveTaxonomyAsync(SmeDispatch dispatch, SmeCaseLog log)
    {
        var builtIns = new (string Id, string Label, string Role)[]
        {
            (AccountTaxonomyBuiltIns.RevenueId, SmeChart.Revenue, AccountTaxonomyBuiltIns.RevenueRole),
            (AccountTaxonomyBuiltIns.ReceivablesId, SmeChart.Receivables, AccountTaxonomyBuiltIns.ReceivablesRole),
            (AccountTaxonomyBuiltIns.CashId, SmeChart.Cash, AccountTaxonomyBuiltIns.CashRole),
            (AccountTaxonomyBuiltIns.ReceiptInAdvanceId, SmeChart.ReceiptInAdvance, AccountTaxonomyBuiltIns.ReceiptInAdvanceRole),
            (AccountTaxonomyBuiltIns.OthersId, SmeChart.Others, AccountTaxonomyBuiltIns.OthersRole),
        };
        var draftIds = SmeChart.CustomCategories
            .Select((category, index) => (category.Label, Draft: $"draft-{index + 1}"))
            .ToDictionary(pair => pair.Label, pair => pair.Draft, StringComparer.Ordinal);
        string? ParentId(string? parent) => parent is null
            ? null
            : builtIns.Any(item => item.Label == parent) ? builtIns.Single(item => item.Label == parent).Id : draftIds[parent];

        var categories = builtIns
            .Select((item, index) => new { categoryId = item.Id, label = item.Label, ordinal = index, semanticRole = item.Role, parentCategoryId = (string?)null })
            .Concat(SmeChart.CustomCategories.Select((category, index) => new
            {
                categoryId = draftIds[category.Label],
                label = category.Label,
                ordinal = builtIns.Length + index,
                semanticRole = category.Role,
                parentCategoryId = ParentId(category.Parent),
            }))
            .ToArray();
        var saved = await log.DispatchAsync(dispatch, "accountTaxonomy.save", new { revision = 1, categories }).ConfigureAwait(false);
        return saved.GetProperty("categories").EnumerateArray().ToDictionary(
            item => item.GetProperty("label").GetString()!,
            item => item.GetProperty("categoryId").GetString()!,
            StringComparer.Ordinal);
    }

    /// <summary>資料驗證摘要：GL 範圍、排除原因、借貸合計、不平傳票、空值與核准日、完整性。</summary>
    private static void CompareValidation(JsonElement validation, SmeCase smeCase, SmeOracle oracle, SmeCaseLog log)
    {
        var population = oracle.Population;
        var outOfPeriod = smeCase.Lines.Count(line =>
            line.PostDate < SmeCalendar.PeriodStart || line.PostDate > SmeCalendar.PeriodEnd);
        var unposted = smeCase.Lines.Count(line =>
            !line.IsPosted && line.PostDate >= SmeCalendar.PeriodStart && line.PostDate <= SmeCalendar.PeriodEnd);
        var stats = validation.GetProperty("stats");
        var summary = validation.GetProperty("populationSummary");
        var nulls = validation.GetProperty("nullRecordsTest");
        var completeness = validation.GetProperty("completenessTest");
        log.CompareNumber("驗證 GL 分錄筆數", population.Count, stats.GetProperty("glRowCount").GetInt64());
        log.CompareNumber("驗證 GL 傳票張數", population.Select(line => line.VoucherNo).Distinct().Count(),
            stats.GetProperty("voucherCount").GetInt64());
        log.CompareNumber("驗證 GL 借方合計", population.Sum(line => line.Debit), stats.GetProperty("totalDebit").GetDecimal());
        log.CompareNumber("驗證 GL 貸方合計", population.Sum(line => line.Credit), stats.GetProperty("totalCredit").GetDecimal());
        log.CompareNumber("驗證 PBC-GL 原始筆數", smeCase.Lines.Count, summary.GetProperty("raw").GetProperty("rowCount").GetInt64());
        log.CompareNumber("驗證 期間外排除筆數", outOfPeriod,
            summary.GetProperty("excluded").GetProperty("byPeriodCount").GetInt64());
        log.CompareNumber("驗證 未過帳排除筆數", unposted,
            summary.GetProperty("excluded").GetProperty("byPostingStatusCount").GetInt64());
        log.CompareNumber("驗證 借貸不平傳票", 0,
            validation.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());
        log.CompareNumber("驗證 科目空白", 0, nulls.GetProperty("nullAccountCount").GetInt64());
        log.CompareNumber("驗證 傳票號碼空白", 0, nulls.GetProperty("nullDocumentCount").GetInt64());
        log.CompareNumber("驗證 摘要空白", population.Count(line => SmeOracle.IsBlank(line.Description)),
            nulls.GetProperty("nullDescriptionCount").GetInt64());
        log.CompareNumber("驗證 核准日不在查核期間",
            population.Count(line => line.ApprovalDate is { } approval
                && (approval < SmeCalendar.PeriodStart || approval > SmeCalendar.PeriodEnd)),
            nulls.GetProperty("outOfRangeDateCount").GetInt64());
        log.CompareNumber("驗證 GL 與 TB 有差異的科目", 0, completeness.GetProperty("diffAccountCount").GetInt64());
        var partA = completeness.GetProperty("partA");
        log.CompareNumber("驗證 存入的分錄筆數與合計相符", 1,
            partA.GetProperty("rowCountMatch").GetBoolean() && partA.GetProperty("amountMatch").GetBoolean() ? 1 : 0);
    }

    private static async Task<HashSet<string>> ReadAllKeysAsync(SmeDispatch dispatch, string action, JsonObject request)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var pages = 0;
        do
        {
            if (++pages > 400)
            {
                throw new InvalidOperationException($"{action} 讀了 400 頁仍未結束。");
            }

            var page = (JsonObject)request.DeepClone();
            page["pageSize"] = 500;
            if (cursor is not null)
            {
                page["cursor"] = cursor;
            }

            var result = await dispatch(action, JsonSerializer.SerializeToElement(page)).ConfigureAwait(false);
            foreach (var row in result.GetProperty("rows").EnumerateArray())
            {
                keys.Add(row.GetProperty("documentNumber").GetString() + "|" + row.GetProperty("lineItem").GetString());
            }

            var next = result.GetProperty("nextCursor");
            cursor = next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (cursor is not null);

        return keys;
    }
}

/// <summary>
/// 一個案件的建案紀錄：每個動作的耗時、每項比對的結果與失敗原因。給了資料夾時另存每個動作的回應，
/// 方便對照；echo 用來把不一致與失敗即時印到主機視窗。
/// </summary>
internal sealed class SmeCaseLog(string caseName, string? directory = null, Action<string>? echo = null)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private int sequence;
    private int matched;
    private int mismatched;

    public string CaseName { get; } = caseName;
    public List<string> Lines { get; } = [];
    public bool Failed { get; private set; }
    public bool AllMatched => mismatched == 0 && !Failed;
    public int MatchedCount => matched;
    public string Summary => $"{matched} 項一致、{mismatched} 項不一致";

    public async Task<JsonElement> DispatchAsync(SmeDispatch dispatch, string action, object payload)
    {
        var stopwatch = Stopwatch.StartNew();
        var element = payload as JsonElement? ?? JsonSerializer.SerializeToElement(payload);
        var result = await dispatch(action, element).ConfigureAwait(false);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            sequence++;
            File.WriteAllText(
                Path.Combine(directory, $"{sequence:00}-{action}.json"),
                JsonSerializer.Serialize(result, Indented),
                new UTF8Encoding(false));
        }

        Lines.Add($"- {action}：完成（{stopwatch.ElapsedMilliseconds:N0} ms）");
        return result;
    }

    public void Compare(string label, HashSet<string> expected, HashSet<string> actual)
    {
        var missing = expected.Except(actual).Order(StringComparer.Ordinal).ToList();
        var extra = actual.Except(expected).Order(StringComparer.Ordinal).ToList();
        if (missing.Count == 0 && extra.Count == 0)
        {
            matched++;
            Lines.Add($"- {label}：一致，{expected.Count:N0} 筆");
            return;
        }

        mismatched++;
        Lines.Add($"- {label}：不一致，預期 {expected.Count:N0} 筆、JET {actual.Count:N0} 筆；"
            + $"JET 少了 {missing.Count:N0} 筆（例如 {string.Join("、", missing.Take(5))}），"
            + $"多了 {extra.Count:N0} 筆（例如 {string.Join("、", extra.Take(5))}）");
        echo?.Invoke($"[seed] {CaseName} {label} 不一致：預期 {expected.Count}、JET {actual.Count}。");
    }

    public void CompareNumber(string label, decimal expected, decimal actual)
    {
        if (expected == actual)
        {
            matched++;
            Lines.Add($"- {label}：一致，{expected:N0}");
            return;
        }

        mismatched++;
        Lines.Add($"- {label}：不一致，預期 {expected:N0}、JET {actual:N0}");
        echo?.Invoke($"[seed] {CaseName} {label} 不一致：預期 {expected}、JET {actual}。");
    }

    public void Fail(Exception exception)
    {
        Failed = true;
        var error = JetWebMessageBridge.ToErrorDto(exception);
        Lines.Add($"- 失敗：code={error.Code}：{error.Message}");
        echo?.Invoke($"[seed] {CaseName} 建立失敗 code={error.Code}: {error.Message}");
    }

    public override string ToString() => $"## {CaseName}{Environment.NewLine}{string.Join(Environment.NewLine, Lines)}";
}
