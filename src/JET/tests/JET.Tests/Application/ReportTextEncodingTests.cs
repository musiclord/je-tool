using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;
using static JET.Tests.Application.SupportCaseHistoryTests;

namespace JET.Tests.Application;

/// <summary>
/// 使用者 2026-10-21 回報驗證報告匯出在寫入 XML 時失敗。來源文字檔帶進 XML 不允許的控制字元時，ValidationReport 與
/// INFReport 仍要產生，借貸不平照樣保留，報告讀回的文字與來源一字不差；Excel 的跳脫格式本身、Tab 與換行也不能被改掉。
/// 寫入仍失敗時，支援日誌要指出工作表、儲存格與字元的碼位。JET 讀 Excel 檔時依同一規則還原，審計員在 Excel 看到什麼就匯入什麼。
/// 文字全部是合成資料。
/// </summary>
public sealed class ReportTextEncodingTests
{
    // 控制字元、看起來像 Excel 跳脫格式的原文，以及引號內的換行。
    private const string Description = "合成\u0001摘要\u000B第二段_x0041_\t定位\r\n下一行";
    private const string AccountName = "合成\u001F資產￿";
    private const string Preparer = "合成\u0008人員";
    // 只在總帳出現的科目：科目配對工作檔的名稱欄取總帳名稱，試算表也有的科目則取試算表名稱。
    private const string GlOnlyAccountName = "合成\u001F費用￿";
    // 欄名本身像跳脫格式，報告裡記下的欄位配對要能原樣讀回。
    private const string DescriptionColumn = "摘要_x0041_";

    // Excel 存檔時寫出的樣子，逐字依 Excel 的規則推得。
    private const string EscapedDescription = "合成_x0001_摘要_x000B_第二段_x005F_x0041_\t定位_x000D_\n下一行";
    private const string EscapedAccountName = "合成_x001F_資產_xFFFF_";
    private const string EscapedDescriptionColumn = "摘要_x005F_x0041_";

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ControlCharactersInSourceText_BothValidationReportsAreProducedAndReadBackUnchanged(string provider)
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var id = await CreateCaseFromTextFileAsync(host, provider);
        var validation = await host.DispatchAsync("validate.run");
        Assert.Equal(1, validation.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        // 預設金額倍率 10,000：借方 10、貸方 9，差額 1。
        var unbalanced = new UnbalancedDocument("V1", 100_000, 90_000, 10_000);
        Assert.Equal(unbalanced, await UnbalancedAsync(host, provider, id));

        var exported = await ExportReportsAsync(host, id, runId);
        var artifacts = exported.GetProperty("artifacts").EnumerateArray()
            .ToDictionary(item => item.GetProperty("kind").GetString()!, item => item.GetProperty("fullPath").GetString()!);
        Assert.Equal(["infReport", "validationReport"], artifacts.Keys.Order(StringComparer.Ordinal));
        foreach (var path in artifacts.Values)
        {
            var texts = ReadTexts(path);
            Assert.Contains(Description, texts);
            Assert.Contains(AccountName, texts);
            Assert.Contains(Preparer, texts);
            Assert.Contains(DescriptionColumn, texts);
        }

        // 匯出不改來源資料：借貸不平的傳票仍在，借貸合計與差額不變，報告也列出這張傳票的兩列原始金額。
        Assert.Equal(unbalanced, await UnbalancedAsync(host, provider, id));
        var unbalancedRows = SheetRows(artifacts["validationReport"], "V_Report 6");
        Assert.Equal(["10", "9"], unbalancedRows.Skip(1).Select(row => row[7]));
        Assert.All(unbalancedRows.Skip(1), row => Assert.Equal("V1", row[0]));

        var restored = await host.DispatchAsync("mapping.restoreDraft", Json(new { filePath = artifacts["validationReport"] }));
        Assert.Equal(DescriptionColumn, restored.GetProperty("gl").GetProperty("mapping").GetProperty("description").GetString());
    }

    /// <summary>審計員在 Excel 存出的總帳：控制字元已是 _xHHHH_ 寫法，JET 匯入後是 Excel 顯示的原文。</summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ExcelSourceSavedWithEscapedText_ImportsWhatExcelShows(string provider)
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        await host.DispatchAsync("project.create", Json(new
        {
            caseName = "Synthetic escaped source", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider,
        }));
        var path = Path.Combine(host.ProjectsRoot, "synthetic-escaped.xlsx");
        WriteEscapedSourceWorkbook(path);
        await host.DispatchAsync("import.gl.fromFile", Json(new { filePath = path, fileName = "synthetic-escaped.xlsx" }));

        var preview = await host.DispatchAsync("query.dataPreview", """{ "dataset": "pbcGl" }""");
        var columns = preview.GetProperty("columns").EnumerateArray().Select(column => column.GetString()).ToList();
        Assert.Contains(DescriptionColumn, columns);
        var rows = preview.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(AccountName, rows[0][columns.IndexOf("科目名稱")].GetString());
        Assert.Equal(Description, rows[0][columns.IndexOf(DescriptionColumn)].GetString());
        Assert.Equal("合成貸方", rows[1][columns.IndexOf(DescriptionColumn)].GetString());
    }

    /// <summary>JET 產生的科目配對工作檔，審計員用 Excel 填好分類後匯回，含控制字元的科目名稱和案件裡的一樣。</summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AccountMappingTemplateFilledInExcel_ImportsNamesWithControlCharactersUnchanged(string provider)
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        await CreateCaseFromTextFileAsync(host, provider);
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var export = await host.DispatchAsync("export.accountMappingTemplate", Json(new { runId }));
        var templatePath = export.GetProperty("filePath").GetString()!;
        var filledPath = Path.Combine(host.ProjectsRoot, "synthetic-filled.xlsx");

        // 審計員在 Excel 填好 C 欄另存；這裡用 ClosedXML 代替 Excel 存檔。
        using (var workbook = new XLWorkbook(templatePath))
        {
            var sheet = workbook.Worksheet("AccountMapping");
            for (var row = 4; row <= sheet.LastRowUsed()!.RowNumber(); row++)
            {
                sheet.Cell(row, 3).Value = AccountMappingCategories.Cash;
            }

            workbook.SaveAs(filledPath);
        }

        var imported = await host.DispatchAsync("import.accountMapping.fromFile", Json(new { filePath = filledPath, fileName = "synthetic-filled.xlsx" }));
        Assert.Equal(3, imported.GetProperty("rowCount").GetInt32());
        var preview = await host.DispatchAsync("query.dataPreview", """{ "dataset": "accountMappings" }""");
        var mapped = preview.GetProperty("rows").EnumerateArray()
            .Select(row => row.EnumerateArray().Select(cell => cell.GetString()).ToArray())
            .Single(row => row[0] == "5000");
        Assert.Equal(GlOnlyAccountName, mapped[1]);
        Assert.Equal(AccountMappingCategories.Cash, mapped[2]);
    }

    [Fact]
    public void Encode_WritesOnlyXmlCharactersThatExcelReadsBackAsTheOriginal()
    {
        // 每一組是原文與依 Excel 跳脫格式應寫出的文字，逐字依規則推得，不由被測程式產生。
        (string Original, string Written)[] cases =
        [
            ("合成", "合成"),
            ("\u0001\u000B\u001F", "_x0001__x000B__x001F_"),
            ("\t第一行\n第二行", "\t第一行\n第二行"),
            ("第一行\r\n第二行\r", "第一行_x000D_\n第二行_x000D_"),
            ("_x0041_", "_x005F_x0041_"),
            ("_x0041_x0042_", "_x005F_x0041_x005F_x0042_"),
            ("_x005F_", "_x005F_x005F_"),
            ("_x0041\u0001", "_x005F_x0041_x0001_"),
            ("_x0041\r", "_x005F_x0041_x000D_"),
            ("_X0041_", "_x005F_X0041_"),
            ("_x0041", "_x0041"),
            ("_x00G1_", "_x00G1_"),
            ("__x0041_", "__x005F_x0041_"),
            ("😀", "😀"),
            ("_x0041😀", "_x0041😀"),
            ("\uD800", "_xD800_"),
            ("\uDC00合成", "_xDC00_合成"),
            ("\uDE00\uD83D", "_xDE00__xD83D_"),
            ("￾￿", "_xFFFE__xFFFF_"),
        ];

        foreach (var (original, written) in cases)
        {
            Assert.Equal(written, ExcelText.Encode(original));
            AssertWritable(written);
            Assert.Equal(original, Decode(written));
            Assert.Equal(original, ExcelText.Decode(written));
        }
    }

    [Fact]
    public void Encode_RandomMixturesOfEscapeLikeTextAndControlCharacters_AlwaysReadBackAsTheOriginal()
    {
        var alphabet = new[] { '_', 'x', 'X', '0', '4', '1', 'F', 'A', '合', '\t', '\n', '\r', '\u0001', '\uD800', '\uDC00', '￿' };
        var random = new Random(20261021);
        for (var round = 0; round < 20_000; round++)
        {
            var original = new string(Enumerable.Range(0, random.Next(1, 16)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var written = ExcelText.Encode(original);
            AssertWritable(written);
            Assert.Equal(original, Decode(written));
            Assert.Equal(original, ExcelText.Decode(written));
        }
    }

    [Fact]
    public void Decode_LeavesTextThatIsNotAnEscapeUnchanged()
    {
        foreach (var text in new[] { "_x 041_", "_x+041_", "_x-041_", "_x004_", "_x0041", "_X0041_", "x0041_", "_x00G1_" })
        {
            Assert.Equal(text, ExcelText.Decode(text));
        }

        Assert.Equal("A_", ExcelText.Decode("_x0041__"));
        Assert.Equal("_x0041_", ExcelText.Decode("_x005F_x0041_"));
    }

    [Fact]
    public void RowWithTextThatBypassesTheEncoder_KeepsTheOriginalErrorAndRecordsSheetCellAndCodePoint()
    {
        using var stream = new MemoryStream();
        using var document = CreateWorkbook(stream, out var parts, out _);
        var error = Record.Exception(() =>
        {
            using var sheet = new ReportSheetWriter("合成二", parts[1]);
            sheet.WriteDataRow(5, [sheet.TextCell(5, 2, "合成"), RawTextCell("C5", "合成\u0001摘要")]);
        });

        // 結束標記不再蓋掉原本的字元錯誤。
        AssertLocation(Assert.IsType<ArgumentException>(error), "合成2", "C5", 0x0001);
    }

    [Fact]
    public void WorkpaperRowWithTextThatBypassesTheEncoder_KeepsTheOriginalErrorAndRecordsSheetCellAndCodePoint()
    {
        using var stream = new MemoryStream();
        using var document = CreateWorkbook(stream, out var parts, out var styles);
        var error = Record.Exception(() =>
        {
            using var sheet = WorkpaperWriter.SheetWriter.CreateContinuation(
                "合成1", parts[0], styles, mapCellStyle: null, CancellationToken.None, pageSetupRelationshipId: null);
            sheet.WriteRow(3, [sheet.TextCell(3, 1, "合成"), RawTextCell("B3", "合成￿")]);
        });

        AssertLocation(Assert.IsType<ArgumentException>(error), "合成1", "B3", 0xFFFF);
    }

    [Fact]
    public void StreamingTemplateRowWithTextThatBypassesTheEncoder_KeepsTheOriginalErrorAndRecordsSheetCellAndCodePoint()
    {
        using var stream = new MemoryStream();
        using var document = CreateWorkbook(stream, out var parts, out _);
        parts[1].Worksheet = new Worksheet(new SheetData(new Row(RawTextCell("A1", "固定列")) { RowIndex = 1 }));
        var error = Record.Exception(() =>
        {
            using var sheet = new DirectTemplateStreamingSheetWriter("合成2", parts[1], firstDynamicRow: 2);
            sheet.WriteDataRow(2, [RawTextCell("B2", "合成\u0008人員")]);
        });

        AssertLocation(Assert.IsType<ArgumentException>(error), "合成2", "B2", 0x0008);
    }

    [Fact]
    public void CancellationWhileWritingAWorkpaperSheet_IsNotRecordedAsAWorksheetFailure()
    {
        using var stream = new MemoryStream();
        using var document = CreateWorkbook(stream, out var parts, out var styles);
        parts[0].Worksheet = new Worksheet(new SheetData(new Row(RawTextCell("A1", "固定列")) { RowIndex = 1 }));
        parts[0].Worksheet.Save();
        using var cancellation = new CancellationTokenSource();
        var plan = new DirectTemplateWorksheetOverlayPlan(
            IsDynamicRow: _ => true,
            IsDynamicCell: (_, _) => true,
            UseSourcePostSheetDataElements: null,
            UseSourceDimension: false,
            PreserveTemplateExtent: false,
            UseSourceColumnsWhenTemplateMissing: false,
            UseSourceColumns: false,
            MergeSourceColumnWidths: false,
            UseSourceCellStyle: (_, _) => true);

        var error = Record.Exception(() =>
        {
            using var sheet = WorkpaperWriter.SheetWriter.OverlayTemplate("合成1", parts[0], plan, styles, mapCellStyle: null, cancellation.Token);
            cancellation.Cancel();
            sheet.WriteRow(2, [sheet.TextCell(2, 1, "合成")]);
        });

        Assert.True(error is OperationCanceledException, error?.ToString());
        Assert.Null(SupportFailureDiagnostics.Find(error));
    }

    [Fact]
    public void FixedTemplateCellWithTextThatBypassesTheEncoder_SavingRecordsSheetCellAndCodePoint()
    {
        using var stream = new MemoryStream();
        using var document = CreateWorkbook(stream, out var parts, out _);
        parts[0].Worksheet = new Worksheet(new SheetData(new Row(RawTextCell("B3", "合成￿")) { RowIndex = 3 }));

        var error = Record.Exception(() => ExcelText.SaveWorksheet(parts[0]));

        AssertLocation(Assert.IsType<ArgumentException>(error), "合成1", "B3", 0xFFFF);
    }

    [Fact]
    public void RewrittenTemplateRowWithTextThatBypassesTheEncoder_RecordsSheetCellAndCodePoint()
    {
        using var stream = new MemoryStream();
        using var document = CreateWorkbook(stream, out var parts, out _);
        parts[1].Worksheet = new Worksheet(
            new Columns(new Column { Min = 1, Max = 1, Style = 0 }),
            new SheetData(new Row { RowIndex = 1 }, new Row { RowIndex = 2 }));
        parts[1].Worksheet.Save();
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var plan = new DirectTemplateWorksheetRewritePlan(
            FirstDynamicRow: 2,
            DimensionReference: "A1:A2",
            PrototypeColumns: [1],
            MapColumnStyle: null,
            CreateDynamicRows: (_, _) =>
            [
                new XElement(spreadsheet + "row", new XAttribute("r", "2"),
                    new XElement(spreadsheet + "c", new XAttribute("r", "A2"), new XAttribute("t", "inlineStr"),
                        new XElement(spreadsheet + "is", new XElement(spreadsheet + "t", "合成\u0008人員")))),
            ]);

        var error = Record.Exception(() => DirectTemplateWorksheetRewriter.Rewrite(parts[1], plan, CancellationToken.None));

        AssertLocation(Assert.IsType<ArgumentException>(error), "合成2", "A2", 0x0008);
    }

    /// <summary>以文字檔匯入總帳，控制字元原樣進到案件；試算表另以活頁簿匯入，與總帳逐科目相符。</summary>
    private static async Task<string> CreateCaseFromTextFileAsync(HandlerTestHost host, string provider)
    {
        var created = await host.DispatchAsync("project.create", Json(new
        {
            caseName = "Synthetic report text", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider,
        }));
        var id = created.GetProperty("projectId").GetString()!;

        // V1 借方 10、貸方 9，借貸不平 1；V2 平衡，借方科目 5000 只在總帳出現。
        var gl = new StringBuilder()
            .Append($"傳票號碼,傳票日期,核准日期,科目代號,科目名稱,{DescriptionColumn},建立人員,金額,借方旗標\n")
            .Append($"V1,2025-06-01,2025-06-02,1000,{Quote(AccountName)},{Quote(Description)},{Quote(Preparer)},10,1\n")
            .Append("V1,2025-06-01,2025-06-02,4000,合成收入,合成貸方,合成人員,9,0\n")
            .Append($"V2,2025-06-03,2025-06-04,5000,{Quote(GlOnlyAccountName)},合成費用,合成人員,5,1\n")
            .Append("V2,2025-06-03,2025-06-04,4000,合成收入,合成貸方,合成人員,5,0\n");
        var glPath = Path.Combine(host.ProjectsRoot, "synthetic-gl.csv");
        await File.WriteAllTextAsync(glPath, gl.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await host.DispatchAsync("import.gl.fromFile", Json(new { filePath = glPath, encoding = "utf-8", delimiter = "," }));

        var tbPath = new InlineTbWorkbookBuilder()
            .AddRow("1000", "合成資產", 10m)
            .AddRow("4000", "合成收入", -9m)
            .WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.tb.fromFile", Json(new { filePath = tbPath, fileName = "synthetic-tb.xlsx" }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(tbPath);
        }

        await host.DispatchAsync("mapping.commit.gl", Json(new
        {
            mapping = new Dictionary<string, string>
            {
                ["docNum"] = "傳票號碼", ["postDate"] = "傳票日期", ["docDate"] = "核准日期", ["accNum"] = "科目代號",
                ["accName"] = "科目名稱", ["description"] = DescriptionColumn, ["createBy"] = "建立人員", ["amount"] = "金額",
                ["dcField"] = "借方旗標", ["dcDebitCode"] = "1", ["dcCreditCode"] = "0",
            },
            amountMode = "flag",
            manualAutoPolicy = new { manualValues = new[] { "true", "1" }, automaticValues = new[] { "false", "0" }, blankValueKind = "reject" },
        }));
        await host.DispatchAsync("mapping.commit.tb", Json(new
        {
            mapping = InlineTbWorkbookBuilder.BuildDirectModeMapping(),
            changeMode = "direct",
        }));
        return id;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>直接寫出 Excel 存檔後的樣子：文字已是 _xHHHH_ 寫法，摘要放在共用字串，其餘放在儲存格內。</summary>
    private static void WriteEscapedSourceWorkbook(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var shared = workbookPart.AddNewPart<SharedStringTablePart>();
        shared.SharedStringTable = new SharedStringTable(new SharedStringItem(new Text(EscapedDescription) { Space = SpaceProcessingModeValues.Preserve }));
        string[] headers = ["傳票號碼", "傳票日期", "科目代號", "科目名稱", EscapedDescriptionColumn, "金額", "借方旗標"];
        var header = new Row { RowIndex = 1 };
        for (var index = 0; index < headers.Length; index++)
        {
            header.Append(RawTextCell(ReportSheetWriter.Reference((uint)index + 1, 1), headers[index]));
        }

        var first = new Row(
            RawTextCell("A2", "V1"), RawTextCell("B2", "2025-06-01"), RawTextCell("C2", "1000"), RawTextCell("D2", EscapedAccountName),
            new Cell { CellReference = "E2", DataType = CellValues.SharedString, CellValue = new CellValue("0") },
            NumberCell("F2", 10), NumberCell("G2", 1)) { RowIndex = 2 };
        var second = new Row(
            RawTextCell("A3", "V1"), RawTextCell("B3", "2025-06-01"), RawTextCell("C3", "4000"), RawTextCell("D3", "合成收入"),
            RawTextCell("E3", "合成貸方"), NumberCell("F3", 9), NumberCell("G3", 0)) { RowIndex = 3 };
        var part = workbookPart.AddNewPart<WorksheetPart>();
        part.Worksheet = new Worksheet(new SheetData(header, first, second));
        workbookPart.Workbook.AppendChild(new Sheets()).Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(part),
            SheetId = 1,
            Name = "Sheet1",
        });
    }

    private static Cell NumberCell(string reference, int value) => new()
    {
        CellReference = reference,
        DataType = CellValues.Number,
        CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture)),
    };

    private static async Task<UnbalancedDocument> UnbalancedAsync(HandlerTestHost host, string provider, string id)
    {
        ILocalProjectDatabase database = provider == "sqlite"
            ? new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot))
            : new DuckDbProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var page = await new LocalDocBalancePageRepository(database).GetPageAsync(
            id, ProjectDocument.DefaultMoneyScale, "2025-01-01", "2025-12-31", new PageRequest(null, 10), CancellationToken.None);
        Assert.Null(page.NextCursor);
        return Assert.Single(page.Rows);
    }

    /// <summary>匯出失敗時改以支援日誌記下的失敗欄位與例外說明失敗原因，測試輸出即是使用者匯出的日誌內容。</summary>
    private static async Task<JsonElement> ExportReportsAsync(HandlerTestHost host, string id, string runId)
    {
        try
        {
            return await host.DispatchAsync("export.validationArtifacts", Json(new { runId }), correlationId: "report-text");
        }
        catch (Exception)
        {
            Assert.Fail("報告匯出失敗，支援日誌記下：" + await DescribeFailureAsync(host, id, "report-text"));
            throw;
        }
    }

    private static async Task<string> DescribeFailureAsync(HandlerTestHost host, string id, string correlationId)
    {
        var log = await ExportAsync(host, id, correlationId);
        var error = Assert.Single(log.Entries, entry => entry.GetProperty("eventName").GetString() == "action.error");
        var fields = new[] { "error_code", "failure_stage", "report_kind", "sheet_name", "cell_reference", "invalid_codepoint" }
            .Select(name => $"{name}={Field(error, name) ?? "(none)"}");
        return string.Join(", ", fields) + "\n" + error.GetProperty("exception").GetString();
    }

    /// <summary>兩張工作表的記憶體活頁簿，附底稿用的樣式。不自動存檔，釋放時不會再寫一次含不合法字元的工作表。</summary>
    private static SpreadsheetDocument CreateWorkbook(Stream stream, out WorksheetPart[] parts, out WorkbookStyleMap styles)
    {
        var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: false);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = new Stylesheet();
        styles = WorkbookStyleMap.Append(stylesPart, WorkpaperStyles.Build());
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        parts = new WorksheetPart[2];
        for (var index = 0; index < parts.Length; index++)
        {
            parts[index] = workbookPart.AddNewPart<WorksheetPart>();
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(parts[index]),
                SheetId = (uint)index + 1,
                Name = $"合成{index + 1}",
            });
        }

        return document;
    }

    /// <summary>不經 ExcelText 直接寫入的文字儲存格，模擬日後新增卻漏掉編碼的寫入點，也用來寫出 Excel 存檔後的樣子。</summary>
    private static Cell RawTextCell(string reference, string text) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve }),
    };

    /// <summary>支援日誌寫入前同樣經過 SupportFailureFields 的白名單，這裡讀到的就是匯出檔裡的欄位。</summary>
    private static void AssertLocation(Exception error, string sheetName, string cellReference, int codePoint)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        SupportFailureFields.Add(fields, error);
        Assert.Equal("worksheet_write", fields["failure_stage"]);
        Assert.Equal(sheetName, fields["sheet_name"]);
        Assert.Equal(cellReference, fields["cell_reference"]);
        Assert.Equal(codePoint, fields["invalid_codepoint"]);
    }

    private static void AssertWritable(string written)
    {
        XmlConvert.VerifyXmlChars(written);
        Assert.DoesNotContain('\r', written);
    }

    /// <summary>讀回活頁簿裡每個文字儲存格，依 Excel 的 _xHHHH_ 規則還原。</summary>
    internal static string[] ReadTexts(string path)
    {
        using var document = SpreadsheetDocument.Open(path, isEditable: false);
        var workbook = document.WorkbookPart!;
        return workbook.WorksheetParts
            .SelectMany(part => part.Worksheet.Descendants<Cell>())
            .Select(cell => CellText(workbook, cell))
            .Where(text => text is not null)
            .Select(text => text!)
            .ToArray();
    }

    /// <summary>讀回指定工作表每一列的文字，空儲存格補空字串，欄位依 A、B、C 的順序。</summary>
    private static List<string[]> SheetRows(string path, string sheetName)
    {
        using var document = SpreadsheetDocument.Open(path, isEditable: false);
        var workbook = document.WorkbookPart!;
        var sheet = workbook.Workbook.Sheets!.Elements<Sheet>().Single(item => item.Name?.Value == sheetName);
        var part = (WorksheetPart)workbook.GetPartById(sheet.Id!.Value!);
        return part.Worksheet.Descendants<Row>()
            .Select(row =>
            {
                var cells = row.Elements<Cell>().ToDictionary(cell => ColumnIndex(cell.CellReference!.Value!), cell => CellText(workbook, cell) ?? "");
                return Enumerable.Range(0, cells.Count == 0 ? 0 : cells.Keys.Max() + 1).Select(index => cells.GetValueOrDefault(index, "")).ToArray();
            })
            .ToList();
    }

    private static int ColumnIndex(string reference) =>
        reference.TakeWhile(char.IsAsciiLetter).Aggregate(0, (total, letter) => total * 26 + letter - 'A' + 1) - 1;

    private static string? CellText(WorkbookPart workbook, Cell cell)
    {
        var text = cell.DataType?.Value == CellValues.InlineString ? cell.InlineString?.InnerText
            : cell.DataType?.Value == CellValues.SharedString && int.TryParse(cell.CellValue?.Text, CultureInfo.InvariantCulture, out var index)
                ? workbook.SharedStringTablePart!.SharedStringTable.Elements<SharedStringItem>().ElementAt(index).InnerText
                : cell.CellValue?.Text;
        return text is null ? null : Decode(text);
    }

    /// <summary>Excel 讀取 _xHHHH_ 的方式，從左到右還原；與被測的 ExcelText.Decode 分開寫，避免用被測程式核對自己。</summary>
    internal static string Decode(string text) =>
        Regex.Replace(text, "_x([0-9A-Fa-f]{4})_", match => ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
}
