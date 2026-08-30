using System.Diagnostics;
using System.Text;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 匯入規模煙霧測試（明確標註 smoke/scale，FIRST-Fast 豁免）。
/// 斷言只鎖正確性（列數、欄位、首尾列身分）；耗時以 ITestOutputHelper 記錄，
/// 不做時間斷言（wall-clock 斷言必然 flaky）。量測值供 140 萬列外插與
/// development-log 的調校決策記錄。
/// </summary>
public sealed class ImportScaleSmokeTests(ITestOutputHelper output)
{
    private const int RowCount = 100_000;

    [Fact]
    public async Task EnsureCreated_SetsWalJournalMode()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Replace100KRowXlsx_StreamsThroughSaxReaderIntoStaging()
    {
        // 10 萬列混合型別（共用字串、數值、日期樣式、整數序號）；
        // 外插：140 萬列 ≈ 14 × 本測試的匯入耗時
        var buildWatch = Stopwatch.StartNew();
        var path = BuildLargeFixture(RowCount);
        buildWatch.Stop();

        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var repository = new LocalImportRepository(database);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        try
        {
            var reader = new OpenXmlSaxTableReader();
            var request = new TabularSourceRequest(path);

            var importWatch = Stopwatch.StartNew();
            var columns = await reader.ReadColumnsAsync(request, CancellationToken.None);
            var result = await repository.ReplaceBatchAsync(
                projectId,
                DatasetKind.Gl,
                new ImportSourceDescriptor(path, "scale.xlsx", null, null, null),
                columns,
                reader.ReadRowsAsync(request, CancellationToken.None),
                CancellationToken.None);
            importWatch.Stop();

            output.WriteLine($"fixture 產生 {buildWatch.ElapsedMilliseconds} ms；" +
                $"匯入 {RowCount:N0} 列耗時 {importWatch.ElapsedMilliseconds} ms" +
                $"（{RowCount * 1000L / Math.Max(1, importWatch.ElapsedMilliseconds):N0} 列/秒）");

            Assert.Equal(RowCount, result.Batch.RowCount);
            Assert.Equal(["傳票號碼", "金額", "過帳日期", "序號"], result.Batch.Columns);

            // 首尾列身分 spot-check：值與來源列號都不能歪
            await using var connection = database.CreateConnection(projectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT row_json FROM staging_gl_raw_row WHERE source_row_number = 2
                UNION ALL
                SELECT row_json FROM staging_gl_raw_row WHERE source_row_number = @last;
                """;
            command.AddWithValue("@last", RowCount + 1);

            var jsons = new List<string>();
            await using var dataReader = await command.ExecuteReaderAsync();
            while (await dataReader.ReadAsync())
            {
                jsons.Add(dataReader.GetString(0));
            }

            Assert.Equal(2, jsons.Count);
            Assert.Contains("\"序號\":\"1\"", jsons[0]);
            Assert.Contains("\"過帳日期\":\"2024-01-01\"", jsons[0]); // 日期樣式經 SAX 轉 ISO
            Assert.Contains($"\"序號\":\"{RowCount}\"", jsons[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string BuildLargeFixture(int rowCount)
    {
        // 手刻 sheetData：RawXlsxBuilder 直寫 XML 比 ClosedXML DOM 快兩個數量級
        var sheetData = new StringBuilder(rowCount * 96);
        sheetData.Append(
            """
            <sheetData>
            <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c><c r="D1" t="s"><v>3</v></c></row>
            """);

        for (var i = 1; i <= rowCount; i++)
        {
            var rowNumber = i + 1;
            var sharedIndex = 4 + (i % 8); // 8 個傳票字串循環
            sheetData.Append("<row r=\"").Append(rowNumber)
                .Append("\"><c r=\"A").Append(rowNumber).Append("\" t=\"s\"><v>").Append(sharedIndex)
                .Append("</v></c><c r=\"B").Append(rowNumber).Append("\"><v>").Append(i).Append(".25")
                .Append("</v></c><c r=\"C").Append(rowNumber).Append("\" s=\"1\"><v>45292</v></c><c r=\"D")
                .Append(rowNumber).Append("\"><v>").Append(i)
                .Append("</v></c></row>");
        }

        sheetData.Append("</sheetData>");

        var sharedStrings = new StringBuilder("<si><t>傳票號碼</t></si><si><t>金額</t></si><si><t>過帳日期</t></si><si><t>序號</t></si>");
        for (var i = 0; i < 8; i++)
        {
            sharedStrings.Append($"<si><t>V-2025-{i:D4}</t></si>");
        }

        return new RawXlsxBuilder()
            .WithSharedStrings(sharedStrings.ToString())
            .WithStyles("""<cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/></cellXfs>""")
            .AddSheet("Sheet1", sheetData.ToString())
            .Save();
    }
}
