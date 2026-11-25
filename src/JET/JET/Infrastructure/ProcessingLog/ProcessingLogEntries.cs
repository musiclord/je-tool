using System.Globalization;
using System.Text.Json;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>寫進哪一份處理紀錄。</summary>
internal enum ProcessingLogTarget
{
    Gl,
    Tb
}

/// <summary>處理紀錄的一筆：寫進哪一份、步驟名稱，以及內容的每一行；標題列的時間與人員由寫檔時加上。</summary>
internal sealed record ProcessingLogEntry(ProcessingLogTarget Target, string Title, IReadOnlyList<string> Lines);

/// <summary>匯出時寫完的一張工作表：報告種類、工作表名稱、資料列數，以及標了符合條件的列數（只有條件篩選報告的明細表有）。</summary>
internal sealed record ProcessingLogSheet(string ReportKind, string SheetName, long Rows, long? MarkedRows);

/// <summary>
/// 一個已存情境在畫面打開時讀到的條件文字、資料庫條件式與兩個筆數；讀不到時只有位置，其他是 null。
/// </summary>
internal sealed record ProcessingLogScenarioDetail(
    int Position,
    string? ConditionText,
    string? ConditionSql,
    long? VoucherCount,
    long? SummaryRowCount);

/// <summary>
/// 依操作的輸入與回應寫出處理紀錄的內容。只讀已經完成的結果，不重新判斷任何審計條件。
/// 欄位與規則的名稱取自欄位目錄與預篩選規則登錄；金額、核准日與過帳狀態的算法，
/// 沿用底稿「自動化工具-檔案欄位資訊」的說明句，所以紀錄和底稿寫的是同一句。
/// 認不得的回應欄位當成沒有，不讓紀錄的寫法擋住操作。
/// </summary>
internal static class ProcessingLogEntries
{
    private const string NotFilled = "（未填）";
    private const string Indent = "    ";

    private static readonly IReadOnlyDictionary<string, string> ReportTitles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["export.validationReports"] = "匯出資料驗證報告",
        ["export.prescreenReport"] = "匯出預篩選報告",
        ["export.criteriaSelectionReport"] = "匯出條件篩選報告",
        ["export.workingPaperStream"] = "匯出工作底稿",
    };

    /// <summary>會寫進處理紀錄的操作：都是改動案件資料或產生檔案的操作；查詢、開案與刪除不寫。</summary>
    internal static bool Handles(string action) => action switch
    {
        "project.create" or "project.update"
            or "import.gl.fromFile" or "import.tb.fromFile"
            or "import.accountMapping.fromFile" or "accountMapping.save" or "accountCategory.save"
            or "import.authorizedPreparer.fromFile" or "import.authorizedPreparer.clear"
            or "import.holiday" or "import.holiday.fromFile" or "import.makeupDay" or "import.makeupDay.fromFile"
            or "calendar.setNonWorkingDays"
            or "mapping.commit.gl" or "mapping.commit.tb"
            or "validate.run" or "prescreen.run"
            or "filter.scenario.save" or "filter.scenario.remove" or "filter.scenario.recheck" or "filter.commit"
            or "export.accountMappingTemplate" => true,
        _ => ReportTitles.ContainsKey(action)
    };

    /// <summary>
    /// 要另外讀條件文字與筆數的情境位置：儲存一個情境時是那一個，整批儲存與重新檢查時是全部；結果沒有算好時不讀。
    /// </summary>
    internal static IReadOnlyList<int> ScenarioPositionsToRead(string action, JsonElement result)
    {
        if (Object(result, "resultRef") is null)
        {
            return [];
        }

        return action switch
        {
            "filter.scenario.save" when Int(result, "position") is { } position => [position],
            "filter.commit" or "filter.scenario.recheck" => Enumerable.Range(1, (int)(Long(result, "savedCount") ?? 0)).ToArray(),
            _ => []
        };
    }

    internal static IReadOnlyList<ProcessingLogEntry> Describe(
        string action,
        JsonElement payload,
        JsonElement result,
        IReadOnlyList<ProcessingLogSheet> sheets,
        IReadOnlyList<ProcessingLogScenarioDetail> scenarios)
    {
        if (ReportTitles.TryGetValue(action, out var reportTitle))
        {
            var lines = ReportLines(action, result, sheets);
            // 資料驗證報告也寫 GL 與 TB 逐科目比對，兩份紀錄都記。
            return action == "export.validationReports" ? Both(reportTitle, lines) : Gl(reportTitle, lines);
        }

        return action switch
        {
            "project.create" => Both("建立案件", ProjectCreated(payload, result)),
            "project.update" => Both("修改案件資料", ProjectUpdated(result)),
            "import.gl.fromFile" => [new(ProcessingLogTarget.Gl, "匯入 GL", Imported("GL", payload, result))],
            "import.tb.fromFile" => [new(ProcessingLogTarget.Tb, "匯入 TB", Imported("TB", payload, result))],
            "import.accountMapping.fromFile" => Gl("匯入科目配對", AccountMappingImported(payload, result)),
            "accountMapping.save" => Gl("儲存科目配對", AccountMappingSaved(result)),
            "accountCategory.save" => Gl("儲存科目分類", CategoriesSaved(result)),
            "import.authorizedPreparer.fromFile" => Gl("匯入授權編製人員清單", PreparersImported(payload, result)),
            "import.authorizedPreparer.clear" => Gl("清除授權編製人員清單", ["已清除授權編製人員清單。"]),
            "import.holiday" => Gl("匯入國定假日", CalendarDays("國定假日", null, payload, result)),
            "import.holiday.fromFile" => Gl("匯入國定假日", CalendarDays("國定假日", payload, payload, result)),
            "import.makeupDay" => Gl("匯入補班日", CalendarDays("補班日", null, payload, result)),
            "import.makeupDay.fromFile" => Gl("匯入補班日", CalendarDays("補班日", payload, payload, result)),
            "calendar.setNonWorkingDays" => Gl("設定每週休息日", NonWorkingDays(result)),
            "mapping.commit.gl" => Gl("確認 GL 欄位配對", GlMappingCommitted(result)),
            "mapping.commit.tb" => [new(ProcessingLogTarget.Tb, "確認 TB 欄位配對", TbMappingCommitted(result))],
            "validate.run" =>
            [
                new(ProcessingLogTarget.Gl, "資料驗證", GlValidation(result)),
                new(ProcessingLogTarget.Tb, "資料驗證", TbValidation(result)),
            ],
            "prescreen.run" => Gl("預篩選", Prescreen(result)),
            "filter.scenario.save" => Gl("儲存篩選情境", ScenariosSaved(result, scenarios, onlyPosition: Int(result, "position"))),
            "filter.commit" => Gl("儲存全部篩選情境", ScenariosSaved(result, scenarios, onlyPosition: null)),
            "filter.scenario.recheck" => Gl("重新檢查篩選情境", ScenariosRechecked(result, scenarios)),
            "filter.scenario.remove" => Gl("移除篩選情境", ScenarioRemoved(payload, result)),
            "export.accountMappingTemplate" => Gl("產生科目配對範本", AccountMappingTemplate(result)),
            _ => []
        };
    }

    private static ProcessingLogEntry[] Both(string title, IReadOnlyList<string> lines) =>
        [new(ProcessingLogTarget.Gl, title, lines), new(ProcessingLogTarget.Tb, title, lines)];

    private static ProcessingLogEntry[] Gl(string title, IReadOnlyList<string> lines) =>
        [new(ProcessingLogTarget.Gl, title, lines)];

    private static List<string> ProjectCreated(JsonElement payload, JsonElement result) =>
    [
        $"案件名稱：{Text(result, "projectId") ?? NotFilled}",
        $"案件編號：{Filled(Text(payload, "projectCode"))}",
        $"客戶名稱：{Filled(Text(payload, "clientName"))}",
        $"查核期間：{Text(payload, "periodStart")} 至 {Text(payload, "periodEnd")}",
        $"財報準備日：{Filled(Text(payload, "prepStartDate"))}",
    ];

    private static List<string> ProjectUpdated(JsonElement result)
    {
        var project = Object(result, "project") ?? default;
        return
        [
            $"案件編號：{Filled(Text(project, "projectCode"))}",
            $"客戶名稱：{Filled(Text(project, "clientName"))}",
            $"財報準備日：{Filled(Text(project, "prepStartDate"))}",
        ];
    }

    private static List<string> Imported(string dataset, JsonElement payload, JsonElement result)
    {
        var append = string.Equals(Text(payload, "mode"), "append", StringComparison.Ordinal);
        var lines = new List<string> { append ? "匯入方式：加在原有資料之後" : "匯入方式：取代原有資料" };

        // 回應列出這一批的全部來源；追加時這次匯入的來源排在最後，和 payload 的來源一一對應。
        var requested = Array(payload, "sources") is { } list
            ? list.EnumerateArray().ToArray()
            : [payload];
        var sources = Array(result, "sources")?.EnumerateArray().ToArray() ?? [];
        var offset = Math.Max(0, sources.Length - requested.Length);
        for (var index = 0; index < requested.Length; index++)
        {
            var source = offset + index < sources.Length ? sources[offset + index] : default;
            var path = Text(requested[index], "filePath") ?? Text(source, "fileName") ?? NotFilled;
            var number = Long(source, "sourceNo") ?? index + 1;
            lines.Add($"來源 {number}：{path}");
            var extension = Path.GetExtension(path);
            var isAccess = extension.Equals(".mdb", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".accdb", StringComparison.OrdinalIgnoreCase);
            var isWorkbook = extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".xls", StringComparison.OrdinalIgnoreCase);
            if ((Text(source, "sheetName") ?? Text(requested[index], "sheetName")) is { } sheet)
            {
                lines.Add($"{Indent}{(isAccess ? "資料表" : "工作表")}：{sheet}");
            }
            else if (isWorkbook)
            {
                // 沒有指定工作表時，JET 讀活頁簿的第一個工作表。
                lines.Add($"{Indent}工作表：未指定，JET 讀第一個工作表");
            }

            if (Text(source, "encoding") is { } encoding)
            {
                lines.Add($"{Indent}編碼：{encoding}");
            }

            if (!isAccess)
            {
                lines.Add($"{Indent}欄名列：第 {Long(source, "headerRow") ?? 1} 列");
            }

            lines.Add($"{Indent}讀入筆數：{Count(Long(source, "rowCount"))}");
        }

        if (append)
        {
            lines.Add($"這次加入：{Count(Long(result, "addedRowCount"))} 筆");
        }

        lines.Add($"{dataset} 合計：{Count(Long(result, "rowCount"))} 筆");
        var columns = Strings(result, "columns");
        lines.Add($"欄位（{columns.Count} 欄）：{string.Join("、", columns)}");
        return lines;
    }

    private static List<string> GlMappingCommitted(JsonElement result)
    {
        var mapping = StringMap(result, "mapping");
        var amountModeName = Text(result, "amountMode");
        var lines = new List<string> { "欄位配對（JET 欄位 ← GL 檔欄名）：" };
        var slots = JetFieldCatalog.GlMappingSlots.Where(static slot => !slot.IsLiteral).OrderBy(static slot => slot.Order).ToArray();
        foreach (var slot in slots)
        {
            if (mapping.TryGetValue(slot.Key, out var column))
            {
                lines.Add($"{Indent}{slot.Label} ← {column}");
            }
        }

        var approvalDateMode = Text(result, "approvalDateMode");
        var unmapped = slots
            .Where(slot => !mapping.ContainsKey(slot.Key)
                && (slot.RequiredModes.Count == 0 || amountModeName is not null && slot.RequiredModes.Contains(amountModeName))
                && !(slot.Key == GlMappingKeys.ApprovalDate && approvalDateMode == ApprovalDateModeNames.SameAsGlDate))
            .Select(static slot => slot.Label)
            .ToArray();
        if (unmapped.Length > 0)
        {
            lines.Add($"沒有配對的欄位：{string.Join("、", unmapped)}");
        }

        if (!mapping.ContainsKey(GlMappingKeys.LineItem))
        {
            lines.Add("傳票文件項次：沒有配對，JET 在每張傳票內依列序自動編號。");
        }

        var spec = GlSpec(result, mapping, amountModeName);
        if (approvalDateMode == ApprovalDateModeNames.SameAsGlDate && spec is not null
            && GeneratedSentence(JetFieldCatalog.GlApprovalDate, spec) is { } approvalSentence)
        {
            lines.Add($"傳票核准日：{approvalSentence}");
        }

        if (spec is not null && GeneratedSentence(JetFieldCatalog.GlAmount, spec) is { } amountSentence)
        {
            lines.Add($"金額：{amountSentence}");
        }

        lines.Add(spec is not null && mapping.ContainsKey(GlMappingKeys.PostingStatus)
            && GeneratedSentence(JetFieldCatalog.GlPostingStatus, spec) is { } postingSentence
                ? $"過帳狀態：{postingSentence}"
                : "過帳狀態：未設定過帳狀態篩選");

        if (mapping.ContainsKey(GlMappingKeys.IsManual) && Object(result, "manualAutoPolicy") is { } manual)
        {
            lines.Add($"人工/自動分錄：{ManualAutoCodes(manual)}");
            lines.Add($"人工/自動分錄的空白：{BlankValueKind(Text(manual, "blankValueKind"))}");
        }

        var rdeFields = Array(result, "rdeFields")?.EnumerateArray().ToArray() ?? [];
        if (rdeFields.Length == 0)
        {
            lines.Add("攸關資料元素：未保留額外的攸關資料元素欄位");
        }
        else
        {
            lines.Add($"攸關資料元素（{rdeFields.Length} 個）：");
            lines.AddRange(rdeFields.Select(field =>
                $"{Indent}{Text(field, "label")} ← {Text(field, "sourceColumn")}（{RdeValueType(Text(field, "valueType"))}）"));
        }

        var fillDown = Strings(result, "fillDownFields");
        lines.Add(fillDown.Count == 0
            ? "往下填補空白：未往下填補空白"
            : $"往下填補空白：{string.Join("、", fillDown.Select(key => JetFieldCatalog.MappingSlotLabel(JetFieldCatalog.GlMappingSlots, key)))}，"
              + $"共補 {Count(Long(result, "fillDownCount"))} 格");

        foreach (var notice in Array(result, "dateNotices")?.EnumerateArray() ?? Enumerable.Empty<JsonElement>())
        {
            lines.Add(DateNotice(notice));
        }

        lines.Add($"存入的分錄：{Count(Long(result, "projectedRowCount"))} 筆，GL 與 GL(未納入測試) 合計");
        return lines;
    }

    private static List<string> TbMappingCommitted(JsonElement result)
    {
        var mapping = StringMap(result, "mapping");
        var lines = new List<string> { "欄位配對（JET 欄位 ← TB 檔欄名）：" };
        foreach (var slot in JetFieldCatalog.TbMappingSlots.Where(static slot => !slot.IsLiteral).OrderBy(static slot => slot.Order))
        {
            if (mapping.TryGetValue(slot.Key, out var column))
            {
                lines.Add($"{Indent}{slot.Label} ← {column}");
            }
        }

        if (TbChangeModeNames.TryParse(Text(result, "changeMode"), out var changeMode) && changeMode != TbChangeMode.DirectChange)
        {
            var probe = Probe(JetFieldCatalog.TbChangeAmount);
            if (FieldInfoProjector.WithCurrentDescriptions([probe], new TbMappingSpec(mapping, changeMode))[0].Description is { } sentence)
            {
                lines.Add($"年度變動金額：{sentence}");
            }
        }

        lines.Add($"存入的科目：{Count(Long(result, "projectedRowCount"))} 筆");
        return lines;
    }

    private static List<string> GlValidation(JsonElement result)
    {
        var stats = Object(result, "stats") ?? default;
        var population = Object(result, "populationSummary") ?? default;
        var raw = Object(population, "raw") ?? default;
        var gl = Object(population, "gl") ?? default;
        var excluded = Object(population, "excluded") ?? default;
        var completeness = Object(result, "completenessTest") ?? default;
        var storedTotals = Object(completeness, "storedTotals") ?? default;
        var balance = Object(result, "voucherBalanceTest") ?? default;
        var blanks = Object(result, "blankRecordsTest") ?? default;
        var sourceQuality = Object(result, "sourceQuality") ?? default;
        var acrossDates = Object(result, "voucherNumberAcrossGlDates") ?? default;
        var sampling = Object(result, "infSamplingTest") ?? default;

        return
        [
            $"查核期間：{Text(stats, "periodStart")} 至 {Text(stats, "periodEnd")}",
            $"匯入的分錄：{Count(Long(raw, "rowCount"))} 筆，借方合計 {Amount(raw, "totalDebit")}，貸方合計 {Amount(raw, "totalCredit")}",
            $"GL：{Count(Long(gl, "rowCount"))} 筆分錄、{Count(Long(gl, "voucherCount"))} 張傳票，"
                + $"借方合計 {Amount(gl, "totalDebit")}，貸方合計 {Amount(gl, "totalCredit")}",
            $"GL(未納入測試)：{Count(Long(excluded, "rowCount"))} 筆；總帳日期空白、無法辨識或不在查核期間 {Count(Long(excluded, "byPeriodCount"))} 筆，"
                + $"過帳狀態不符 {Count(Long(excluded, "byPostingStatusCount"))} 筆",
            $"存入筆數與合計核對：{Match("筆數", Bool(storedTotals, "rowCountMatch"))}，{Match("合計", Bool(storedTotals, "amountMatch"))}",
            AccountComparison(completeness),
            $"借貸不平測試：借貸不平的傳票 {Count(Long(balance, "unbalancedVoucherCount"))} 張",
            $"資料缺漏輔助檢視：空白傳票號碼 {Count(Long(blanks, "blankVoucherNumberCount"))} 筆，"
                + $"空白科目編號 {Count(Long(blanks, "blankAccountNumberCount"))} 筆",
            Long(blanks, "approvalDateNotInPeriodCount") is { } outside
                ? $"核准日不在期間：{Count(outside)} 筆"
                : $"核准日不在期間：無法執行。{Text(blanks, "approvalDateNotInPeriodNaReason")}",
            $"總帳日期空白：{Count(Long(sourceQuality, "findingCount"))} 筆",
            $"同一傳票號碼出現在不同總帳日期：{Count(Long(acrossDates, "voucherNumberCount"))} 個號碼，{Count(Long(acrossDates, "entryCount"))} 筆分錄",
            $"資料可靠性測試：指定 {Count(Long(sampling, "requestedSampleSize"))} 筆，抽出 {Count(Long(sampling, "sampleSize"))} 筆，"
                + $"隨機種子序號 {Long(sampling, "seed")}",
        ];
    }

    private static List<string> TbValidation(JsonElement result)
    {
        var stats = Object(result, "stats") ?? default;
        return
        [
            $"查核期間：{Text(stats, "periodStart")} 至 {Text(stats, "periodEnd")}",
            AccountComparison(Object(result, "completenessTest") ?? default),
        ];
    }

    /// <summary>有無法執行的原因才算沒有比對；狀態沒有差異時也是 na，不能拿狀態判斷。</summary>
    private static string AccountComparison(JsonElement completeness) =>
        Text(completeness, "naReason") is { } reason
            ? $"GL 與 TB 逐科目比對：無法執行。{reason}"
            : $"GL 與 TB 逐科目比對：差異科目 {Count(Long(completeness, "diffAccountCount"))} 個";

    private static List<string> Prescreen(JsonElement result)
    {
        var rulePeriod = Object(result, "rulePeriod") ?? default;
        var zeros = Long(Object(result, "trailingZeros") ?? default, "zerosThreshold");
        var lines = new List<string> { $"母體：查核期間內的 GL 分錄 {Count(Long(rulePeriod, "population"))} 筆" };
        var titles = PrescreenRuleDefinitions.All.ToDictionary(static rule => rule.Key, static rule => rule.Title, StringComparer.Ordinal);
        foreach (var rule in Array(rulePeriod, "rules")?.EnumerateArray() ?? Enumerable.Empty<JsonElement>())
        {
            var key = Text(rule, "key") ?? string.Empty;
            var title = titles.GetValueOrDefault(key, key);
            if (Text(rule, "naReason") is { } naReason)
            {
                lines.Add($"{title}：無法執行。{naReason}");
                continue;
            }

            var threshold = key == PrescreenRuleKeys.TrailingZeros && zeros is not null ? $"（尾數連續 {zeros} 個 0）" : string.Empty;
            lines.Add($"{title}：{Count(Long(rule, "hitLines"))} 筆分錄，{Count(Long(rule, "hitVouchers"))} 張傳票{threshold}");
        }

        var preparers = Object(result, "preparerSummary") ?? default;
        lines.Add(Text(preparers, "naReason") is { } preparerReason
            ? $"依分錄編製者彙總：無法執行。{preparerReason}"
            : $"依分錄編製者彙總：{Count(Long(preparers, "totalPreparerCount"))} 位人員");
        lines.Add($"較少使用之科目：GL 有 {Count(Long(Object(result, "rareAccounts") ?? default, "distinctAccountCount"))} 個科目");
        return lines;
    }

    private static List<string> ScenariosSaved(
        JsonElement result, IReadOnlyList<ProcessingLogScenarioDetail> details, int? onlyPosition)
    {
        var scenarios = Array(result, "scenarios")?.EnumerateArray().ToArray() ?? [];
        var computed = Object(result, "resultRef") is not null;
        var lines = new List<string>();
        for (var index = 0; index < scenarios.Length; index++)
        {
            var position = index + 1;
            if (onlyPosition is not null && position != onlyPosition)
            {
                continue;
            }

            lines.AddRange(Scenario(position, scenarios[index], details.FirstOrDefault(detail => detail.Position == position), computed));
        }

        if (!computed)
        {
            lines.Add("有情境不能套用目前的篩選規則，這次沒有計算符合條件的筆數。");
        }

        lines.AddRange(ScenarioCheckProblems(result));
        lines.Add($"目前共有 {Count(Long(result, "savedCount"))} 個篩選情境");
        return lines;
    }

    private static List<string> ScenariosRechecked(JsonElement result, IReadOnlyList<ProcessingLogScenarioDetail> details)
    {
        var check = Object(result, "filterScenarioCheck") ?? default;
        var lines = new List<string>
        {
            string.Equals(Text(check, "status"), "current", StringComparison.Ordinal)
                ? "全部情境都能套用目前的篩選規則。"
                : "有情境不能套用目前的篩選規則，結果沒有重新計算。",
            $"改用目前規則的情境：{Count(Long(check, "recalculatedCount"))} 個",
        };
        var scenarios = Array(result, "scenarios")?.EnumerateArray().ToArray() ?? [];
        var computed = Object(result, "resultRef") is not null;
        for (var index = 0; index < scenarios.Length; index++)
        {
            lines.AddRange(Scenario(index + 1, scenarios[index], details.FirstOrDefault(detail => detail.Position == index + 1), computed));
        }

        lines.AddRange(ScenarioCheckProblems(result));
        return lines;
    }

    private static IEnumerable<string> Scenario(int position, JsonElement scenario, ProcessingLogScenarioDetail? detail, bool computed)
    {
        yield return $"情境 {position}：{Text(scenario, "name")}";
        yield return $"篩選動機：{Filled(Text(scenario, "rationale"))}";
        if (detail?.ConditionText is { } condition)
        {
            yield return $"條件：{condition}";
        }

        if (detail?.ConditionSql is { } sql)
        {
            yield return "資料庫條件式：";
            foreach (var line in sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                yield return Indent + line;
            }
        }

        if (detail?.VoucherCount is { } vouchers)
        {
            yield return $"符合條件的傳票數量：{Count(vouchers)}";
        }

        if (detail?.SummaryRowCount is { } rows)
        {
            yield return $"符合條件的分錄筆數：{Count(rows)}";
        }

        if (computed && (detail is null || detail.ConditionText is null))
        {
            yield return "這個情境的條件與筆數沒有讀到，請在第五步打開這個情境確認。";
        }
    }

    private static IEnumerable<string> ScenarioCheckProblems(JsonElement result)
    {
        var problems = Array(Object(result, "filterScenarioCheck") ?? default, "problems")?.EnumerateArray() ?? Enumerable.Empty<JsonElement>();
        foreach (var problem in problems)
        {
            yield return $"情境 {Long(problem, "position")}「{Text(problem, "name")}」需要修改：{string.Join("", Strings(problem, "messages"))}";
        }
    }

    private static List<string> ScenarioRemoved(JsonElement payload, JsonElement result) =>
    [
        $"移除情境 {Long(payload, "position")}：{Filled(Text(payload, "expectedName"))}",
        $"目前共有 {Count(Long(result, "savedCount"))} 個篩選情境",
    ];

    private static List<string> ReportLines(string action, JsonElement result, IReadOnlyList<ProcessingLogSheet> sheets)
    {
        JsonElement[] reports;
        if (Array(result, "publishedReports") is { } several)
        {
            reports = several.EnumerateArray().ToArray();
        }
        else
        {
            reports = Object(result, "publishedReport") is { } single ? new[] { single } : System.Array.Empty<JsonElement>();
        }

        var lines = new List<string>();
        foreach (var report in reports)
        {
            lines.Add($"檔案：{Text(report, "fullPath") ?? Text(report, "fileName")}");
            var kind = Text(report, "kind");
            var written = sheets.Where(sheet => sheet.ReportKind == kind).ToList();
            if (written.Count == 0 && action == "export.workingPaperStream")
            {
                // 底稿的回應另外列出每張工作表寫了幾列。
                written = (Array(result, "sheetStats")?.EnumerateArray() ?? Enumerable.Empty<JsonElement>())
                    .Select(stat => new ProcessingLogSheet(kind ?? string.Empty, Text(stat, "sheetName") ?? string.Empty,
                        Long(stat, "rowsWritten") ?? 0, null))
                    .ToList();
            }

            // 只列有資料列的工作表與條件篩選報告的明細表；固定格式的說明頁與摘要頁沒有資料列。
            var listed = written.Where(static sheet => sheet.Rows > 0 || sheet.MarkedRows is not null).ToArray();
            if (listed.Length == 0)
            {
                continue;
            }

            lines.Add("各工作表的資料列數：");
            lines.AddRange(listed.Select(sheet => sheet.MarkedRows is { } marked
                ? $"{Indent}{sheet.SheetName}：{Count(sheet.Rows)} 列，其中 {Count(marked)} 列的 Final_TAG 是 Y"
                : $"{Indent}{sheet.SheetName}：{Count(sheet.Rows)} 列"));
        }

        return lines;
    }

    private static List<string> AccountMappingImported(JsonElement payload, JsonElement result)
    {
        var lines = new List<string> { $"來源：{Text(payload, "filePath") ?? Text(result, "fileName")}" };
        if (Text(payload, "sheetName") is { } sheet)
        {
            lines.Add($"工作表：{sheet}");
        }

        lines.Add($"讀入科目：{Count(Long(result, "rowCount"))} 個");
        lines.Add($"分類留白、算成 Others 的科目：{Count(Long(result, "blankCategoryCount"))} 個");
        if (Long(result, "mappingOnlyCount") is { } mappingOnly)
        {
            lines.Add($"配對檔有、本案沒有的科目：{Count(mappingOnly)} 個");
        }

        if (Long(result, "unmappedCount") is { } unmapped)
        {
            lines.Add($"本案有、配對檔沒列的科目：{Count(unmapped)} 個");
        }

        if (Array(Object(result, "accountCategory") ?? default, "categories") is { } categories)
        {
            lines.Add($"分類：{string.Join("、", categories.EnumerateArray().Select(category => Text(category, "label")))}");
        }

        return lines;
    }

    private static List<string> AccountMappingSaved(JsonElement result) =>
        Long(result, "rowCount") is { } count
            ? [$"已儲存的科目：{Count(count)} 個", $"分類留白、算成 Others 的科目：{Count(Long(result, "blankCategoryCount"))} 個"]
            : ["已儲存科目的分類。"];

    private static List<string> CategoriesSaved(JsonElement result)
    {
        var categories = Array(result, "categories")?.EnumerateArray().Select(category => Text(category, "label")).ToArray() ?? [];
        return [$"分類（{categories.Length} 個）：{string.Join("、", categories)}"];
    }

    private static List<string> PreparersImported(JsonElement payload, JsonElement result)
    {
        var lines = new List<string> { $"來源：{Text(payload, "filePath") ?? Text(result, "fileName")}" };
        if (Text(payload, "sheetName") is { } sheet)
        {
            lines.Add($"工作表：{sheet}");
        }

        lines.Add($"識別欄：{Text(result, "sourceColumn")}");
        lines.Add($"讀入的資料列：{Count(Long(result, "sourceRowCount"))} 列，識別欄空白 {Count(Long(result, "blankRowCount"))} 列，"
            + $"重複 {Count(Long(result, "duplicateRowCount"))} 列");
        lines.Add($"授權編製人員：{Count(Long(result, "rowCount"))} 位");
        lines.Add(Long(result, "matchedPreparerCount") is { } matched
            ? $"出現在 GL 傳票建立人員的：{Count(matched)} 位"
            : "出現在 GL 傳票建立人員的：GL 還沒有確認傳票建立人員的配對，沒有比對");
        return lines;
    }

    private static List<string> CalendarDays(string label, JsonElement? fileSource, JsonElement payload, JsonElement result)
    {
        var lines = new List<string>();
        if (fileSource is { } file)
        {
            lines.Add($"來源：{Text(file, "filePath")}");
            if (Text(file, "sheetName") is { } sheet)
            {
                lines.Add($"工作表：{sheet}");
            }
        }

        lines.Add($"{label}：{Count(Long(result, "count"))} 天");
        if (fileSource is null)
        {
            lines.AddRange(Strings(payload, "dates").Select(date => Indent + date));
        }

        return lines;
    }

    private static List<string> NonWorkingDays(JsonElement result)
    {
        // 和第二步的勾選一樣從星期一排到星期日。
        string[] names = ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"];
        var days = (Array(result, "nonWorkingDays")?.EnumerateArray() ?? Enumerable.Empty<JsonElement>())
            .Where(static day => day.ValueKind == JsonValueKind.Number && day.TryGetInt32(out var value) && value is >= 0 and <= 6)
            .Select(static day => day.GetInt32())
            .OrderBy(static day => day == 0 ? 7 : day)
            .Select(day => names[day])
            .ToArray();
        return [days.Length == 0 ? "每週休息日：沒有" : $"每週休息日：{string.Join("、", days)}"];
    }

    private static List<string> AccountMappingTemplate(JsonElement result) =>
        string.Equals(Text(result, "disposition"), "kept", StringComparison.Ordinal)
            ? [$"檔案：{Text(result, "filePath")}", "檔案已經存在，保留原檔，沒有重新產生。"]
            : [$"檔案：{Text(result, "filePath")}", $"列出的科目：{Count(Long(result, "rowCount"))} 個"];

    private static string ManualAutoCodes(JsonElement manual)
    {
        var manualValues = string.Join("、", Strings(manual, "manualValues"));
        var automaticValues = string.Join("、", Strings(manual, "automaticValues"));
        return Text(manual, "unlistedValueKind") switch
        {
            "automatic" => $"人工代碼 {manualValues}，其餘非空白值為自動",
            "manual" => $"自動代碼 {automaticValues}，其餘非空白值為人工",
            _ => $"人工代碼 {manualValues}，自動代碼 {automaticValues}"
        };
    }

    private static string BlankValueKind(string? kind) => kind switch
    {
        "manual" => "視為人工",
        "automatic" => "視為自動",
        "unclassified" => "不判定人工或自動",
        _ => "先補齊來源資料"
    };

    private static string RdeValueType(string? valueType) => valueType switch
    {
        "date" => "日期",
        "amount" => "金額",
        _ => "文字"
    };

    private static string DateNotice(JsonElement notice)
    {
        var kind = Text(notice, "kind") switch
        {
            "unusualYear" => "年份不在 1900 到 2100 年之間",
            "textInWorkingPaper" => "日期早於 1900-03-01、底稿寫成文字",
            _ => "讀不成日期"
        };
        var handling = Text(notice, "handling") switch
        {
            "excluded" => "整列列入 GL(未納入測試)",
            "notUsed" => "那一格當成沒有日期",
            _ => "照來源資料保留"
        };
        return $"日期提醒：{Text(notice, "label")}（{Text(notice, "sourceColumn")}）有 {Count(Long(notice, "count"))} 筆{kind}，{handling}";
    }

    private static GlMappingSpec? GlSpec(JsonElement result, IReadOnlyDictionary<string, string> mapping, string? amountModeName)
    {
        if (!GlAmountModeNames.TryParse(amountModeName, out var amountMode))
        {
            return null;
        }

        // 回應裡保留了兩側代碼，說明句要寫出來；配對清單上只列來源欄，所以代碼另外放回。
        var full = new Dictionary<string, string>(mapping, StringComparer.Ordinal);
        foreach (var (key, value) in StringMap(result, "mapping", includeLiterals: true))
        {
            full[key] = value;
        }

        var posting = Object(result, "postingStatusPolicy") is { } policy
            ? new GlPostingStatusPolicy(Strings(policy, "acceptedValues"), Bool(policy, "includeBlank") ?? false)
            : null;
        var options = new GlMappingOptions(
            Text(result, "approvalDateMode") ?? ApprovalDateModeNames.Unmapped,
            posting,
            new GlManualAutoPolicy([], []),
            []);
        return new GlMappingSpec(full, amountMode) { Options = options };
    }

    /// <summary>底稿「自動化工具-檔案欄位資訊」裡系統產生欄位的說明句；配對沒有這一欄時是 null。</summary>
    private static string? GeneratedSentence(string jetField, GlMappingSpec spec) =>
        FieldInfoProjector.WithCurrentDescriptions([Probe(jetField)], spec)[0].Description;

    private static FieldInfoState Probe(string jetField) =>
        new(0, jetField, null, LegacyFieldKind.Text, 0, null, 0, false, FieldInfoOrigin.Generated, jetField);

    private static string Match(string label, bool? matched) => matched switch
    {
        true => $"{label}一致",
        false => $"{label}不一致",
        null => $"{label}無法核對"
    };

    private static string Filled(string? value) => string.IsNullOrWhiteSpace(value) ? NotFilled : value;

    private static string Count(long? value) =>
        value is { } number ? number.ToString("N0", CultureInfo.InvariantCulture) : "（不明）";

    private static string Amount(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number.ToString("#,##0.##########", CultureInfo.InvariantCulture)
            : "（不明）";

    private static JsonElement? Object(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static JsonElement? Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    private static long? Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static int? Int(JsonElement element, string name) =>
        Long(element, name) is { } number && number is >= int.MinValue and <= int.MaxValue ? (int)number : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    private static IReadOnlyList<string> Strings(JsonElement element, string name) =>
        Array(element, name)?.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()!)
            .ToArray() ?? [];

    /// <summary>配對清單：配對鍵對來源欄，空白的不算配對。字面值代碼（借方與貸方代碼）預設不列，算說明句時才放回。</summary>
    private static Dictionary<string, string> StringMap(JsonElement element, string name, bool includeLiterals = false)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return map;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                continue;
            }

            if (!includeLiterals && (JetFieldCatalog.IsGlLiteralMappingKey(property.Name) || JetFieldCatalog.IsTbLiteralMappingKey(property.Name)))
            {
                continue;
            }

            map[property.Name] = property.Value.GetString()!;
        }

        return map;
    }
}
