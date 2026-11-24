using ClosedXML.Excel;
using JET.Domain;
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

/// <summary>
/// JET 存完案件設定檔就把它設成唯讀。測試要模擬審計員改設定檔時，照審計員的做法先取消唯讀再寫入；
/// 清掉暫存的案件位置前，也先取消唯讀，Directory.Delete 才刪得掉。
/// </summary>
internal static class ProjectJsonEdits
{
    // 不進入連結指向的資料夾，以免改到暫存資料夾以外的檔案。
    private static readonly EnumerationOptions WithinRoot = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true
    };

    public static void WriteAllText(string path, string contents)
    {
        ReleaseReadOnly(path);
        File.WriteAllText(path, contents);
    }

    public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        ReleaseReadOnly(path);
        return File.WriteAllTextAsync(path, contents, cancellationToken);
    }

    public static void ReleaseReadOnly(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    public static void ReleaseAllUnder(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, JetProjectFolder.ProjectJsonFileName, WithinRoot))
        {
            ReleaseReadOnly(path);
        }
    }
}

/// <summary>
/// 測試要固定隨機種子或民國年選項這類建案後不能改的設定時，同時改案件設定檔與案件資料庫記的設定，
/// 讓開啟中的案件與重新開案都用改過的值。畫面做不到這件事；資料庫用觸發器擋下修改，
/// 這裡在同一個交易裡先移除觸發器，改完再照原本的定義建回去。
/// </summary>
internal static class ProtectedSettingEdits
{
    public static async Task ApplyAsync(string projectsRoot, string projectId, Func<ProjectDocument, ProjectDocument> change)
    {
        var folder = new JetProjectFolder(projectsRoot);
        var store = new JsonFileProjectStore(folder);
        var document = await store.FindAsync(projectId, CancellationToken.None)
            ?? throw new InvalidOperationException($"Project '{projectId}' was not found.");
        var changed = change(document);
        await store.SaveAsync(changed, CancellationToken.None);

        var settings = ProtectedProjectSettings.From(changed);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = folder.GetDatabasePath(projectId),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var triggers = new List<(string Name, string Sql)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'project_setting';";
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                triggers.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (name, _) in triggers)
        {
            await ExecuteAsync(connection, transaction, $"DROP TRIGGER \"{name}\";");
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE project_setting
                SET period_start = $periodStart, period_end = $periodEnd, money_scale = $moneyScale,
                    roc_date_enabled = $rocDateEnabled, sample_seed = $sampleSeed, sample_seed_version = $sampleSeedVersion,
                    operator_id = $operatorId, last_accounting_period_date = $lastAccountingPeriodDate,
                    non_working_days = $nonWorkingDays
                WHERE singleton = 1;
                """;
            update.Parameters.AddWithValue("$periodStart", settings.PeriodStart);
            update.Parameters.AddWithValue("$periodEnd", settings.PeriodEnd);
            update.Parameters.AddWithValue("$moneyScale", settings.MoneyScale);
            update.Parameters.AddWithValue("$rocDateEnabled", settings.RocDateEnabled ? 1 : 0);
            update.Parameters.AddWithValue("$sampleSeed", settings.SampleSeed);
            update.Parameters.AddWithValue("$sampleSeedVersion", settings.SampleSeedVersion);
            update.Parameters.AddWithValue("$operatorId", settings.OperatorId);
            update.Parameters.AddWithValue("$lastAccountingPeriodDate", (object?)settings.LastAccountingPeriodDate ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "$nonWorkingDays",
                settings.NonWorkingDays is null ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(settings.NonWorkingDays));
            if (await update.ExecuteNonQueryAsync() != 1)
            {
                throw new InvalidOperationException("The case database has no project settings row.");
            }
        }

        foreach (var (_, sql) in triggers)
        {
            await ExecuteAsync(connection, transaction, sql);
        }

        await transaction.CommitAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>temp 專案根目錄；Dispose 時只清理自己根目錄下的 SQLite pools，取消案件設定檔的唯讀，再遞迴刪除。</summary>
internal sealed class TempProjectRoot : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jet-projects-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteTestPool.ClearAllUnder(Path);
        ProjectJsonEdits.ReleaseAllUnder(Path);

        if (Directory.Exists(Path))
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 只把當下開著剩下檔案的程序附進訊息，照樣失敗、不重試；原因見 FileHolderDiagnostics。
                var message = exception.Message + "\n" + FileHolderDiagnostics.Describe(Path);
                throw exception is IOException
                    ? new IOException(message, exception)
                    : new UnauthorizedAccessException(message, exception);
            }
        }
    }
}
