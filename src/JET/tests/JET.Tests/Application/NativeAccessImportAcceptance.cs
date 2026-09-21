using System.Data.OleDb;
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
            Assert.Equal(sheetName, Assert.Single(inspection.GetProperty("worksheets").EnumerateArray()).GetProperty("name").GetString());
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
            await host.DispatchAsync("import.gl.fromFile", source);
            await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
            {
                amountMode = "signed", mapping = new { docNum = "Document", postDate = "Post Date", accNum = "Account",
                    accName = "Name", description = "Description", amount = "Amount", createBy = "Creator" }
            }));
            var validation = await host.DispatchAsync("validate.run");
            Assert.Equal(0, validation.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());
            var filtered = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new
            {
                scenario = new { groups = new[] { new { rules = new[] { new { type = "customKeywords", keywords = "合成" } } } } }
            }));
            Assert.Equal(2, filtered.GetProperty("scenario").GetProperty("count").GetInt64());
            await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            completed.Add(new { format = extension, provider, inspected = true, previewed = true, importedRows = 2,
                retried = true, validated = true, filtered = true, reopened = true, sourceUnchanged = true });
        }
        await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "native-source-import-verification.json"),
            JsonSerializer.Serialize(new { status = "passed", cases = completed }));
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
