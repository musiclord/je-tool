using System.Data.Common;
using System.Text;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 2026-10-04 第二遍回饋審閱第 2 批（C3，使用者裁定「要修」）：同一份資料不論從 .csv、.xlsx 或 .xls 匯入、存進 SQLite 或 DuckDB，
/// 人員識別值去掉頭尾空白（含全形空白、不換行空格與 Tab）、只含空白的值當成空白、比對與分組一律不分大小寫，固定答案都相同。
/// 涵蓋審閱 U08、U30、U39、U42、U44、U56、U66 與 L17、L56、L76、L80、L86。Access 以讀取器替身另測（AccessTableReaderTests）。
/// </summary>
public sealed class WhitespaceAndCaseConsistencyTests
{
    public static IEnumerable<object[]> ProviderAndFormat()
    {
        foreach (var provider in new[] { "sqlite", "duckdb" })
        foreach (var format in new[] { "csv", "xlsx", "xls" })
            yield return [provider, format];
    }

    [Theory]
    [MemberData(nameof(ProviderAndFormat))]
    public async Task PreparerIdentifiers_AreTrimmedAndCaseInsensitive_AcrossFormatsAndDatabases(string provider, string format)
    {
        using var host = new HandlerTestHost();
        // .xls 的最小 BIFF2 寫法只放得下 ASCII，所以它的空白變化用半形空白與 Tab；其他格式再加全形空白與不換行空格。
        string[] variants = format == "xls"
            ? ["U1", " U1 ", "u1", "\tU1"]
            : ["U1", " U1　", "u1", " U1"];
        string[] blanks = format == "xls" ? ["   ", "\t"] : ["   ", "　"];
        var rows = new List<string?[]>();
        var n = 0;
        foreach (var variant in variants)
            for (var i = 0; i < 3; i++)
                rows.Add([$"JV-{++n}", "2025-03-01", "1000", "Synthetic A", $"row {n}", "10", variant]);
        rows.Add([$"JV-{++n}", "2025-03-01", "1000", "Synthetic A", $"row {n}", "10", "U2"]);
        foreach (var blank in blanks)
            rows.Add([$"JV-{++n}", "2025-03-01", "1000", "Synthetic A", $"row {n}", "10", blank]);
        await SetupAsync(host, provider, format, rows, tbTotal: 10m * rows.Count);

        Assert.Equal(12, await CountAsync(host, new { type = "entityFrequency", field = "createBy", countUnit = "entries", countOperator = "equals", countFrom = 12 }));
        Assert.Equal(12, await CountAsync(host, new { type = "fieldValue", field = "createBy", @operator = "in", values = new[] { " u1 " } }));
        Assert.Equal(2, await CountAsync(host, new { type = "fieldValue", field = "createBy", @operator = "isBlank" }));
        Assert.Equal(1, await CountAsync(host, new { type = "prescreen", prescreenKey = "lowFrequencyPreparer" }));

        var list = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "員工代碼";
            sheet.Cell(2, 1).Value = " u1 ";
            sheet.Cell(3, 1).Value = "U1";
        });
        try
        {
            var imported = await host.DispatchAsync("import.authorizedPreparer.fromFile",
                JsonSerializer.Serialize(new { filePath = list, sourceColumn = "員工代碼" }));
            Assert.Equal(1, imported.GetProperty("rowCount").GetInt32());
            Assert.Equal(1, imported.GetProperty("duplicateRowCount").GetInt32());
        }
        finally { TestWorkbookBuilder.Delete(list); }
        Assert.Equal(1, await CountAsync(host, new { type = "prescreen", prescreenKey = "nonAuthorizedPreparer" }));

        var prescreen = await host.DispatchAsync("prescreen.run");
        var creators = prescreen.GetProperty("creatorSummary").GetProperty("creators").EnumerateArray()
            .ToDictionary(c => c.GetProperty("createdBy").GetString()!, c => c.GetProperty("entryCount").GetInt64());
        Assert.Equal(12, creators["U1"]);
        Assert.Equal(1, creators["U2"]);
        Assert.Equal(2, creators[""]);
        Assert.Equal(3, creators.Count);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task RowsStoredBeforeThisChange_AreNormalizedTheSameWayInBothDatabases(string provider)
    {
        // 這一批之前匯入的案件，資料庫裡的文字可能還帶著頭尾空白；SQL 端去掉的空白字元要和 .NET 相同，兩個資料庫一樣。
        using var host = new HandlerTestHost();
        var rows = new List<string?[]>
        {
            new string?[] { "JV-1", "2025-03-01", "1000", "Synthetic A", "row 1", "10", "U1" },
            new string?[] { "JV-2", "2025-03-01", "1000", "Synthetic A", "row 2", "10", "U1" },
            new string?[] { "JV-3", "2025-03-01", "1000", "Synthetic A", "row 3", "10", "U1" },
            new string?[] { "JV-4", "2025-03-01", "1000", "Synthetic A", "legacy-a", "10", "U2" },
            new string?[] { "JV-5", "2025-03-01", "1000", "Synthetic A", "legacy-b", "10", "U3" },
            new string?[] { "JV-6", "2025-03-01", "1000", "Synthetic A", "legacy-blank", "10", "U4" },
        };
        var id = await SetupAsync(host, provider, "csv", rows, tbTotal: 60m);
        await UpdateCreatedByAsync(host, provider, id, "legacy-a", " u1　");
        await UpdateCreatedByAsync(host, provider, id, "legacy-b", "\tU1 ");
        await UpdateCreatedByAsync(host, provider, id, "legacy-blank", "　\t");

        Assert.Equal(5, await CountAsync(host, new { type = "fieldValue", field = "createBy", @operator = "in", values = new[] { "U1" } }));
        Assert.Equal(5, await CountAsync(host, new { type = "entityFrequency", field = "createBy", countUnit = "entries", countOperator = "equals", countFrom = 5 }));
        Assert.Equal(1, await CountAsync(host, new { type = "fieldValue", field = "createBy", @operator = "isBlank" }));

        var prescreen = await host.DispatchAsync("prescreen.run");
        var creators = prescreen.GetProperty("creatorSummary").GetProperty("creators").EnumerateArray()
            .ToDictionary(c => c.GetProperty("createdBy").GetString()!, c => c.GetProperty("entryCount").GetInt64());
        Assert.Equal(5, creators["U1"]);
        Assert.Equal(1, creators[""]);
        Assert.Equal(2, creators.Count);
    }

    private static async Task<long> CountAsync(HandlerTestHost host, object rule)
    {
        var scenario = new { name = "Synthetic", rationale = "fixed answer", groups = new[] { new { rules = new[] { rule } } } };
        var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }));
        return preview.GetProperty("scenario").GetProperty("count").GetInt64();
    }

    private static async Task UpdateCreatedByAsync(HandlerTestHost host, string provider, string projectId, string description, string value)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        DbConnection connection = provider == "duckdb"
            ? new DuckDbProjectDatabase(folder).CreateConnection(projectId)
            : new SqliteProjectDatabase(folder).CreateConnection(projectId);
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE target_gl_entry SET created_by = @value WHERE document_description = @description;";
            command.AddWithValue("@value", value);
            command.AddWithValue("@description", description);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
    }

    private static readonly string[] Columns = ["Document", "Date", "Account", "Name", "Description", "Amount", "Creator"];

    private static async Task<string> SetupAsync(HandlerTestHost host, string provider, string format, List<string?[]> rows, decimal tbTotal)
    {
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            caseName = "whitespace-" + format, periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        var id = created.GetProperty("projectId").GetString()!;
        Directory.CreateDirectory(host.ProjectsRoot);
        var glPath = Path.Combine(host.ProjectsRoot, "synthetic-gl." + format);
        WriteGl(glPath, format, rows);
        var tbPath = Path.Combine(host.ProjectsRoot, "synthetic-tb.csv");
        await File.WriteAllTextAsync(tbPath, "Account,Name,Change\n1000,Synthetic A," + tbTotal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glPath }));
        await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tbPath }));
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            amountMode = "signed",
            mapping = new { docNum = "Document", postDate = "Date", accNum = "Account", accName = "Name", description = "Description", amount = "Amount", createBy = "Creator" }
        }));
        await host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
        {
            mapping = new { accNum = "Account", accName = "Name", amount = "Change" }, changeMode = "direct"
        }));
        await host.DispatchAsync("validate.run");
        return id;
    }

    private static void WriteGl(string path, string format, List<string?[]> rows)
    {
        switch (format)
        {
            case "csv":
            {
                var text = new StringBuilder(string.Join(",", Columns) + "\n");
                foreach (var row in rows) text.Append(string.Join(",", row.Select(Quote))).Append('\n');
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
                return;
            }
            case "xlsx":
            {
                var written = TestWorkbookBuilder.WriteWorkbook(sheet =>
                {
                    for (var c = 0; c < Columns.Length; c++) sheet.Cell(1, c + 1).SetValue(Columns[c]);
                    for (var r = 0; r < rows.Count; r++)
                        for (var c = 0; c < rows[r].Length; c++)
                            if (rows[r][c] is { } value) sheet.Cell(r + 2, c + 1).SetValue(value);
                });
                File.Move(written, path, overwrite: true);
                return;
            }
            case "xls":
                WriteBiff2(path, rows);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }
    }

    private static string Quote(string? value) => value is null ? "" : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // 最小 BIFF2 活頁簿，只放 ASCII 文字（和 BinaryExcelImportWorkflowTests 同一寫法）。
    private static void WriteBiff2(string path, List<string?[]> rows)
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
        Record(0x0000, w => { w.Write((ushort)0); w.Write((ushort)(rows.Count + 1)); w.Write((ushort)0); w.Write((ushort)Columns.Length); });
        for (ushort c = 0; c < Columns.Length; c++) Label(0, c, Columns[c]);
        for (var r = 0; r < rows.Count; r++)
            for (ushort c = 0; c < rows[r].Length; c++)
                if (rows[r][c] is { } value) Label((ushort)(r + 1), c, value);
        Record(0x000A, _ => { });
    }
}
