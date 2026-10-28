using System.Data.Common;

namespace JET.Infrastructure;

/// <summary>
/// SQLite 與 DuckDB 共用的結構升版順序：第 6 版起每一版升到下一版，直到目前版本。
/// 兩種資料庫只提供自己寫法不同的前三步；科目分類、科目分類階層、訊息紀錄等級、GL 未納入原因與匯入來源的欄名列五步是共用程式。
/// SQL Server 另有自己的升版，不在這裡。
/// </summary>
internal static class LocalSchemaUpgrades
{
    /// <summary>各資料庫自己實作的升版步驟，以及第 12 版訊息紀錄表的建表語句。</summary>
    internal sealed record Steps(
        Func<DbConnection, CancellationToken, Task> V6ToV7,
        Func<DbConnection, CancellationToken, Task> V7ToV8,
        Func<DbConnection, CancellationToken, Task> V8ToV9,
        string MessageLogTableSql);

    internal static async Task ApplyAsync(
        DbConnection connection,
        int version,
        Steps steps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(steps);

        if (version == 6)
        {
            await steps.V6ToV7(connection, cancellationToken);
            version = 7;
        }

        if (version == 7)
        {
            await steps.V7ToV8(connection, cancellationToken);
            version = 8;
        }

        if (version == 8)
        {
            await steps.V8ToV9(connection, cancellationToken);
            version = 9;
        }

        if (version == 9)
        {
            await AccountClassificationMigration.UpgradeLocalAsync(connection, cancellationToken);
            version = 10;
        }

        if (version == 10)
        {
            await AccountTaxonomyHierarchy.UpgradeLocalAsync(connection, cancellationToken);
            version = 11;
        }

        if (version == 11)
        {
            await MessageLogLevelMigration.UpgradeLocalAsync(connection, steps.MessageLogTableSql, cancellationToken);
            version = 12;
        }

        if (version == 12)
        {
            await GlExclusionReasonMigration.UpgradeLocalAsync(connection, cancellationToken);
            version = 13;
        }

        if (version == 13)
        {
            await ImportSourceHeaderRowMigration.UpgradeLocalAsync(connection, cancellationToken);
        }
    }
}
