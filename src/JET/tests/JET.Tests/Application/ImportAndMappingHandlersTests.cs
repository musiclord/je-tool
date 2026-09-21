using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class ImportAndMappingHandlersTests
{
    private const string CreatePayload =
        """
        {
          "projectCode": "ENG-2024-001",
          "entityName": "範例股份有限公司",
          "operatorId": "auditor01",
          "periodStart": "2024-01-01",
          "periodEnd": "2024-12-31"
        }
        """;

    /// <summary>5 列平衡 GL workbook：稀疏借貸欄 + 一筆文字金額 + 日期 cell。</summary>
    private static string WriteGlWorkbook()
    {
        return TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            string[] headers = ["日期", "傳票號碼", "會計項目", "項目名稱", "摘要", "借方金額", "貸方金額"];
            for (var i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
            }

            void Row(int r, string doc, string acc, string name, string desc, object? debit, object? credit)
            {
                ws.Cell(r, 1).Value = new DateTime(2024, 1, r - 1);
                ws.Cell(r, 2).Value = doc;
                ws.Cell(r, 3).Value = acc;
                ws.Cell(r, 4).Value = name;
                ws.Cell(r, 5).Value = desc;
                if (debit is string ds) { ws.Cell(r, 6).Value = ds; }
                else if (debit is double dn) { ws.Cell(r, 6).Value = dn; }
                if (credit is string cs) { ws.Cell(r, 7).Value = cs; }
                else if (credit is double cn) { ws.Cell(r, 7).Value = cn; }
            }

            Row(2, "D001", "5100", "進貨", "期初轉入", "100.50", null);   // 文字金額
            Row(3, "D001", "1101", "現金", "期初轉入", null, 100.50);     // 數字金額
            Row(4, "D002", "5100", "進貨", "採購", 250.00, null);
            Row(5, "D002", "2101", "應付帳款", "採購", null, 200.00);
            Row(6, "D002", "1101", "現金", "採購", null, 50.00);
        });
    }

    private static string WriteTbWorkbook()
    {
        return TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            string[] headers = ["會計科目編號", "會計科目名稱", "借方金額", "貸方金額"];
            for (var i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
            }

            ws.Cell(2, 1).Value = "110201";
            ws.Cell(2, 2).Value = "現金-台幣";
            ws.Cell(2, 3).Value = 418509;
            ws.Cell(2, 4).Value = 418509;

            ws.Cell(3, 1).Value = "110202";
            ws.Cell(3, 2).Value = "現金-美元";
            ws.Cell(3, 3).Value = 107228;
            ws.Cell(3, 4).Value = 108719;
        });
    }

    private const string GlCommitPayload =
        """
        {
          "mapping": {
            "docNum": "傳票號碼",
            "postDate": "日期",
            "accNum": "會計項目",
            "accName": "項目名稱",
            "description": "摘要",
            "debitAmount": "借方金額",
            "creditAmount": "貸方金額"
          },
          "amountMode": "dual"
        }
        """;

    [Fact]
    public async Task Import_WithoutActiveProject_ThrowsNoActiveProject()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("import.gl.fromFile", """{ "filePath": "C:\\nope.xlsx" }"""));

        Assert.Equal(JetErrorCodes.NoActiveProject, ex.Code);
    }

    [Fact]
    public async Task Import_MissingFile_ThrowsFileNotFound()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("import.gl.fromFile", """{ "filePath": "C:\\definitely-not-here.xlsx" }"""));

        Assert.Equal(JetErrorCodes.FileNotFound, ex.Code);
    }

    [Fact]
    public async Task Import_UnsupportedExtension_ThrowsUnsupportedFileType()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        // D05 已支援 .xls；使用者明確排除 .xlsb，仍須拒絕並保留多檔錯誤定位。
        var xlsPath = Path.Combine(Path.GetTempPath(), $"jet-{Guid.NewGuid():N}.xlsb");
        await File.WriteAllTextAsync(xlsPath, "not a real xls");
        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = xlsPath })));

            Assert.Equal(JetErrorCodes.UnsupportedFileType, ex.Code);
        }
        finally
        {
            File.Delete(xlsPath);
        }
    }

    [Fact]
    public async Task Import_InvalidMode_ThrowsUnsupportedMode()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        // mode 白名單：replace / append（manifest unsupported_mode）
        var glPath = WriteGlWorkbook();
        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync(
                    "import.gl.fromFile",
                    JsonSerializer.Serialize(new { filePath = glPath, mode = "merge" })));

            Assert.Equal(JetErrorCodes.UnsupportedMode, ex.Code);
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
        }
    }

    [Fact]
    public async Task Import_Append_WithoutBatch_ThrowsNoImportBatch()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        // 狀態轉換負向：第一個來源必須走 replace（manifest append 語意）
        var glPath = WriteGlWorkbook();
        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync(
                    "import.gl.fromFile",
                    JsonSerializer.Serialize(new { filePath = glPath, mode = "append" })));

            Assert.Equal(JetErrorCodes.NoImportBatch, ex.Code);
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
        }
    }

    [Fact]
    public async Task Import_Append_ColumnMismatch_ReportsBothDirections()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var firstPath = TestCsvBuilder.WriteFile("a,b\n1,2\n", TestCsvBuilder.Utf8NoBom);
        var mismatchPath = TestCsvBuilder.WriteFile("a,c\n1,2\n", TestCsvBuilder.Utf8NoBom);
        try
        {
            await host.DispatchAsync(
                "import.gl.fromFile", JsonSerializer.Serialize(new { filePath = firstPath }));

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync(
                    "import.gl.fromFile",
                    JsonSerializer.Serialize(new { filePath = mismatchPath, mode = "append" })));

            // 雙向差集都要指名：來源多出 c、來源缺少 b（manifest column_mismatch）
            Assert.Equal(JetErrorCodes.ColumnMismatch, ex.Code);
            Assert.Contains("c", ex.Message);
            Assert.Contains("b", ex.Message);
        }
        finally
        {
            TestCsvBuilder.Delete(firstPath);
            TestCsvBuilder.Delete(mismatchPath);
        }
    }

    [Fact]
    public async Task Import_BatchPayload_UsesOneActionAndReturnsOrderedSources()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var firstPath = TestCsvBuilder.WriteFile("a,b\nfirst-1,1\nfirst-2,2\n", TestCsvBuilder.Utf8NoBom);
        var secondPath = TestCsvBuilder.WriteFile("a,b\nsecond-1,3\nsecond-2,4\n", TestCsvBuilder.Utf8NoBom);
        try
        {
            var response = await host.DispatchAsync(
                "import.gl.fromFile",
                JsonSerializer.Serialize(new
                {
                    mode = "replace",
                    sources = new object[]
                    {
                        new { filePath = firstPath, fileName = "first.csv" },
                        new { filePath = secondPath, fileName = "second.csv" }
                    }
                }));

            Assert.Equal(4, response.GetProperty("rowCount").GetInt32());
            Assert.Equal(4, response.GetProperty("addedRowCount").GetInt32());
            var sources = response.GetProperty("sources").EnumerateArray().ToArray();
            Assert.Equal(["first.csv", "second.csv"],
                sources.Select(source => source.GetProperty("fileName").GetString()
                    ?? throw new InvalidOperationException("Expected source fileName.")).ToArray());
            Assert.Equal([1, 2],
                sources.Select(source => source.GetProperty("sourceNo").GetInt32()).ToArray());
            Assert.All(sources, source => Assert.Equal(2, source.GetProperty("rowCount").GetInt32()));
        }
        finally
        {
            TestCsvBuilder.Delete(firstPath);
            TestCsvBuilder.Delete(secondPath);
        }
    }

    [Theory]
    [InlineData("""{"filePath":"C:\\missing.csv","sources":[{"filePath":"C:\\other.csv"}]}""")]
    [InlineData("""{"sources":[]}""")]
    [InlineData("""{"encoding":"utf-8","sources":[{"filePath":"C:\\missing.csv"}]}""")]
    public async Task Import_BatchPayload_InvalidShape_ThrowsInvalidPayload(string payload)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("import.gl.fromFile", payload));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
    }

    [Fact]
    public async Task Import_BatchPayload_UnsupportedLaterSource_NamesFailingFile()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var firstPath = TestCsvBuilder.WriteFile("a,b\n1,2\n", TestCsvBuilder.Utf8NoBom);
        var unsupportedPath = Path.Combine(Path.GetTempPath(), $"later-{Guid.NewGuid():N}.xlsb");
        await File.WriteAllTextAsync(unsupportedPath, "unsupported later source");
        try
        {
            var exception = await Assert.ThrowsAsync<JetActionException>(() =>
                host.DispatchAsync(
                    "import.gl.fromFile",
                    JsonSerializer.Serialize(new
                    {
                        sources = new object[]
                        {
                            new { filePath = firstPath },
                            new { filePath = unsupportedPath }
                        }
                    })));

            Assert.Equal(JetErrorCodes.UnsupportedFileType, exception.Code);
            Assert.Contains(Path.GetFileName(unsupportedPath), exception.Message, StringComparison.Ordinal);
            Assert.Contains(".xlsb", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestCsvBuilder.Delete(firstPath);
            File.Delete(unsupportedPath);
        }
    }

    /// <summary>Q1（5 列）+ Q2（4 列）兩個工作表、同一組欄名的活頁簿（多來源精靈的典型輸入）。</summary>
    private static string WriteQuarterWorkbook()
    {
        return TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            string[] headers = ["日期", "傳票號碼", "會計項目", "項目名稱", "摘要", "借方金額", "貸方金額"];

            void Header(ClosedXML.Excel.IXLWorksheet sheet)
            {
                for (var i = 0; i < headers.Length; i++)
                {
                    sheet.Cell(1, i + 1).Value = headers[i];
                }
            }

            void Row(ClosedXML.Excel.IXLWorksheet sheet, int r, string doc, double? debit, double? credit)
            {
                sheet.Cell(r, 1).Value = new DateTime(2024, 1, 1);
                sheet.Cell(r, 2).Value = doc;
                sheet.Cell(r, 3).Value = "1101";
                sheet.Cell(r, 4).Value = "現金";
                sheet.Cell(r, 5).Value = "季別資料";
                if (debit is not null) { sheet.Cell(r, 6).Value = debit.Value; }
                if (credit is not null) { sheet.Cell(r, 7).Value = credit.Value; }
            }

            ws.Name = "Q1";
            Header(ws);
            Row(ws, 2, "D001", 100, null);
            Row(ws, 3, "D001", null, 100);
            Row(ws, 4, "D002", 250, null);
            Row(ws, 5, "D002", null, 200);
            Row(ws, 6, "D002", null, 50);

            var q2 = ws.Workbook.Worksheets.Add("Q2");
            Header(q2);
            Row(q2, 2, "D101", 100, null);
            Row(q2, 3, "D101", null, 100);
            Row(q2, 4, "D102", 250, null);
            Row(q2, 5, "D102", null, 250);
        });
    }

    // journey test：多工作表合併匯入的完整使用者旅程（replace Q1 → commit → append Q2 →
    // 配對失效 → 重新 commit → resume）。分階段斷言屬同一行為鏈（jet-testing skill §3 例外條款）。
    [Fact]
    public async Task Append_TwoWorksheets_FullPipeline()
    {
        using var host = new HandlerTestHost();
        var path = WriteQuarterWorkbook();

        try
        {
            var created = await host.DispatchAsync("project.create", CreatePayload);
            var projectId = created.GetProperty("projectId").GetString()!;

            // 階段 1：第一個工作表以 replace 開批（manifest：rowCount = addedRowCount）
            var first = await host.DispatchAsync(
                "import.gl.fromFile",
                JsonSerializer.Serialize(new { filePath = path, sheetName = "Q1" }));
            Assert.Equal(5, first.GetProperty("rowCount").GetInt32());
            Assert.Equal(5, first.GetProperty("addedRowCount").GetInt32());
            var firstSource = Assert.Single(first.GetProperty("sources").EnumerateArray());
            Assert.Equal("Q1", firstSource.GetProperty("sheetName").GetString());

            // 階段 2：commit 配對（之後會被 append 失效）
            var commit = await host.DispatchAsync("mapping.commit.gl", GlCommitPayload);
            Assert.Equal(5, commit.GetProperty("projectedRowCount").GetInt32());

            // 階段 3：第二個工作表 append（批次總列數累計、批次 ID 不變、欄序不變）
            var second = await host.DispatchAsync(
                "import.gl.fromFile",
                JsonSerializer.Serialize(new { filePath = path, sheetName = "Q2", mode = "append" }));
            Assert.Equal(first.GetProperty("batchId").GetString(), second.GetProperty("batchId").GetString());
            Assert.Equal(9, second.GetProperty("rowCount").GetInt32());
            Assert.Equal(4, second.GetProperty("addedRowCount").GetInt32());
            Assert.Equal(7, second.GetProperty("columns").GetArrayLength());

            var sources = second.GetProperty("sources").EnumerateArray().ToList();
            Assert.Equal(2, sources.Count);
            Assert.Equal("Q2", sources[1].GetProperty("sheetName").GetString());
            Assert.Equal(4, sources[1].GetProperty("rowCount").GetInt32());

            // 階段 4：append 使配對失效（project.load 的 mapping.gl 為 null）、來源清單可 resume
            var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
            Assert.Equal(JsonValueKind.Null, loaded.GetProperty("mapping").GetProperty("gl").ValueKind);
            var glState = loaded.GetProperty("importState").GetProperty("gl");
            Assert.Equal(9, glState.GetProperty("rowCount").GetInt32());
            Assert.Equal(2, glState.GetProperty("sources").GetArrayLength());

            // 階段 5：重新 commit → 投影涵蓋全部 9 列（兩個來源合併為單一母體）
            var recommit = await host.DispatchAsync("mapping.commit.gl", GlCommitPayload);
            Assert.Equal(9, recommit.GetProperty("projectedRowCount").GetInt32());
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task FullPipeline_CreateImportSuggestCommitLoad()
    {
        using var host = new HandlerTestHost();
        var glPath = WriteGlWorkbook();
        var tbPath = WriteTbWorkbook();

        try
        {
            var created = await host.DispatchAsync("project.create", CreatePayload);
            var projectId = created.GetProperty("projectId").GetString()!;

            // import GL
            var glImport = await host.DispatchAsync(
                "import.gl.fromFile",
                JsonSerializer.Serialize(new { filePath = glPath }));
            Assert.Equal(5, glImport.GetProperty("rowCount").GetInt32());
            Assert.Equal(7, glImport.GetProperty("columns").GetArrayLength());

            // commit 先於 TB import → no_import_batch
            var noBatch = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync(
                    "mapping.commit.tb",
                    """{ "mapping": { "accNum": "x", "accName": "y", "debitAmt": "d", "creditAmt": "c" }, "changeMode": "debitCredit" }"""));
            Assert.Equal(JetErrorCodes.NoImportBatch, noBatch.Code);

            // import TB
            var tbImport = await host.DispatchAsync(
                "import.tb.fromFile",
                JsonSerializer.Serialize(new { filePath = tbPath }));
            Assert.Equal(2, tbImport.GetProperty("rowCount").GetInt32());

            // 2026-09-17 使用者要求移除自動建議，必須以明確手動配對繼續。
            await Assert.ThrowsAsync<KeyNotFoundException>(() => host.DispatchAsync(
                "mapping.autoSuggest", "{}"));

            // 缺 description → missing_required_mapping
            var missing = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync(
                    "mapping.commit.gl",
                    """
                    {
                      "mapping": {
                        "docNum": "傳票號碼", "postDate": "日期",
                        "accNum": "會計項目", "accName": "項目名稱",
                        "debitAmount": "借方金額", "creditAmount": "貸方金額"
                      },
                      "amountMode": "dual"
                    }
                    """));
            Assert.Equal(JetErrorCodes.MissingRequiredMapping, missing.Code);
            Assert.Contains("description", missing.Message);

            // commit GL（dual）
            var glCommit = await host.DispatchAsync("mapping.commit.gl", GlCommitPayload);
            Assert.True(glCommit.GetProperty("ok").GetBoolean());
            Assert.Equal(5, glCommit.GetProperty("projectedRowCount").GetInt32());

            // commit TB（debitCredit）
            var tbCommit = await host.DispatchAsync(
                "mapping.commit.tb",
                """
                {
                  "mapping": {
                    "accNum": "會計科目編號", "accName": "會計科目名稱",
                    "debitAmt": "借方金額", "creditAmt": "貸方金額"
                  },
                  "changeMode": "debitCredit"
                }
                """);
            Assert.Equal(2, tbCommit.GetProperty("projectedRowCount").GetInt32());

            // 平衡 workbook → target SUM = 0；dev panel 路徑驗證
            var page = await host.DispatchAsync(
                "dev.db.tableData",
                """{ "tableName": "target_gl_entry", "limit": 50 }""");
            Assert.Equal(5, page.GetProperty("totalCount").GetInt64());

            // project.load 回傳 resume 狀態
            var loaded = await host.DispatchAsync(
                "project.load",
                JsonSerializer.Serialize(new { projectId }));

            Assert.Equal(5, loaded.GetProperty("importState").GetProperty("gl").GetProperty("rowCount").GetInt32());
            Assert.Equal("dual", loaded.GetProperty("mapping").GetProperty("gl").GetProperty("amountMode").GetString());
            Assert.Equal("debitCredit", loaded.GetProperty("mapping").GetProperty("tb").GetProperty("changeMode").GetString());
            Assert.Equal(3, loaded.GetProperty("project").GetProperty("currentStep").GetInt32());
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
            TestWorkbookBuilder.Delete(tbPath);
        }
    }

    [Fact]
    public async Task CommitGl_BadAmountInWorkbook_ThrowsProjectionFailedWithRowDetail()
    {
        using var host = new HandlerTestHost();

        var glPath = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            string[] headers = ["日期", "傳票號碼", "會計項目", "項目名稱", "摘要", "借方金額", "貸方金額"];
            for (var i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
            }

            ws.Cell(2, 1).Value = new DateTime(2024, 1, 1);
            ws.Cell(2, 2).Value = "D001";
            ws.Cell(2, 3).Value = "5100";
            ws.Cell(2, 4).Value = "進貨";
            ws.Cell(2, 5).Value = "x";
            ws.Cell(2, 6).Value = "not-a-number";
        });

        try
        {
            await host.DispatchAsync("project.create", CreatePayload);
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glPath }));

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("mapping.commit.gl", GlCommitPayload));

            Assert.Equal(JetErrorCodes.ProjectionFailed, ex.Code);
            Assert.Contains("第 2 列", ex.Message);
            Assert.Contains("借方金額", ex.Message);
            Assert.Contains("not-a-number", ex.Message);
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
        }
    }

    [Fact]
    public async Task CommitGl_NonBlankInvalidPostDate_RollsBackWholeProjectionAndPreservesCommittedState()
    {
        using var host = new HandlerTestHost();
        var glPath = WriteGlWorkbook();

        try
        {
            var created = await host.DispatchAsync("project.create", CreatePayload);
            var projectId = created.GetProperty("projectId").GetString()!;
            await host.DispatchAsync(
                "import.gl.fromFile",
                JsonSerializer.Serialize(new { filePath = glPath }));
            await host.DispatchAsync("mapping.commit.gl", GlCommitPayload);

            var beforeRows = await DemoProjectPipeline.QueryStringListAsync(
                host,
                projectId,
                "SELECT document_number || '|' || COALESCE(line_item, '') || '|' || amount_scaled " +
                "FROM target_gl_entry ORDER BY entry_id;");
            var beforeLoad = await host.DispatchAsync(
                "project.load",
                JsonSerializer.Serialize(new { projectId }));
            var beforeMapping = beforeLoad.GetProperty("mapping").GetProperty("gl");
            var beforeBatchId = beforeMapping.GetProperty("sourceBatchId").GetString();
            var beforeStep = beforeLoad.GetProperty("project").GetProperty("currentStep").GetInt32();

            // 模擬同一已提交 batch 的第二列來源值被外部修正為非空非法日期：recommit 會先處理
            // 第一列，再於第二列失敗。projection transaction 必須把 delete/partial insert/config update 全部回滾。
            var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
            await using (var connection = database.CreateConnection(projectId))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE staging_gl_raw_row " +
                    "SET row_json = json_set(row_json, '$.\"日期\"', 'not-a-date') " +
                    "WHERE row_number = (SELECT MIN(row_number) + 1 FROM staging_gl_raw_row);";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            var exception = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("mapping.commit.gl", GlCommitPayload));

            Assert.Equal(JetErrorCodes.ProjectionFailed, exception.Code);
            Assert.Contains("not-a-date", exception.Message, StringComparison.Ordinal);
            var afterRows = await DemoProjectPipeline.QueryStringListAsync(
                host,
                projectId,
                "SELECT document_number || '|' || COALESCE(line_item, '') || '|' || amount_scaled " +
                "FROM target_gl_entry ORDER BY entry_id;");
            Assert.Equal(beforeRows, afterRows);

            var afterLoad = await host.DispatchAsync(
                "project.load",
                JsonSerializer.Serialize(new { projectId }));
            Assert.Equal(
                beforeBatchId,
                afterLoad.GetProperty("mapping").GetProperty("gl").GetProperty("sourceBatchId").GetString());
            Assert.Equal(beforeStep, afterLoad.GetProperty("project").GetProperty("currentStep").GetInt32());
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
        }
    }

    /// <summary>含 RFC4180 引號千分位金額與民國年日期的 GL CSV（4 列、兩張平衡傳票）。</summary>
    private const string GlCsvContent =
        "日期,傳票號碼,會計項目,項目名稱,摘要,借方金額,貸方金額\n" +
        "2024-01-01,D001,5100,進貨,期初轉入,\"100.50\",\n" +
        "2024-01-01,D001,1101,現金,期初轉入,,100.50\n" +
        "114/01/02,D900,5100,進貨,民國日期列,\"1,250.00\",\n" +
        "114/01/02,D900,1101,現金,民國日期列,,\"1,250.00\"\n";

    // 契約：manifest import.*.fromFile 支援 .csv / .txt（.txt 視為 CSV 內容）
    [Theory]
    [InlineData(".csv")]
    [InlineData(".txt")]
    public async Task ImportCsv_EndToEnd_StagesCommitsAndProjectsRocDate(string extension)
    {
        using var host = new HandlerTestHost();
        var csvPath = TestCsvBuilder.WriteFile(GlCsvContent, TestCsvBuilder.Utf8NoBom, extension);

        try
        {
            await host.DispatchAsync("project.create", CreatePayload);

            var import = await host.DispatchAsync(
                "import.gl.fromFile",
                JsonSerializer.Serialize(new { filePath = csvPath }));
            Assert.Equal(4, import.GetProperty("rowCount").GetInt32());
            Assert.Equal(7, import.GetProperty("columns").GetArrayLength());
            Assert.Equal("日期", import.GetProperty("columns")[0].GetString());

            var commit = await host.DispatchAsync("mapping.commit.gl", GlCommitPayload);
            Assert.Equal(4, commit.GetProperty("projectedRowCount").GetInt32());

            // 投影結果（值＋身分）：D900 的民國日期 114/01/02 須標準化為 2025-01-02，
            // 引號千分位金額 "1,250.00" 須以 scale 10000 轉為 12500000。
            var table = await host.DispatchAsync(
                "dev.db.tableData",
                """{ "tableName": "target_gl_entry", "limit": 50 }""");

            var columns = table.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
            var docIndex = columns.IndexOf("document_number");
            var dateIndex = columns.IndexOf("post_date");
            var debitIndex = columns.IndexOf("debit_amount_scaled");

            var d900Rows = table.GetProperty("rows").EnumerateArray()
                .Where(r => r[docIndex].GetString() == "D900")
                .ToList();

            Assert.Equal(2, d900Rows.Count);
            Assert.All(d900Rows, r => Assert.Equal("2025-01-02", r[dateIndex].GetString()));
            Assert.Contains(d900Rows, r => r[debitIndex].GetString() == "12500000");
        }
        finally
        {
            TestCsvBuilder.Delete(csvPath);
        }
    }

    [Fact]
    public async Task Import_SheetName_SelectsNamedWorksheet()
    {
        using var host = new HandlerTestHost();

        // Q1 是第一個工作表；request 指定 Q2 必須讀到 Q2 的標頭與列數
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Name = "Q1";
            ws.Cell(1, 1).Value = "A";
            ws.Cell(2, 1).Value = "q1";

            var q2 = ws.Workbook.Worksheets.Add("Q2");
            q2.Cell(1, 1).Value = "B";
            q2.Cell(2, 1).Value = "q2";
            q2.Cell(3, 1).Value = "q2b";
        });

        try
        {
            await host.DispatchAsync("project.create", CreatePayload);

            var import = await host.DispatchAsync(
                "import.gl.fromFile",
                JsonSerializer.Serialize(new { filePath = path, sheetName = "Q2" }));

            Assert.Equal(2, import.GetProperty("rowCount").GetInt32());
            Assert.Equal("B", import.GetProperty("columns")[0].GetString());
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task Import_SheetNameNotFound_ThrowsSheetNotFound()
    {
        using var host = new HandlerTestHost();

        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "A";
        });

        try
        {
            await host.DispatchAsync("project.create", CreatePayload);

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync(
                    "import.gl.fromFile",
                    JsonSerializer.Serialize(new { filePath = path, sheetName = "Q9" })));

            Assert.Equal(JetErrorCodes.SheetNotFound, ex.Code);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    /// <summary>單欄最小來源檔（payload 選項驗證用）；兩種副檔名都以 TestWorkbookBuilder.Delete 清理。</summary>
    private static string WriteMinimalSource(string extension)
    {
        return extension == ".csv"
            ? TestCsvBuilder.WriteFile("a,b\n1,2\n", TestCsvBuilder.Utf8NoBom)
            : TestWorkbookBuilder.WriteWorkbook(ws =>
            {
                ws.Cell(1, 1).Value = "A";
                ws.Cell(2, 1).Value = "1";
            });
    }

    // 決策表：可選欄位 × 副檔名適用性（manifest import.*.fromFile 細節），每列一個行為
    [Theory]
    [InlineData(".csv", "sheetName", "Q1")]     // sheetName 僅適用 .xlsx
    [InlineData(".csv", "encoding", "latin-1")] // encoding 白名單（utf-8/big5/utf-16）之外
    [InlineData(".xlsx", "encoding", "utf-8")]  // encoding 不適用 .xlsx
    [InlineData(".csv", "delimiter", "::")]     // delimiter 必須是白名單單字元
    [InlineData(".xlsx", "delimiter", ",")]     // delimiter 不適用 .xlsx
    public async Task Import_InvalidOptionField_ThrowsInvalidPayload(string extension, string field, string value)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var path = WriteMinimalSource(extension);
        try
        {
            var payload = JsonSerializer.Serialize(
                new Dictionary<string, string> { ["filePath"] = path, [field] = value });

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("import.gl.fromFile", payload));

            Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }
}
