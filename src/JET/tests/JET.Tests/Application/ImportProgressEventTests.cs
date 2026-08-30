using System.Text;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// import.progress 事件（manifest「Host→Web 事件」章節）。
/// 節奏以 WithProgress 迭代器直測（不經 WebView）；wire payload 經 HandlerTestHost
/// 的 recording publisher 以 wire shape 斷言。oracle：manifest 事件規格（每 20,000 列一次）。
/// </summary>
public sealed class ImportProgressEventTests
{
    private static async IAsyncEnumerable<StagingRow> RowsOf(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            yield return new StagingRow(i + 1, new Dictionary<string, string> { ["a"] = i.ToString() });
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task WithProgress_ReportsAtEveryInterval_PassesRowsThrough()
    {
        var reports = new List<int>();
        var passed = 0;

        // 45 列、間隔 20：回報於 20、40（結尾不補發——完成以 action response 為準）
        await foreach (var _ in ImportFromFileHandler.WithProgress(RowsOf(45), interval: 20, reports.Add))
        {
            passed++;
        }

        Assert.Equal(45, passed);
        Assert.Equal([20, 40], reports);
    }

    [Fact]
    public async Task WithProgress_BelowInterval_ReportsNothing()
    {
        var reports = new List<int>();

        await foreach (var _ in ImportFromFileHandler.WithProgress(RowsOf(19), interval: 20, reports.Add))
        {
        }

        Assert.Empty(reports);
    }

    [Fact]
    public async Task ImportGl_SmallFile_NoProgressEvents_ResponseUnchanged()
    {
        using var host = new HandlerTestHost();
        await CreateProjectAsync(host);

        var path = JET.Tests.Infrastructure.TestCsvBuilder.WriteFile(
            "doc,amt\nD1,100\nD2,200\n", JET.Tests.Infrastructure.TestCsvBuilder.Utf8Bom, ".csv");

        try
        {
            var data = await host.DispatchAsync(
                "import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));

            Assert.Equal(2, data.GetProperty("rowCount").GetInt32());
            Assert.Empty(host.PublishedEvents); // 低於 20,000 列門檻 → 零事件
        }
        finally
        {
            JET.Tests.Infrastructure.TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ImportGl_LargeCsv_PublishesProgressWithWireShape()
    {
        using var host = new HandlerTestHost();
        await CreateProjectAsync(host);

        // 25,000 列 → 恰好一次 import.progress（rowsRead=20000）；payload 形狀依 manifest
        var csv = new StringBuilder("doc,amt\n");
        for (var i = 1; i <= 25_000; i++)
        {
            csv.Append("D").Append(i).Append(",1\n");
        }

        var path = JET.Tests.Infrastructure.TestCsvBuilder.WriteFile(
            csv.ToString(), JET.Tests.Infrastructure.TestCsvBuilder.Utf8Bom, ".csv");

        try
        {
            var data = await host.DispatchAsync(
                "import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            Assert.Equal(25_000, data.GetProperty("rowCount").GetInt32());

            var (eventName, payload) = Assert.Single(host.PublishedEvents);
            Assert.Equal("import.progress", eventName);

            var json = JsonDocument.Parse(
                JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;
            Assert.Equal("gl", json.GetProperty("kind").GetString());
            Assert.Equal(1, json.GetProperty("sourceNo").GetInt32());
            Assert.Equal(1, json.GetProperty("sourceCount").GetInt32());
            Assert.Equal(Path.GetFileName(path), json.GetProperty("fileName").GetString());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("sheetName").ValueKind); // CSV 無工作表
            Assert.Equal(20_000, json.GetProperty("rowsRead").GetInt32());
        }
        finally
        {
            JET.Tests.Infrastructure.TestCsvBuilder.Delete(path);
        }
    }

    private static async Task CreateProjectAsync(HandlerTestHost host)
    {
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "T-001",
            entityName = "測試公司",
            operatorId = "op",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31"
        }));
    }
}
