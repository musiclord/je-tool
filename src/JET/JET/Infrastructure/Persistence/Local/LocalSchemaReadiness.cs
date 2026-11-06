using System.Collections.Concurrent;
using System.Globalization;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 記住本程序內哪些本機資料庫檔已完成建表與版本檢查。repository 每次讀寫前呼叫
/// <see cref="ILocalProjectDatabase.EnsureReadyAsync"/>，只有第一次會真的開連線檢查，之後只確認檔案仍在。
/// 建案時呼叫的 <see cref="IProjectDatabaseInitializer.EnsureCreatedAsync"/> 與開案時呼叫的
/// <see cref="IProjectDatabaseInitializer.EnsureExistingAsync"/> 一律重新檢查，
/// 刪除案件時清掉紀錄，所以程式開著時用舊的備份檔覆蓋已關閉的案件，下次載入會重新檢查版本並拒絕舊版。
/// 只有建案會建立新的資料庫；開案與讀寫遇到資料庫不見或沒有內容時回報，不默默建出空的資料庫（裁定 2026-11-05-72）。
/// 同一個檔案的檢查一次只跑一個，避免兩個作業同時建表。
/// </summary>
internal sealed class LocalSchemaReadiness
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> ready = new(StringComparer.OrdinalIgnoreCase);

    internal Task EnsureReadyAsync(
        string databasePath,
        Func<CancellationToken, Task> ensureCreated,
        CancellationToken cancellationToken) =>
        IsReady(databasePath)
            ? Task.CompletedTask
            : RunAsync(databasePath, ensureCreated, force: false, cancellationToken);

    internal Task EnsureCreatedAsync(
        string databasePath,
        Func<CancellationToken, Task> ensureCreated,
        CancellationToken cancellationToken) =>
        RunAsync(databasePath, ensureCreated, force: true, cancellationToken);

    /// <summary>
    /// 開啟既有案件時呼叫，一律重新檢查。建案中的標記還在，表示上一次建案在完成前中斷，審計員沒有拿到這個案件，
    /// 資料庫裡不會有資料：照建案的做法把初始化做完，再刪掉標記。標記不在時，資料庫一定要已經建好，
    /// 不建立新的資料庫（裁定 2026-11-05-72）。<paramref name="ensureCreated"/> 的第一個參數是可不可以建立資料庫。
    /// </summary>
    internal async Task EnsureExistingAsync(
        string databasePath,
        string creationMarkerPath,
        Func<bool, CancellationToken, Task> ensureCreated,
        CancellationToken cancellationToken)
    {
        var finishCreation = File.Exists(creationMarkerPath);
        await RunAsync(databasePath, ct => ensureCreated(finishCreation, ct), force: true, cancellationToken)
            .ConfigureAwait(false);
        if (!finishCreation)
        {
            return;
        }

        try
        {
            File.Delete(creationMarkerPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 資料庫已經建好；標記刪不掉時，下次開案再確認一次。
        }
    }

    internal void Forget(string databasePath) => ready.TryRemove(databasePath, out _);

    /// <summary>
    /// 開案與讀寫前確認資料庫檔還在而且不是 0 位元組，在開連線之前檢查，讓連線不會替缺檔的案件建出新檔。
    /// </summary>
    internal static void RequireDatabaseFile(string projectId, string databasePath)
    {
        var file = new FileInfo(databasePath);
        if (!file.Exists)
        {
            throw new JetActionException(JetErrorCodes.FileReadError, ProjectFolderMessages.DatabaseMissing(projectId));
        }

        if (file.Length == 0)
        {
            throw EmptyDatabase(projectId);
        }
    }

    /// <summary>資料庫檔是 0 位元組，或有檔案但沒有任何資料表。</summary>
    internal static JetActionException EmptyDatabase(string projectId) =>
        new(JetErrorCodes.FileReadError, ProjectFolderMessages.DatabaseEmpty(projectId));

    internal static bool IsEmptyFile(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length == 0;
    }

    /// <summary>SQLite 與 DuckDB 案件資料庫的現行結構版本；兩邊的建表程式都直接寫這一版。</summary>
    internal const int CurrentSchemaVersion = 20;

    /// <summary>
    /// 已經建過表的資料庫只接受現行版本（裁定 2026-10-30-23：案件資料庫只留現行結構，版本不符時請審計員刪除後重建）。
    /// 低於現行版本是舊版 JET 建立的案件；版本值缺漏、讀不出整數或高於現行版本時無法判斷內容，當成版本資訊無法辨識。
    /// 呼叫端必須在執行任何寫入之前檢查，讓檔案保持原狀。
    /// </summary>
    internal static void RejectUnsupportedSchemaVersion(string? existingVersion)
    {
        if (!int.TryParse(existingVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            || version > CurrentSchemaVersion)
        {
            throw new JetActionException(JetErrorCodes.InvalidProjectSchema, ProjectFolderMessages.UnreadableDatabaseVersion);
        }

        if (version < CurrentSchemaVersion)
        {
            throw new JetActionException(JetErrorCodes.InvalidProjectSchema, ProjectFolderMessages.OldVersionProject());
        }
    }

    private bool IsReady(string databasePath) =>
        ready.ContainsKey(databasePath) && File.Exists(databasePath);

    private async Task RunAsync(
        string databasePath,
        Func<CancellationToken, Task> ensureCreated,
        bool force,
        CancellationToken cancellationToken)
    {
        var gate = gates.GetOrAdd(databasePath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && IsReady(databasePath))
            {
                return;
            }

            ready.TryRemove(databasePath, out _);
            await ensureCreated(cancellationToken).ConfigureAwait(false);
            ready[databasePath] = 0;
        }
        finally
        {
            gate.Release();
        }
    }
}
