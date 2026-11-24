using System.Data.Common;
using System.Globalization;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 案件資料庫的一步升版：把第 <paramref name="FromVersion"/> 版的資料庫改成下一版。
/// 步驟在升版的交易裡執行，只改結構與既有資料，不提交也不復原交易。
/// 交易裡無效或不允許的操作（例如切換外鍵檢查、VACUUM）不能放進步驟。
/// </summary>
internal sealed record ProjectSchemaUpgrade(
    int FromVersion,
    Func<DbConnection, DbTransaction, CancellationToken, Task> ApplyAsync)
{
    /// <summary>只需要執行 SQL 的步驟。</summary>
    internal static ProjectSchemaUpgrade Sql(int fromVersion, string sql) =>
        new(fromVersion, async (connection, transaction, cancellationToken) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        });
}

/// <summary>
/// 案件資料庫支援哪些版本，以及較早的版本怎麼升到現行版本。
/// <see cref="OldestUpgradable"/> 是首次發布的版本，之後不再提高：這一版起建立的案件，後續版本的 JET 都要能開啟。
/// 資料庫結構每改一次，現行版本加一，並在步驟清單最後加一步，把前一版的資料庫改成新的結構；
/// 建表程式同時改成新結構，新案件直接建成現行版本。步驟清單要從最早可升級的版本連續排到現行版本的前一版。
/// 版本比 <see cref="OldestUpgradable"/> 還早的資料庫是發布前建立的，不升級；比現行版本新的資料庫由較新版本的 JET 寫過，不開啟。
/// </summary>
internal sealed class ProjectSchemaVersions
{
    private readonly IReadOnlyList<ProjectSchemaUpgrade> steps;

    internal ProjectSchemaVersions(int oldestUpgradable, int current, IReadOnlyList<ProjectSchemaUpgrade> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (current < oldestUpgradable || steps.Count != current - oldestUpgradable)
        {
            throw new InvalidOperationException(
                $"Schema versions {oldestUpgradable} to {current} need exactly {Math.Max(0, current - oldestUpgradable)} upgrade steps, one per version.");
        }

        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index].FromVersion != oldestUpgradable + index)
            {
                throw new InvalidOperationException(
                    $"Upgrade step {index + 1} must upgrade schema version {oldestUpgradable + index}, not {steps[index].FromVersion}.");
            }
        }

        OldestUpgradable = oldestUpgradable;
        Current = current;
        this.steps = steps;
    }

    /// <summary>目前這一版 JET 的定義。首次發布的版本就是現行版本，所以還沒有升版步驟。</summary>
    internal static ProjectSchemaVersions Released { get; } =
        new(oldestUpgradable: 21, current: LocalSchemaReadiness.CurrentSchemaVersion, steps: []);

    internal int OldestUpgradable { get; }

    internal int Current { get; }

    /// <summary>
    /// 檢查資料庫記的版本，只讀取、不寫入。已經是現行版本時回 false，需要升版時回 true；
    /// 版本值缺漏或讀不出整數、比現行版本新，或比最早可升級的版本還早時，各用自己的訊息拒絕。
    /// </summary>
    internal bool RequiresUpgrade(string? storedVersion)
    {
        if (!int.TryParse(storedVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
        {
            throw new JetActionException(JetErrorCodes.InvalidProjectSchema, ProjectFolderMessages.UnreadableDatabaseVersion);
        }

        if (version > Current)
        {
            throw new JetActionException(JetErrorCodes.InvalidProjectSchema, ProjectFolderMessages.NewerVersionProject());
        }

        if (version < OldestUpgradable)
        {
            throw new JetActionException(JetErrorCodes.InvalidProjectSchema, ProjectFolderMessages.OldVersionProject());
        }

        return version < Current;
    }

    /// <summary>
    /// 在一個交易裡把資料庫逐版升到現行版本，全部完成才記下新的版本；任何一步失敗，整個交易復原，資料庫留在原本的版本。
    /// 取得交易後重新讀一次版本，另一條連線已經升好時不重複執行。
    /// 磁碟已滿、檔案唯讀或損毀這類認得的資料庫錯誤，照一般的資料庫錯誤回報，使用者才知道要處理的是檔案本身；
    /// 另一個程式正在寫入而取不到交易，以及其他失敗，回報升版失敗，請使用者關閉其他程式後再開啟。
    /// </summary>
    internal async Task UpgradeAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var stored = await ReadVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (!RequiresUpgrade(stored))
            {
                return;
            }

            var from = int.Parse(stored!, NumberStyles.Integer, CultureInfo.InvariantCulture);
            for (var version = from; version < Current; version++)
            {
                await steps[version - OldestUpgradable].ApplyAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
            }

            await using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "UPDATE schema_info SET value = @version WHERE key = 'schema_version';";
                var parameter = record.CreateParameter();
                parameter.ParameterName = "@version";
                parameter.Value = Current.ToString(CultureInfo.InvariantCulture);
                record.Parameters.Add(parameter);
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not JetActionException)
        {
            var translated = LocalEngineErrors.TryTranslate(exception);
            if (translated is not null
                && translated.Code is not (JetErrorCodes.DatabaseBusy or JetErrorCodes.DatabaseLocked))
            {
                throw translated;
            }

            throw new JetActionException(
                JetErrorCodes.InvalidProjectSchema,
                ProjectFolderMessages.DatabaseUpgradeFailed,
                innerException: exception);
        }
    }

    /// <summary>讀出資料庫記的版本。存的不是文字（例如二進位）時回 null，由版本檢查當成無法辨識。</summary>
    internal static async Task<string?> ReadVersionAsync(
        DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version';";
        return await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }
}
