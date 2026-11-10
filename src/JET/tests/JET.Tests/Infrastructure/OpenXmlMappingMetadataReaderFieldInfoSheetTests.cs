using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 裁定 2026-11-09-28：檔案沒有 JET 記下的隱藏配對時，讀「自動化工具-檔案欄位資訊」看得見的 A 到 E 欄。
/// 舊版 IDEA JET 與新版 JET 的版面都用合成活頁簿檢查；配對怎麼推得另見 FieldInfoSheetMappingTests。
/// </summary>
public sealed class OpenXmlMappingMetadataReaderFieldInfoSheetTests
{
    [Fact]
    public async Task ReadAsync_LegacyLayout_ReadsBothSectionsByTheirTitles()
    {
        var path = FieldInfoSheetWorkbook.Write(FieldInfoSheetWorkbook.LegacyLayout(
            [
                ["Acct", "數字型態", null, "0"],
                ["Acct", "文字型態", "10", null, "會計科目編號_TB"],
                ["Acct Name", "文字型態", "20", null, "會計科目名稱_TB"],
                ["試算表變動金額_TB 由系統產生 : End - Begin", "數字型態", null, "2", "試算表變動金額_TB"]
            ],
            [
                ["Doc No", "數字型態", null, "0"],
                ["Doc No", "文字型態", "12", null, "傳票號碼_JE"],
                ["Post_x0020_Date", "日期型態", null, null, "總帳日期_JE"],
                ["  Memo  ", "文字型態", "40", null, "傳票摘要_JE"],
                ["Amount", "數字型態", null, "2"],
                ["傳票金額_JE 由系統產生 : Amount", "數字型態", null, "2", "傳票金額_JE"]
            ]));
        try
        {
            var source = await new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None);

            Assert.Null(source.Recorded);
            Assert.NotNull(source.FieldInfoSheet);
            Assert.Equal(
                [
                    new FieldInfoSheetRow("Acct", "數字型態", null),
                    new FieldInfoSheetRow("Acct", "文字型態", "會計科目編號_TB"),
                    new FieldInfoSheetRow("Acct Name", "文字型態", "會計科目名稱_TB"),
                    new FieldInfoSheetRow("試算表變動金額_TB 由系統產生 : End - Begin", "數字型態", "試算表變動金額_TB")
                ],
                source.FieldInfoSheet.Tb);
            Assert.Equal(
                [
                    new FieldInfoSheetRow("Doc No", "數字型態", null),
                    new FieldInfoSheetRow("Doc No", "文字型態", "傳票號碼_JE"),
                    new FieldInfoSheetRow("Post Date", "日期型態", "總帳日期_JE"),
                    new FieldInfoSheetRow("  Memo  ", "文字型態", "傳票摘要_JE"),
                    new FieldInfoSheetRow("Amount", "數字型態", null),
                    new FieldInfoSheetRow("傳票金額_JE 由系統產生 : Amount", "數字型態", "傳票金額_JE")
                ],
                source.FieldInfoSheet.Gl);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_CurrentLayoutWithoutTheHiddenMarker_ReadsTheMarkedNamesAsWritten()
    {
        var path = FieldInfoSheetWorkbook.Write(FieldInfoSheetWorkbook.CurrentLayout(
            [
                ["【Account】", "文字型態", "10", null, "會計科目編號_TB"],
                ["【Movement】", "數字型態", null, "2", "試算表變動金額_TB"]
            ],
            [
                ["【Doc No】", "文字型態", "12", null, "傳票號碼_JE"],
                ["Amount", "數字型態", null, "2"],
                ["【傳票金額_JE】由系統產生 : 【Amount】", "數字型態", null, "2", "傳票金額_JE"]
            ]));
        try
        {
            var source = await new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None);

            Assert.Null(source.Recorded);
            Assert.NotNull(source.FieldInfoSheet);
            Assert.Equal(
                [
                    new FieldInfoSheetRow("【Account】", "文字型態", "會計科目編號_TB"),
                    new FieldInfoSheetRow("【Movement】", "數字型態", "試算表變動金額_TB")
                ],
                source.FieldInfoSheet.Tb);
            Assert.Equal(
                [
                    new FieldInfoSheetRow("【Doc No】", "文字型態", "傳票號碼_JE"),
                    new FieldInfoSheetRow("Amount", "數字型態", null),
                    new FieldInfoSheetRow("【傳票金額_JE】由系統產生 : 【Amount】", "數字型態", "傳票金額_JE")
                ],
                source.FieldInfoSheet.Gl);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_SectionsInAnyOrderWithoutHeaders_StopAtABlankRowOrTheNextTitle()
    {
        var path = FieldInfoSheetWorkbook.Write(
        [
            [" GL檔案配對前後欄位對照表 "],
            ["Doc No", "文字型態", null, null, "傳票號碼_JE"],
            [FieldInfoSheetWorkbook.TbTitle],
            ["Acct", "文字型態", null, null, "會計科目編號_TB"],
            null,
            ["對照表以外的文字"]
        ]);
        try
        {
            var source = await new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None);

            Assert.NotNull(source.FieldInfoSheet);
            Assert.Equal([new FieldInfoSheetRow("Doc No", "文字型態", "傳票號碼_JE")], source.FieldInfoSheet.Gl);
            Assert.Equal([new FieldInfoSheetRow("Acct", "文字型態", "會計科目編號_TB")], source.FieldInfoSheet.Tb);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_WithoutTheFieldInfoWorksheet_AsksForAWorkingPaper()
    {
        var path = FieldInfoSheetWorkbook.Write([["合成資料"]], sheetName: "其他工作表");
        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));

            Assert.Equal(JetErrorCodes.MappingMetadataMissing, ex.Code);
            Assert.Equal("這份檔案沒有「自動化工具-檔案欄位資訊」工作表，無法帶入欄位。請改選工作底稿，或直接在下方選擇欄位。", ex.Message);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_WithoutTheGlSection_SaysTheGlTableIsMissing()
    {
        var path = FieldInfoSheetWorkbook.Write(
        [
            ["V.2019"],
            [FieldInfoSheetWorkbook.TbTitle],
            FieldInfoSheetWorkbook.Header,
            ["Acct", "文字型態", null, null, "會計科目編號_TB"]
        ]);
        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));

            Assert.Equal(JetErrorCodes.MappingMetadataMissing, ex.Code);
            Assert.Equal("「自動化工具-檔案欄位資訊」裡找不到 GL 的欄位對照表，無法帶入欄位。請改選工作底稿，或直接在下方選擇欄位。", ex.Message);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_HiddenRecordsThatCannotBeUsed_RejectTheWholeFileWithTheNextStep()
    {
        var badReportMarker = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Name = ReportWorkbookMetadataFormat.WorksheetName;
            sheet.Cell("A1").Value = "NOT_THE_MARKER";
        });
        var unsupportedVersion = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Name = MappingMetadataFormat.WorksheetName;
            sheet.Cell(MappingMetadataFormat.MarkerCell).Value = MappingMetadataFormat.Marker;
            sheet.Cell(MappingMetadataFormat.VersionCell).Value = "3";
            sheet.Cell(MappingMetadataFormat.PayloadCell).Value = "{}";
        });
        try
        {
            var marker = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(badReportMarker, CancellationToken.None));
            var version = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(unsupportedVersion, CancellationToken.None));

            Assert.Equal(JetErrorCodes.MappingMetadataInvalid, marker.Code);
            Assert.Equal("檔案裡的欄位配對紀錄格式無效，無法帶入。請直接在下方選擇欄位。", marker.Message);
            Assert.Equal(JetErrorCodes.MappingMetadataInvalid, version.Code);
            Assert.Equal("檔案裡的欄位配對紀錄版本不受支援，無法帶入。請直接在下方選擇欄位。", version.Message);
        }
        finally
        {
            TestWorkbookBuilder.Delete(badReportMarker);
            TestWorkbookBuilder.Delete(unsupportedVersion);
        }
    }

    [Fact]
    public async Task ReadAsync_WrongExtensionOrMissingFile_SaysWhatHappened()
    {
        var reader = new OpenXmlMappingMetadataReader();
        var csv = Path.Combine(Path.GetTempPath(), $"jet-field-info-{Guid.NewGuid():N}.csv");
        var missing = Path.Combine(Path.GetTempPath(), $"jet-field-info-{Guid.NewGuid():N}.xlsx");

        var unsupported = await Assert.ThrowsAsync<JetActionException>(() => reader.ReadAsync(csv, CancellationToken.None));
        var notFound = await Assert.ThrowsAsync<JetActionException>(() => reader.ReadAsync(missing, CancellationToken.None));

        Assert.Equal("由底稿匯入欄位只支援 .xlsx 檔案。請改選 .xlsx 的工作底稿，或直接在下方選擇欄位。", unsupported.Message);
        Assert.Equal("找不到指定的檔案。請重新選擇檔案，或直接在下方選擇欄位。", notFound.Message);
    }

    // 損壞的檔案列號大到換算成 int 會溢位（超過 2,147,483,647）時，照讀不到的檔案處理，不變成非預期的錯誤。
    [Fact]
    public async Task ReadAsync_RowNumberTooLargeToRead_SaysTheFileCannotBeRead()
    {
        var path = FieldInfoSheetWorkbook.Write(FieldInfoSheetWorkbook.CurrentLayout(
            [],
            [["【Doc No】", "文字型態", "12", null, "傳票號碼_JE"]]));
        try
        {
            using (var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(path, true))
            {
                Assert.IsType<DocumentFormat.OpenXml.Spreadsheet.Worksheet>(document.WorkbookPart!.WorksheetParts.Single().Worksheet)
                    .Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>().Last().RowIndex = uint.MaxValue;
            }

            var ex = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, ex.Code);
            Assert.Equal(UnreadableMessage, ex.Message);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    // 列沒有寫列號時照出現順序推列號；前一列的列號已是 int 上限時，推算溢位也照讀不到的檔案處理，不變成非預期的錯誤。
    // 這是新增的測試。
    [Fact]
    public async Task ReadAsync_InferredRowNumberTooLargeToRead_SaysTheFileCannotBeRead()
    {
        var path = FieldInfoSheetWorkbook.Write(FieldInfoSheetWorkbook.CurrentLayout(
            [],
            [["【Doc No】", "文字型態", "12", null, "傳票號碼_JE"]]));
        try
        {
            using (var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(path, true))
            {
                var last = Assert.IsType<DocumentFormat.OpenXml.Spreadsheet.Worksheet>(document.WorkbookPart!.WorksheetParts.Single().Worksheet)
                    .Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>().Last();
                last.RowIndex = int.MaxValue;
                foreach (var cell in last.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>())
                {
                    cell.CellReference = null;
                }

                last.InsertAfterSelf(new DocumentFormat.OpenXml.Spreadsheet.Row(
                    new DocumentFormat.OpenXml.Spreadsheet.Cell
                    {
                        DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.InlineString,
                        InlineString = new DocumentFormat.OpenXml.Spreadsheet.InlineString(
                            new DocumentFormat.OpenXml.Spreadsheet.Text("【Memo】"))
                    }));
            }

            var ex = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, ex.Code);
            Assert.Equal(UnreadableMessage, ex.Message);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    // 儲存格沒有寫參照時照出現順序推欄號；前一格的欄號已是 int 上限時，推算溢位也照讀不到的檔案處理，不變成非預期的錯誤。
    // 欄名 FXSHRXW 換算成欄號剛好是 2,147,483,647。這是新增的測試。
    [Fact]
    public async Task ReadAsync_InferredColumnNumberTooLargeToRead_SaysTheFileCannotBeRead()
    {
        var path = FieldInfoSheetWorkbook.Write(FieldInfoSheetWorkbook.CurrentLayout(
            [],
            [["【Doc No】", "文字型態", "12", null, "傳票號碼_JE"]]));
        try
        {
            using (var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(path, true))
            {
                var row = Assert.IsType<DocumentFormat.OpenXml.Spreadsheet.Worksheet>(document.WorkbookPart!.WorksheetParts.Single().Worksheet)
                    .Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>().Last();
                var cells = row.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>().ToArray();
                cells[0].CellReference = "FXSHRXW" + row.RowIndex!.Value;
                cells[1].CellReference = null;
            }

            var ex = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, ex.Code);
            Assert.Equal(UnreadableMessage, ex.Message);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    // 隱藏紀錄被改過、含單獨的代理字元（無效的 UTF-16）時，和其他壞掉的 JSON 一樣算格式無效，不說成讀不到檔案。
    // F1:H1 與 JET_Metadata 兩種紀錄都檢查。這是新增的測試。
    [Fact]
    public async Task ReadAsync_HiddenRecordsWithInvalidUtf16_RejectAsInvalidFormat()
    {
        var fieldInfo = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Name = MappingMetadataFormat.WorksheetName;
            sheet.Cell(MappingMetadataFormat.MarkerCell).Value = MappingMetadataFormat.Marker;
            sheet.Cell(MappingMetadataFormat.VersionCell).Value = MappingMetadataFormat.CurrentVersion;
            sheet.Cell(MappingMetadataFormat.PayloadCell).Value = "{\"\\ud800\":{}}";
        });
        var reportMetadata = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Name = ReportWorkbookMetadataFormat.WorksheetName;
            sheet.Cell("A1").Value = ReportWorkbookMetadataFormat.Marker;
            sheet.Cell("B1").Value = ReportWorkbookMetadataFormat.CurrentVersion;
            sheet.Cell("C1").Value = 1;
            sheet.Cell("A2").Value = "1";
            sheet.Cell("B2").Value = "{\"mapping\":{\"\\ud800\":1}}";
        });
        try
        {
            foreach (var path in new[] { fieldInfo, reportMetadata })
            {
                var ex = await Assert.ThrowsAsync<JetActionException>(() =>
                    new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));

                Assert.Equal(JetErrorCodes.MappingMetadataInvalid, ex.Code);
                Assert.Equal("檔案裡的欄位配對紀錄格式無效，無法帶入。請直接在下方選擇欄位。", ex.Message);
            }
        }
        finally
        {
            TestWorkbookBuilder.Delete(fieldInfo);
            TestWorkbookBuilder.Delete(reportMetadata);
        }
    }

    // 工作表的 XML 壞掉時，訊息只寫讀不到與下一步，不推測原因（裁定 2026-10-30-20）。
    [Fact]
    public async Task ReadAsync_BrokenWorksheetXml_SaysTheFileCannotBeRead()
    {
        var path = FieldInfoSheetWorkbook.Write(FieldInfoSheetWorkbook.CurrentLayout(
            [],
            [["【Doc No】", "文字型態", "12", null, "傳票號碼_JE"]]));
        try
        {
            using (var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(path, true))
            {
                var part = document.WorkbookPart!.WorksheetParts.Single();
                using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
                using var writer = new StreamWriter(stream);
                writer.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\">");
            }

            var ex = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, ex.Code);
            Assert.Equal(UnreadableMessage, ex.Message);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    // F1 到 H1 三格都空白才改讀看得見的表；有任何一格有內容而 F1 不是標記，算紀錄存在但格式不對，整份不帶入（裁定 2026-11-05-14）。
    [Theory]
    [InlineData("不是標記", null, null)]
    [InlineData(null, "2", null)]
    [InlineData(null, null, "{}")]
    public async Task ReadAsync_HiddenCellsWithContentButNoMarker_RejectTheWholeFile(string? f1, string? g1, string? h1)
    {
        var rows = FieldInfoSheetWorkbook.CurrentLayout(
            [],
            [["【Doc No】", "文字型態", "12", null, "傳票號碼_JE"]]).ToList();
        rows[0] = [null, null, null, null, null, f1, g1, h1];
        var path = FieldInfoSheetWorkbook.Write(rows);
        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));

            Assert.Equal(JetErrorCodes.MappingMetadataInvalid, ex.Code);
            Assert.Equal("檔案裡的欄位配對紀錄格式無效，無法帶入。請直接在下方選擇欄位。", ex.Message);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    // 只有其他程式獨占檔案時才讀不到，訊息和其他匯入共用同一句；Excel 一般開著的檔照樣讀得到（裁定 2026-11-06-12）。
    [Fact]
    public async Task ReadAsync_FileLockedByAnotherProgram_UsesTheSharedOpenMessage()
    {
        var path = FieldInfoSheetWorkbook.Write(FieldInfoSheetWorkbook.CurrentLayout(
            [],
            [["【Doc No】", "文字型態", "12", null, "傳票號碼_JE"]]));
        try
        {
            JetActionException ex;
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                ex = await Assert.ThrowsAsync<JetActionException>(() =>
                    new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));
            }

            Assert.Equal(JetErrorCodes.FileReadError, ex.Code);
            Assert.Equal($"檔案「{Path.GetFileName(path)}」正被其他程式開啟（例如 Excel）。請關閉該檔案後重試。", ex.Message);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    private const string UnreadableMessage = "無法讀取這份檔案。請改選能用 Excel 開啟的 .xlsx 工作底稿，或直接在下方選擇欄位。";
}
