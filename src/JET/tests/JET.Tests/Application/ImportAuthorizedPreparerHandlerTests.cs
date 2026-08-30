using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// import.authorizedPreparer.fromFile 黑箱驗收（manifest 細節段）：
/// 單欄姓名 .xlsx、匯入即投影、replace-only、TRIM/空白略過/去重。
/// oracle：自建工作簿的去重後姓名集合 + 獨立 SQL recount。
/// </summary>
public sealed class ImportAuthorizedPreparerHandlerTests
{
    private static string WriteCsv(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "jet-ap-tests", Guid.NewGuid().ToString("N") + ".csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ImportAuthorizedPreparer_SingleColumn_PersistsTrimmedDedupedNames()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        // 8 資料列：含前後空白（TRIM 後同名）、重複、純空白列。去重後應為 3 個姓名。
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "AUTHORIZED_PREPARER";
            ws.Cell(2, 1).Value = "王小明";
            ws.Cell(3, 1).Value = "  王小明  ";
            ws.Cell(4, 1).Value = "李大華";
            ws.Cell(5, 1).Value = "李大華";
            ws.Cell(6, 1).Value = "   ";
            ws.Cell(7, 1).Value = "陳美玲";
            ws.Cell(8, 1).Value = " 陳美玲";
        });

        try
        {
            var data = await host.DispatchAsync("import.authorizedPreparer.fromFile", JsonSerializer.Serialize(new
            {
                filePath = path,
                fileName = "authorized-preparers.xlsx"
            }));

            Assert.Equal(3, data.GetProperty("rowCount").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("batchId").GetString()));
            Assert.Equal("authorized-preparers.xlsx", data.GetProperty("fileName").GetString());

            var targetCount = await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM target_authorized_preparer;");
            Assert.Equal(3, targetCount);

            // TRIM 正規化：落地姓名不含前後空白。
            Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId,
                "SELECT COUNT(*) FROM target_authorized_preparer WHERE name = '王小明';"));
            Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId,
                "SELECT COUNT(*) FROM target_authorized_preparer WHERE name = '  王小明  ';"));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ImportAuthorizedPreparer_ReplaceOnly_SecondImportSupersedesFirst()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var first = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "姓名";
            ws.Cell(2, 1).Value = "甲";
            ws.Cell(3, 1).Value = "乙";
        });
        var second = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "姓名";
            ws.Cell(2, 1).Value = "丙";
        });

        try
        {
            await host.DispatchAsync("import.authorizedPreparer.fromFile",
                JsonSerializer.Serialize(new { filePath = first }));
            await host.DispatchAsync("import.authorizedPreparer.fromFile",
                JsonSerializer.Serialize(new { filePath = second }));

            // replace：第二次匯入整份取代,只剩「丙」。
            Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM target_authorized_preparer;"));
            Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId, "SELECT COUNT(*) FROM target_authorized_preparer WHERE name = '丙';"));
        }
        finally
        {
            TestWorkbookBuilder.Delete(first);
            TestWorkbookBuilder.Delete(second);
        }
    }

    [Fact]
    public async Task ImportAuthorizedPreparer_AppendMode_ThrowsUnsupportedMode()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "姓名";
            ws.Cell(2, 1).Value = "甲";
        });

        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "import.authorizedPreparer.fromFile", JsonSerializer.Serialize(new
                {
                    filePath = path,
                    mode = "append"
                })));

            Assert.Equal("unsupported_mode", ex.Code);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ImportAuthorizedPreparer_CsvExtension_ThrowsUnsupportedFileType()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        var path = WriteCsv("姓名\n甲\n乙\n");

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "import.authorizedPreparer.fromFile", JsonSerializer.Serialize(new { filePath = path })));

        Assert.Equal("unsupported_file_type", ex.Code);
    }

    [Fact]
    public async Task ProjectLoad_AfterAuthorizedPreparerImport_ResumesImportState()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        // 3 個去重後姓名（含空白列、TRIM 後同名）。
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "姓名";
            ws.Cell(2, 1).Value = "甲";
            ws.Cell(3, 1).Value = "乙";
            ws.Cell(4, 1).Value = " 乙 ";
            ws.Cell(5, 1).Value = "   ";
            ws.Cell(6, 1).Value = "丙";
        });

        try
        {
            await host.DispatchAsync("import.authorizedPreparer.fromFile",
                JsonSerializer.Serialize(new { filePath = path }));

            var loaded = await host.DispatchAsync(
                "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

            var state = loaded.GetProperty("importState").GetProperty("authorizedPreparer");
            Assert.Equal(JsonValueKind.Object, state.ValueKind);
            Assert.Equal(3, state.GetProperty("rowCount").GetInt32());

            // 授權清單不持久化 fileName/importedUtc（name 集合，不入 import_batch）。
            Assert.False(state.TryGetProperty("fileName", out _));
            Assert.False(state.TryGetProperty("importedUtc", out _));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ProjectLoad_WithoutAuthorizedPreparerImport_ReturnsNullImportState()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, importAuthorizedPreparer: false);

        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        var state = loaded.GetProperty("importState").GetProperty("authorizedPreparer");
        Assert.Equal(JsonValueKind.Null, state.ValueKind);
    }
}
