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
            Assert.Equal("無法讀取欄位配對報告。請確認檔案沒有被其他程式開啟，而且是 JET 匯出的 xlsx 檔。", ex.Message);
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

            var draft = await new OpenXmlMappingMetadataReader().ReadAsync(copyPath, CancellationToken.None);

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
