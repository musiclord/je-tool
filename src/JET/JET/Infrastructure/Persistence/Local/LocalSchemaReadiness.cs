using System.Collections.Concurrent;
using System.Globalization;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 記住本程序內哪些本機資料庫檔已完成建表與版本檢查。repository 每次讀寫前呼叫
/// <see cref="ILocalProjectDatabase.EnsureReadyAsync"/>，只有第一次會真的開連線檢查，之後只確認檔案仍在。
/// 建立或載入案件時呼叫的 <see cref="IProjectDatabaseInitializer.EnsureCreatedAsync"/> 一律重新檢查，
/// 刪除案件時清掉紀錄，所以程式開著時用舊的備份檔覆蓋已關閉的案件，下次載入會重新檢查版本並拒絕舊版。
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

    internal void Forget(string databasePath) => ready.TryRemove(databasePath, out _);

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
