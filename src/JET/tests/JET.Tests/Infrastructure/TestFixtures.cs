using ClosedXML.Excel;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;

namespace JET.Tests.Infrastructure;

internal static class TestWorkbookBuilder
{
    public static string WriteWorkbook(Action<IXLWorksheet> build)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-fixture-{Guid.NewGuid():N}.xlsx");

        using var workbook = new XLWorkbook();
        build(workbook.AddWorksheet("Sheet1"));
        workbook.SaveAs(path);

        return path;
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>測試專用的 SQLite pool 清理；一律限縮到指定專案，避免干擾平行測試。</summary>
internal static class SqliteTestPool
{
    public static void Clear(string projectsRoot, string projectId)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(projectsRoot));
        using var connection = (SqliteConnection)database.CreateConnection(projectId);
        SqliteConnection.ClearPool(connection);
    }

    public static void ClearAllUnder(string projectsRoot)
    {
        if (!Directory.Exists(projectsRoot))
        {
            return;
        }

        foreach (var databasePath in Directory.EnumerateFiles(
                     projectsRoot,
                     JetProjectFolder.DatabaseFileName,
                     SearchOption.AllDirectories))
        {
            var projectId = Directory.GetParent(databasePath)!.Name;
            Clear(projectsRoot, projectId);
        }
    }
}

/// <summary>temp 專案根目錄；Dispose 時只清理自己根目錄下的 SQLite pools 再遞迴刪除。</summary>
internal sealed class TempProjectRoot : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jet-projects-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteTestPool.ClearAllUnder(Path);

        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
