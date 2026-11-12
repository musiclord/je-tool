using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// import.accountMapping.fromFile 黑箱驗收：
/// 匯入即投影、replace-only、分類欄的新名稱新增成自訂分類、解鎖未預期借貸組合。
/// oracle：demo 科目配對派生規格 + 獨立參數化 SQL recount。
/// </summary>
public sealed class ImportAccountMappingHandlerTests
{
    [Theory]
    [InlineData("sqlite")]
    public async Task ThirdRowHeaderWithAutoFilterAndHiddenRow_ImportsEveryAccount(string provider)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectName = "合成篩選工作簿", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "Synthetic heading";
            ws.Cell(3, 1).Value = "GL_Number"; ws.Cell(3, 2).Value = "GL_Name"; ws.Cell(3, 3).Value = "Standardized Account Name*";
            ws.Cell(4, 1).Value = "A"; ws.Cell(4, 2).Value = "Visible"; ws.Cell(4, 3).Value = "Cash";
            ws.Cell(5, 1).Value = "B"; ws.Cell(5, 2).Value = "Hidden"; ws.Cell(5, 3).Value = "Revenue";
            ws.Range("A3:C5").SetAutoFilter();
            ws.Row(5).Hide();
        });
        try
        {
            var imported = await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            Assert.Equal(2, imported.GetProperty("rowCount").GetInt32());
            var preview = await host.DispatchAsync("query.dataPreview", """{"dataset":"accountMapping"}""");
            Assert.Equal(new[] { "A|Visible|Cash", "B|Hidden|Revenue" },
                preview.GetProperty("rows").EnumerateArray().Select(r => string.Join("|", r.EnumerateArray().Select(c => c.GetString()))));
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    [Fact]
    public async Task DifferenceQuery_RequiresProjectAndRejectsWrongListOrCursor()
    {
        using var host = new HandlerTestHost();
        var noProject = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "query.accountMappingDifferencePage", """{"kind":"mappingOnly"}"""));
        Assert.Equal(JetErrorCodes.NoActiveProject, noProject.Code);
        await host.DispatchAsync("project.create", """{"projectName":"合成差異查詢","periodStart":"2025-01-01","periodEnd":"2025-12-31"}""");
        foreach (var payload in new[]
        {
            """{"kind":"not-a-list"}""",
            """{"kind":"mappingOnly","cursor":"not-base64!"}""",
            JsonSerializer.Serialize(new { kind = "unmapped", cursor = PageCursor.Encode("mappingOnly\nE") })
        })
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("query.accountMappingDifferencePage", payload));
            Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        }
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task ImportCountsAndPages_UseGlUnionTb_NotTheMappingEditorUnion(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, b => b
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V1", "2025-01-02", " \tA　", "GL A", "Synthetic", 10, 1)
            .AddRow("V1", "2025-01-02", "B", "GL B", "Synthetic", 10, 0)
            .AddRow("V2", "2025-02-02", "A", "GL A", "Synthetic", 20, 1)
            .AddRow("V2", "2025-02-02", "C", "GL C", "Synthetic", 20, 0)
            .AddRow("OUT", "2024-01-02", "X", "Outside period", "Synthetic", 10, 1),
            databaseProvider: provider, configureTb: b => b.AddRow("A", "TB A", 30)
                .AddRow(" T ", "TB only", -30).AddRow("Z", "TB zero", 0));
        var path = WriteCsv("account code,account name,category\n　A　,Mapping A,Cash\nB,Blank category,\nE,Extra,Cash\nX,Outside,Revenue\n");
        try
        {
            var imported = await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            Assert.Equal(2, imported.GetProperty("mappingOnlyCount").GetInt64());
            Assert.Equal(3, imported.GetProperty("unmappedCount").GetInt64());
            Assert.Equal(1, imported.GetProperty("blankCategoryCount").GetInt32());
            Assert.True(imported.GetProperty("hasRevenue").GetBoolean()); // 額外科目對前置條件的影響保留。
            Assert.Equal(4, imported.GetProperty("rowCount").GetInt32());
            foreach (var (kind, expected) in new[]
            {
                ("mappingOnly", new[] { "E|Extra", "X|Outside" }),
                ("unmapped", new[] { "C|GL C", "T|TB only", "Z|TB zero" })
            })
            {
                var rows = new List<string>();
                string? cursor = null;
                do
                {
                    var page = await host.DispatchAsync("query.accountMappingDifferencePage", JsonSerializer.Serialize(new { kind, cursor, pageSize = 1 }));
                    if (cursor is null) Assert.Equal(expected.Length, page.GetProperty("totalCount").GetInt64());
                    else Assert.Equal(JsonValueKind.Null, page.GetProperty("totalCount").ValueKind);
                    var row = Assert.Single(page.GetProperty("rows").EnumerateArray());
                    rows.Add(row.GetProperty("accountNumber").GetString() + "|" + row.GetProperty("accountName").GetString());
                    cursor = page.GetProperty("nextCursor").GetString();
                    Assert.True(rows.Count <= expected.Length);
                } while (cursor is not null);
                Assert.Equal(expected, rows);
            }
            await host.DispatchAsync("project.releaseLock");
            var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
            Assert.False(loaded.GetProperty("importState").GetProperty("accountMapping").TryGetProperty("mappingOnlyCount", out _));
            await File.WriteAllTextAsync(path, "account code,account name,category\nA,A,Cash\nB,B,\nC,C,Others\nT,T,Others\nZ,Z,Others\n");
            var replaced = await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            Assert.Equal(0, replaced.GetProperty("mappingOnlyCount").GetInt64());
            Assert.Equal(0, replaced.GetProperty("unmappedCount").GetInt64());
            var empty = await host.DispatchAsync("query.accountMappingDifferencePage", """{"kind":"mappingOnly"}""");
            Assert.Empty(empty.GetProperty("rows").EnumerateArray());
            Assert.Equal(0, empty.GetProperty("totalCount").GetInt64());
            Assert.Equal(JsonValueKind.Null, empty.GetProperty("nextCursor").ValueKind);
        }
        finally { File.Delete(path); }
    }

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
            host, context.ProjectId, "SELECT COUNT(*) FROM account_mapping;");
        Assert.Equal(DemoDataFactory.TbAccountCount, targetCount);

        var cashCount = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            "SELECT COUNT(*) FROM account_mapping WHERE standardized_category = 'Cash';");
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
                "SELECT COUNT(*) FROM account_mapping WHERE category_id = 'builtin.others';"));
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
                "SELECT COUNT(*) FROM account_mapping WHERE account_number='4001' AND standardized_category='Revenue';"));
            Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId,
                "SELECT COUNT(*) FROM account_mapping WHERE account_number='1201' AND standardized_category='Receivables';"));
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

        // 三個欄名都認不出來，分類欄是照欄位順序認的；這時案件沒有的分類名稱不會變成新分類。
        var path = WriteCsv("甲,乙,丙\n1101,現金,Cash\n9999,神祕科目,NotACategory\n");

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path })));

        Assert.Equal("projection_failed", ex.Code);
        Assert.Equal(
                        // 這句的用字已定案。
                        // 一律寫「科目配對範本」。
                        "科目配對範本有 1 列無法讀取，本次匯入未儲存："
            + "第 3 列：分類「NotACategory」不在本案件的科目分類中"
            + "（Revenue、Receivables、Cash、Receipt in advance、Others），請改用清單中的分類名稱；"
            + "要新增分類，請先把這一欄的欄名改成「Standardized Account Name*」。",
            ex.Message);

        // 整批 rollback：target 與批次皆不留殘骸，分類也沒有多出來。
        Assert.Equal(AccountCategoryBuiltIns.All.Count, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM account_category;"));
        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM account_mapping;"));
        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            "SELECT COUNT(*) FROM import_batch WHERE dataset_kind = 'account_mapping';"));
    }

    [Fact]
    public async Task ImportAccountMapping_NewCategoryNames_BecomeCustomCategoriesOfTheCase()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);
        var before = (await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }))).GetProperty("accountCategory");
        var path = WriteCsv(
            "科目代號,科目名稱,標準化分類\n1101,現金,Cash\n1301,商品存貨,存貨\n1302,在途存貨, 存貨 \n1501,機器設備,固定資產\n");

        try
        {
            var data = await host.DispatchAsync(
                "import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));

            var snapshot = data.GetProperty("accountCategory");
            Assert.Equal(before.GetProperty("revision").GetInt32() + 1, snapshot.GetProperty("revision").GetInt32());
            var categories = snapshot.GetProperty("categories").EnumerateArray().ToArray();
            Assert.Equal(
                ["Revenue", "Receivables", "Cash", "Receipt in advance", "Others", "存貨", "固定資產"],
                categories.Select(item => item.GetProperty("label").GetString()));
            Assert.Equal(
                [0, 1, 2, 3, 4, 5, 6],
                categories.Select(item => item.GetProperty("ordinal").GetInt32()));
            foreach (var added in categories.Skip(5))
            {
                Assert.True(AccountCategoryInvariant.IsCustomId(added.GetProperty("categoryId").GetString()));
                Assert.Equal(AccountCategoryBuiltIns.OthersRole, added.GetProperty("semanticRole").GetString());
                Assert.False(added.GetProperty("isBuiltIn").GetBoolean());
                Assert.Equal(JsonValueKind.Null, added.GetProperty("parentCategoryId").ValueKind);
            }

            // 新分類和科目配對在同一次匯入寫進案件：兩個科目歸到「存貨」，舊版分類欄照舊寫 Others。
            Assert.Equal(2, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId,
                """
                SELECT COUNT(*) FROM account_mapping m
                JOIN account_category c ON c.category_id = m.category_id
                WHERE c.label = '存貨' AND m.standardized_category = 'Others';
                """));
            Assert.Equal(7, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM account_category;"));
            // 每個分類在上下層對照表都有自己對自己的一列，依分類篩選才找得到新分類的科目。
            Assert.Equal(7, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId,
                "SELECT COUNT(*) FROM account_category_path WHERE ancestor_id = descendant_id;"));

            var loaded = (await host.DispatchAsync(
                "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }))).GetProperty("accountCategory");
            Assert.Equal(snapshot.GetRawText(), loaded.GetRawText());

            // 再匯入同一個檔案：分類已經在案件裡，不重複新增，分類清單的版本也不變。
            var again = await host.DispatchAsync(
                "import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            Assert.Equal(snapshot.GetRawText(), again.GetProperty("accountCategory").GetRawText());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ImportAccountMapping_NewCategoryNames_KeepExistingCustomCategories()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);
        var before = (await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }))).GetProperty("accountCategory");
        var saved = await host.DispatchAsync("accountCategory.save", JsonSerializer.Serialize(new
        {
            revision = before.GetProperty("revision").GetInt32(),
            categories = before.GetProperty("categories").EnumerateArray()
                .Select(item => (object)new
                {
                    categoryId = item.GetProperty("categoryId").GetString(),
                    label = item.GetProperty("label").GetString(),
                    ordinal = item.GetProperty("ordinal").GetInt32(),
                    semanticRole = item.GetProperty("semanticRole").GetString(),
                    parentCategoryId = (string?)null
                })
                .Append(new { label = "預付款項", ordinal = 5, semanticRole = "others", parentCategoryId = (string?)null })
                .ToArray()
        }));
        var existingId = saved.GetProperty("categories").EnumerateArray()
            .Single(item => item.GetProperty("label").GetString() == "預付款項").GetProperty("categoryId").GetString();
        var path = WriteCsv("科目代號,科目名稱,標準化分類\n1401,預付貨款,預付款項\n1301,商品存貨,存貨\n");

        try
        {
            var data = await host.DispatchAsync(
                "import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));

            var categories = data.GetProperty("accountCategory").GetProperty("categories").EnumerateArray().ToArray();
            Assert.Equal(
                ["Revenue", "Receivables", "Cash", "Receipt in advance", "Others", "預付款項", "存貨"],
                categories.Select(item => item.GetProperty("label").GetString()));
            Assert.Equal(existingId, categories[5].GetProperty("categoryId").GetString());
            Assert.Equal(
                saved.GetProperty("revision").GetInt32() + 1,
                data.GetProperty("accountCategory").GetProperty("revision").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ImportAccountMapping_FailedImportWithNewCategoryNames_AddsNoCategory()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);
        var before = (await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }))).GetProperty("accountCategory");
        var path = WriteCsv("科目代號,科目名稱,標準化分類\n1301,商品存貨,存貨\n,沒有編號的科目,固定資產\n");

        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path })));

            Assert.Equal("projection_failed", ex.Code);
            Assert.Equal("科目配對範本有 1 列無法讀取，本次匯入未儲存：第 3 列：科目編號空白。", ex.Message);
            var after = (await host.DispatchAsync(
                "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }))).GetProperty("accountCategory");
            Assert.Equal(before.GetRawText(), after.GetRawText());
            Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM account_mapping;"));
        }
        finally
        {
            File.Delete(path);
        }
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
    public async Task ImportAccountMapping_DuplicateAccountNumber_LastRowWins()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        // 同一科目代號兩列：投影層 last-wins 去重（避免同科目雙分類的判定歧義）。
        var path = WriteCsv("科目代號,科目名稱,標準化分類\n1101,現金,Cash\n1101,現金,Revenue\n");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));

        Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM account_mapping;"));
        Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            "SELECT COUNT(*) FROM account_mapping WHERE account_number = '1101' AND standardized_category = 'Revenue';"));
    }

    [Fact]
    public async Task PrescreenRun_AfterAccountMappingImport_UnexpectedAccountPairMatchesRecount()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var data = await host.DispatchAsync("prescreen.run");

        // 獨立 recount（寫法獨立於述詞：貸方 Revenue 列 JOIN 取得，
        // 「同傳票有正常對方借方」以 voucher_number NOT IN 反查表達，等價於述詞的 NOT EXISTS）。
        // 未預期借貸組合照舊版 R3 在第四步列整張命中傳票，並要求傳票裡有金額大於 0 的分錄，
        // recount 改數命中傳票的全部分錄；
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            """
            SELECT COUNT(*) FROM gl w
            WHERE w.voucher_number IN (
                SELECT g.voucher_number FROM gl g
                JOIN account_mapping m
                  ON m.account_number = g.account_number AND m.standardized_category = 'Revenue'
                WHERE g.amount_scaled < 0
                  AND g.voucher_number IN (SELECT p.voucher_number FROM gl p WHERE p.amount_scaled > 0)
                  AND g.voucher_number NOT IN (
                      SELECT d.voucher_number FROM gl d
                      JOIN account_mapping md ON md.account_number = d.account_number
                      WHERE d.amount_scaled > 0
                        AND md.standardized_category IN ('Receivables','Cash','Receipt in advance')));
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
                        conditions = new object[]
                        {
                            new { join = "AND", type = "prescreen", prescreenKey = "unexpectedAccountPair" }
                        }
                    }
                }
            }
        }));

        // 未預期借貸組合照舊版 R3 在第四步列整張命中傳票，第四步的筆數和第五步畫面的分錄筆數（summaryRowCount）比。
        Assert.Equal(
            prescreen.GetProperty("unexpectedAccountPair").GetProperty("count").GetInt64(),
            preview.GetProperty("scenario").GetProperty("summaryRowCount").GetInt64());
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
                            conditions = new object[]
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
            "query.dataPreview", JsonSerializer.Serialize(new { dataset = "accountMapping" }));

        Assert.Equal(DemoDataFactory.TbAccountCount, data.GetProperty("totalCount").GetInt64());
        Assert.Equal(
            new[] { "accountNumber", "accountName", "standardizedCategory" },
            data.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToArray());
        // 明細每頁最多 200 筆，這份固定合成母體的 150 筆應在同一頁。
        Assert.Equal(150, data.GetProperty("rows").GetArrayLength());
    }
}
