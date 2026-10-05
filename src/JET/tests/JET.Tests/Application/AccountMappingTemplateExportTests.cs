using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 科目配對範本產生（export.accountMappingTemplate）驗收：
/// (a) 母體＝GL∪TB（使用者關切點，必測）——repo 讀回 GL-only／TB-only／共有科目皆列、升冪、GL-only 寫真實 GL 名；
/// (b) 下拉存在——writer 讀回 C4 DataValidation 為 list 型五類別、inCellDropdown、A/B 已填 C 留空；
/// (c) round-trip——產範本→填 C 欄→import.accountMapping.fromFile→target_account_mapping 落地正確；
/// (d) handler 端到端——GL+TB 已 commit → 範本檔寫出、rowCount == 獨立 recount diff 基數；
/// (e) provider parity——SQLite 對 SQL Server 的範本列等價（[SqlServerFact]）。
///
/// oracle 一律獨立參數化 SQL recount（DemoProjectPipeline.QueryScalarAsync）或 ClosedXML 讀回，
/// 不做弱斷言。母體以 InlineWorkbookProject（自含小母體）或 DemoProjectPipeline（deterministic demo）建。
/// </summary>
public sealed class AccountMappingTemplateExportTests
{
    private const string CodeHeader = "GL_Number";
    private const string NameHeader = "GL_Name";
    private const string CategoryHeader = "Standardized Account Name*";

    private static string NewTempXlsxPath() =>
        Path.Combine(Path.GetTempPath(), $"jet-amtpl-{Guid.NewGuid():N}.xlsx");

    private static IAccountMappingExportRepository SqliteRepo(HandlerTestHost host) =>
        new LocalAccountMappingExportRepository(new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot)));

    /// <summary>GL-only「5101」、TB-only「2101」、共有「1101」的小母體：GL 建 1101/5101、TB 建 1101/2101。</summary>
    private static Task<string> SetupGlUnionTbProjectAsync(
        HandlerTestHost host,
        string databaseProvider = "sqlite") =>
        InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                // 共有科目 1101（GL 側名稱刻意與 TB 不同，證 diff 取 COALESCE(tb, gl) 的 TB 名）。
                .AddRow("JV-001", "2025-03-05", "1101", "現金_GL", "借方", "300.00", 1)
                // GL-only 科目 5101（TB 無）：範本 B 欄必須寫此真實 GL 名，而非「Not in TB」。
                .AddRow("JV-001", "2025-03-05", "5101", "薪資費用", "貸方", "300.00", 0),
            databaseProvider: databaseProvider,
            configureTb: tb => tb
                .AddRow("1101", "現金", 300)      // 共有
                .AddRow("2101", "應付帳款", 500)); // TB-only

    // ================= (a) 母體＝GL∪TB（使用者關切點）=================

    [Fact]
    public async Task FetchTemplateRowsAsync_ReturnsGlUnionTb_AscendingByCode_WithRealGlNameForGlOnly()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupGlUnionTbProjectAsync(host);

        var rows = await SqliteRepo(host).FetchTemplateRowsAsync(
            projectId, "2025-01-01", "2025-12-31", CancellationToken.None);

        // 三科目全列（GL-only 5101、TB-only 2101、共有 1101），依 account_code 升冪。
        Assert.Equal(new[] { "1101", "2101", "5101" }, rows.Select(r => r.AccountCode).ToArray());

        // GL-only 5101 的 B 欄寫真實 GL 名「薪資費用」（非「Not in TB」）——使用者靠名稱辨識科目才能分類。
        var glOnly = rows.Single(r => r.AccountCode == "5101");
        Assert.Equal("薪資費用", glOnly.AccountName);

        // 共有 1101 取 diff 的 COALESCE(tb, gl) → TB 名「現金」（證聯集鍵與名稱來源正確）。
        Assert.Equal("現金", rows.Single(r => r.AccountCode == "1101").AccountName);

        // 獨立 recount：diff 基數 == 3（GL∪TB distinct account_code）。
        var diffCount = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, ValidationProcedures.CompletenessDiffCte + "\nSELECT COUNT(*) FROM diff;",
            ("@periodStart", "2025-01-01"), ("@periodEnd", "2025-12-31"));
        Assert.Equal(3, diffCount);
        Assert.Equal(diffCount, rows.Count);
    }

    // ================= (b) writer：下拉存在 + A/B 已填 + C 留空 + 三標頭 =================

    [Fact]
    public async Task Writer_WritesThreeHeaders_FillsAB_LeavesCEmpty_WithFiveCategoryDropdown()
    {
        var rows = new List<AccountMappingTemplateRow>
        {
            new("1101", "現金"),
            new("5101", "薪資費用"),
            new("9999", null), // 名稱 null → B 欄寫空字串（不炸）
        };

        using var stream = new MemoryStream();
        await new AccountMappingTemplateWriter().WriteAsync(stream, rows, CancellationToken.None);

        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheet("AccountMapping");

        Assert.Equal("Account Mapping Table", ws.Cell("B1").GetString());
        Assert.Equal(
            "請在 C 欄選擇標準科目分類；允許分類僅限 Revenue、Receivables、Cash、Receipt in advance、Others；A、B 欄由系統產生，不應修改。",
            ws.Cell("A2").GetString());

        // 三標頭沿用內嵌 legacy 範本；逐一仍可被 AccountMappingColumnResolver keyword-match。
        Assert.Equal(CodeHeader, ws.Cell("A3").GetString());
        Assert.Equal(NameHeader, ws.Cell("B3").GetString());
        Assert.Equal(CategoryHeader, ws.Cell("C3").GetString());

        // A/B 已填、C 留空（供審計員填）。
        Assert.Equal("1101", ws.Cell("A4").GetString());
        Assert.Equal("現金", ws.Cell("B4").GetString());
        Assert.True(ws.Cell("C4").IsEmpty());
        Assert.Equal("9999", ws.Cell("A6").GetString());
        Assert.True(ws.Cell("B6").IsEmpty()); // null 名稱 → 空
        Assert.True(ws.Cell("C6").IsEmpty());

        // C4 的 DataValidation：list 型、inCellDropdown；List!A1 保留範本靜態標題，
        // current Domain 白名單五類必須完整落在 A2:A6（尤其 Revenue 不得被範圍排除）。
        var dv = ws.DataValidations.GetAllInRange(ws.Cell("C4").AsRange().RangeAddress).Single();
        Assert.Equal(XLAllowedValues.List, dv.AllowedValues);
        Assert.True(dv.InCellDropdown);
        Assert.Equal("List!$A$2:$A$6", dv.Value);
        var list = workbook.Worksheet("List");
        Assert.Equal("Accounts", list.Cell("A1").GetString());
        Assert.Equal(
            new[] { "Revenue", "Receivables", "Cash", "Receipt in advance", "Others" },
            list.Column(1).Cells(2, 6).Select(cell => cell.GetString()));
        Assert.True(list.Cell("A7").IsEmpty());
        Assert.True(list.Cell("A8").IsEmpty());

        // 下拉覆蓋最後一列 C6（整個 C4:C{n} 範圍，非只有第一列）。
        Assert.Single(ws.DataValidations.GetAllInRange(ws.Cell("C6").AsRange().RangeAddress));
        Assert.Empty(ws.DataValidations.GetAllInRange(ws.Cell("C7").AsRange().RangeAddress));
        using (var package = SpreadsheetDocument.Open(new MemoryStream(stream.ToArray()), false))
        {
            var workbookPart = Assert.IsType<WorkbookPart>(package.WorkbookPart);
            var sheets = Assert.IsType<Sheets>(workbookPart.Workbook.Sheets);
            var listSheet = sheets.Elements<Sheet>()
                .Single(item => item.Name?.Value == "List");
            var listPart = Assert.IsType<WorksheetPart>(
                workbookPart.GetPartById(listSheet.Id!.Value!));
            var pageSetup = Assert.Single(listPart.Worksheet.Elements<PageSetup>());
            Assert.False(string.IsNullOrWhiteSpace(pageSetup.Id?.Value));
            Assert.Contains(
                "printerSettings",
                listPart.GetPartById(pageSetup.Id!.Value!).Uri.OriginalString,
                StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(ws.Protection.IsProtected);
        Assert.True(ws.Cell("A4").Style.Protection.Locked);
        Assert.False(ws.Cell("C4").Style.Protection.Locked);
        Assert.True(ws.Cell("A6").Style.Protection.Locked);
        Assert.False(ws.Cell("C6").Style.Protection.Locked);
        Assert.True(ws.Cell("C7").Style.Protection.Locked);
    }

    [Fact]
    public async Task Writer_ProjectTaxonomy_RoundTripsCustomLabelsIntoDynamicDropdown()
    {
        AccountTaxonomyCategory[] categories =
        [
            .. AccountTaxonomyBuiltIns.All,
            new AccountTaxonomyCategory(
                "custom.0123456789abcdef0123456789abcdef",
                "Contract assets",
                5,
                AccountTaxonomyBuiltIns.ReceivablesRole,
                false)
        ];
        using var stream = new MemoryStream();
        var writer = Assert.IsAssignableFrom<IAccountMappingTaxonomyTemplateWriter>(
            new AccountMappingTemplateWriter());

        await writer.WriteAsync(
            stream,
            [new AccountMappingTemplateRow("1101", "現金")],
            categories,
            CancellationToken.None);

        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        Assert.Equal(
            categories.Select(item => item.Label),
            workbook.Worksheet("List").Column(1).Cells(2, 7).Select(cell => cell.GetString()));
        var validation = workbook.Worksheet("AccountMapping").DataValidations
            .GetAllInRange(workbook.Worksheet("AccountMapping").Cell("C4").AsRange().RangeAddress)
            .Single();
        Assert.Equal("List!$A$2:$A$7", validation.Value);
    }

    [Fact]
    public async Task ProjectTaxonomy_TemplateImportRoundTrip_PersistsCustomIdAndBlankAsOthers()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupGlUnionTbProjectAsync(host);
        var savePayload = JsonSerializer.Serialize(new
        {
            revision = 1,
            categories = AccountTaxonomyBuiltIns.All.Select(item => new
            {
                categoryId = (string?)item.CategoryId,
                label = item.Label,
                ordinal = item.Ordinal,
                semanticRole = item.SemanticRole
            }).Append(new
            {
                categoryId = (string?)null,
                label = "Contract assets",
                ordinal = 5,
                semanticRole = AccountTaxonomyBuiltIns.ReceivablesRole
            })
        });
        var saved = await host.DispatchAsync("accountTaxonomy.save", savePayload);
        var custom = saved.GetProperty("categories").EnumerateArray()
            .Single(item => !item.GetProperty("isBuiltIn").GetBoolean());
        var customId = custom.GetProperty("categoryId").GetString()!;
        var categories = saved.GetProperty("categories").EnumerateArray()
            .Select(item => new AccountTaxonomyCategory(
                item.GetProperty("categoryId").GetString()!,
                item.GetProperty("label").GetString()!,
                item.GetProperty("ordinal").GetInt32(),
                item.GetProperty("semanticRole").GetString()!,
                item.GetProperty("isBuiltIn").GetBoolean()))
            .ToArray();
        var rows = await SqliteRepo(host).FetchTemplateRowsAsync(
            projectId, "2025-01-01", "2025-12-31", CancellationToken.None);
        var path = NewTempXlsxPath();
        try
        {
            await using (var output = File.Create(path))
            {
                await ((IAccountMappingTaxonomyTemplateWriter)new AccountMappingTemplateWriter())
                    .WriteAsync(output, rows, categories, CancellationToken.None);
            }
            using (var workbook = new XLWorkbook(path))
            {
                var sheet = workbook.Worksheet("AccountMapping");
                sheet.Cell("C4").Value = "Contract assets";
                sheet.Cell("C5").Value = string.Empty;
                sheet.Cell("C6").Value = AccountMappingCategories.Revenue;
                workbook.Save();
            }

            await host.DispatchAsync(
                "import.accountMapping.fromFile",
                JsonSerializer.Serialize(new { filePath = path, fileName = "taxonomy-roundtrip.xlsx" }));

            Assert.Equal(
                1,
                await DemoProjectPipeline.QueryScalarAsync(
                    host,
                    projectId,
                    "SELECT COUNT(*) FROM target_account_mapping WHERE category_id = @categoryId;",
                    ("@categoryId", customId)));
            Assert.Equal(
                1,
                await DemoProjectPipeline.QueryScalarAsync(
                    host,
                    projectId,
                    "SELECT COUNT(*) FROM target_account_mapping WHERE category_id = @categoryId;",
                    ("@categoryId", AccountTaxonomyBuiltIns.OthersId)));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Writer_AutoFitsFromLateRowsWithoutShrinkingTemplateAndKeepsCellsUnwrapped()
    {
        const int rowCount = 17;
        var longCode = new string('9', 40);
        var longName = new string('科', 40);
        var rows = Enumerable.Range(0, rowCount)
            .Select(index => new AccountMappingTemplateRow(
                index == rowCount - 1 ? longCode : $"A-{index + 1}",
                index == rowCount - 1 ? longName : $"科目-{index + 1}"))
            .ToArray();
        using var stream = new MemoryStream();

        await new AccountMappingTemplateWriter().WriteAsync(
            stream,
            rows,
            CancellationToken.None);

        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet("AccountMapping");

        Assert.InRange(sheet.Column(1).Width, 46.5, 255);
        Assert.InRange(sheet.Column(2).Width, 92.5, 255);
        Assert.True(sheet.Column(3).Width >= 72.5D);
        foreach (var cell in new[]
                 {
                     sheet.Cell("A3"),
                     sheet.Cell("B3"),
                     sheet.Cell("C3"),
                     sheet.Cell("A4"),
                     sheet.Cell("B20"),
                     sheet.Cell("C20")
                 })
        {
            Assert.False(cell.Style.Alignment.WrapText);
            Assert.Equal(0, cell.Style.Alignment.Indent);
            Assert.False(cell.Style.Alignment.ShrinkToFit);
        }

        using var document = SpreadsheetDocument.Open(
            new MemoryStream(stream.ToArray(), writable: false),
            false);
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var mappingSheet = workbookPart.Workbook.Sheets!.Elements<Sheet>()
            .Single(item => item.Name?.Value == "AccountMapping");
        var worksheet = Assert.IsType<WorksheetPart>(
            workbookPart.GetPartById(mappingSheet.Id!.Value!)).Worksheet;
        var columns = worksheet.GetFirstChild<Columns>();
        var missingAutoFitColumns = Enumerable.Range(1, 3)
            .Where(index =>
            {
                var column = columns?.Elements<Column>().SingleOrDefault(item =>
                    item.Min?.Value <= (uint)index
                    && item.Max?.Value >= (uint)index);
                return column is null
                    || column.Width is null
                    || column.BestFit?.Value != true
                    || column.CustomWidth?.Value != true;
            })
            .ToArray();
        Assert.True(
            missingAutoFitColumns.Length == 0,
            "Stage 8 AccountMapping consistency diff: AccountMapping!columns A-C "
            + "must retain width and carry BestFit/CustomWidth; missing columns "
            + string.Join(",", missingAutoFitColumns));
    }

    [Fact]
    public async Task Writer_WritesProvenLegacyLayoutAtRowsThreeAndFour()
    {
        using var stream = new MemoryStream();
        await new AccountMappingTemplateWriter().WriteAsync(
            stream,
            [new AccountMappingTemplateRow("1101", "現金")],
            CancellationToken.None);

        stream.Position = 0;
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>()
            .Single(item => item.Name?.Value == "AccountMapping");
        var worksheet = Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet;

        Assert.Equal(
            new[] { "B1:C1", "A2:C2" },
            worksheet.Descendants<MergeCell>().Select(item => item.Reference!.Value).ToArray());
        var rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToDictionary(row => row.RowIndex!.Value);
        Assert.Equal(52.7, rows[1].Height?.Value);
        Assert.Equal(45.6, rows[2].Height?.Value);
        Assert.Equal(18, rows[3].Height?.Value);
        Assert.Equal(13.5, rows[4].Height?.Value);
        var columns = worksheet.GetFirstChild<Columns>()!.Elements<Column>().ToArray();
        Assert.Equal(21.85546875, columns.Single(column => column.Min?.Value == 1).Width!.Value, 6);
        Assert.Equal(36.85546875, columns.Single(column => column.Min?.Value == 2).Width!.Value, 6);
        Assert.Equal(74.28515625, columns.Single(column => column.Min?.Value == 3).Width!.Value, 6);
        Assert.Null(worksheet.Descendants<SheetView>().Single().ZoomScale);
        Assert.Equal(OrientationValues.Portrait, worksheet.GetFirstChild<PageSetup>()?.Orientation?.Value);
        Assert.NotNull(worksheet.GetFirstChild<PageMargins>());
        Assert.Empty(new OpenXmlValidator().Validate(document));
    }

    [Fact]
    public async Task Writer_TooManyRows_RejectsBeforeCreatingPartialWorkbook()
    {
        Assert.Equal(1_048_573, AccountMappingTemplateWriter.MaxDataRowsPerWorkbook);
        using var stream = new MemoryStream();
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new AccountMappingTemplateWriter().WriteAsync(
                stream,
                new OversizedAccountMappingRows(),
                CancellationToken.None));

        Assert.Contains(AccountMappingTemplateWriter.MaxDataRowsPerWorkbook.ToString("N0"), exception.Message);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task Writer_ExactMaximumRowCount_EntersWritePathInsteadOfRejecting()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AccountMappingTemplateWriter().WriteAsync(
                stream,
                new MaximumRowsCancelingOnFirstAccess(cancellation),
                cancellation.Token));

        Assert.True(stream.Length > 0, "合法最大列數應先進入 template copy／write path，再由測試 token 取消。");
    }

    // ================= (c) round-trip：產範本→填 C→匯入→target_account_mapping 落地 =================

    [Fact]
    public async Task RoundTrip_FilledTemplate_ImportsBackIntoTargetAccountMapping()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupGlUnionTbProjectAsync(host);

        // 產範本到確定性落點。
        string? templatePath = null;
        var filledPath = NewTempXlsxPath();
        try
        {
            var validate = await host.DispatchAsync("validate.run");
            var runId = validate.GetProperty("resultRef").GetProperty("runId").GetString();
            var export = await host.DispatchAsync("export.accountMappingTemplate",
                JsonSerializer.Serialize(new { runId }));
            Assert.True(export.GetProperty("ok").GetBoolean());
            Assert.Equal(3, export.GetProperty("rowCount").GetInt32());
            templatePath = export.GetProperty("filePath").GetString()!;
            Assert.Equal(
                Path.Combine(host.ProjectsRoot, projectId),
                Path.GetDirectoryName(templatePath),
                StringComparer.OrdinalIgnoreCase);

            // 審計員填 C 欄（1101→Cash、2101→Receivables、5101→Revenue），原檔另存後上傳。
            var fill = new Dictionary<string, string>
            {
                ["1101"] = AccountMappingCategories.Cash,
                ["2101"] = AccountMappingCategories.Receivables,
                ["5101"] = AccountMappingCategories.Revenue,
            };
            using (var wb = new XLWorkbook(templatePath))
            {
                var ws = wb.Worksheet("AccountMapping");
                var last = ws.LastRowUsed()!.RowNumber();
                for (var row = 4; row <= last; row++)
                {
                    ws.Cell(row, 3).Value = fill[ws.Cell(row, 1).GetString()];
                }

                wb.SaveAs(filledPath);
            }

            // 原檔上傳（三欄標頭可被 resolver 讀回即證 round-trip）。
            var import = await host.DispatchAsync("import.accountMapping.fromFile",
                JsonSerializer.Serialize(new { filePath = filledPath, fileName = "filled.xlsx" }));
            Assert.Equal(3, import.GetProperty("rowCount").GetInt32());

            // target_account_mapping 落地正確：逐科目 code=category（獨立讀回，證分類白名單投影成功）。
            var landed = await DemoProjectPipeline.QueryStringListAsync(
                host, projectId,
                "SELECT account_code || '=' || standardized_category FROM target_account_mapping ORDER BY account_code;");
            Assert.Equal(
                new[] { "1101=Cash", "2101=Receivables", "5101=Revenue" },
                landed.ToArray());
        }
        finally
        {
            if (templatePath is not null && File.Exists(templatePath)) { File.Delete(templatePath); }
            if (File.Exists(filledPath)) { File.Delete(filledPath); }
        }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task RoundTrip_UnchangedTemplate_ImportsWithoutInventingMappings(string databaseProvider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupGlUnionTbProjectAsync(host, databaseProvider);
        var validate = await host.DispatchAsync("validate.run");
        var runId = validate.GetProperty("resultRef").GetProperty("runId").GetString();
        var export = await host.DispatchAsync(
            "export.accountMappingTemplate",
            JsonSerializer.Serialize(new { runId }));
        var templatePath = export.GetProperty("filePath").GetString()!;
        var fileName = Path.GetFileName(templatePath);

        Assert.Equal(".xlsx", Path.GetExtension(fileName));

        var import = await host.DispatchAsync(
            "import.accountMapping.fromFile",
            JsonSerializer.Serialize(new { filePath = templatePath, fileName }));

        Assert.Equal(3, import.GetProperty("rowCount").GetInt32());
        var preview = await host.DispatchAsync(
            "query.dataPreview",
            JsonSerializer.Serialize(new { dataset = "accountMappings" }));
        Assert.Equal(3, preview.GetProperty("totalCount").GetInt64());
        Assert.Equal(
            new[] { "accountCode", "accountName", "standardizedCategory" },
            preview.GetProperty("columns").EnumerateArray().Select(column => column.GetString()).ToArray());
        var firstPreviewRow = preview.GetProperty("rows")[0];
        Assert.Equal("1101", firstPreviewRow[0].GetString());
        Assert.Equal("現金", firstPreviewRow[1].GetString());
        Assert.Equal(JsonValueKind.Null, firstPreviewRow[2].ValueKind);

        var loaded = await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(
            3,
            loaded.GetProperty("importState").GetProperty("accountMapping").GetProperty("rowCount").GetInt32());

    }

    // ================= (d) handler 端到端：寫檔 + rowCount == 獨立 recount diff =================

    [Fact]
    public async Task ExportAccountMappingTemplate_Demo_WritesFile_RowCountMatchesDiff()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);

        var validate = await host.DispatchAsync("validate.run");
        var runId = validate.GetProperty("resultRef").GetProperty("runId").GetString();
        var data = await host.DispatchAsync("export.accountMappingTemplate",
            JsonSerializer.Serialize(new { runId }));
        var outputPath = data.GetProperty("filePath").GetString()!;
        Assert.Equal(
            Path.Combine(host.ProjectsRoot, ctx.ProjectId),
            Path.GetDirectoryName(outputPath),
            StringComparer.OrdinalIgnoreCase);

        Assert.True(data.GetProperty("ok").GetBoolean());
        Assert.False(data.TryGetProperty("outputPath", out _));
        Assert.True(File.Exists(outputPath), "handler 應把報告寫到目前專案目錄");

            // rowCount == 獨立 recount 的 GL∪TB diff 基數（demo 母體本期）。
            var diffCount = await DemoProjectPipeline.QueryScalarAsync(
                host, ctx.ProjectId, ValidationProcedures.CompletenessDiffCte + "\nSELECT COUNT(*) FROM diff;",
                ("@periodStart", "2025-01-01"), ("@periodEnd", "2025-12-31"));
            Assert.True(diffCount > 0, "demo 應有科目母體");
            Assert.Equal(diffCount, data.GetProperty("rowCount").GetInt64());

            // 檔案讀回：資料列數（扣標頭）== diffCount、C 欄全空。
        using var wb = new XLWorkbook(outputPath);
        var ws = wb.Worksheet("AccountMapping");
        var dataRows = ws.LastRowUsed()!.RowNumber() - 3;
        Assert.Equal(diffCount, dataRows);
        for (var row = 4; row <= ws.LastRowUsed()!.RowNumber(); row++)
        {
            Assert.True(ws.Cell(row, 3).IsEmpty(), $"C{row} 應留空供審計員填");
        }
    }

    [Fact]
    public async Task ExportAccountMappingTemplate_WithoutGlMapping_RequiresMapping()
    {
        // 9/23：配對工作檔不讀驗證結果，但仍需要已確認的 GL 欄位用途。
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "EMPTY-2025-001",
            entityName = "空母體案件",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
        }));

        var ex = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync("export.accountMappingTemplate", JsonSerializer.Serialize(new { runId = new string('a', 32) })));
        Assert.Equal(JetErrorCodes.StaleResult, ex.Code);
        // 2026-10-04 第 8 批 Q8：已確認配對的狀態說法統一，仍要求缺 GL 配對時明確拒絕。
        // 第一次失敗：20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d。
        Assert.Contains("正式報表需要目前已確認配對的 GL 欄位", ex.Message);
        Assert.DoesNotContain("驗證", ex.Message);
    }

    // ================= (e) provider parity：SQLite 對 SQL Server 範本列等價 =================

    [SqlServerFact]
    public async Task AccountMappingTemplate_SqlServerDemo_ReadsBackAndImportsUnchanged()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return;
        }

        // SQLite oracle：範本的 (code, name) 列序。
        using var sqliteHost = new HandlerTestHost();
        var sqliteProject = await DemoProjectPipeline.SetupAsync(sqliteHost);
        var sqliteValidate = await sqliteHost.DispatchAsync("validate.run");
        var sqliteExport = await sqliteHost.DispatchAsync("export.accountMappingTemplate",
            JsonSerializer.Serialize(new
            {
                runId = sqliteValidate.GetProperty("resultRef").GetProperty("runId").GetString()
            }));
        var sqliteOut = sqliteExport.GetProperty("filePath").GetString()!;
        List<(string Code, string Name)> sqliteRows;
        sqliteRows = ReadTemplateRows(sqliteOut);

        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            var sqlProject = await DemoProjectPipeline.SetupAsync(sqlServerHost, databaseProvider: "sqlServer");
            var sqlValidate = await sqlServerHost.DispatchAsync("validate.run");
            var sqlExport = await sqlServerHost.DispatchAsync("export.accountMappingTemplate",
                JsonSerializer.Serialize(new
                {
                    runId = sqlValidate.GetProperty("resultRef").GetProperty("runId").GetString()
                }));
            var sqlServerOut = sqlExport.GetProperty("filePath").GetString()!;
            var sqlServerRows = ReadTemplateRows(sqlServerOut);

            // provider 等價：範本列（code + name）逐項相同。
            Assert.Equal(sqliteRows, sqlServerRows);

            // SQL Server 也必須接受自己產生的全空白 C 欄範本；blank 依 taxonomy 契約投影為 Others。
            var imported = await sqlServerHost.DispatchAsync(
                "import.accountMapping.fromFile",
                JsonSerializer.Serialize(new
                {
                    filePath = sqlServerOut,
                    fileName = Path.GetFileName(sqlServerOut)
            }));
            Assert.Equal(sqlServerRows.Count, imported.GetProperty("rowCount").GetInt32());
            var preview = await sqlServerHost.DispatchAsync(
                "query.dataPreview",
                JsonSerializer.Serialize(new { dataset = "accountMappings" }));
            Assert.Equal(sqlServerRows.Count, preview.GetProperty("totalCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, preview.GetProperty("rows")[0][2].ValueKind);
            var sqlServerRepository = new SqlServerAccountMappingExportRepository(
                new SqlServerProjectDatabase(new SqlServerConnectionOptions(connectionString)));
            var exportedRows = await sqlServerRepository.FetchAllAsync(
                sqlProject.ProjectId,
                "2025-01-01",
                "2025-12-31",
                CancellationToken.None);
            Assert.Equal(sqlServerRows.Count, exportedRows.Count);
            Assert.All(exportedRows, row => Assert.Equal("Others", row.Category));
        }
        finally
        {
            await DropSqlServerProjectsAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    private static List<(string Code, string Name)> ReadTemplateRows(string path)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("AccountMapping");
        var last = ws.LastRowUsed()!.RowNumber();
        var rows = new List<(string, string)>();
        for (var row = 4; row <= last; row++)
        {
            rows.Add((ws.Cell(row, 1).GetString(), ws.Cell(row, 2).GetString()));
        }

        return rows;
    }

    private static async Task DropSqlServerProjectsAsync(string projectsRoot, string connectionString)
    {
        if (!Directory.Exists(projectsRoot))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(projectsRoot))
        {
            await TempSqlServerProject.DropDatabaseAsync(connectionString, Path.GetFileName(dir));
        }
    }

    private sealed class OversizedAccountMappingRows : IReadOnlyList<AccountMappingTemplateRow>
    {
        public int Count => 1_048_574;

        public AccountMappingTemplateRow this[int index] => throw new IndexOutOfRangeException();

        public IEnumerator<AccountMappingTemplateRow> GetEnumerator() =>
            Enumerable.Empty<AccountMappingTemplateRow>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class MaximumRowsCancelingOnFirstAccess(CancellationTokenSource cancellation)
        : IReadOnlyList<AccountMappingTemplateRow>
    {
        public int Count => 1_048_573;

        public AccountMappingTemplateRow this[int index]
        {
            get
            {
                cancellation.Cancel();
                return new AccountMappingTemplateRow(index.ToString(), null);
            }
        }

        public IEnumerator<AccountMappingTemplateRow> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
