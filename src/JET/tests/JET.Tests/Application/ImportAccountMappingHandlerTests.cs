using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// import.accountMapping.fromFile 黑箱驗收（manifest 細節段）：
/// 匯入即投影、replace-only、分類白名單、解鎖未預期借貸組合。
/// oracle：demo 科目配對派生規格 + 獨立參數化 SQL recount。
/// </summary>
public sealed class ImportAccountMappingHandlerTests
{
    private static string WriteCsv(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "jet-am-tests", Guid.NewGuid().ToString("N") + ".csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ImportAccountMapping_DemoFile_ReturnsBatchShapeAndPersistsTargets()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var file = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAccountMappingFile");
        var data = await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = file.GetProperty("filePath").GetString(),
            fileName = file.GetProperty("fileName").GetString()
        }));

        // demo 派生規格：全 150 科目列入（Cash 2、Receivables 2、Receipt in advance 1、4 開頭 Revenue 3、其餘 Others）。
        Assert.Equal(DemoDataFactory.TbAccountCount, data.GetProperty("rowCount").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("batchId").GetString()));
        Assert.Equal("AccountMapping-demo-2025.xlsx", data.GetProperty("fileName").GetString());
        Assert.True(data.GetProperty("hasAnyCategory").GetBoolean());
        Assert.True(data.GetProperty("hasRevenue").GetBoolean());
        Assert.True(data.GetProperty("hasCounterpart").GetBoolean());

        var targetCount = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM target_account_mapping;");
        Assert.Equal(DemoDataFactory.TbAccountCount, targetCount);

        var cashCount = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            "SELECT COUNT(*) FROM target_account_mapping WHERE standardized_category = 'Cash';");
        Assert.Equal(2, cashCount);
    }

    [Fact]
    public async Task ImportAccountMapping_AllBlankCategories_ProjectsToOthersAndReturnsEligibleContentFacts()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);
        var path = WriteCsv("account code,account name,category\n1000,Blank one,\n2000,Blank two,\n");

        try
        {
            var data = await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
            {
                filePath = path,
                fileName = "blank-categories.csv"
            }));

            Assert.Equal(2, data.GetProperty("rowCount").GetInt32());
            Assert.True(data.GetProperty("hasAnyCategory").GetBoolean());
            Assert.False(data.GetProperty("hasRevenue").GetBoolean());
            Assert.False(data.GetProperty("hasCounterpart").GetBoolean());

            Assert.Equal(2, await DemoProjectPipeline.QueryScalarAsync(
                host,
                context.ProjectId,
                "SELECT COUNT(*) FROM target_account_mapping WHERE category_id = 'builtin.others';"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ImportAccountMapping_FirmEnglishHeaders_ImportsViaKeyword()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "GL_NUMBER";
            ws.Cell(1, 2).Value = "GL_NAME";
            ws.Cell(1, 3).Value = "STANDARDIZED_ACCOUNT_NAME";
            ws.Cell(2, 1).Value = "1101"; ws.Cell(2, 2).Value = "現金"; ws.Cell(2, 3).Value = "Cash";
            ws.Cell(3, 1).Value = "4001"; ws.Cell(3, 2).Value = "銷貨收入"; ws.Cell(3, 3).Value = "Revenue";
            ws.Cell(4, 1).Value = "1201"; ws.Cell(4, 2).Value = "應收帳款"; ws.Cell(4, 3).Value = "Receivables";
        });

        try
        {
            var data = await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
            {
                filePath = path,
                fileName = "firm-account-mapping.xlsx"
            }));

            Assert.Equal(3, data.GetProperty("rowCount").GetInt32());
            Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId,
                "SELECT COUNT(*) FROM target_account_mapping WHERE account_code='4001' AND standardized_category='Revenue';"));
            Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId,
                "SELECT COUNT(*) FROM target_account_mapping WHERE account_code='1201' AND standardized_category='Receivables';"));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ImportAccountMapping_AppendMode_ThrowsUnsupportedMode()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var file = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAccountMappingFile");

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "import.accountMapping.fromFile", JsonSerializer.Serialize(new
            {
                filePath = file.GetProperty("filePath").GetString(),
                mode = "append"
            })));

        Assert.Equal("unsupported_mode", ex.Code);
    }

    [Fact]
    public async Task ImportAccountMapping_InvalidCategory_ThrowsProjectionFailedAndRollsBack()
    {
        using var host = new HandlerTestHost();
        // 關閉預設科目配對匯入:本測驗證「失敗匯入整批 rollback、不留殘骸」,需從空 target 起算。
        var context = await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);

        var path = WriteCsv("科目代號,科目名稱,標準化分類\n1101,現金,Cash\n9999,神祕科目,NotACategory\n");

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path })));

        Assert.Equal("projection_failed", ex.Code);
        Assert.Equal(
            "科目配對檔有 1 列無法轉換（整批已還原）："
            + "第 3 列：分類「NotACategory」不存在於目前專案的科目分類"
            + "（Revenue、Receivables、Cash、Receipt in advance、Others）。",
            ex.Message);

        // 整批 rollback：target 與批次皆不留殘骸。
        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM target_account_mapping;"));
        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            "SELECT COUNT(*) FROM import_batch WHERE dataset_kind = 'account_mapping';"));
    }

    [Fact]
    public async Task ImportAccountMapping_TxtExtension_ThrowsUnsupportedFileType()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        var path = WriteCsv("a,b,c\n1,2,Cash\n").Replace(".csv", ".txt");
        File.WriteAllText(path, "a,b,c\n1,2,Cash\n");

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path })));

        Assert.Equal("unsupported_file_type", ex.Code);
    }

    [Fact]
    public async Task ImportAccountMapping_DuplicateAccountCode_LastRowWins()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        // 同一科目代號兩列：投影層 last-wins 去重（避免同科目雙分類的判定歧義）。
        var path = WriteCsv("科目代號,科目名稱,標準化分類\n1101,現金,Cash\n1101,現金,Revenue\n");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));

        Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM target_account_mapping;"));
        Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            "SELECT COUNT(*) FROM target_account_mapping WHERE account_code = '1101' AND standardized_category = 'Revenue';"));
    }

    [Fact]
    public async Task PrescreenRun_AfterAccountMappingImport_UnexpectedAccountPairMatchesRecount()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var data = await host.DispatchAsync("prescreen.run");

        // 獨立 recount（guide §5 否定面；寫法獨立於述詞：貸方 Revenue 列 JOIN 取得，
        // 「同傳票有正常對方借方」以 document_number NOT IN 反查表達，等價於述詞的 NOT EXISTS）。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            """
            SELECT COUNT(*) FROM target_gl_entry g
            JOIN target_account_mapping m
              ON m.account_code = g.account_code AND m.standardized_category = 'Revenue'
            WHERE g.amount_scaled < 0
              AND g.document_number NOT IN (
                  SELECT d.document_number FROM target_gl_entry d
                  JOIN target_account_mapping md ON md.account_code = d.account_code
                  WHERE d.amount_scaled >= 0
                    AND md.standardized_category IN ('Receivables','Cash','Receipt in advance'));
            """);

        var rule = data.GetProperty("unexpectedAccountPair");
        Assert.True(recount > 0);
        Assert.Equal(recount, rule.GetProperty("count").GetInt64());
        Assert.Equal("V", rule.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ProjectLoad_AfterAccountMappingImport_ResumesImportState()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        var state = loaded.GetProperty("importState").GetProperty("accountMapping");
        Assert.Equal(DemoDataFactory.TbAccountCount, state.GetProperty("rowCount").GetInt32());
        Assert.Equal("AccountMapping-demo-2025.xlsx", state.GetProperty("fileName").GetString());
        Assert.True(state.GetProperty("hasAnyCategory").GetBoolean());
        Assert.True(state.GetProperty("hasRevenue").GetBoolean());
        Assert.True(state.GetProperty("hasCounterpart").GetBoolean());
    }

    [Fact]
    public async Task FilterPreview_UnexpectedAccountPairKey_EqualsPrescreenRunCount()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        var prescreen = await host.DispatchAsync("prescreen.run");
        var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new
        {
            scenario = new
            {
                name = "借貸組合",
                rationale = "共用述詞 seam 驗證",
                groups = new object[]
                {
                    new
                    {
                        join = "AND",
                        rules = new object[]
                        {
                            new { join = "AND", type = "prescreen", prescreenKey = "unexpectedAccountPair" }
                        }
                    }
                }
            }
        }));

        Assert.Equal(
            prescreen.GetProperty("unexpectedAccountPair").GetProperty("count").GetInt64(),
            preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_UnexpectedAccountPairWithoutImport_ThrowsInvalidScenario()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "filter.preview", JsonSerializer.Serialize(new
            {
                scenario = new
                {
                    name = "借貸組合",
                    rationale = "未匯入科目配對應被擋下",
                    groups = new object[]
                    {
                        new
                        {
                            join = "AND",
                            rules = new object[]
                            {
                                new { join = "AND", type = "prescreen", prescreenKey = "unexpectedAccountPair" }
                            }
                        }
                    }
                }
            })));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task QueryDataPreview_AccountMappings_ReturnsFixedColumnsAndTotal()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        var data = await host.DispatchAsync(
            "query.dataPreview", JsonSerializer.Serialize(new { dataset = "accountMappings" }));

        Assert.Equal(DemoDataFactory.TbAccountCount, data.GetProperty("totalCount").GetInt64());
        Assert.Equal(
            new[] { "accountCode", "accountName", "standardizedCategory" },
            data.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToArray());
        Assert.Equal(50, data.GetProperty("rows").GetArrayLength());
    }
}
