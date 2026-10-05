using System.Text;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// .xls 讀取器的儲存格邊界（2026-10-04 第二遍回饋審閱第 1 批，審閱 U16）。
/// oracle：手刻的最小 BIFF2 活頁簿，只含合成值；錯誤儲存格用 BOOLERR 記錄寫入。
/// </summary>
public sealed class BinaryExcelTableReaderTests
{
    [Fact]
    public async Task ErrorCell_KeepsExcelErrorText_LikeXlsx()
    {
        // 以前 #N/A 這類錯誤儲存格被讀成空白、金額變成 0；同一份活頁簿存成 .xlsx 會保留「#N/A」原文並以金額無效報錯。
        var path = Path.Combine(Path.GetTempPath(), $"jet-xls-{Guid.NewGuid():N}.xls");
        WriteBiff2WithErrorCell(path);
        try
        {
            var reader = new BinaryExcelTableReader();
            var rows = new List<StagingRow>();
            await foreach (var row in reader.ReadRowsAsync(new TabularSourceRequest(path), CancellationToken.None))
            {
                rows.Add(row);
            }

            Assert.Equal(2, rows.Count);
            Assert.Equal("#N/A", rows[0].Values["Amount"]);
            Assert.Equal("12.34", rows[1].Values["Amount"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ErrorCellInAmountColumn_FailsMappingExplicitly(string provider)
    {
        using var host = new HandlerTestHost();
        Directory.CreateDirectory(host.ProjectsRoot);
        var path = Path.Combine(host.ProjectsRoot, "synthetic-error.xls");
        WriteBiff2WithErrorCell(path);
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            caseName = "Synthetic XLS error", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        var imported = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
        Assert.Equal(2, imported.GetProperty("rowCount").GetInt32());

        var failure = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            amountMode = "signed",
            mapping = new { docNum = "Document", postDate = "Date", accNum = "Account", accName = "Name", description = "Description", amount = "Amount" }
        })));

        Assert.Equal(JetErrorCodes.ProjectionFailed, failure.Code);
        Assert.Contains("#N/A", failure.Message, StringComparison.Ordinal);
    }

    // 最小 BIFF2 活頁簿：LABEL 記錄寫文字，BOOLERR 記錄寫錯誤儲存格（fError = 1，代碼 0x2A = #N/A）。
    private static void WriteBiff2WithErrorCell(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        void Record(ushort id, Action<BinaryWriter> data)
        {
            using var body = new MemoryStream();
            using (var record = new BinaryWriter(body, Encoding.ASCII, leaveOpen: true)) data(record);
            writer.Write(id); writer.Write((ushort)body.Length); writer.Write(body.ToArray());
        }
        void Label(ushort row, ushort column, string text) => Record(0x0004, w =>
        {
            w.Write(row); w.Write(column); w.Write(new byte[3]);
            var bytes = Encoding.ASCII.GetBytes(text); w.Write((byte)bytes.Length); w.Write(bytes);
        });
        Record(0x0009, w => { w.Write((ushort)0x0002); w.Write((ushort)0x0010); });
        Record(0x0000, w => { w.Write((ushort)0); w.Write((ushort)3); w.Write((ushort)0); w.Write((ushort)6); });
        string[] header = ["Document", "Date", "Account", "Name", "Description", "Amount"];
        for (ushort c = 0; c < header.Length; c++) Label(0, c, header[c]);
        string[] first = ["JV-1", "2025-03-01", "1101", "Cash", "Synthetic debit"];
        for (ushort c = 0; c < first.Length; c++) Label(1, c, first[c]);
        Record(0x0005, w => { w.Write((ushort)1); w.Write((ushort)5); w.Write(new byte[3]); w.Write((byte)0x2A); w.Write((byte)1); });
        string[] second = ["JV-1", "2025-03-01", "4101", "Revenue", "Synthetic credit", "12.34"];
        for (ushort c = 0; c < second.Length; c++) Label(2, c, second[c]);
        Record(0x000A, _ => { });
    }
}
