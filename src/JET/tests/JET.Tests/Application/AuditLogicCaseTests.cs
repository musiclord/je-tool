using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 六項核心查核邏輯的案例。每個案例用哪一份合成資料、執行的操作與預期結果都寫在
/// Fixtures/audit-logic-cases.json，這裡照定義建立案件、匯入資料、確認欄位配對並執行該項操作，
/// 再核對實際結果和定義的預期結果完全相同。預期結果是照規則人工推得的，這裡不計算任何預期值。
/// 用同一份資料的案例共用一個案件：案件只建立一次，每個案例各自執行自己的操作。
/// </summary>
public sealed class AuditLogicCaseTests : IClassFixture<AuditLogicCaseTests.CaseProjects>
{
    private const string BlankVoucherNumber = TaiwanSmeScenarioSupport.BlankVoucherNumber;
    private const string RdePrefix = "rde:";

    private readonly CaseProjects _projects;

    public AuditLogicCaseTests(CaseProjects projects) => _projects = projects;

    public static TheoryData<string> CaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in LoadDefinition()["cases"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()))
        {
            data.Add(id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task Case_ProducesTheExpectedResult(string caseId)
    {
        var definition = LoadDefinition();
        var testCase = definition["cases"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == caseId)!.AsObject();
        var action = testCase["action"]!.AsObject();
        var kind = action["kind"]!.GetValue<string>();
        var datasetName = testCase["dataset"]!.GetValue<string>();
        Assert.True(definition["datasets"]![datasetName] is JsonObject, $"{caseId}：找不到測試資料 {datasetName}。");
        var dataset = definition["datasets"]![datasetName]!.AsObject();
        AssertWellFormed(caseId, testCase, dataset, kind, definition["outcomes"]!.AsObject());
        var rows = CaseRow.Read(testCase, dataset);
        var project = await _projects.GetAsync(datasetName, () => SetupAsync(dataset, definition["fields"]!.AsObject()));
        var host = project.Host;

        switch (kind)
        {
            case "population":
                await AssertPopulationAsync(host, caseId, rows, project.Validation);
                break;
            case "completeness":
                await AssertPopulationAsync(host, caseId, rows, project.Validation);
                AssertCompleteness(caseId, testCase, dataset, rows, project.Validation);
                break;
            case "voucherBalance":
                await AssertPopulationAsync(host, caseId, rows, project.Validation);
                AssertVoucherBalance(caseId, testCase, rows, project.Validation);
                break;
            case "infSampling":
                await AssertInfSamplingAsync(host, caseId, testCase, rows);
                break;
            case "prescreen":
                await AssertPrescreenAsync(host, caseId, testCase, rows);
                break;
            case "prescreenSummary":
                await AssertPrescreenSummaryAsync(host, caseId, testCase);
                break;
            case "blankRecords":
                await AssertBlankRecordsAsync(host, caseId, testCase, rows, project.Validation);
                break;
            case "filter":
                await AssertFilterAsync(project, caseId, testCase, rows);
                break;
            case "limit":
                await AssertLimitAsync(host, caseId, testCase);
                break;
            default:
                Assert.Fail($"{caseId}：案例定義的操作種類 {kind} 沒有對應的執行方式。");
                break;
        }
    }

    private static readonly string[] CaseKeys =
        ["id", "logic", "group", "directoryNo", "name", "purpose", "rule", "fixedItems", "dataset", "rows", "action", "expected"];

    private static readonly string[] DatasetKeys = ["setup", "glFields", "gl", "tbFields", "tb"];

    private static readonly string[] SetupKeys =
    [
        "periodStart", "periodEnd", "prepStartDate", "holidays", "makeupDays", "postingStatus", "manualAuto", "accountMapping",
        "amountMode", "tbMode", "rdeFields", "authorizedPreparers"
    ];

    private static readonly string[] PopulationOutcomes =
        ["included", "blankGlDate", "unrecognizedGlDate", "beforePeriod", "afterPeriod", "postingStatus"];

    /// <summary>每種操作認得的 action 鍵、expected 鍵，以及一筆分錄可以寫的預期歸屬。</summary>
    private static readonly Dictionary<string, (string[] Action, string[] Expected, string[] Outcomes)> KindShapes = new(StringComparer.Ordinal)
    {
        ["population"] = (["kind"], [], PopulationOutcomes),
        ["completeness"] = (["kind"], ["accounts", "diffAccounts"], PopulationOutcomes),
        ["voucherBalance"] = (["kind"], ["vouchers", "unbalancedVouchers"], PopulationOutcomes),
        ["infSampling"] = (["kind", "sampleSize"], ["requestedSampleSize", "sampleSize", "allIncludedSampled", "sameSampleOnRepeat"], PopulationOutcomes),
        ["prescreen"] = (["kind", "ruleKey"], ["zerosThreshold", "derivation"], ["hit", "miss"]),
        ["prescreenSummary"] = (["kind", "ruleKey"], ["totalCount", "groups"], ["note"]),
        ["blankRecords"] = (["kind", "category"], [], ["listed", "notListed"]),
        ["filter"] = (["kind", "condition"], ["summaryRowCount"], ["hit", "miss"]),
        ["limit"] = (["kind", "what", "count"], ["accepted", "message"], ["note"])
    };

    /// <summary>
    /// 案例檔寫了這裡不認得的鍵，或某一筆分錄的預期歸屬不屬於這種操作時，測試失敗。
    /// 這樣寫錯字的鍵不會被默默略過，讓案例看起來有核對、實際上沒有。
    /// </summary>
    private static void AssertWellFormed(string caseId, JsonObject testCase, JsonObject dataset, string kind, JsonObject outcomes)
    {
        Assert.True(KindShapes.TryGetValue(kind, out var shape), $"{caseId}：案例定義的操作種類 {kind} 沒有對應的執行方式。");
        AssertKeys(caseId, "案例", testCase, CaseKeys, required: true);
        AssertKeys(caseId, "測試資料", dataset, DatasetKeys, required: true);
        AssertKeys(caseId, "setup", dataset["setup"]!.AsObject(), SetupKeys, required: true);
        AssertKeys(caseId, "action", testCase["action"]!.AsObject(), shape.Action, required: true);
        // 預篩選只有由資料算出 0 的個數的規則才寫 zerosThreshold 與 derivation；篩選只有畫面的分錄筆數和直接符合的筆數不同時才寫
        // summaryRowCount。其餘種類的 expected 每個鍵都要有。
        AssertKeys(caseId, "expected", testCase["expected"]!.AsObject(), shape.Expected, required: kind is not ("prescreen" or "filter"));
        var caseRows = testCase["rows"]!.AsArray();
        Assert.True(caseRows.Count == dataset["gl"]!.AsArray().Count, $"{caseId}：預期歸屬的筆數和測試資料的分錄筆數不同。");
        var fieldCount = dataset["glFields"]!.AsArray().Count;
        Assert.True(dataset["gl"]!.AsArray().All(row => row!.AsArray().Count == fieldCount), $"{caseId}：測試資料有分錄的欄位數和欄位清單不同。");
        var rowNumber = 0;
        foreach (var row in caseRows)
        {
            rowNumber++;
            AssertKeys(caseId, $"第 {rowNumber} 筆分錄", row!.AsObject(), ["expected", "reason"], required: true);
            var expected = row["expected"]!.GetValue<string>();
            Assert.True(shape.Outcomes.Contains(expected) && outcomes.ContainsKey(expected),
                $"{caseId}：第 {rowNumber} 筆分錄的預期歸屬 {expected} 不屬於 {kind} 這種操作。");
        }
    }

    private static void AssertKeys(string caseId, string where, JsonObject node, string[] known, bool required)
    {
        var unknown = node.Select(pair => pair.Key).Except(known).ToArray();
        Assert.True(unknown.Length == 0, $"{caseId}：{where} 有測試不認得的鍵：{string.Join("、", unknown)}");
        if (required)
        {
            var missing = known.Where(key => !node.ContainsKey(key)).ToArray();
            Assert.True(missing.Length == 0, $"{caseId}：{where} 少了鍵：{string.Join("、", missing)}");
        }
    }

    /// <summary>照測試資料的定義建立案件、匯入 GL 與 TB、設定行事曆、確認欄位配對，並執行資料驗證。</summary>
    private static async Task<CaseProject> SetupAsync(JsonObject dataset, JsonObject fieldLabels)
    {
        var host = new HandlerTestHost();
        try
        {
            var setup = dataset["setup"]!.AsObject();
            var labels = Labels(fieldLabels["gl"]!);
            var tbLabels = Labels(fieldLabels["tb"]!);
            var fields = dataset["glFields"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
            await host.DispatchAsync("project.create", new JsonObject
            {
                ["projectCode"] = "AUDIT-LOGIC-CASE",
                ["clientName"] = "合成案件",
                ["operatorId"] = "tester",
                ["periodStart"] = setup["periodStart"]!.GetValue<string>(),
                ["periodEnd"] = setup["periodEnd"]!.GetValue<string>(),
                ["prepStartDate"] = setup["prepStartDate"]?.GetValue<string>(),
                ["databaseProvider"] = "sqlite"
            }.ToJsonString());

            var glRows = dataset["gl"]!.AsArray().Select(row => row!.AsArray()).ToArray();
            await ImportAsync(host, "import.gl.fromFile", "audit-logic-gl.xlsx", fields.Select(field => labels[field]).ToArray(), glRows);
            var tbFields = dataset["tbFields"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
            var tbRows = dataset["tb"]!.AsArray().Select(row => row!.AsArray()).ToArray();
            if (tbRows.Length > 0)
            {
                await ImportAsync(host, "import.tb.fromFile", "audit-logic-tb.xlsx", tbFields.Select(field => tbLabels[field]).ToArray(), tbRows);
            }

            foreach (var (key, importAction) in new[] { ("holidays", "import.holiday"), ("makeupDays", "import.makeupDay") })
            {
                var dates = setup[key]!.AsArray();
                if (dates.Count > 0)
                {
                    await host.DispatchAsync(importAction, new JsonObject { ["dates"] = dates.DeepClone() }.ToJsonString());
                }
            }

            // 攸關資料元素欄位在欄位清單裡寫成「rde:顯示名稱」，不進核心欄位的配對，另外列在 rdeFields。
            var commit = new JsonObject
            {
                ["mapping"] = new JsonObject(fields.Where(field => !field.StartsWith(RdePrefix, StringComparison.Ordinal))
                    .Select(field => KeyValuePair.Create(field, (JsonNode?)labels[field]))),
                ["amountMode"] = setup["amountMode"]!.GetValue<string>()
            };
            if (setup["postingStatus"] is JsonObject postingStatus)
            {
                commit["postingStatusPolicy"] = postingStatus.DeepClone();
            }

            if (setup["manualAuto"] is JsonObject manualAuto)
            {
                commit["manualAutoPolicy"] = manualAuto.DeepClone();
            }

            var rdeFields = setup["rdeFields"]!.AsArray();
            if (rdeFields.Count > 0)
            {
                commit["rdeFields"] = new JsonArray(rdeFields.Select(item => (JsonNode)new JsonObject
                {
                    ["sourceColumn"] = item!["label"]!.GetValue<string>(),
                    ["label"] = item["label"]!.GetValue<string>(),
                    ["valueType"] = item["valueType"]!.GetValue<string>()
                }).ToArray());
            }

            var committed = await host.DispatchAsync("mapping.commit.gl", commit.ToJsonString());
            var rdeFieldIds = rdeFields.Count == 0
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : committed.GetProperty("rdeFields").EnumerateArray().ToDictionary(
                    item => item.GetProperty("label").GetString()!, item => item.GetProperty("fieldId").GetString()!, StringComparer.Ordinal);
            if (tbRows.Length > 0)
            {
                await host.DispatchAsync("mapping.commit.tb", new JsonObject
                {
                    ["mapping"] = new JsonObject(tbFields.Select(field => KeyValuePair.Create(field, (JsonNode?)tbLabels[field]))),
                    ["changeMode"] = setup["tbMode"]!.GetValue<string>()
                }.ToJsonString());
            }

            var validation = await host.DispatchAsync("validate.run");
            var accountMapping = setup["accountMapping"]!.AsArray();
            if (accountMapping.Count > 0)
            {
                await TaiwanSmeScenarioSupport.ImportAccountMappingAsync(host, accountMapping
                    .Select(row => (row![0]!.GetValue<string>(), row[1]!.GetValue<string>(), row[2]!.GetValue<string>()))
                    .ToArray());
            }

            if (setup["authorizedPreparers"] is JsonArray preparers)
            {
                await TaiwanSmeScenarioSupport.ImportAuthorizedPreparersAsync(host, preparers.Select(item => item?.GetValue<string>()).ToArray());
            }

            return new CaseProject(host, validation, rdeFieldIds);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static Dictionary<string, string> Labels(JsonNode fields) => fields.AsArray()
        .ToDictionary(item => item!["key"]!.GetValue<string>(), item => item!["label"]!.GetValue<string>(), StringComparer.Ordinal);

    /// <summary>把一張表寫成活頁簿再匯入：第一列是欄名，數字寫成數值儲存格，文字與日期照定義寫成文字，null 留空。</summary>
    private static async Task ImportAsync(HandlerTestHost host, string action, string fileName, string[] headers, JsonArray[] rows)
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            for (var column = 0; column < headers.Length; column++)
            {
                sheet.Cell(1, column + 1).SetValue(headers[column]);
            }

            for (var row = 0; row < rows.Length; row++)
            {
                for (var column = 0; column < rows[row].Count; column++)
                {
                    WriteCell(sheet.Cell(row + 2, column + 1), rows[row][column]);
                }
            }
        });
        try
        {
            await host.DispatchAsync(action, JsonSerializer.Serialize(new { filePath = path, fileName }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    private static void WriteCell(IXLCell cell, JsonNode? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.GetValueKind() == JsonValueKind.Number)
        {
            cell.SetValue(value.GetValue<decimal>());
        }
        else
        {
            cell.SetValue(value.GetValue<string>());
        }
    }

    /// <summary>納入測試的分錄範圍：每一筆分錄在 GL 或 GL(未納入測試)，以及未納入的原因。</summary>
    private static async Task AssertPopulationAsync(HandlerTestHost host, string caseId, IReadOnlyList<CaseRow> rows, JsonElement validation)
    {
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        var gl = await host.DispatchAsync("query.dataPreview", """{"dataset":"gl","limit":500}""");
        foreach (var key in PreviewKeys(gl))
        {
            actual.Add(key.Key, "included");
        }

        var excluded = await host.DispatchAsync("query.dataPreview", """{"dataset":"glExcluded","limit":500}""");
        foreach (var key in PreviewKeys(excluded))
        {
            // GL(未納入測試) 每一列的最後一欄是未納入原因。
            actual.Add(key.Key, key.Row[key.Row.GetArrayLength() - 1].GetString()!);
        }

        AssertSame(caseId, "每一筆分錄的歸屬",
            rows.Select(row => row.Number + ":" + row.Expected),
            rows.Select(row => row.Number + ":" + actual.GetValueOrDefault(row.Key, "找不到這筆分錄")));

        var population = validation.GetProperty("populationSummary");
        var dateReasons = new[] { "blankGlDate", "unrecognizedGlDate", "beforePeriod", "afterPeriod" };
        Assert.Equal(rows.Count, population.GetProperty("raw").GetProperty("rowCount").GetInt64());
        Assert.Equal(rows.Count(row => row.Expected == "included"), population.GetProperty("gl").GetProperty("rowCount").GetInt64());
        Assert.Equal(rows.Count(row => dateReasons.Contains(row.Expected)),
            population.GetProperty("excluded").GetProperty("byPeriodCount").GetInt64());
        Assert.Equal(rows.Count(row => row.Expected == "postingStatus"),
            population.GetProperty("excluded").GetProperty("byPostingStatusCount").GetInt64());
    }

    private static IEnumerable<(string Key, JsonElement Row)> PreviewKeys(JsonElement page)
    {
        var keys = page.GetProperty("columnDetails").EnumerateArray().Select(column => column.GetProperty("key").GetString()).ToList();
        var voucher = keys.IndexOf("voucherNumber");
        var line = keys.IndexOf("lineItem");
        Assert.True(voucher >= 0 && line >= 0, "資料預覽沒有傳票號碼或傳票文件項次欄。");
        foreach (var row in page.GetProperty("rows").EnumerateArray())
        {
            yield return (RowKey(row[voucher].GetString(), row[line].GetString()), row);
        }
    }

    /// <summary>
    /// 完整性測試：差異科目和定義完全相同。定義另外列出每個科目的 TB 與 GL 金額給人核對：
    /// GL 金額要等於定義裡納入測試的分錄的合計；沒有列為差異的科目，程式已確認 GL 合計等於 TB 的年度變動金額，
    /// 所以定義寫的 TB 金額也要等於 GL 金額。這樣表上的每個數字都經過核對。
    /// </summary>
    private static void AssertCompleteness(
        string caseId, JsonObject testCase, JsonObject dataset, IReadOnlyList<CaseRow> rows, JsonElement validation)
    {
        var expected = testCase["expected"]!.AsObject();
        var completeness = validation.GetProperty("completenessTest");
        static string Format(string? account, decimal tb, decimal gl, decimal diff, bool notInTb) =>
            string.Create(CultureInfo.InvariantCulture, $"{account}|TB {tb:0.####}|GL {gl:0.####}|差異 {diff:0.####}|{(notInTb ? "不在 TB" : "在 TB")}");
        var expectedDiffs = expected["diffAccounts"]!.AsArray().Select(item => Format(
            item!["accountNumber"]!.GetValue<string>(), item["tbAmount"]!.GetValue<decimal>(), item["glAmount"]!.GetValue<decimal>(),
            item["diff"]!.GetValue<decimal>(), item["notInTb"]!.GetValue<bool>())).Order(StringComparer.Ordinal).ToArray();
        var actualDiffs = completeness.GetProperty("diffAccounts").EnumerateArray().Select(item => Format(
            item.GetProperty("accountNumber").GetString(), item.GetProperty("tbAmount").GetDecimal(), item.GetProperty("glAmount").GetDecimal(),
            item.GetProperty("diff").GetDecimal(), item.GetProperty("notInTb").GetBoolean())).Order(StringComparer.Ordinal).ToArray();
        AssertSame(caseId, "差異科目", expectedDiffs, actualDiffs);
        Assert.Equal(expectedDiffs.Length, completeness.GetProperty("diffAccountCount").GetInt64());
        Assert.True(completeness.GetProperty("storedTotals").GetProperty("rowCountMatch").GetBoolean());
        Assert.True(completeness.GetProperty("storedTotals").GetProperty("amountMatch").GetBoolean());

        var glByAccount = rows.Where(row => row.Expected == "included" && row.AccountNumber is not null)
            .GroupBy(row => row.AccountNumber!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Debit - row.Credit), StringComparer.Ordinal);
        var diffByAccount = expected["diffAccounts"]!.AsArray().ToDictionary(item => item!["accountNumber"]!.GetValue<string>(), StringComparer.Ordinal);
        var tbAccounts = dataset["tb"]!.AsArray().Select(row => row![0]!.GetValue<string>());
        var listed = expected["accounts"]!.AsArray();
        AssertSame(caseId, "定義列出的科目",
            tbAccounts.Union(glByAccount.Keys).Order(StringComparer.Ordinal),
            listed.Select(item => item![0]!.GetValue<string>()).Order(StringComparer.Ordinal));
        foreach (var item in listed)
        {
            var account = item![0]!.GetValue<string>();
            var tb = item[1]!.GetValue<decimal>();
            var gl = item[2]!.GetValue<decimal>();
            Assert.True(glByAccount.GetValueOrDefault(account) == gl, $"{caseId}：定義寫的科目 {account} GL 金額和定義裡納入測試的分錄合計不同。");
            if (diffByAccount.TryGetValue(account, out var diff))
            {
                Assert.True(diff!["tbAmount"]!.GetValue<decimal>() == tb && diff["glAmount"]!.GetValue<decimal>() == gl,
                    $"{caseId}：定義寫的科目 {account} 金額和差異科目寫的不同。");
            }
            else
            {
                Assert.True(tb == gl, $"{caseId}：科目 {account} 沒有列為差異，定義寫的 TB 金額卻和 GL 金額不同。");
            }
        }
    }

    /// <summary>
    /// 借貸不平測試：列出的傳票、借方合計、貸方合計與差額和定義完全相同。定義另外列出每張傳票的借貸合計給人核對，
    /// 這些數字要等於定義裡納入測試的分錄的合計，其中借貸不相等的傳票要正好是定義列為借貸不平的那幾張。
    /// </summary>
    private static void AssertVoucherBalance(string caseId, JsonObject testCase, IReadOnlyList<CaseRow> rows, JsonElement validation)
    {
        static string Format(string? voucher, decimal debit, decimal credit, decimal diff) =>
            string.Create(CultureInfo.InvariantCulture, $"{voucher}|借 {debit:0.####}|貸 {credit:0.####}|差 {diff:0.####}");
        var expected = testCase["expected"]!["unbalancedVouchers"]!.AsArray().Select(item => Format(
            item!["voucherNumber"]!.GetValue<string>(), item["debit"]!.GetValue<decimal>(), item["credit"]!.GetValue<decimal>(),
            item["diff"]!.GetValue<decimal>())).ToArray();
        var balance = validation.GetProperty("voucherBalanceTest");
        // 差額只比大小，方向由借方合計與貸方合計看得出來。
        var actual = balance.GetProperty("unbalancedVouchers").EnumerateArray().Select(item => Format(
            item.GetProperty("voucherNumber").GetString(), item.GetProperty("debit").GetDecimal(), item.GetProperty("credit").GetDecimal(),
            Math.Abs(item.GetProperty("diff").GetDecimal()))).ToArray();
        AssertSame(caseId, "借貸不平的傳票", expected, actual);
        Assert.Equal(expected.Length, balance.GetProperty("unbalancedVoucherCount").GetInt64());

        var fromRows = rows.Where(row => row.Expected == "included" && row.VoucherNumber is not null)
            .GroupBy(row => row.VoucherNumber!, StringComparer.Ordinal)
            .Select(group => (Voucher: group.Key, Debit: group.Sum(row => row.Debit), Credit: group.Sum(row => row.Credit)))
            .OrderBy(item => item.Voucher, StringComparer.Ordinal)
            .ToArray();
        var listed = testCase["expected"]!["vouchers"]!.AsArray()
            .Select(item => (Voucher: item![0]!.GetValue<string>(), Debit: item[1]!.GetValue<decimal>(), Credit: item[2]!.GetValue<decimal>()))
            .ToArray();
        AssertSame(caseId, "定義列出的各傳票借貸合計",
            fromRows.Select(item => Format(item.Voucher, item.Debit, item.Credit, Math.Abs(item.Debit - item.Credit))),
            listed.OrderBy(item => item.Voucher, StringComparer.Ordinal).Select(item => Format(item.Voucher, item.Debit, item.Credit, Math.Abs(item.Debit - item.Credit))));
        AssertSame(caseId, "定義裡借貸不相等的傳票",
            listed.Where(item => item.Debit != item.Credit).Select(item => Format(item.Voucher, item.Debit, item.Credit, Math.Abs(item.Debit - item.Credit))),
            expected);
    }

    /// <summary>
    /// 資料可靠性測試：要抽的筆數與實際抽出的筆數和定義相同，樣本互不重複，而且只來自定義裡納入測試的分錄。
    /// 抽到哪幾筆由案件的隨機種子決定，所以定義只在全部抽出時指定是哪幾筆，重複執行時比較兩次的樣本。
    /// </summary>
    private static async Task AssertInfSamplingAsync(HandlerTestHost host, string caseId, JsonObject testCase, IReadOnlyList<CaseRow> rows)
    {
        var expected = testCase["expected"]!.AsObject();
        var sampleSize = testCase["action"]!["sampleSize"] is { } size ? size.GetValue<int>() : (int?)null;
        var sampling = (await ValidateAsync(host, sampleSize)).GetProperty("infSamplingTest");
        Assert.Equal(expected["requestedSampleSize"]!.GetValue<int>(), sampling.GetProperty("requestedSampleSize").GetInt32());
        Assert.Equal(expected["sampleSize"]!.GetValue<int>(), sampling.GetProperty("sampleSize").GetInt32());

        var eligible = rows.Where(row => row.Expected == "included").Select(row => row.SampleKey).ToArray();
        var sample = await ReadSampleAsync(host);
        Assert.Equal(expected["sampleSize"]!.GetValue<int>(), sample.Length);
        Assert.Equal(sample.Length, sample.Distinct(StringComparer.Ordinal).Count());
        Assert.True(sample.All(eligible.Contains), $"{caseId}：樣本裡有未納入測試的分錄：{string.Join("、", sample.Except(eligible))}");
        if (expected["allIncludedSampled"]!.GetValue<bool>())
        {
            AssertSame(caseId, "抽出的樣本", eligible.Order(StringComparer.Ordinal), sample);
        }

        if (expected["sameSampleOnRepeat"]!.GetValue<bool>())
        {
            var repeated = await ValidateAsync(host, sampleSize);
            Assert.Equal(sampling.GetProperty("seed").GetInt64(), repeated.GetProperty("infSamplingTest").GetProperty("seed").GetInt64());
            AssertSame(caseId, "重新執行後的樣本", sample, await ReadSampleAsync(host));
        }
    }

    private static Task<JsonElement> ValidateAsync(HandlerTestHost host, int? sampleSize) => sampleSize is { } size
        ? host.DispatchAsync("validate.run", JsonSerializer.Serialize(new { sampleSize = size }))
        : host.DispatchAsync("validate.run");

    private static async Task<string[]> ReadSampleAsync(HandlerTestHost host)
    {
        var page = await host.DispatchAsync("query.infSamplePage", """{"cursor":null,"pageSize":200}""");
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        return page.GetProperty("rows").EnumerateArray()
            .Select(row => CaseRow.FormatSampleKey(
                row.GetProperty("voucherNumber").GetString(), row.GetProperty("accountNumber").GetString(),
                row.GetProperty("debit").GetDecimal(), row.GetProperty("credit").GetDecimal()))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>預篩選：這條規則的命中筆數與明細列出的分錄和定義完全相同；由資料算出的 0 的個數也要相同。</summary>
    private static async Task AssertPrescreenAsync(HandlerTestHost host, string caseId, JsonObject testCase, IReadOnlyList<CaseRow> rows)
    {
        var ruleKey = testCase["action"]!["ruleKey"]!.GetValue<string>();
        var expectedHits = rows.Where(row => row.Expected == "hit").Select(row => row.Key).Order(StringComparer.Ordinal).ToArray();
        var prescreen = await host.DispatchAsync("prescreen.run");
        var rule = prescreen.GetProperty("rulePeriod").GetProperty("rules").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == ruleKey);
        Assert.True(rule.GetProperty("naReason").ValueKind == JsonValueKind.Null, $"{caseId}：規則無法執行：{rule.GetProperty("naReason")}");
        Assert.Equal(expectedHits.Length, rule.GetProperty("hitLines").GetInt64());
        if (testCase["expected"]!["zerosThreshold"] is { } zeros)
        {
            Assert.Equal(zeros.GetValue<int>(), prescreen.GetProperty("trailingZeros").GetProperty("zerosThreshold").GetInt32());
        }

        var page = await host.DispatchAsync("query.prescreenPage", JsonSerializer.Serialize(new { ruleKey, pageSize = 200 }));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        var actualHits = page.GetProperty("rows").EnumerateArray()
            .Select(row => RowKey(row.GetProperty("voucherNumber").GetString(), row.GetProperty("lineItem").GetString()))
            .Order(StringComparer.Ordinal)
            .ToArray();
        AssertSame(caseId, "命中的分錄", expectedHits, actualHits);
    }

    /// <summary>
    /// 預篩選的兩份彙總：每一組的分錄筆數、借方合計與貸方合計，以及組數，和定義完全相同。
    /// 貸方合計只比大小；空白的人員在定義裡寫成「（空白）」。
    /// </summary>
    private static async Task AssertPrescreenSummaryAsync(HandlerTestHost host, string caseId, JsonObject testCase)
    {
        var ruleKey = testCase["action"]!["ruleKey"]!.GetValue<string>();
        var expected = testCase["expected"]!.AsObject();
        var (listName, keyName, countName) = ruleKey switch
        {
            "preparerSummary" => ("preparers", "createdBy", "totalPreparerCount"),
            "rareAccounts" => ("accounts", "accountNumber", "distinctAccountCount"),
            _ => throw new InvalidOperationException($"{caseId}：彙總 {ruleKey} 沒有對應的讀法。")
        };
        var summary = (await host.DispatchAsync("prescreen.run")).GetProperty(ruleKey);
        static string Format(string? key, long count, decimal debit, decimal credit) =>
            string.Create(CultureInfo.InvariantCulture, $"{(string.IsNullOrEmpty(key) ? "（空白）" : key)}|{count} 筆|借 {debit:0.####}|貸 {Math.Abs(credit):0.####}");
        AssertSame(caseId, "各組的筆數與金額",
            expected["groups"]!.AsArray().Select(item => Format(
                item![0]!.GetValue<string>(), item[1]!.GetValue<long>(), item[2]!.GetValue<decimal>(), item[3]!.GetValue<decimal>())).Order(StringComparer.Ordinal),
            summary.GetProperty(listName).EnumerateArray().Select(item => Format(
                item.GetProperty(keyName).GetString(), item.GetProperty("entryCount").GetInt64(),
                item.GetProperty("debitTotal").GetDecimal(), item.GetProperty("creditTotal").GetDecimal())).Order(StringComparer.Ordinal));
        Assert.Equal(expected["totalCount"]!.GetValue<long>(), summary.GetProperty(countName).GetInt64());
    }

    /// <summary>
    /// 資料驗證列出的空白與期間檢查：這一類的筆數與明細列出的分錄和定義完全相同。
    /// 明細沒有傳票文件項次，改用傳票號碼與科目編號對回定義裡的分錄。
    /// </summary>
    private static async Task AssertBlankRecordsAsync(
        HandlerTestHost host, string caseId, JsonObject testCase, IReadOnlyList<CaseRow> rows, JsonElement validation)
    {
        var category = testCase["action"]!["category"]!.GetValue<string>();
        var expected = rows.Where(row => row.Expected == "listed").Select(row => row.VoucherNumber + "|" + row.AccountNumber).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(rows.Count, rows.Select(row => row.VoucherNumber + "|" + row.AccountNumber).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected.Length, validation.GetProperty("blankRecordsTest").GetProperty(category + "Count").GetInt64());
        var page = await host.DispatchAsync("query.blankRecordsPage",
            new JsonObject { ["category"] = category, ["cursor"] = null, ["pageSize"] = 200, ["search"] = null, ["sort"] = null }.ToJsonString());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        AssertSame(caseId, "列出的分錄", expected, page.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("voucherNumber").GetString() + "|" + row.GetProperty("accountNumber").GetString())
            .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// 進階條件篩選：只有這一條條件的情境，畫面的分錄筆數與儲存後讀回的命中分錄和定義完全相同。
    /// 畫面的分錄筆數通常就是直接符合的筆數；規則另外把命中傳票的其他分錄算進筆數時，定義用 summaryRowCount 寫出那個筆數。
    /// </summary>
    private static async Task AssertFilterAsync(CaseProject project, string caseId, JsonObject testCase, IReadOnlyList<CaseRow> rows)
    {
        var expectedHits = rows.Where(row => row.Expected == "hit").Select(row => row.Key).Order(StringComparer.Ordinal).ToArray();
        var condition = testCase["action"]!["condition"]!.DeepClone();
        UseRdeFieldIds(caseId, condition, project.RdeFieldIds);
        var scenario = TaiwanSmeScenarioSupport.OneRule(caseId, condition.ToJsonString());
        var preview = (await project.Host.DispatchAsync("filter.preview", new JsonObject { ["scenario"] = scenario.DeepClone() }.ToJsonString()))
            .GetProperty("scenario");
        Assert.Equal(expectedHits.Length, preview.GetProperty("count").GetInt64());
        // 畫面上「符合條件的分錄筆數」讀的是 summaryRowCount。
        var shownCount = testCase["expected"]!["summaryRowCount"] is { } written ? written.GetValue<long>() : expectedHits.Length;
        Assert.Equal(shownCount, preview.GetProperty("summaryRowCount").GetInt64());
        var hits = await TaiwanSmeScenarioSupport.CommitAndReadHitsAsync(project.Host, scenario);
        AssertSame(caseId, "命中的分錄", expectedHits, Assert.Single(hits));
    }

    /// <summary>攸關資料元素欄位的識別碼在確認配對時才產生，定義裡寫「rde:顯示名稱」，這裡換成這個案件的識別碼。</summary>
    private static void UseRdeFieldIds(string caseId, JsonNode node, IReadOnlyDictionary<string, string> rdeFieldIds)
    {
        if (node is JsonObject item)
        {
            if (item["fieldId"] is JsonValue value && value.GetValue<string>() is var written && written.StartsWith(RdePrefix, StringComparison.Ordinal))
            {
                Assert.True(rdeFieldIds.TryGetValue(written[RdePrefix.Length..], out var fieldId), $"{caseId}：測試資料沒有攸關資料元素欄位 {written}。");
                item["fieldId"] = fieldId;
            }

            foreach (var child in item.Select(pair => pair.Value).OfType<JsonNode>().ToArray())
            {
                UseRdeFieldIds(caseId, child, rdeFieldIds);
            }
        }
        else if (node is JsonArray list)
        {
            foreach (var child in list.OfType<JsonNode>().ToArray())
            {
                UseRdeFieldIds(caseId, child, rdeFieldIds);
            }
        }
    }

    /// <summary>
    /// 篩選上限：照定義的個數組出一條清單條件或一批情境後儲存，核對是否存得進去；存不進去時，回給使用者的原因要包含定義寫的那句話。
    /// 清單的值是「值001」起依序編號，每個情境只有一條「借貸別：借方」。
    /// </summary>
    private static async Task AssertLimitAsync(HandlerTestHost host, string caseId, JsonObject testCase)
    {
        var action = testCase["action"]!.AsObject();
        var count = action["count"]!.GetValue<int>();
        var expected = testCase["expected"]!.AsObject();
        JsonObject[] scenarios;
        switch (action["what"]!.GetValue<string>())
        {
            case "listValues":
                var values = new JsonArray(Enumerable.Range(1, count).Select(number => (JsonNode)$"值{number:000}").ToArray());
                scenarios =
                [
                    TaiwanSmeScenarioSupport.OneRule(caseId,
                        new JsonObject { ["type"] = "fieldValue", ["field"] = "description", ["operator"] = "in", ["values"] = values }.ToJsonString())
                ];
                break;
            case "scenarios":
                scenarios = Enumerable.Range(1, count)
                    .Select(number => TaiwanSmeScenarioSupport.OneRule($"{caseId} 情境 {number}", """{"type":"drCrOnly","drCr":"debit"}"""))
                    .ToArray();
                break;
            default:
                throw new InvalidOperationException($"{caseId}：上限 {action["what"]} 沒有對應的執行方式。");
        }

        if (expected["accepted"]!.GetValue<bool>())
        {
            Assert.True(expected["message"] is null, $"{caseId}：可以儲存的案例不寫原因。");
            var committed = await TaiwanSmeScenarioSupport.CommitAsync(host, scenarios);
            Assert.Equal(JsonValueKind.String, committed.GetProperty("resultRef").GetProperty("revision").ValueKind);
        }
        else
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => TaiwanSmeScenarioSupport.CommitAsync(host, scenarios));
            var message = expected["message"]!.GetValue<string>();
            Assert.True(error.Message.Contains(message, StringComparison.Ordinal), $"{caseId}：原因預期包含「{message}」，實際「{error.Message}」");
        }
    }

    private static void AssertSame(string caseId, string what, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var expectedText = string.Join("、", expected);
        var actualText = string.Join("、", actual);
        Assert.True(expectedText == actualText, $"{caseId} {what}：預期「{expectedText}」，實際「{actualText}」");
    }

    private static string RowKey(string? voucherNumber, string? lineItem) =>
        (string.IsNullOrEmpty(voucherNumber) ? BlankVoucherNumber : voucherNumber) + "|" + lineItem;

    private static JsonNode LoadDefinition() =>
        JsonNode.Parse(File.ReadAllText(TestRepositoryPaths.Fixture("audit-logic-cases.json")))!;

    /// <summary>一份測試資料建立好的案件：測試主機、資料驗證的結果，以及攸關資料元素欄位的識別碼。</summary>
    internal sealed record CaseProject(HandlerTestHost Host, JsonElement Validation, IReadOnlyDictionary<string, string> RdeFieldIds);

    /// <summary>
    /// 每一份測試資料只建立一次案件，給用到它的案例共用；全部案例跑完後一起關閉。
    /// 建立失敗時記下那一次的錯誤，用到這份資料的每個案例都以同一個錯誤失敗。
    /// </summary>
    public sealed class CaseProjects : IDisposable
    {
        private readonly Dictionary<string, Task<CaseProject>> _projects = new(StringComparer.Ordinal);

        internal Task<CaseProject> GetAsync(string dataset, Func<Task<CaseProject>> create)
        {
            lock (_projects)
            {
                if (!_projects.TryGetValue(dataset, out var project))
                {
                    project = create();
                    _projects.Add(dataset, project);
                }

                return project;
            }
        }

        public void Dispose()
        {
            foreach (var project in _projects.Values.Where(task => task.IsCompletedSuccessfully))
            {
                project.Result.Host.Dispose();
            }
        }
    }

    /// <summary>
    /// 測試資料裡的一筆分錄：第幾筆、對照查詢結果用的識別、借貸金額，以及案例寫的預期歸屬。
    /// 匯入時文字欄位會去掉頭尾的空白，所以識別用的傳票號碼與科目編號也去掉頭尾空白。
    /// </summary>
    private sealed record CaseRow(
        int Number, string Key, string SampleKey, string Expected, string? VoucherNumber, string? AccountNumber, decimal Debit, decimal Credit)
    {
        public static IReadOnlyList<CaseRow> Read(JsonObject testCase, JsonObject dataset)
        {
            var fields = dataset["glFields"]!.AsArray().Select(item => item!.GetValue<string>()).ToList();
            var expected = testCase["rows"]!.AsArray();
            string? Text(JsonArray values, string field) =>
                fields.IndexOf(field) is var index and >= 0 && values[index] is { } value && value.ToString().Trim() is { Length: > 0 } text ? text : null;
            // 金額照產品的精度保留到小數第 4 位，第 5 位四捨五入；括號或千分位這類文字金額不在這裡換算，當成 0。
            decimal Amount(JsonArray values, string field) =>
                fields.IndexOf(field) is var index and >= 0 && values[index] is { } value
                && decimal.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
                    ? Math.Round(amount, 4, MidpointRounding.AwayFromZero)
                    : 0m;
            var rows = dataset["gl"]!.AsArray().Select((row, index) =>
            {
                var values = row!.AsArray();
                var debit = Amount(values, "debitAmount");
                var credit = Amount(values, "creditAmount");
                return new CaseRow(
                    index + 1,
                    RowKey(Text(values, "voucherNumber"), Text(values, "lineItem")),
                    FormatSampleKey(Text(values, "voucherNumber"), Text(values, "accountNumber"), debit, credit),
                    expected[index]!["expected"]!.GetValue<string>(),
                    Text(values, "voucherNumber"),
                    Text(values, "accountNumber"),
                    debit,
                    credit);
            }).ToArray();
            Assert.Equal(rows.Length, rows.Select(row => row.Key).Distinct(StringComparer.Ordinal).Count());
            return rows;
        }

        /// <summary>抽樣明細沒有傳票文件項次，改用傳票號碼、科目與借貸金額對回定義裡的分錄。</summary>
        public static string FormatSampleKey(string? voucherNumber, string? accountNumber, decimal debit, decimal credit) =>
            string.Create(CultureInfo.InvariantCulture, $"{voucherNumber}|{accountNumber}|{debit:0.####}|{credit:0.####}");
    }
}
