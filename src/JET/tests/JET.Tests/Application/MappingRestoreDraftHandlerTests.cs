using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class MappingRestoreDraftHandlerTests
{
    [Fact]
    public async Task RestoreDraft_OneDatasetIncompatible_RejectsWholeResponseWithoutMutation()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var before = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        var (gl, tb) = CommittedFrom(before);
        var badTb = tb.Mapping.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        badTb[TbMappingKeys.AccNum] = "目前批次不存在的欄位";
        var path = Workbook(MappingMetadataCodec.Encode(
            gl,
            tb with { Mapping = badTb }));
        var glRows = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM target_gl_entry;");
        var tbRows = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM target_tb_balance;");

        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = path })));

            Assert.Equal(JetErrorCodes.MappingColumnNotFound, ex.Code);
            var after = await host.DispatchAsync(
                "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
            Assert.Equal(before.GetProperty("mapping").GetRawText(), after.GetProperty("mapping").GetRawText());
            Assert.Equal(glRows, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM target_gl_entry;"));
            Assert.Equal(tbRows, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM target_tb_balance;"));
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
        mapping.Remove(GlMappingKeys.VoucherDate);

        // 2026-10-04 第 8 批 L21：示範來源的「傳票登錄日」改稱「傳票日期」。
        // 仍移除 core voucherDate 配對，避免同一來源重複指派；stable ID 與 round-trip 斷言全部保留。
        // 第一次失敗：20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d。
        var committed = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping,
            amountMode = originalGl.ModeName,
            approvalDateMode = ApprovalDateModeNames.Mapped,
            manualAutoPolicy = new
            {
                manualValues = new[] { "1" },
                automaticValues = new[] { "0" }
            },
            rdeFields = new[]
            {
                new
                {
                    sourceColumn = "傳票日期",
                    label = "傳票日期",
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
                ApprovalDateModeNames.Mapped,
                null,
                new GlManualAutoPolicy(["1"], ["0"]),
                [
                    new GlRdeFieldMetadata(
                        stableId,
                        // 同一 L21 改名同步寫入回存 metadata，不更換欄位身分。
                        "傳票日期",
                        "傳票日期",
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
                host, context.ProjectId, "SELECT COUNT(*) FROM config_gl_rde_field;"));
            Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM target_gl_rde_value;"));

            var restored = await host.DispatchAsync(
                "mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = path }));
            var recommitted = await host.DispatchAsync(
                "mapping.commit.gl", restored.GetProperty("gl").GetRawText());

            Assert.Equal(
                stableId,
                recommitted.GetProperty("rdeFields")[0].GetProperty("fieldId").GetString());
            Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM config_gl_rde_field;"));
            Assert.True(await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM target_gl_rde_value;") > 0);
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
        incomplete.Remove(GlMappingKeys.DocNum);
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

    [Fact]
    public async Task RestoreDraft_WithoutCurrentImports_RejectsBeforeReturningDrafts()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", """
        {
          "projectCode":"P2-NO-IMPORT",
          "entityName":"P2 合成案件",
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
            [GlMappingKeys.DocNum] = "Document",
            [GlMappingKeys.PostDate] = "PostDate",
            [GlMappingKeys.AccNum] = "Account",
            [GlMappingKeys.AccName] = "AccountName",
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
            [TbMappingKeys.AccNum] = "Account",
            [TbMappingKeys.AccName] = "AccountName",
            [TbMappingKeys.Amount] = "Movement"
        },
        TbChangeModeNames.Direct,
        "tb-batch",
        DateTimeOffset.UnixEpoch);
}
