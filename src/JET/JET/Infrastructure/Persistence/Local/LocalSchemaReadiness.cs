using System.Collections.Concurrent;
using System.Globalization;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 記住本程序內哪些本機資料庫檔已完成建表與升版檢查。repository 每次讀寫前呼叫
/// <see cref="ILocalProjectDatabase.EnsureReadyAsync"/>，只有第一次會真的開連線檢查，之後只確認檔案仍在。
/// 建立或載入案件時呼叫的 <see cref="IProjectDatabaseInitializer.EnsureCreatedAsync"/> 一律重新檢查，
/// 刪除案件時清掉紀錄，所以程式開著時用備份檔覆蓋已關閉的案件，下次載入仍會升版。
/// 同一個檔案的檢查一次只跑一個，避免兩個作業同時升版。
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

    /// <summary>目前版本能開啟的最舊本機 schema 版本；新資料庫一律從這一版建起再逐版升到現行版。</summary>
    internal const int OldestSupportedSchemaVersion = 6;

    internal const string LegacySchemaMessage =
        "這個案件的資料庫是舊版 JET 建立的格式，目前版本無法開啟。請用目前版本重新建立案件，再重新匯入資料。";

    internal const string UnreadableSchemaVersionMessage =
        "這個案件資料庫的版本資訊無法辨識，目前版本無法開啟。請從備份還原案件資料夾，或用目前版本重新建立案件，再重新匯入資料。";

    /// <summary>
    /// 既有資料庫的 schema 版本低於第 6 版時，是舊版 JET 建立的案件，目前版本不再升版；
    /// 版本值讀不出整數時是檔案損壞，不猜測要從哪一版升級。尚未建表的新資料庫傳入 null，照常建立。
    /// 呼叫端必須在執行任何建表語句之前檢查，讓檔案保持原狀。
    /// </summary>
    internal static void RejectUnsupportedSchemaVersion(string? existingVersion)
    {
        if (existingVersion is null)
        {
            return;
        }

        if (!int.TryParse(existingVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
        {
            throw new JetActionException(JetErrorCodes.InvalidProjectSchema, UnreadableSchemaVersionMessage);
        }

        if (version < OldestSupportedSchemaVersion)
        {
            throw new JetActionException(JetErrorCodes.InvalidProjectSchema, LegacySchemaMessage);
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
