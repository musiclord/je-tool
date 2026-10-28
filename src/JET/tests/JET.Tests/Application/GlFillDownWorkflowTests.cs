using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 預篩選收斂計畫階段六的「往下填補空白」固定答案。使用者 2026-10-22 裁定放在第三步確認配對時：
/// 審計員勾選已配對的 GL 欄位，確認配對時把那些欄位來源欄的空白補成上一列的值，PBC-GL 保持原樣，
/// 配對確認的回應寫出填了幾格；勾選跟著配對保存，重開案件帶回來；改選擇只要重新確認配對，走既有的失效規則。
/// 合成資料是傳票號碼與傳票日期只寫在每張傳票第一列的總帳，另配一個攸關資料元素欄，確認第二遍投影也照同一份填補。
/// 第一次失敗：Focused 收據 20261023-194731179-deb160873ed24e5794b04cfe70125f3f（fillDownFields 被忽略，回應沒有 fillDownFields 與 fillDownCount，空白列仍是空白）。
/// </summary>
public sealed class GlFillDownWorkflowTests
{
    private static string CreatePayload(string provider) => JsonSerializer.Serialize(new
    {
        projectCode = "FILL-DOWN-001",
        entityName = "合成填補案件",
        operatorId = "tester",
        periodStart = "2025-01-01",
        periodEnd = "2025-12-31",
        databaseProvider = provider
    });

    // 傳票號碼與傳票日期只寫在每張傳票的第一列；部門是攸關資料元素欄，第 2、5 列空白。
    private static InlineGlWorkbookBuilder Workbook() => new InlineGlWorkbookBuilder()
        .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "部門")
        .AddRow("JV-1", "2025-03-01", "1000", "合成現金", "合成摘要", 10, 1, "財務部")
        .AddRow(null, null, "4000", "合成收入", "合成摘要", 10, 0, null)
        .AddRow("JV-2", "2025-03-04", "1000", "合成現金", "合成摘要", 20, 1, "業務部")
        .AddRow(null, null, "4000", "合成收入", "合成摘要", 15, 0, "業務部")
        .AddRow(null, null, "2000", "合成應付", "合成摘要", 5, 0, null);

    private static object CommitPayload(InlineGlWorkbookBuilder builder, string[] fillDownFields, object? rdeFieldId = null) => new
    {
        mapping = builder.BuildFlagModeMapping(),
        amountMode = "flag",
        manualAutoPolicy = new { manualValues = new[] { "1" }, automaticValues = new[] { "0" } },
        rdeFields = new object[]
        {
            rdeFieldId is null
                ? new { sourceColumn = "部門", label = "部門", valueType = "text" }
                : new { sourceColumn = "部門", label = "部門", valueType = "text", fieldId = rdeFieldId }
        },
        fillDownFields
    };

    private static string?[] Column(JsonElement preview, string label)
    {
        var details = preview.GetProperty("columnDetails").EnumerateArray().ToArray();
        var index = Array.FindIndex(details, detail => detail.GetProperty("label").GetString() == label);
        Assert.True(index >= 0, $"資料預覽沒有「{label}」欄");
        return preview.GetProperty("rows").EnumerateArray()
            .Select(row => row[index].ValueKind == JsonValueKind.Null ? null : row[index].GetString())
            .ToArray();
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!).ToArray();

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task FillDown_FillsBlanksOfChosenFieldsFromThePreviousRow_AndReportsTheCount(string provider)
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", CreatePayload(provider));
        var projectId = created.GetProperty("projectId").GetString()!;
        var builder = Workbook();
        var path = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path, fileName = "合成總帳.xlsx" }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }

        var committed = await host.DispatchAsync("mapping.commit.gl",
            JsonSerializer.Serialize(CommitPayload(builder, ["postDate", "docNum"])));
        // 回應照欄位目錄的順序寫出勾選的欄位，填了幾格是第 2、4、5 列各兩格。
        Assert.Equal(["docNum", "postDate"], Strings(committed.GetProperty("fillDownFields")));
        Assert.Equal(6, committed.GetProperty("fillDownCount").GetInt64());
        Assert.Equal(5, committed.GetProperty("projectedRowCount").GetInt32());
        var rdeFieldId = committed.GetProperty("rdeFields")[0].GetProperty("fieldId").GetString()!;

        // GL 是整理後的值：每張傳票的續列帶著第一列的傳票號碼與日期，項次照傳票自動編號；沒勾選的攸關資料元素欄照原樣空白。
        var gl = await host.DispatchAsync("query.dataPreview", """{ "dataset": "gl" }""");
        Assert.Equal(5, gl.GetProperty("totalCount").GetInt64());
        Assert.Equal(["JV-1", "JV-1", "JV-2", "JV-2", "JV-2"], Column(gl, "傳票號碼"));
        Assert.Equal(["2025-03-01", "2025-03-01", "2025-03-04", "2025-03-04", "2025-03-04"], Column(gl, "總帳日期"));
        Assert.Equal(["1", "2", "1", "2", "3"], Column(gl, "傳票文件項次"));
        Assert.Equal(["財務部", null, "業務部", "業務部", null], Column(gl, "部門"));

        // PBC-GL 保持原樣：續列的傳票號碼與日期還是空白。
        var pbc = await host.DispatchAsync("query.dataPreview", """{ "dataset": "pbcGl" }""");
        var pbcColumns = pbc.GetProperty("columns").EnumerateArray().Select(column => column.GetString()).ToList();
        var pbcRows = pbc.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(5, pbcRows.Length);
        Assert.Equal(JsonValueKind.Null, pbcRows[1][pbcColumns.IndexOf("傳票號碼")].ValueKind);
        Assert.Equal(JsonValueKind.Null, pbcRows[1][pbcColumns.IndexOf("傳票日期")].ValueKind);
        Assert.Equal("JV-2", pbcRows[2][pbcColumns.IndexOf("傳票號碼")].GetString());

        // 重開案件：勾選跟著已確認的配對帶回來。
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(["docNum", "postDate"], Strings(loaded.GetProperty("mapping").GetProperty("gl").GetProperty("fillDownFields")));

        // 取消勾選並重新確認：只重新投影，續列的總帳日期空白，所以落到 GL(未納入測試)。
        var recommitted = await host.DispatchAsync("mapping.commit.gl",
            JsonSerializer.Serialize(CommitPayload(builder, [], rdeFieldId)));
        Assert.Empty(recommitted.GetProperty("fillDownFields").EnumerateArray());
        Assert.Equal(0, recommitted.GetProperty("fillDownCount").GetInt64());
        var effective = await host.DispatchAsync("query.dataPreview", """{ "dataset": "gl" }""");
        Assert.Equal(2, effective.GetProperty("totalCount").GetInt64());
        var excluded = await host.DispatchAsync("query.dataPreview", """{ "dataset": "glExcluded" }""");
        Assert.Equal(3, excluded.GetProperty("totalCount").GetInt64());
        Assert.Equal([null, null, null], Column(excluded, "傳票號碼"));
    }

    // 填補是對來源欄做的：總帳日期和傳票核准日配同一個來源欄時只勾總帳日期，傳票核准日也一起填補，
    // 回應與重開案件都列出兩者（使用者 2026-10-28 裁定依來源欄成組）。
    // 第一次失敗：Focused 收據 20261028-025427302-c9b53532147943d28f35cdcfb18402d2（回應只列出勾選的欄位）。
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task FillDown_FieldsSharingASourceColumn_AreFilledAndListedTogether(string provider)
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", CreatePayload(provider));
        var projectId = created.GetProperty("projectId").GetString()!;
        var builder = Workbook();
        await ImportGlAsync(host, builder);

        var mapping = builder.BuildFlagModeMapping();
        mapping[GlMappingKeys.DocDate] = "傳票日期";
        var committed = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping,
            amountMode = "flag",
            approvalDateMode = "mapped",
            manualAutoPolicy = new { manualValues = new[] { "1" }, automaticValues = new[] { "0" } },
            rdeFields = Array.Empty<object>(),
            fillDownFields = new[] { "docNum", "postDate" }
        }));
        Assert.Equal(["docNum", "postDate", "docDate"], Strings(committed.GetProperty("fillDownFields")));
        // 同一個來源欄只填一次：傳票號碼與傳票日期兩欄，第 2、4、5 列各一格。
        Assert.Equal(6, committed.GetProperty("fillDownCount").GetInt64());

        var gl = await host.DispatchAsync("query.dataPreview", """{ "dataset": "gl" }""");
        Assert.Equal(["2025-03-01", "2025-03-01", "2025-03-04", "2025-03-04", "2025-03-04"], Column(gl, "傳票核准日"));

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(["docNum", "postDate", "docDate"],
            Strings(loaded.GetProperty("mapping").GetProperty("gl").GetProperty("fillDownFields")));
    }

    // 填了幾格跟著已確認的配對保存，重開案件時第三步摘要看得到；取消勾選重新確認後是 0（使用者 2026-10-28 裁定隨配對保存）。
    // 第一次失敗：Focused 收據 20261028-025427302-c9b53532147943d28f35cdcfb18402d2（重開案件的配對沒有 fillDownCount）。
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task FillDown_CountIsSavedWithTheConfirmedMapping(string provider)
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", CreatePayload(provider));
        var projectId = created.GetProperty("projectId").GetString()!;
        var builder = Workbook();
        await ImportGlAsync(host, builder);

        var committed = await host.DispatchAsync("mapping.commit.gl",
            JsonSerializer.Serialize(CommitPayload(builder, ["postDate", "docNum"])));
        Assert.Equal(6, committed.GetProperty("fillDownCount").GetInt64());
        var rdeFieldId = committed.GetProperty("rdeFields")[0].GetProperty("fieldId").GetString()!;

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(6, loaded.GetProperty("mapping").GetProperty("gl").GetProperty("fillDownCount").GetInt64());

        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(CommitPayload(builder, [], rdeFieldId)));
        var reloaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(0, reloaded.GetProperty("mapping").GetProperty("gl").GetProperty("fillDownCount").GetInt64());
    }

    // 往下填補之後，INF 報告主表與底稿第二步的「分錄來源」讀整理後的值，和同一列的傳票號碼一致；
    // 「借貸代號」在原始列空白時，依整理後的借貸方向寫已確認配對的借方或貸方代碼。
    // 「可靠性樣本_所有欄位」仍是 PBC-GL 原樣（使用者 2026-10-28 裁定改讀整理後的值）。
    // 第一次失敗：Focused 收據 20261028-025427302-c9b53532147943d28f35cdcfb18402d2（續列的分錄來源與第 2 列的借貸代號是空白）。
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task FillDown_InfAndWorkpaperStep2_ShowTheCleanedSourceModuleAndDirection(string provider)
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", CreatePayload(provider));
        var projectId = created.GetProperty("projectId").GetString()!;
        // 一張傳票三列，傳票號碼、日期與來源模組只寫在第一列；第 2 列的借方旗標也空白，往下填補後是借方。
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "來源模組", "金額", "借方旗標")
            .AddRow("JV-1", "2025-03-01", "1000", "合成現金", "合成摘要", "GL-A", 10, 1)
            .AddRow(null, null, "1100", "合成存款", "合成摘要", null, 10, null)
            .AddRow(null, null, "4000", "合成收入", "合成摘要", null, 20, 0);
        await ImportGlAsync(host, builder);
        var tb = new InlineTbWorkbookBuilder()
            .AddRow("1000", "合成現金", 10)
            .AddRow("1100", "合成存款", 10)
            .AddRow("4000", "合成收入", -20);
        var tbPath = tb.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tbPath, fileName = "合成試算表.xlsx" }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(tbPath);
        }

        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = builder.BuildFlagModeMapping(),
            amountMode = "flag",
            manualAutoPolicy = new { manualValues = new[] { "1" }, automaticValues = new[] { "0" } },
            rdeFields = Array.Empty<object>(),
            fillDownFields = new[] { "docNum", "postDate", "jeSource", "dcField" }
        }));
        await host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
        {
            mapping = InlineTbWorkbookBuilder.BuildDirectModeMapping(),
            changeMode = "direct"
        }));
        var validation = await host.DispatchAsync("validate.run");
        Assert.True(validation.GetProperty("completenessTest").GetProperty("eligibility").GetProperty("isEligible").GetBoolean());
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;

        var artifacts = await host.DispatchAsync("export.validationArtifacts", JsonSerializer.Serialize(new { runId }));
        var inf = Assert.Single(artifacts.GetProperty("artifacts").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "infReport");
        using (var workbook = new XLWorkbook(Path.Combine(host.ProjectsRoot, projectId, inf.GetProperty("fileName").GetString()!)))
        {
            AssertCleanedSampleRows(workbook.Worksheet("INF Testing 可靠性測試"));

            var allFields = workbook.Worksheet("可靠性樣本_所有欄位");
            var headers = Enumerable.Range(1, allFields.Row(1).LastCellUsed()!.Address.ColumnNumber)
                .Select(column => allFields.Cell(1, column).GetString())
                .ToList();
            Assert.Equal("GL-A", allFields.Cell(2, headers.IndexOf("來源模組") + 1).GetString());
            Assert.True(allFields.Cell(3, headers.IndexOf("傳票號碼") + 1).IsEmpty());
            Assert.True(allFields.Cell(3, headers.IndexOf("來源模組") + 1).IsEmpty());
            Assert.True(allFields.Cell(3, headers.IndexOf("借方旗標") + 1).IsEmpty());
        }

        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var scenarios = await host.DispatchAsync("filter.commit", """
            {
              "scenarios": [
                {
                  "name": "合成借方",
                  "rationale": "產出底稿",
                  "groups": [
                    { "join": "AND", "rules": [ { "join": "AND", "type": "drCrOnly", "drCr": "debit" } ] }
                  ]
                }
              ]
            }
            """);
        var revision = scenarios.GetProperty("resultRef").GetProperty("revision").GetString()!;
        await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new
        {
            validationRunId = runId,
            prescreenRunId,
            revision
        }));
        var workpaper = await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        {
            validationRunId = runId,
            prescreenRunId,
            scenarioRevision = revision,
            scenarioPositions = new[] { 1 }
        }));
        using var workingPaper = new XLWorkbook(Path.Combine(
            host.ProjectsRoot, projectId, workpaper.GetProperty("artifact").GetProperty("fileName").GetString()!));
        AssertCleanedSampleRows(workingPaper.Worksheet("step2 可靠性測試"));
    }

    // 多個來源追加時不跨來源延續：第二份來源第一列的傳票號碼空白，不會補成第一份來源最後一列的值；
    // 同一份來源裡的空白照樣補。配一個攸關資料元素欄，確認第二遍投影也走同一份填補。
    // 2026-10-28 複審補的整合測試；程式本來就這樣處理，第一次就通過，沒有第一次失敗的收據。
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task FillDown_DoesNotCarryValuesIntoAnAppendedSource(string provider)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload(provider));
        var builder = Workbook();
        await ImportGlAsync(host, builder);
        var appendPath = TestCsvBuilder.WriteFile(
            "傳票號碼,傳票日期,科目代號,科目名稱,摘要,金額,借方旗標,部門\n" +
            ",2025-03-06,1000,合成現金,合成摘要,30,1,財務部\n" +
            "JV-3,,4000,合成收入,合成摘要,30,0,\n",
            TestCsvBuilder.Utf8NoBom);
        try
        {
            var appended = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                mode = "append",
                sources = new object[] { new { filePath = appendPath, fileName = "合成總帳補充.csv" } }
            }));
            Assert.Equal(7, appended.GetProperty("rowCount").GetInt32());
        }
        finally
        {
            TestCsvBuilder.Delete(appendPath);
        }

        var committed = await host.DispatchAsync("mapping.commit.gl",
            JsonSerializer.Serialize(CommitPayload(builder, ["docNum", "postDate"])));
        // 第一份來源 6 格；第二份來源只有第二列的日期補上 1 格，第一列的傳票號碼維持空白。
        Assert.Equal(7, committed.GetProperty("fillDownCount").GetInt64());

        var gl = await host.DispatchAsync("query.dataPreview", """{ "dataset": "gl" }""");
        Assert.Equal(7, gl.GetProperty("totalCount").GetInt64());
        Assert.Equal(["JV-1", "JV-1", "JV-2", "JV-2", "JV-2", null, "JV-3"], Column(gl, "傳票號碼"));
        Assert.Equal(
            ["2025-03-01", "2025-03-01", "2025-03-04", "2025-03-04", "2025-03-04", "2025-03-06", "2025-03-06"],
            Column(gl, "總帳日期"));
        Assert.Equal(["財務部", null, "業務部", "業務部", null, "財務部", null], Column(gl, "部門"));
    }

    // INF 主表與底稿第二步共用的版面：樣本從第 53 列起，B 欄傳票號碼、E 欄借貸代號、J 欄分錄來源。
    private static void AssertCleanedSampleRows(IXLWorksheet sheet)
    {
        Assert.Equal(["JV-1", "JV-1", "JV-1"], new[] { 53, 54, 55 }.Select(row => sheet.Cell(row, "B").GetString()));
        Assert.Equal(["1", "1", "0"], new[] { 53, 54, 55 }.Select(row => sheet.Cell(row, "E").GetString()));
        Assert.Equal(["GL-A", "GL-A", "GL-A"], new[] { 53, 54, 55 }.Select(row => sheet.Cell(row, "J").GetString()));
        Assert.True(sheet.Cell(56, "B").IsEmpty());
    }

    private static async Task ImportGlAsync(HandlerTestHost host, InlineGlWorkbookBuilder builder)
    {
        var path = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path, fileName = "合成總帳.xlsx" }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    // 只能勾選已配對的欄位；沒有來源欄的欄位、字面值的借貸代碼與不是陣列的資料，畫面照正常操作送不出來。
    [Theory]
    [InlineData("""["approveBy"]""")]
    [InlineData("""["dcDebitCode"]""")]
    [InlineData("""["docNum", "docNum"]""")]
    [InlineData("""["notAField"]""")]
    [InlineData("\"docNum\"")]
    public async Task FillDown_RejectsFieldsWithoutASourceColumn(string fillDownFieldsJson)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload("sqlite"));
        var builder = Workbook();
        var path = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path, fileName = "合成總帳.xlsx" }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }

        var payload = JsonSerializer.Serialize(new
        {
            mapping = builder.BuildFlagModeMapping(),
            amountMode = "flag",
            manualAutoPolicy = new { manualValues = new[] { "1" }, automaticValues = new[] { "0" } },
            rdeFields = Array.Empty<object>()
        });
        payload = payload[..^1] + ",\"fillDownFields\":" + fillDownFieldsJson + "}";

        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("mapping.commit.gl", payload));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
    }
}
