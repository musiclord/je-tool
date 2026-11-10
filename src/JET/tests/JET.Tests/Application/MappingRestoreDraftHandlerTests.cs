using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class MappingRestoreDraftHandlerTests
{
    // 裁定 2026-11-05-03：報告裡選的來源欄在目前資料找不到時，對得上的照樣帶入，找不到的列出來請審計員重選，不整份拒絕。
    // 原本這裡斷言整份回 mapping_column_not_found；改成斷言 TB 只少了找不到的那一欄、unmatchedFields 寫出它，
    // 還原仍是唯讀，不改動已確認的配對與標準資料。改前的第一次失敗：Focused 收據 20261105-021308140-a48dfbf83644426388937a4bc7f51278。
    [Fact]
    public async Task RestoreDraft_OneDatasetColumnMissing_KeepsTheMatchedFieldsListsTheMissingOneWithoutMutation()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var before = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        var (gl, tb) = CommittedFrom(before);
        var badTb = tb.Mapping.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        badTb[TbMappingKeys.AccountNumber] = "目前批次不存在的欄位";
        var path = Workbook(MappingMetadataCodec.Encode(
            gl,
            tb with { Mapping = badTb }));
        var glRows = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM gl;");
        var tbRows = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM tb;");

        try
        {
            var restored = await host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = path }));

            Assert.Equal(gl.Mapping, StringMap(restored.GetProperty("gl").GetProperty("mapping")));
            var expectedTb = tb.Mapping.Where(pair => pair.Key != TbMappingKeys.AccountNumber)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            Assert.Equal(expectedTb, StringMap(restored.GetProperty("tb").GetProperty("mapping")));
            var unmatched = Assert.Single(restored.GetProperty("unmatchedFields").EnumerateArray());
            Assert.Equal("tb", unmatched.GetProperty("dataset").GetString());
            Assert.Equal(TbMappingKeys.AccountNumber, unmatched.GetProperty("key").GetString());
            Assert.Equal("會計科目編號", unmatched.GetProperty("label").GetString());
            Assert.Equal("目前批次不存在的欄位", unmatched.GetProperty("sourceColumn").GetString());
            var after = await host.DispatchAsync(
                "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
            Assert.Equal(before.GetProperty("mapping").GetRawText(), after.GetProperty("mapping").GetRawText());
            Assert.Equal(glRows, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM gl;"));
            Assert.Equal(tbRows, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM tb;"));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task RestoreDraft_V2RdeAfterGlReimport_AuthorizesExactStableIdForCommit()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        var (originalGl, tb) = CommittedFrom(loaded);
        var mapping = originalGl.Mapping.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        // 裁定 2026-10-08-03 移除傳票日期與回溯過帳：示範資料不再有「傳票日期」欄。
        // 改用「傳票核准日」當自訂日期欄，先移除 core approvalDate 配對並把核准日設為未配對，避免同一來源重複指派；
        // stable ID 與 round-trip 斷言全部保留。
        mapping.Remove(GlMappingKeys.ApprovalDate);
        var committed = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping,
            amountMode = originalGl.ModeName,
            approvalDateMode = ApprovalDateModeNames.Unmapped,
            manualAutoPolicy = new
            {
                manualValues = new[] { "1" },
                automaticValues = new[] { "0" }
            },
            rdeFields = new[]
            {
                new
                {
                    sourceColumn = "傳票核准日",
                    label = "傳票核准日",
                    valueType = RdeFieldValueTypeNames.Date
                }
            }
        }));
        var stableId = committed.GetProperty("rdeFields")[0].GetProperty("fieldId").GetString()!;
        var committedGl = new CommittedMapping(
            DatasetKind.Gl,
            StringMap(committed.GetProperty("mapping")),
            committed.GetProperty("amountMode").GetString()!,
            committed.GetProperty("batchId").GetString()!,
            DateTimeOffset.UnixEpoch)
        {
            GlOptions = new GlMappingOptions(
                ApprovalDateModeNames.Unmapped,
                null,
                new GlManualAutoPolicy(["1"], ["0"]),
                [
                    new GlRdeFieldMetadata(
                        stableId,
                        "傳票核准日",
                        "傳票核准日",
                        RdeFieldValueTypeNames.Date)
                ])
        };
        var path = Workbook(MappingMetadataCodec.Encode(committedGl, tb));

        try
        {
            var glFile = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportGlFile");
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                filePath = glFile.GetProperty("filePath").GetString(),
                fileName = glFile.GetProperty("fileName").GetString()
            }));
            Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM gl_rde_field;"));
            Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM gl_rde_value;"));

            var restored = await host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = path }));
            var recommitted = await host.DispatchAsync(
                "mapping.commit.gl", restored.GetProperty("gl").GetRawText());

            Assert.Equal(
                stableId,
                recommitted.GetProperty("rdeFields")[0].GetProperty("fieldId").GetString());
            Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM gl_rde_field;"));
            Assert.True(await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM gl_rde_value;") > 0);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task RestoreDraft_MissingModeRequiredKey_IsInvalidMetadata()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        var (gl, tb) = CommittedFrom(loaded);
        var incomplete = gl.Mapping.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        incomplete.Remove(GlMappingKeys.VoucherNumber);
        var path = Workbook(MappingMetadataCodec.Encode(gl with { Mapping = incomplete }, tb));

        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = path })));
            Assert.Equal(JetErrorCodes.MappingMetadataInvalid, ex.Code);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    // 裁定 2026-10-30-20：讀不到報告時不寫「可能」，改寫審計員接下來能做什麼。改字前依預期失敗：Focused 收據 20261031-005903747-67070610b7ef42478de8cfc5c13facb4。
    // 按鈕改名「由底稿匯入欄位」後，舊版 IDEA JET 的底稿也能選，訊息用詞改成「檔案」，不再限定 JET 匯出（裁定 2026-11-09-27、2026-11-09-28）。
    // 改字前依預期失敗：Focused 收據 20261109-145908138-d8bb48b344bd4556930b393e6ba6b450。
    // 複審後拿掉「沒有被其他程式開啟」（開著的檔也讀得到，裁定 2026-11-06-12）並補上下一步，
    // 改字前依預期失敗：Focused 收據 20261109-161450553-bdcd04fe17194aef923a7e94213d00a8。
    // 第二次確認時拿掉推測原因的「可能」（裁定 2026-10-30-20），改字前依預期失敗：Focused 收據 20261109-170136963-bef1c5fabdf84abcaf8fa45265465d85。
    [Fact]
    public async Task RestoreDraft_UnreadableReport_SaysWhatToCheckWithoutGuessing()
    {
        var path = Path.Combine(Path.GetTempPath(), "jet-restore-" + Guid.NewGuid().ToString("N") + ".xlsx");
        await File.WriteAllBytesAsync(path, "not-an-xlsx"u8.ToArray(), CancellationToken.None);
        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));
            Assert.Equal(JetErrorCodes.FileReadError, ex.Code);
            Assert.Equal("無法讀取這份檔案。請改選能用 Excel 開啟的 .xlsx 工作底稿，或直接在下方選擇欄位。", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RestoreDraft_WithoutCurrentImports_RejectsBeforeReturningDrafts()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", """
        {
          "projectCode":"P2-NO-IMPORT",
          "clientName":"P2 合成案件",
          "operatorId":"p2-auditor",
          "periodStart":"2025-01-01",
          "periodEnd":"2025-12-31",
          "moneyScale":10000,
          "databaseProvider":"sqlite"
        }
        """);
        var path = Workbook(MappingMetadataCodec.Encode(MinimalGl(), MinimalTb()));

        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = path })));
            Assert.Equal(JetErrorCodes.NoImportBatch, ex.Code);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    // 裁定 2026-11-05-03：沒有匯入 TB 時只帶入 GL，tb 回 null；報告裡的 TB 配對不檢查。
    [Fact]
    public async Task RestoreDraft_WithoutTbImport_ReturnsOnlyTheGlDraft()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", """
        {
          "projectCode":"RESTORE-GL-ONLY",
          "clientName":"合成案件",
          "operatorId":"tester",
          "periodStart":"2025-01-01",
          "periodEnd":"2025-12-31",
          "databaseProvider":"sqlite"
        }
        """);
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns("Document", "GlDate", "Account", "AccountName", "Description", "Amount")
            .AddRow("JV-1", "2025-03-01", "1101", "現金", "合成", "100")
            .AddRow("JV-1", "2025-03-01", "4101", "收入", "合成", "-100");
        var glPath = builder.WriteWorkbook();
        var path = Workbook(MappingMetadataCodec.Encode(MinimalGl(), MinimalTb()));
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glPath }));
            var restored = await host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = path }));

            Assert.Equal(MinimalGl().Mapping, StringMap(restored.GetProperty("gl").GetProperty("mapping")));
            Assert.Equal(JsonValueKind.Null, restored.GetProperty("tb").ValueKind);
            Assert.Empty(restored.GetProperty("unmatchedFields").EnumerateArray());
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
            TestWorkbookBuilder.Delete(path);
        }
    }

    // 裁定 2026-11-05-03：JET 匯出的六種檔案都能帶入配對。科目配對工作檔與預篩選報告沒有「自動化工具-檔案欄位資訊」的配對，
    // 只有六份正式報表共用的隱藏工作表 JET_Metadata；從它帶回的配對要和案件確認的配對相同。
    [Fact]
    public async Task RestoreDraft_FromReportsWithOnlyTheSharedHiddenMetadata_ReturnsTheCommittedMapping()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var (gl, tb) = CommittedFrom(await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId })));
        var validation = await host.DispatchAsync("validate.run");
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString();
        var template = await host.DispatchAsync(
            "export.accountMappingTemplate", JsonSerializer.Serialize(new { resultId = validationResultId }));
        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenReport = await host.DispatchAsync("export.prescreenReport", JsonSerializer.Serialize(new
        {
            resultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString(),
        }));

        foreach (var path in new[]
                 {
                     template.GetProperty("filePath").GetString()!,
                     prescreenReport.GetProperty("publishedReport").GetProperty("fullPath").GetString()!,
                 })
        {
            using (var workbook = new ClosedXML.Excel.XLWorkbook(path))
            {
                Assert.True(workbook.Worksheets.Contains(ReportWorkbookMetadataFormat.WorksheetName), path);
                Assert.False(
                    workbook.Worksheets.TryGetWorksheet(MappingMetadataFormat.WorksheetName, out var fieldInfo)
                    && fieldInfo.Cell(MappingMetadataFormat.MarkerCell).GetString() == MappingMetadataFormat.Marker,
                    "這份檔案不該靠「自動化工具-檔案欄位資訊」帶回配對：" + path);
            }

            var restored = await host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = path }));

            Assert.Equal(gl.Mapping, StringMap(restored.GetProperty("gl").GetProperty("mapping")));
            Assert.Equal(gl.ModeName, restored.GetProperty("gl").GetProperty("amountMode").GetString());
            Assert.Equal(tb.Mapping, StringMap(restored.GetProperty("tb").GetProperty("mapping")));
            Assert.Equal(tb.ModeName, restored.GetProperty("tb").GetProperty("changeMode").GetString());
            Assert.Empty(restored.GetProperty("unmatchedFields").EnumerateArray());
        }
    }

    // 報表匯出時 TB 還沒確認配對，JET_Metadata 的 tb 是 null：目前案件有匯入 TB 也只帶入 GL，tb 回 null。
    [Fact]
    public async Task RestoreDraft_ReportExportedWithoutTbMapping_ReturnsOnlyTheGlDraftEvenWithTbImported()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var (gl, _) = CommittedFrom(await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId })));
        var path = SharedMetadataWorkbook(new ReportWorkbookMetadata("2025-01-01", "2025-12-31", 1, gl, null));
        try
        {
            var restored = await host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = path }));

            Assert.Equal(gl.Mapping, StringMap(restored.GetProperty("gl").GetProperty("mapping")));
            Assert.Equal(JsonValueKind.Null, restored.GetProperty("tb").ValueKind);
            Assert.Empty(restored.GetProperty("unmatchedFields").EnumerateArray());
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    /// <summary>只有一張空白工作表，再用正式報表的寫法加上 JET_Metadata。</summary>
    [Fact]
    public async Task FieldInfoSheetWithoutVersionText_StillCarriesTheCommittedMapping()
    {
        // 裁定 2026-11-06-11 拿掉「自動化工具-檔案欄位資訊」第 1 列的 V.2019。第 1 列右邊 F1:H1 的隱藏配對資訊照舊寫入，
        // 拿掉 JET_Metadata 之後也讀得回同一份配對。這是新增的測試。
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var (gl, tb) = CommittedFrom(await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId })));
        var validation = await host.DispatchAsync("validate.run");
        var exported = await host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new
        {
            resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString(),
        }));
        var reportPath = exported.GetProperty("publishedReports").EnumerateArray()
            .Single(item => item.GetProperty("kind").GetString() == "validationReport")
            .GetProperty("fullPath").GetString()!;
        var copyPath = Path.Combine(Path.GetTempPath(), $"jet-field-info-{Guid.NewGuid():N}.xlsx");
        try
        {
            using (var workbook = new ClosedXML.Excel.XLWorkbook(reportPath))
            {
                var sheet = workbook.Worksheet(MappingMetadataFormat.WorksheetName);
                Assert.True(sheet.Cell("A1").IsEmpty());
                Assert.DoesNotContain(sheet.MergedRanges, range => range.RangeAddress.ToString() == "A1:E1");
                Assert.Equal(MappingMetadataFormat.Marker, sheet.Cell(MappingMetadataFormat.MarkerCell).GetString());
                workbook.Worksheet(ReportWorkbookMetadataFormat.WorksheetName).Delete();
                workbook.SaveAs(copyPath);
            }

            // ReadAsync 改回傳 MappingDraftSource（裁定 2026-11-09-28），F1:H1 的隱藏紀錄在 Recorded；斷言不變。
            // 第一次失敗：Build 收據 20261109-145225420-a56baf66e9a64e52951bd296df7a960e（編譯失敗）。
            var draft = (await new OpenXmlMappingMetadataReader().ReadAsync(copyPath, CancellationToken.None)).Recorded!;

            Assert.Equal(gl.Mapping.OrderBy(item => item.Key, StringComparer.Ordinal),
                draft.Gl.Mapping.OrderBy(item => item.Key, StringComparer.Ordinal));
            Assert.Equal(gl.ModeName, draft.Gl.AmountMode);
            Assert.NotNull(draft.Tb);
            Assert.Equal(tb.Mapping.OrderBy(item => item.Key, StringComparer.Ordinal),
                draft.Tb.Mapping.OrderBy(item => item.Key, StringComparer.Ordinal));
            Assert.Equal(tb.ModeName, draft.Tb.ChangeMode);
        }
        finally
        {
            File.Delete(copyPath);
        }
    }

    // 裁定 2026-11-09-28：舊版 IDEA JET 的底稿沒有隱藏配對紀錄，從看得見的對照表推得草稿。
    // 欄名正規化後唯一對得上的換成目前的欄名；改了名的來源欄與 TB 兩欄相減列出請審計員重選；草稿按確認配對就能儲存。這是新增的測試。
    [Fact]
    public async Task RestoreDraft_LegacyWorkingPaperWithoutHiddenRecords_InfersTheDraftAndListsFieldsToReselect()
    {
        using var host = new HandlerTestHost();
        await CreateSyntheticProjectAsync(host, "RESTORE-LEGACY-PAPER");
        var glPath = LegacyCaseGlWorkbook();
        var tbPath = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            string[][] rows =
            [
                ["Account", "Account Name", "End", "Begin"],
                ["1101", "現金", "100", "0"],
                ["4101", "收入", "-100", "0"]
            ];
            for (var row = 0; row < rows.Length; row++)
            {
                for (var column = 0; column < rows[row].Length; column++)
                {
                    sheet.Cell(row + 1, column + 1).SetValue(rows[row][column]);
                }
            }
        });
        var paperPath = LegacyWorkingPaper();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glPath }));
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tbPath }));

            var restored = await host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = paperPath }));

            var gl = restored.GetProperty("gl");
            Assert.Equal(
                new Dictionary<string, string>
                {
                    [GlMappingKeys.VoucherNumber] = "Doc No",
                    [GlMappingKeys.GlDate] = "Post Date",
                    [GlMappingKeys.AccountNumber] = "Acct",
                    [GlMappingKeys.AccountName] = "ACCT_NAME",
                    [GlMappingKeys.Description] = "Memo",
                    [GlMappingKeys.Amount] = "Amount"
                },
                StringMap(gl.GetProperty("mapping")));
            Assert.Equal(GlAmountModeNames.Signed, gl.GetProperty("amountMode").GetString());
            Assert.Equal(ApprovalDateModeNames.SameAsGlDate, gl.GetProperty("approvalDateMode").GetString());
            Assert.Equal(JsonValueKind.Null, gl.GetProperty("postingStatusPolicy").ValueKind);
            Assert.Equal(["1"], gl.GetProperty("manualAutoPolicy").GetProperty("manualValues").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(["0"], gl.GetProperty("manualAutoPolicy").GetProperty("automaticValues").EnumerateArray().Select(item => item.GetString()));
            var rde = Assert.Single(gl.GetProperty("rdeFields").EnumerateArray());
            Assert.Equal("Region", rde.GetProperty("sourceColumn").GetString());
            Assert.Equal("地區", rde.GetProperty("label").GetString());
            Assert.Equal(RdeFieldValueTypeNames.Text, rde.GetProperty("valueType").GetString());
            var fieldId = rde.GetProperty("fieldId").GetString();
            Assert.True(GlMappingOptionsRules.IsCanonicalFieldId(fieldId));

            var tb = restored.GetProperty("tb");
            Assert.Equal(
                new Dictionary<string, string>
                {
                    [TbMappingKeys.AccountNumber] = "Account",
                    [TbMappingKeys.AccountName] = "Account Name"
                },
                StringMap(tb.GetProperty("mapping")));
            Assert.Equal(TbChangeModeNames.Direct, tb.GetProperty("changeMode").GetString());

            Assert.Equal(
                [
                    ("gl", GlMappingKeys.ApprovedBy, "傳票核准人員", "核准者"),
                    ("tb", TbMappingKeys.ChangeAmount, "年度變動金額", "End - Begin")
                ],
                UnmatchedFields(restored));

            var committed = await host.DispatchAsync("mapping.commit.gl", gl.GetRawText());
            Assert.Equal(fieldId, committed.GetProperty("rdeFields")[0].GetProperty("fieldId").GetString());
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
            TestWorkbookBuilder.Delete(tbPath);
            TestWorkbookBuilder.Delete(paperPath);
        }
    }

    // 裁定 2026-11-09-28 與 2026-11-05-03：沒有匯入 TB 時只推 GL，tb 回 null，也不列 TB 的欄位。這是新增的測試。
    [Fact]
    public async Task RestoreDraft_LegacyWorkingPaperWithoutTbImport_ReturnsOnlyTheGlDraft()
    {
        using var host = new HandlerTestHost();
        await CreateSyntheticProjectAsync(host, "RESTORE-LEGACY-GL-ONLY");
        var glPath = LegacyCaseGlWorkbook();
        var paperPath = LegacyWorkingPaper();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glPath }));

            var restored = await host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = paperPath }));

            Assert.Equal("Doc No", restored.GetProperty("gl").GetProperty("mapping").GetProperty(GlMappingKeys.VoucherNumber).GetString());
            Assert.Equal(JsonValueKind.Null, restored.GetProperty("tb").ValueKind);
            Assert.Equal([("gl", GlMappingKeys.ApprovedBy, "傳票核准人員", "核准者")], UnmatchedFields(restored));
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
            TestWorkbookBuilder.Delete(paperPath);
        }
    }

    // GL 的對照表推不出任何欄位時，回錯誤而不是回一份空的 GL 草稿蓋掉審計員已選的欄位；
    // TB 段有配對、也已匯入 TB 時一樣，因為 GL 草稿會整份換掉；只有讀不懂的列時也一樣。這是新增的測試。
    [Fact]
    public async Task RestoreDraft_GlTableWithoutAnyMappedRow_RejectsInsteadOfReturningAnEmptyGlDraft()
    {
        using var host = new HandlerTestHost();
        await CreateSyntheticProjectAsync(host, "RESTORE-LEGACY-EMPTY");
        var glPath = LegacyCaseGlWorkbook();
        var tbPath = LegacyCaseTbWorkbook();
        string?[][] glRows =
        [
            ["Doc No", "文字型態", "12", null],
            ["Amount", "數字型態", null, "2"]
        ];
        var emptyPath = FieldInfoSheetWorkbook.Write(FieldInfoSheetWorkbook.LegacyLayout([], glRows));
        var tbOnlyPath = FieldInfoSheetWorkbook.Write(FieldInfoSheetWorkbook.LegacyLayout(
            [["Account", "文字型態", "10", null, "會計科目編號_TB"]],
            glRows));
        var unreadableOnlyPath = FieldInfoSheetWorkbook.Write(FieldInfoSheetWorkbook.LegacyLayout(
            [],
            [["Status 看不懂的說明", "文字型態", "1", null, "過帳碼_JE"]]));
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glPath }));
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tbPath }));

            foreach (var paperPath in new[] { emptyPath, tbOnlyPath, unreadableOnlyPath })
            {
                var exception = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                    "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = paperPath })));

                Assert.Equal(JetErrorCodes.MappingMetadataMissing, exception.Code);
                Assert.Equal("這份檔案裡 GL 的欄位對照表沒有可帶入的欄位，無法帶入。請直接在下方選擇欄位。", exception.Message);
            }
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
            TestWorkbookBuilder.Delete(tbPath);
            TestWorkbookBuilder.Delete(emptyPath);
            TestWorkbookBuilder.Delete(tbOnlyPath);
            TestWorkbookBuilder.Delete(unreadableOnlyPath);
        }
    }

    // TB 段沒有任何配對時不回 TB 草稿，審計員已選的 TB 欄位與金額格式不動。這是新增的測試。
    [Fact]
    public async Task RestoreDraft_TbTableWithoutAnyMappedRow_LeavesTheTbDraftAlone()
    {
        using var host = new HandlerTestHost();
        await CreateSyntheticProjectAsync(host, "RESTORE-LEGACY-NO-TB-ROWS");
        var glPath = LegacyCaseGlWorkbook();
        var tbPath = LegacyCaseTbWorkbook();
        var paperPath = FieldInfoSheetWorkbook.Write(FieldInfoSheetWorkbook.LegacyLayout(
            [["Account", "文字型態", "10", null]],
            [["Doc No", "文字型態", "12", null, "傳票號碼_JE"]]));
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glPath }));
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tbPath }));

            var restored = await host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = paperPath }));

            Assert.Equal("Doc No", restored.GetProperty("gl").GetProperty("mapping").GetProperty(GlMappingKeys.VoucherNumber).GetString());
            Assert.Equal(JsonValueKind.Null, restored.GetProperty("tb").ValueKind);
            Assert.Empty(UnmatchedFields(restored));
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
            TestWorkbookBuilder.Delete(tbPath);
            TestWorkbookBuilder.Delete(paperPath);
        }
    }

    // 新版底稿拿掉隱藏紀錄後，兩個名稱相同、來源欄不同的攸關資料元素都帶入，也能確認配對。這是新增的測試。
    [Fact]
    public async Task RestoreDraft_RelevantDataElementsWithTheSameName_AreBothRestoredAndCommitted()
    {
        using var host = new HandlerTestHost();
        await CreateSyntheticProjectAsync(host, "RESTORE-SAME-RDE-NAME");
        var glPath = new InlineGlWorkbookBuilder()
            .WithColumns("Doc No", "Post Date", "Acct", "Acct Name", "Memo", "Region", "Region 2", "Amount")
            .AddRow("JV-1", "2025-03-01", "1101", "現金", "合成", "北區", "甲區", "100")
            .AddRow("JV-1", "2025-03-01", "4101", "收入", "合成", "北區", "甲區", "-100")
            .WriteWorkbook();
        var paperPath = FieldInfoSheetWorkbook.Write(FieldInfoSheetWorkbook.CurrentLayout(
            [],
            [
                ["【Doc No】", "文字型態", "12", null, "傳票號碼_JE"],
                ["【Post Date】", "日期型態", null, null, "總帳日期_JE"],
                ["【Acct】", "文字型態", "10", null, "會計科目編號_JE"],
                ["【Acct Name】", "文字型態", "20", null, "會計科目名稱_JE"],
                ["【Memo】", "文字型態", "40", null, "傳票摘要_JE"],
                ["【Region】", "文字型態", "10", null, "地區（Region）_JE"],
                ["【Region 2】", "文字型態", "10", null, "地區（Region 2）_JE"],
                ["【傳票金額_JE】由系統產生 : 【Amount】", "數字型態", null, "2", "傳票金額_JE"]
            ]));
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glPath }));

            var restored = await host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = paperPath }));
            var gl = restored.GetProperty("gl");
            var committed = await host.DispatchAsync("mapping.commit.gl", gl.GetRawText());

            Assert.Empty(UnmatchedFields(restored));
            Assert.Equal(
                [("Region", "地區"), ("Region 2", "地區")],
                committed.GetProperty("rdeFields").EnumerateArray()
                    .Select(field => (field.GetProperty("sourceColumn").GetString(), field.GetProperty("label").GetString()))
                    .ToArray());
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
            TestWorkbookBuilder.Delete(paperPath);
        }
    }

    // 訊息用詞照由底稿匯入欄位計畫改寫：寫欄位名稱而不是配對鍵、下一步寫「請直接在下方選擇欄位」。這是新增的測試。
    [Fact]
    public async Task RestoreDraft_MessagesNameTheGlImportAndTheMissingFieldInScreenWords()
    {
        using var host = new HandlerTestHost();
        await CreateSyntheticProjectAsync(host, "RESTORE-MESSAGES");
        var paperPath = LegacyWorkingPaper();
        var incomplete = MinimalGl().Mapping
            .Where(pair => pair.Key != GlMappingKeys.VoucherNumber)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var recordedPath = Workbook(MappingMetadataCodec.Encode(MinimalGl() with { Mapping = incomplete }, MinimalTb()));
        var glPath = new InlineGlWorkbookBuilder()
            .WithColumns("Document", "GlDate", "Account", "AccountName", "Description", "Amount")
            .AddRow("JV-1", "2025-03-01", "1101", "現金", "合成", "100")
            .AddRow("JV-1", "2025-03-01", "4101", "收入", "合成", "-100")
            .WriteWorkbook();
        try
        {
            var noGl = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = paperPath })));
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glPath }));
            var missing = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = recordedPath })));

            Assert.Equal(JetErrorCodes.NoImportBatch, noGl.Code);
            Assert.Equal("還沒有匯入 GL，無法帶入欄位配對。請先匯入 GL。", noGl.Message);
            Assert.Equal(JetErrorCodes.MappingMetadataInvalid, missing.Code);
            Assert.Equal("檔案裡的欄位配對缺少必要欄位：GL 傳票號碼，無法帶入。請直接在下方選擇欄位。", missing.Message);
        }
        finally
        {
            TestWorkbookBuilder.Delete(paperPath);
            TestWorkbookBuilder.Delete(recordedPath);
            TestWorkbookBuilder.Delete(glPath);
        }
    }

    // 隱藏紀錄是金額加借貸別，卻兩側代碼都沒有：整份不帶入，訊息寫沒有借貸代碼與和其他拒絕相同的下一步，
    // 不寫成「缺少必要欄位：GL 借方代碼、GL 貸方代碼」。這是新增的測試。
    [Fact]
    public async Task RestoreDraft_RecordedSideAmountWithoutAnyCode_SaysTheCodesAreMissing()
    {
        using var host = new HandlerTestHost();
        await CreateSyntheticProjectAsync(host, "RESTORE-NO-CODES");
        var sideMapping = new Dictionary<string, string>(MinimalGl().Mapping, StringComparer.Ordinal)
        {
            [GlMappingKeys.DrCr] = "Side"
        };
        var recordedPath = Workbook(MappingMetadataCodec.Encode(
            MinimalGl() with { Mapping = sideMapping, ModeName = GlAmountModeNames.Side },
            MinimalTb()));
        var glPath = new InlineGlWorkbookBuilder()
            .WithColumns("Document", "GlDate", "Account", "AccountName", "Description", "Amount", "Side")
            .AddRow("JV-1", "2025-03-01", "1101", "現金", "合成", "100", "D")
            .AddRow("JV-1", "2025-03-01", "4101", "收入", "合成", "100", "C")
            .WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glPath }));
            var exception = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = recordedPath })));

            Assert.Equal(JetErrorCodes.MappingMetadataInvalid, exception.Code);
            Assert.Equal("檔案裡的欄位配對沒有借貸代碼，無法帶入。請直接在下方選擇欄位。", exception.Message);
        }
        finally
        {
            TestWorkbookBuilder.Delete(recordedPath);
            TestWorkbookBuilder.Delete(glPath);
        }
    }

    private static async Task CreateSyntheticProjectAsync(HandlerTestHost host, string projectCode)
    {
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode,
            clientName = "合成案件",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = "sqlite"
        }));
    }

    /// <summary>目前匯入的 GL：「ACCT_NAME」和舊底稿寫的「Acct Name」只差大小寫與符號；核准人員欄改名成「Approver」。</summary>
    private static string LegacyCaseGlWorkbook() => new InlineGlWorkbookBuilder()
        .WithColumns("Doc No", "Post Date", "Acct", "ACCT_NAME", "Memo", "Approver", "Region", "Amount")
        .AddRow("JV-1", "2025-03-01", "1101", "現金", "合成", "甲", "北區", "100")
        .AddRow("JV-1", "2025-03-01", "4101", "收入", "合成", "甲", "北區", "-100")
        .WriteWorkbook();

    /// <summary>目前匯入的 TB：欄名和舊底稿相同。</summary>
    private static string LegacyCaseTbWorkbook() => TestWorkbookBuilder.WriteWorkbook(sheet =>
    {
        string[][] rows =
        [
            ["Account", "Account Name", "End", "Begin"],
            ["1101", "現金", "100", "0"],
            ["4101", "收入", "-100", "0"]
        ];
        for (var row = 0; row < rows.Length; row++)
        {
            for (var column = 0; column < rows[row].Length; column++)
            {
                sheet.Cell(row + 1, column + 1).SetValue(rows[row][column]);
            }
        }
    });

    /// <summary>舊版 IDEA JET 版面的合成底稿，沒有任何隱藏配對紀錄。</summary>
    private static string LegacyWorkingPaper() => FieldInfoSheetWorkbook.Write(FieldInfoSheetWorkbook.LegacyLayout(
        [
            ["Account", "文字型態", "10", null, "會計科目編號_TB"],
            ["Account Name", "文字型態", "20", null, "會計科目名稱_TB"],
            ["End", "數字型態", null, "2"],
            ["Begin", "數字型態", null, "2"],
            ["試算表變動金額_TB 由系統產生 : End - Begin", "數字型態", null, "2", "試算表變動金額_TB"]
        ],
        [
            ["Doc No", "文字型態", "12", null, "傳票號碼_JE"],
            ["Post Date", "日期型態", null, null, "總帳日期_JE"],
            ["Acct", "文字型態", "10", null, "會計科目編號_JE"],
            ["Acct Name", "文字型態", "20", null, "會計科目名稱_JE"],
            ["Memo", "文字型態", "40", null, "傳票摘要_JE"],
            ["核准者", "文字型態", "10", null, "傳票核准人員_JE"],
            ["Region", "文字型態", "10", null, "地區_JE"],
            ["Amount", "數字型態", null, "2"],
            ["傳票金額_JE 由系統產生 : Amount", "數字型態", null, "2", "傳票金額_JE"],
            ["系統產生 : 同總帳日期_JE來源", "日期型態", null, null, "傳票核准日_JE"]
        ]));

    private static (string Dataset, string Key, string Label, string SourceColumn)[] UnmatchedFields(JsonElement restored) =>
        restored.GetProperty("unmatchedFields").EnumerateArray()
            .Select(item => (
                item.GetProperty("dataset").GetString()!,
                item.GetProperty("key").GetString()!,
                item.GetProperty("label").GetString()!,
                item.GetProperty("sourceColumn").GetString()!))
            .ToArray();

    private static string SharedMetadataWorkbook(ReportWorkbookMetadata metadata)
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet => sheet.Name = "合成報表");
        using (var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(path, true))
        {
            ReportWorkbookMetadataWorksheet.Append(document, metadata, CancellationToken.None);
        }

        return path;
    }

    private static (CommittedMapping Gl, CommittedMapping Tb) CommittedFrom(JsonElement loaded)
    {
        var mapping = loaded.GetProperty("mapping");
        var gl = mapping.GetProperty("gl");
        var tb = mapping.GetProperty("tb");
        return (
            new CommittedMapping(
                DatasetKind.Gl,
                StringMap(gl.GetProperty("mapping")),
                gl.GetProperty("amountMode").GetString()!,
                gl.GetProperty("sourceBatchId").GetString()!,
                gl.GetProperty("committedUtc").GetDateTimeOffset()),
            new CommittedMapping(
                DatasetKind.Tb,
                StringMap(tb.GetProperty("mapping")),
                tb.GetProperty("changeMode").GetString()!,
                tb.GetProperty("sourceBatchId").GetString()!,
                tb.GetProperty("committedUtc").GetDateTimeOffset()));
    }

    private static Dictionary<string, string> StringMap(JsonElement element)
        => element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetString()!,
            StringComparer.Ordinal);

    private static string Workbook(
        string payload,
        int formatVersion = MappingMetadataFormat.CurrentVersion) => TestWorkbookBuilder.WriteWorkbook(sheet =>
    {
        sheet.Name = MappingMetadataFormat.WorksheetName;
        sheet.Cell(MappingMetadataFormat.MarkerCell).Value = MappingMetadataFormat.Marker;
        sheet.Cell(MappingMetadataFormat.VersionCell).Value = formatVersion;
        sheet.Cell(MappingMetadataFormat.PayloadCell).Value = payload;
        sheet.Columns(6, 8).Hide();
    });

    private static CommittedMapping MinimalGl() => new(
        DatasetKind.Gl,
        new Dictionary<string, string>
        {
            [GlMappingKeys.VoucherNumber] = "Document",
            [GlMappingKeys.GlDate] = "GlDate",
            [GlMappingKeys.AccountNumber] = "Account",
            [GlMappingKeys.AccountName] = "AccountName",
            [GlMappingKeys.Description] = "Description",
            [GlMappingKeys.Amount] = "Amount"
        },
        GlAmountModeNames.Signed,
        "gl-batch",
        DateTimeOffset.UnixEpoch);

    private static CommittedMapping MinimalTb() => new(
        DatasetKind.Tb,
        new Dictionary<string, string>
        {
            [TbMappingKeys.AccountNumber] = "Account",
            [TbMappingKeys.AccountName] = "AccountName",
            [TbMappingKeys.ChangeAmount] = "Movement"
        },
        TbChangeModeNames.Direct,
        "tb-batch",
        DateTimeOffset.UnixEpoch);
}
