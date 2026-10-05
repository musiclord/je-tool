using System.Data.OleDb;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Explicit Excel verification only: use the installed Office engine with generated Access files.
/// This is not a public test that silently returns when Office is absent.
/// </summary>
internal static class NativeAccessImportAcceptance
{
    private static readonly string LongDescription = new('合', 600);

    internal static async Task VerifyAsync(string evidenceDirectory)
    {
        var completed = new List<object>();
        foreach (var extension in new[] { ".mdb", ".accdb", ".xls" })
        foreach (var provider in new[] { "sqlite", "duckdb" })
        {
            using var host = new HandlerTestHost();
            var sourceDirectory = Path.Combine(host.ProjectsRoot, "office-source");
            Directory.CreateDirectory(sourceDirectory);
            var path = Path.Combine(sourceDirectory, "synthetic" + extension);
            CreateSource(path, extension);
            if (extension != ".xls") CreateAdditionalAccessTables(path);
            var original = await File.ReadAllBytesAsync(path);
            var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
            {
                caseName = "Synthetic Access", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
            }));
            var projectId = created.GetProperty("projectId").GetString();
            var inspection = await host.DispatchAsync("import.inspectFile", JsonSerializer.Serialize(new { filePath = path }));
            Assert.Equal(extension == ".xls" ? "xls" : "access", inspection.GetProperty("fileType").GetString());
            // ACE 建立 Excel 工作表時將空格改成底線；Access 資料表保留空格。
            var sheetName = extension == ".xls" ? "GL_data" : "GL data";
            if (extension == ".xls")
                Assert.Equal(sheetName, Assert.Single(inspection.GetProperty("worksheets").EnumerateArray()).GetProperty("name").GetString());
            else
                // 新合成資料包含五個一般資料表和一個儲存查詢；儲存查詢不能混入可選來源。
                Assert.Equal(["Binary data", "Empty data", "GL data", "TB data", "Text data"],
                    inspection.GetProperty("worksheets").EnumerateArray().Select(table => table.GetProperty("name").GetString()!).ToArray());
            var source = JsonSerializer.Serialize(new { filePath = path, sheetName });
            var preview = await host.DispatchAsync("import.previewFile", source);
            Assert.Equal(2, preview.GetProperty("sampleRows").GetArrayLength());
            Assert.Equal("001", preview.GetProperty("sampleRows")[0][6].GetString());
            var imported = await host.DispatchAsync("import.gl.fromFile", source);
            Assert.Equal(2, imported.GetProperty("rowCount").GetInt32());
            var missing = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("import.gl.fromFile",
                JsonSerializer.Serialize(new { filePath = path, sheetName = "Missing table" })));
            Assert.Equal(JetErrorCodes.SheetNotFound, missing.Code);
            var beforeRetry = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
            Assert.Equal(2, beforeRetry.GetProperty("importState").GetProperty("gl").GetProperty("rowCount").GetInt32());
            if (extension != ".xls") await VerifyAccessSourceBoundariesAsync(host, projectId!, path);
            await host.DispatchAsync("import.gl.fromFile", source);
            await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
            {
                amountMode = "signed", mapping = new { docNum = "Document", postDate = "Post Date", accNum = "Account",
                    accName = "Name", description = "Description", amount = "Amount", createBy = "Creator" }
            }));
            if (extension != ".xls")
            {
                var tb = await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = path, sheetName = "TB data" }));
                Assert.Equal(2, tb.GetProperty("rowCount").GetInt32());
                await host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
                {
                    changeMode = "direct", mapping = new { accNum = "Account", accName = "Name", amount = "Movement" }
                }));
            }
            var validation = await host.DispatchAsync("validate.run");
            Assert.Equal(0, validation.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());
            if (extension != ".xls")
            {
                // GL 和 TB 各有兩個科目，變動金額均為 12.34 與 -12.34；逐科目無差異。
                var completeness = validation.GetProperty("completenessTest");
                Assert.Equal(0, completeness.GetProperty("diffAccountCount").GetInt64());
                Assert.Equal(JsonValueKind.Null, completeness.GetProperty("naReason").ValueKind);
                Assert.True(completeness.GetProperty("partA").GetProperty("rowCountMatch").GetBoolean());
                Assert.True(completeness.GetProperty("partA").GetProperty("amountMatch").GetBoolean());
            }
            var filtered = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new
            {
                scenario = new { groups = new[] { new { rules = new[] { new { type = "customKeywords", keywords = "合成" } } } } }
            }));
            Assert.Equal(2, filtered.GetProperty("scenario").GetProperty("count").GetInt64());
            await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            completed.Add(new { format = extension, provider, inspected = true, previewed = true, importedRows = 2,
                retried = true, validated = true, filtered = true, reopened = true, sourceUnchanged = true,
                accessBoundaries = extension == ".xls" ? null : new
                {
                    ordinaryTables = 5, savedQueryExcluded = true, previewRows = 10, completeTextRows = 12,
                    longTextCharacters = 600, nullPreserved = true, binaryRejected = true, emptyImportRetainsBatch = true,
                    cancelledImportRetainsBatch = true, tbImportedAndMapped = true, completenessDifferences = 0
                } });
        }
        await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "native-source-import-verification.json"),
            JsonSerializer.Serialize(new { status = "passed", cases = completed }));
    }

    private static async Task VerifyAccessSourceBoundariesAsync(HandlerTestHost host, string projectId, string path)
    {
        var textSource = JsonSerializer.Serialize(new { filePath = path, sheetName = "Text data" });
        var preview = await host.DispatchAsync("import.previewFile", textSource);
        var previewRows = preview.GetProperty("sampleRows").EnumerateArray().ToArray();
        Assert.Equal(10, previewRows.Length);
        Assert.All(previewRows, row =>
        {
            Assert.Equal(LongDescription, row[1].GetString());
            Assert.Equal(JsonValueKind.Null, row[2].ValueKind);
            // 第1批L09已裁定Access布林統一true/false；原生Excel首敗20261004-114941125-6735047433f74b95b10e90c4620b93bd只更新此固定預期。
            Assert.Equal("true", row[3].GetString());
        });
        var full = await host.DispatchAsync("import.gl.fromFile", textSource);
        Assert.Equal(12, full.GetProperty("rowCount").GetInt32());
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path, sheetName = "GL data" }));

        foreach (var (table, expectedCode) in new[]
        {
            ("Empty data", JetErrorCodes.EmptyWorkbook), ("Binary data", JetErrorCodes.FileReadError),
            ("Saved query", JetErrorCodes.SheetNotFound)
        })
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("import.gl.fromFile",
                JsonSerializer.Serialize(new { filePath = path, sheetName = table })));
            Assert.Equal(expectedCode, error.Code);
            var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
            Assert.Equal(2, loaded.GetProperty("importState").GetProperty("gl").GetProperty("rowCount").GetInt32());
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.DispatchAsync("import.gl.fromFile", textSource, cancellation.Token));
        var afterCancel = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(2, afterCancel.GetProperty("importState").GetProperty("gl").GetProperty("rowCount").GetInt32());
    }

    private static void CreateAdditionalAccessTables(string path)
    {
        using var connection = new OleDbConnection(new OleDbConnectionStringBuilder
        {
            Provider = "Microsoft.ACE.OLEDB.16.0", DataSource = path
        }.ConnectionString);
        connection.Open();
        foreach (var sql in new[]
        {
            "CREATE TABLE [TB data] ([Account] TEXT(20), [Name] TEXT(100), [Movement] CURRENCY)",
            "CREATE TABLE [Empty data] ([Account] TEXT(20))",
            "CREATE TABLE [Text data] ([Identifier] TEXT(20), [Description] LONGTEXT, [Optional value] TEXT(20), [Manual] BIT)",
            "CREATE TABLE [Binary data] ([Identifier] TEXT(20), [Payload] BINARY(8))",
            "CREATE VIEW [Saved query] AS SELECT * FROM [GL data]"
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        foreach (var (account, name, amount) in new[] { ("1101", "現金", 12.34m), ("4101", "收入", -12.34m) })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO [TB data] VALUES (?, ?, ?)";
            command.Parameters.AddWithValue("account", account);
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("amount", amount);
            command.ExecuteNonQuery();
        }
        for (var i = 1; i <= 12; i++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO [Text data] VALUES (?, ?, ?, ?)";
            command.Parameters.AddWithValue("identifier", i.ToString("D3", CultureInfo.InvariantCulture));
            command.Parameters.Add("description", OleDbType.LongVarWChar, LongDescription.Length).Value = LongDescription;
            command.Parameters.Add("optional", OleDbType.VarWChar, 20).Value = DBNull.Value;
            command.Parameters.AddWithValue("manual", true);
            command.ExecuteNonQuery();
        }
        using var binary = connection.CreateCommand();
        binary.CommandText = "INSERT INTO [Binary data] VALUES (?, ?)";
        binary.Parameters.AddWithValue("identifier", "001");
        binary.Parameters.Add("payload", OleDbType.Binary).Value = new byte[] { 1, 2, 3 };
        binary.ExecuteNonQuery();
    }

    private static void CreateSource(string path, string extension)
    {
        var builder = new OleDbConnectionStringBuilder
        {
            Provider = "Microsoft.ACE.OLEDB.16.0", DataSource = path
        };
        if (extension == ".xls") builder["Extended Properties"] = "Excel 8.0;HDR=YES";
        else CreateAccessContainer(builder, extension);
        builder["OLE DB Services"] = -2;
        using var connection = new OleDbConnection(builder.ConnectionString);
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE [GL data] ([Document] TEXT(50), [Post Date] DATETIME, [Account] TEXT(20), " +
            "[Name] TEXT(100), [Description] TEXT(255), [Amount] CURRENCY, [Creator] TEXT(20))";
        create.ExecuteNonQuery();
        foreach (var (account, name, amount) in new[] { ("1101", "現金", 12.34m), ("4101", "收入", -12.34m) })
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO [GL data] VALUES (?, ?, ?, ?, ?, ?, ?)";
            insert.Parameters.AddWithValue("doc", "NATIVE-1");
            insert.Parameters.AddWithValue("date", new DateTime(2025, 3, 1));
            insert.Parameters.AddWithValue("account", account);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("description", "合成來源測試");
            insert.Parameters.AddWithValue("amount", amount);
            insert.Parameters.AddWithValue("creator", "001");
            insert.ExecuteNonQuery();
        }
    }

    private static void CreateAccessContainer(OleDbConnectionStringBuilder builder, string extension)
    {
        if (extension == ".mdb") builder["Jet OLEDB:Engine Type"] = 5;
        var catalogType = Type.GetTypeFromProgID("ADOX.Catalog", throwOnError: true)!;
        object? catalog = null;
        object? active = null;
        try
        {
            catalog = Activator.CreateInstance(catalogType)!;
            ((dynamic)catalog).Create(builder.ConnectionString);
            active = ((dynamic)catalog).ActiveConnection;
            ((dynamic)active).Close();
        }
        finally
        {
            if (active is not null && Marshal.IsComObject(active)) Marshal.FinalReleaseComObject(active);
            if (catalog is not null && Marshal.IsComObject(catalog)) Marshal.FinalReleaseComObject(catalog);
        }
        builder.Remove("Jet OLEDB:Engine Type");
    }
}
