using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// GL 欄位可用性的處理器固定答案。第五步不能用 GL 沒有配對的欄位（裁定 2026-10-22-05），用到這種欄位的已存情境列進待修正清單（裁定 2026-10-22-04）；
/// 傳票文件項次自動編號與傳票核准日選「與總帳日期相同」都算已配對。第四步與第五步對同一條規則給同一個原因，
/// 缺傳票核准日的配對時，週末與假日兩張卡照常顯示總帳日期那一側，並說明只檢查了總帳日期。
/// 第一次失敗：Focused 收據 20261023-031239785-46183e1247de492ebc2157e840e38b45（10 則失敗，例如缺傳票核准日的配對時週末卡整張無法執行，
/// 用到未配對欄位的已存情境開案時沒有列進待修正清單）。
/// 裁定 2026-10-23-01 追加：傳票核准日不可用時，第四步資料驗證的「核准日不在期間」無法執行並寫原因，
/// 判斷讀欄位目錄算出的可用欄位，驗證報告照同一個結果寫 N/A。第一次失敗：Focused 收據
/// 20261023-101047314-3a877fec42034e99b98965fd1a79c0b2（5 則失敗：沒有配對時筆數仍是 0、回應沒有原因欄、存下的結果還不接受原因欄）。
/// </summary>
public sealed class GlFieldAvailabilityWorkflowTests
{
    private const string FieldFix = "，請回第三步欄位配對補上。";
    private const string ApprovalDateMissing = "「傳票核准日」尚未選擇來源欄";
    private const string CreatedByMissing = "「傳票建立人員」尚未選擇來源欄";

    // 傳票建立人員、核准人員、核准日、來源模組、人工或自動與傳票項次都沒有來源欄。
    // 2025-03-01 是星期六；需要假日時把 2025-03-04（星期二）設成假日。
    private static Task<string> SetupAsync(HandlerTestHost host, string provider, IReadOnlyList<string>? holidays = null) =>
        InlineWorkbookProject.SetupAsync(host, builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-1", "2025-03-01", "1000", "合成現金", "合成摘要", "10", 1)
                .AddRow("JV-1", "2025-03-01", "4000", "合成收入", "合成摘要", "10", 0)
                .AddRow("JV-2", "2025-03-04", "1000", "合成現金", "合成摘要", "20", 1)
                .AddRow("JV-2", "2025-03-04", "4000", "合成收入", "合成摘要", "20", 0),
            holidays: holidays, databaseProvider: provider, validateForDownstream: true);

    private static string Preview(object rule) => JsonSerializer.Serialize(new
    {
        scenario = new
        {
            name = "合成欄位條件",
            rationale = "確認欄位配對前置",
            groups = new[] { new { join = "AND", conditions = new[] { rule } } }
        }
    });

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task UnmappedCoreFields_AreRejectedForEveryComparison(string provider)
    {
        using var host = new HandlerTestHost();
        await SetupAsync(host, provider);
        var cases = new (object Rule, string[] Labels)[]
        {
            (new { join = "AND", type = "fieldValue", field = "createdBy", @operator = "contains", values = new[] { "E0" } }, ["傳票建立人員"]),
            (new { join = "AND", type = "fieldValue", field = "createdBy", @operator = "equals", value = "E01" }, ["傳票建立人員"]),
            (new { join = "AND", type = "fieldValue", field = "createdBy", @operator = "in", values = new[] { "E01" } }, ["傳票建立人員"]),
            (new { join = "AND", type = "fieldValue", field = "approvedBy", @operator = "notEquals", value = "X" }, ["傳票核准人員"]),
            (new { join = "AND", type = "fieldValue", field = "approvedBy", @operator = "isNotBlank" }, ["傳票核准人員"]),
            (new { join = "AND", type = "fieldValue", field = "sourceModule", @operator = "startsWith", value = "GL" }, ["分錄來源模組"]),
            (new { join = "AND", type = "fieldValue", field = "approvalDate", @operator = "after", value = "2025-01-01" }, ["傳票核准日"]),
            (new { join = "AND", type = "fieldValue", field = "approvalDate", @operator = "isWeekend" }, ["傳票核准日"]),
            (new { join = "AND", type = "fieldValue", field = "approvalDate", @operator = "between", from = "2025-01-01", to = "2025-12-31" }, ["傳票核准日"]),
            (new { join = "AND", type = "text", field = "createdBy", keywords = "E0", mode = "contains" }, ["傳票建立人員"]),
            (new { join = "AND", type = "dateRange", field = "approvalDate", from = "2025-01-01", to = "2025-12-31" }, ["傳票核准日"]),
            (new { join = "AND", type = "entityFrequency", field = "approvedBy", countUnit = "entries", countOperator = "lessThan", countFrom = 3 }, ["傳票核准人員"]),
            (new { join = "AND", type = "manualAuto", isManual = true }, ["人工/自動分錄"]),
            (new { join = "AND", type = "preparerEqualsApprover" }, ["傳票建立人員", "傳票核准人員"])
        };

        foreach (var (rule, labels) in cases)
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.preview", Preview(rule)));
            Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
            Assert.Equal(
                string.Join("；", labels.Select(label => $"第 1 組第 1 條：「{label}」尚未選擇來源欄")),
                error.Message);
        }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AutoNumberedLineItemAndApprovalDateSameAsGlDate_CountAsMapped(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host, provider);

        // 傳票項次沒有來源欄時逐張傳票自動編號，兩張傳票的第一筆都是 1。
        var lineItem = await host.DispatchAsync("filter.preview", Preview(
            new { join = "AND", type = "fieldValue", field = "lineItem", @operator = "equals", value = "1" }));
        Assert.Equal(2, lineItem.GetProperty("scenario").GetProperty("count").GetInt64());

        // 傳票核准日改選「與總帳日期相同」；JV-1 的兩筆在星期六。
        await RecommitApprovalDateSameAsGlDateAsync(host, projectId);
        await host.DispatchAsync("validate.run");

        var approval = await host.DispatchAsync("filter.preview", Preview(
            new { join = "AND", type = "fieldValue", field = "approvalDate", @operator = "isWeekend" }));
        Assert.Equal(2, approval.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SavedScenarioUsingUnmappedField_IsListedForEditOnLoad(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host, provider);
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new
            {
                name = "舊欄位情境",
                rationale = "合成舊案件",
                groups = new[] { new { join = "AND", conditions = new[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } } } }
            } }
        }));

        // 模擬裁定 2026-10-22-05 之前存下的情境：條件用到沒有配對的傳票建立人員，當時的檢查不擋「包含任一文字」。
        var definitionJson = await TextAsync(host, projectId, provider,
            "SELECT definition_json FROM filter_scenario WHERE position = 1;");
        var definition = JsonNode.Parse(definitionJson)!.AsObject();
        definition["groups"]![0]!["conditions"]!.AsArray().Add(JsonNode.Parse(
            """{"join":"AND","type":"fieldValue","field":"createdBy","operator":"contains","values":["E0"]}"""));
        await ExecuteAsync(host, projectId, provider,
            "UPDATE filter_scenario SET definition_json = @json WHERE position = 1;",
            ("@json", definition.ToJsonString()));

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));

        var check = loaded.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Equal(1, problem.GetProperty("position").GetInt32());
        Assert.Equal(
            ["第 1 組第 2 條：" + CreatedByMissing],
            problem.GetProperty("messages").EnumerateArray().Select(static message => message.GetString()).ToArray());
        // 改好之前照待修正的規則鎖住：篩選結果不可用，報表與第六步依此標成過期。
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("filterResultRef").ValueKind);
        Assert.True(loaded.GetProperty("staleState").GetProperty("filter").GetBoolean());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Step4AndStep5_GiveTheSameReasonForTheSameRule(string provider)
    {
        using var host = new HandlerTestHost();
        // 沒有期末財報準備日、科目配對與授權名單；假日檔已上傳，第五步照裁定 2026-10-04-01 本來就不檢查假日檔。
        await SetupAsync(host, provider, holidays: ["2025-03-04"]);
        var prescreen = await host.DispatchAsync("prescreen.run");
        var step4 = prescreen.GetProperty("rulePeriod").GetProperty("rules").EnumerateArray()
            .ToDictionary(
                static rule => rule.GetProperty("key").GetString()!,
                static rule => rule.GetProperty("naReason").GetString(),
                StringComparer.Ordinal);

        const string prepStartDate = "尚未填寫期末財報準備日，請先在第一步按「修改案件資料」填寫";
        const string noCategory = "科目配對中尚無任何科目設定分類，請先在第四步設定";
        const string noPreparerList = "尚未匯入授權編製人員清單，請先在第二步匯入";
        var expected = new Dictionary<string, (string? Step4, string[] Step5)>(StringComparer.Ordinal)
        {
            [PrescreenRuleKeys.PrepPeriodApproval] = (ApprovalDateMissing + FieldFix + prepStartDate + "。", [ApprovalDateMissing, prepStartDate]),
            [PrescreenRuleKeys.SuspiciousKeywords] = (null, []),
            [PrescreenRuleKeys.UnexpectedAccountPair] = (noCategory + "。", [noCategory]),
            [PrescreenRuleKeys.TrailingZeros] = (null, []),
            [PrescreenRuleKeys.WeekendPosting] = (null, []),
            [PrescreenRuleKeys.WeekendApproval] = (ApprovalDateMissing + FieldFix, [ApprovalDateMissing]),
            [PrescreenRuleKeys.HolidayPosting] = (null, []),
            [PrescreenRuleKeys.HolidayApproval] = (ApprovalDateMissing + FieldFix, [ApprovalDateMissing]),
            [PrescreenRuleKeys.BlankDescription] = (null, []),
            [PrescreenRuleKeys.NonAuthorizedPreparer] = (noPreparerList + "。" + CreatedByMissing + FieldFix, [noPreparerList, CreatedByMissing])
            // 第四步照舊版拿掉編製分錄較少的人員與使用較少的科目兩條規則（裁定 2026-10-29-27），這裡跟著少兩列；第一次失敗是建置失敗：收據 20261030-163008036-bb2e987946cc4c72a351610e661109c5。
        };
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), step4.Keys.Order(StringComparer.Ordinal));

        foreach (var (key, (step4Reason, step5Reasons)) in expected)
        {
            Assert.Equal(step4Reason, step4[key]);
            var payload = Preview(new { join = "AND", type = "prescreen", prescreenKey = key });
            if (step5Reasons.Length == 0)
            {
                await host.DispatchAsync("filter.preview", payload);
                continue;
            }

            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.preview", payload));
            Assert.Equal(string.Join("；", step5Reasons.Select(static reason => "第 1 組第 1 條：" + reason)), error.Message);
        }

        // 人員彙總與集中度沿用同一條規則的原因。
        Assert.Equal(CreatedByMissing + FieldFix, prescreen.GetProperty("preparerSummary").GetProperty("naReason").GetString());
        Assert.Equal(CreatedByMissing + FieldFix, prescreen.GetProperty("concentration").GetProperty("naReason").GetString());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MissingApprovalDate_ActivityCardsShowPostingSideAndSayOnlyGlDateWasChecked(string provider)
    {
        using var host = new HandlerTestHost();
        await SetupAsync(host, provider, holidays: ["2025-03-04"]);

        var prescreen = await host.DispatchAsync("prescreen.run");

        foreach (var card in new[] { "weekendActivity", "holidayActivity" })
        {
            var activity = prescreen.GetProperty(card);
            Assert.Equal(JsonValueKind.Null, activity.GetProperty("naReason").ValueKind);
            Assert.Equal("只檢查了總帳日期。" + ApprovalDateMissing + FieldFix, activity.GetProperty("approvalNaReason").GetString());
            Assert.Equal("V", activity.GetProperty("status").GetString());
            Assert.Equal(2, activity.GetProperty("postingCount").GetInt64());
            Assert.Equal(JsonValueKind.Null, activity.GetProperty("approvalCount").ValueKind);
        }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MissingHolidayCalendar_HolidayCardCannotRunAndListsEveryReason(string provider)
    {
        using var host = new HandlerTestHost();
        await SetupAsync(host, provider);

        var prescreen = await host.DispatchAsync("prescreen.run");

        var holiday = prescreen.GetProperty("holidayActivity");
        Assert.Equal("na", holiday.GetProperty("status").GetString());
        Assert.Equal(
            // 裁定 2026-11-03-10 改畫面用字：第一次失敗：Focused 收據 20261103-075328999-91d0abe94c75427bb2babda5bdd66cc4。
            "尚未上傳國定假日清單，請先在第二步「國定假日與補班日」上傳。" + ApprovalDateMissing + FieldFix,
            holiday.GetProperty("naReason").GetString());
        Assert.Equal(JsonValueKind.Null, holiday.GetProperty("approvalNaReason").ValueKind);
        // 週末卡不需要假日檔，照常顯示總帳日期那一側。
        var weekend = prescreen.GetProperty("weekendActivity");
        Assert.Equal(JsonValueKind.Null, weekend.GetProperty("naReason").ValueKind);
        Assert.Equal(2, weekend.GetProperty("postingCount").GetInt64());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MissingApprovalDate_OutOfPeriodApprovalCannotRun_AndTheReportSaysNa(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host, provider);

        var validation = await host.DispatchAsync("validate.run");

        // 沒有傳票核准日就無法判斷核准日在不在期間：筆數不寫 0，改寫缺什麼、到哪裡補（裁定 2026-10-23-01）。
        var blankRecords = validation.GetProperty("blankRecordsTest");
        Assert.Equal(JsonValueKind.Null, blankRecords.GetProperty("approvalDateNotInPeriodCount").ValueKind);
        Assert.Equal(ApprovalDateMissing + FieldFix, blankRecords.GetProperty("approvalDateNotInPeriodNaReason").GetString());
        // 其他三類照常檢查；合成資料沒有空白的科目、傳票號碼與摘要。
        Assert.Equal(0, blankRecords.GetProperty("blankAccountNumberCount").GetInt64());
        Assert.Equal(0, blankRecords.GetProperty("blankVoucherNumberCount").GetInt64());
        Assert.Equal(0, blankRecords.GetProperty("blankDescriptionCount").GetInt64());

        // 重開案件讀回同一份結果。
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var stored = loaded.GetProperty("validationResult").GetProperty("blankRecordsTest");
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("approvalDateNotInPeriodCount").ValueKind);
        Assert.Equal(ApprovalDateMissing + FieldFix, stored.GetProperty("approvalDateNotInPeriodNaReason").GetString());

        // 驗證報告照同一個結果寫 N/A，不產生核准日不在期間的明細頁。
        using var workbook = await ExportValidationReportAsync(host, projectId, validation);
        var summary = workbook.Worksheet("ValidationReport");
        Assert.Equal("N/A", summary.Cell("D23").GetString());
        // 裁定 2026-11-03-04 與 2026-11-03-17：報告句子裡的欄位名稱用【】標示。第一次失敗：Focused 收據 20261103-073302593-afc0685ef03e47fea453092a25ec1f17。
        Assert.Equal("未設定【傳票核准日】欄位", summary.Cell("E23").GetString());
        Assert.DoesNotContain("V_Report 4", workbook.Worksheets.Select(static sheet => sheet.Name));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ApprovalDateSameAsGlDate_OutOfPeriodApprovalRuns_AndTheReportWritesTheCount(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host, provider);
        await RecommitApprovalDateSameAsGlDateAsync(host, projectId);

        var validation = await host.DispatchAsync("validate.run");

        // 欄位目錄把「與總帳日期相同」算成可用的傳票核准日，照常檢查；四筆分錄的總帳日期都在查核期間內。
        var blankRecords = validation.GetProperty("blankRecordsTest");
        Assert.Equal(0, blankRecords.GetProperty("approvalDateNotInPeriodCount").GetInt64());
        Assert.Equal(JsonValueKind.Null, blankRecords.GetProperty("approvalDateNotInPeriodNaReason").ValueKind);

        using var workbook = await ExportValidationReportAsync(host, projectId, validation);
        var summary = workbook.Worksheet("ValidationReport");
        Assert.Equal(XLDataType.Number, summary.Cell("D23").DataType);
        Assert.Equal(0d, summary.Cell("D23").GetDouble());
        Assert.NotEqual("未設定【傳票核准日】欄位", summary.Cell("E23").GetString());
        Assert.DoesNotContain("V_Report 4", workbook.Worksheets.Select(static sheet => sheet.Name));
    }

    [Fact]
    public void StoredValidation_OutOfPeriodApprovalHasEitherACountOrAReason()
    {
        var complete = JsonNode.Parse(CurrentValidationSummaryTestData.Create())!.AsObject();

        // 能檢查時是非負的筆數、原因是 null；傳票核准日不可用時筆數是 null，原因寫缺什麼。
        Assert.True(IsValidWith(complete, count: 3, reason: null));
        Assert.True(IsValidWith(complete, count: null, reason: ApprovalDateMissing + FieldFix));
        // 兩者都有、都沒有、原因空白或不是文字、筆數是負數，都不能當成目前的結果。
        Assert.False(IsValidWith(complete, count: 3, reason: ApprovalDateMissing + FieldFix));
        Assert.False(IsValidWith(complete, count: null, reason: null));
        Assert.False(IsValidWith(complete, count: null, reason: " "));
        Assert.False(IsValidWith(complete, count: -1, reason: null));
        var numericReason = complete.DeepClone().AsObject();
        numericReason["blankRecordsTest"]!["approvalDateNotInPeriodCount"] = null;
        numericReason["blankRecordsTest"]!["approvalDateNotInPeriodNaReason"] = 0;
        Assert.False(ValidationSummaryShapeValidator.IsValid(numericReason.ToJsonString()));
        // 2026-10-23 以前的結果沒有原因欄，傳票核准日沒有配對時也寫 0，必須重新驗證。
        var withoutReason = complete.DeepClone().AsObject();
        withoutReason["blankRecordsTest"]!.AsObject().Remove("approvalDateNotInPeriodNaReason");
        withoutReason["blankRecordsTest"]!["approvalDateNotInPeriodCount"] = 0;
        Assert.False(ValidationSummaryShapeValidator.IsValid(withoutReason.ToJsonString()));
    }

    private static bool IsValidWith(JsonObject complete, long? count, string? reason)
    {
        var changed = complete.DeepClone().AsObject();
        changed["blankRecordsTest"]!["approvalDateNotInPeriodCount"] = count;
        changed["blankRecordsTest"]!["approvalDateNotInPeriodNaReason"] = reason;
        return ValidationSummaryShapeValidator.IsValid(changed.ToJsonString());
    }

    // 確認配對時把傳票核准日改選「與總帳日期相同」，其他配對照舊。
    private static async Task RecommitApprovalDateSameAsGlDateAsync(HandlerTestHost host, string projectId)
    {
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var gl = loaded.GetProperty("mapping").GetProperty("gl");
        var recommit = new JsonObject
        {
            ["mapping"] = JsonNode.Parse(gl.GetProperty("mapping").GetRawText()),
            ["amountMode"] = gl.GetProperty("amountMode").GetString(),
            ["approvalDateMode"] = ApprovalDateModeNames.SameAsGlDate,
            ["manualAutoPolicy"] = JsonNode.Parse(gl.GetProperty("manualAutoPolicy").GetRawText())
        };
        await host.DispatchAsync("mapping.commit.gl", recommit.ToJsonString());
    }

    private static async Task<XLWorkbook> ExportValidationReportAsync(HandlerTestHost host, string projectId, JsonElement validation)
    {
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var response = await host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new { resultId }));
        var report = response.GetProperty("publishedReports").EnumerateArray()
            .Single(static item => item.GetProperty("kind").GetString() == "validationReport");
        return new XLWorkbook(Path.Combine(host.ProjectsRoot, projectId, report.GetProperty("fileName").GetString()!));
    }

    private static ILocalProjectDatabase Database(HandlerTestHost host, string provider)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        return provider == "sqlite" ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);
    }

    private static async Task ExecuteAsync(
        HandlerTestHost host, string projectId, string provider, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = Database(host, provider).CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> TextAsync(HandlerTestHost host, string projectId, string provider, string sql)
    {
        await using var connection = Database(host, provider).CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
