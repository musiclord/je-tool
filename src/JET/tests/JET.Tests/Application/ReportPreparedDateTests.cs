using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 裁定 2026-11-02-03：資料驗證報告與預篩選報告的編製日期，寫按下匯出那一天的本機日期，
/// 和條件篩選報告同一規則（裁定 2026-11-01-04）。原本寫執行資料驗證或預篩選的時間，而且取 UTC 的日期，
/// 台灣早上 8 點以前執行的會寫成前一天。這是新增的測試，改程式前失敗：Focused 收據 20261102-043936871-d640be8b80074a2486fd31b4ffc73a39。
/// </summary>
public sealed class ReportPreparedDateTests
{
    [Theory]
    [InlineData("sqlite")]
    public async Task ValidationAndPrescreenReports_PreparedDateIsTheExportDay_NotWhenTheRunsHappened(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var validation = await host.DispatchAsync("validate.run");
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString();
        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString();

        // 執行時間改到很久以前；準備案件時跑過的其他結果改得更早，這次的結果仍是最新的一筆。報告仍要寫匯出當天的本機日期。
        await ExecuteAsync(host, context.ProjectId, provider,
            $"UPDATE validation_result SET generated_utc = CASE WHEN result_id = '{validationResultId}' " +
            "THEN '2020-01-02T00:00:00.0000000+00:00' ELSE '2019-01-02T00:00:00.0000000+00:00' END;");
        await ExecuteAsync(host, context.ProjectId, provider,
            $"UPDATE prescreen_result SET generated_utc = CASE WHEN result_id = '{prescreenResultId}' " +
            "THEN '2020-01-02T00:00:01.0000000+00:00' ELSE '2019-01-02T00:00:01.0000000+00:00' END;");

        var before = DateTime.Now.Date;
        var validationExport = await host.DispatchAsync("export.validationReports",
            JsonSerializer.Serialize(new { resultId = validationResultId }));
        var prescreenExport = await host.DispatchAsync("export.prescreenReport",
            JsonSerializer.Serialize(new { resultId = prescreenResultId }));
        var after = DateTime.Now.Date;

        var expected = new[] { before, after }
            .Select(day => string.Create(CultureInfo.InvariantCulture, $"Prepared date: {day:yyyy/MM/dd}"))
            .ToArray();
        var validationReport = Assert.Single(validationExport.GetProperty("publishedReports").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "validationReport");
        var paths = new[]
        {
            validationReport.GetProperty("fullPath").GetString()!,
            prescreenExport.GetProperty("publishedReport").GetProperty("fullPath").GetString()!
        };
        foreach (var path in paths)
        {
            using var workbook = new XLWorkbook(path);
            var prepared = workbook.Worksheets
                .SelectMany(sheet => sheet.CellsUsed())
                .Select(cell => cell.GetString())
                .Where(text => text.StartsWith("Prepared date:", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(prepared);
            Assert.All(prepared, text => Assert.Contains(text, expected));
        }
    }

    private static async Task ExecuteAsync(HandlerTestHost host, string projectId, string provider, string sql)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
