using System.Text;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

public sealed class BinaryExcelImportWorkflowTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task BiffWorkbook_InspectPreviewImportMapValidateAndReopen(string provider)
    {
        using var host = new HandlerTestHost();
        var root = Path.Combine(host.ProjectsRoot, "source");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "synthetic.xls");
        WriteBiff2(path);
        var original = await File.ReadAllBytesAsync(path);
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            caseName = "Synthetic XLS", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        var projectId = created.GetProperty("projectId").GetString();
        var inspection = await host.DispatchAsync("import.inspectFile", JsonSerializer.Serialize(new { filePath = path }));
        Assert.Equal("xls", inspection.GetProperty("fileType").GetString());
        var sheet = inspection.GetProperty("worksheets")[0].GetProperty("name").GetString();
        var preview = await host.DispatchAsync("import.previewFile", JsonSerializer.Serialize(new { filePath = path, sheetName = sheet }));
        Assert.Equal(2, preview.GetProperty("sampleRows").GetArrayLength());
        var imported = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path, sheetName = sheet }));
        Assert.Equal(2, imported.GetProperty("rowCount").GetInt32());
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            amountMode = "signed", mapping = new { docNum = "Document", postDate = "Date", accNum = "Account",
                accName = "Name", description = "Description", amount = "Amount" }
        }));
        var validation = await host.DispatchAsync("validate.run");
        Assert.Equal(0, validation.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(2, loaded.GetProperty("importState").GetProperty("gl").GetProperty("rowCount").GetInt32());
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    // Minimal BIFF2 workbook made solely from synthetic values; no Office or external fixture is needed.
    private static void WriteBiff2(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        void Record(ushort id, Action<BinaryWriter> data)
        {
            using var body = new MemoryStream();
            using (var record = new BinaryWriter(body, Encoding.ASCII, leaveOpen: true)) data(record);
            writer.Write(id); writer.Write((ushort)body.Length); writer.Write(body.ToArray());
        }
        Record(0x0009, w => { w.Write((ushort)0x0002); w.Write((ushort)0x0010); });
        Record(0x0000, w => { w.Write((ushort)0); w.Write((ushort)3); w.Write((ushort)0); w.Write((ushort)6); });
        string[][] rows = [
            ["Document", "Date", "Account", "Name", "Description", "Amount"],
            ["JV-1", "2025-03-01", "1101", "Cash", "Synthetic debit", "12.34"],
            ["JV-1", "2025-03-01", "4101", "Revenue", "Synthetic credit", "-12.34"]
        ];
        for (ushort r = 0; r < rows.Length; r++)
        for (ushort c = 0; c < rows[r].Length; c++)
        {
            var text = rows[r][c];
            Record(0x0004, w => {
                w.Write(r); w.Write(c); w.Write(new byte[3]);
                var bytes = Encoding.ASCII.GetBytes(text); w.Write((byte)bytes.Length); w.Write(bytes);
            });
        }
        Record(0x000A, _ => { });
    }
}
