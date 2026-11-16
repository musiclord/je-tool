using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 本機案件資料庫的介面，每個案件一個資料庫檔。目前只有 SQLite 一種實作。
/// 名稱以 <c>Local</c> 開頭的 repository 只用這個介面與 <see cref="ISqlDialect"/>，不直接用特定資料庫的 ADO.NET 型別。
/// <see cref="IProjectDatabaseInitializer"/> 建立案件資料庫，<see cref="IProjectDatabaseDeleter"/> 刪除案件資料庫；
/// <see cref="IProjectDatabaseCheckpoint"/> 是作業結束與離開案件時的收尾。
/// </summary>
public interface ILocalProjectDatabase : IProjectDatabaseInitializer, IProjectDatabaseDeleter, IProjectDatabaseCheckpoint
{
    /// <summary>這個案件的資料庫檔路徑。開發用的唯讀檢視工具用它找到檔案並回報檔案大小。</summary>
    string GetDatabasePath(string projectId);

    /// <summary>
    /// repository 每次讀寫前呼叫，不建立新的資料庫。SQLite 實作在同一程序內
    /// 對每個資料庫檔只做一次完整檢查（見 <see cref="LocalSchemaReadiness"/>）；未覆寫的實作沿用開案時的完整檢查。
    /// </summary>
    Task EnsureReadyAsync(string projectId, CancellationToken cancellationToken) =>
        EnsureExistingAsync(projectId, cancellationToken);

    /// <summary>
    /// 建立讀寫連線；案件資料夾不存在時建立資料夾，資料庫檔不存在時開啟連線會建檔。
    /// 回傳的型別是不分資料庫種類的 <see cref="DbConnection"/>。
    /// </summary>
    DbConnection CreateConnection(string projectId);

    /// <summary>開發用的唯讀檢視工具專用的連線：唯讀、私有快取、不進連線池。資料庫檔不存在時開啟就失敗，不建檔。</summary>
    DbConnection CreateReadOnlyConnection(string projectId);

    /// <summary>這種資料庫的 SQL 寫法：LIMIT、週末判定、不分大小寫的包含比對與參數命名。</summary>
    IProviderSqlDialect Dialect { get; }

    /// <summary>
    /// 只給匯入用的連線設定，在連線開啟之後、交易開始之前套用。
    /// SQLite 在這裡設定 synchronous、temp_store 與 cache_size 三個 pragma。
    /// </summary>
    Task ApplyImportSessionSettingsAsync(DbConnection connection, CancellationToken cancellationToken);

    /// <summary>
    /// 建立一個 <see cref="IBulkRowWriter"/>，把列數很多的逐列 INSERT 交給它，用來加快大量匯入。
    /// <paramref name="columns"/> 是呼叫端每一列會依序給值的欄位名，可以比資料表的欄位少；沒列的欄位與自動編號由實作補上。
    /// 寫入器用傳入的 <paramref name="connection"/> 與 <paramref name="transaction"/> 執行。
    /// <see cref="IBulkRowWriter.AppendAsync"/> 回來時已經把那一次的 values 用完，呼叫端可以在 await 之後重用同一個陣列；
    /// <see cref="IBulkRowWriter.CompleteAsync"/> 之後，同一個交易裡的命令讀得到寫入的列。
    /// </summary>
    IBulkRowWriter CreateBulkRowWriter(
        DbConnection connection, DbTransaction transaction, string table, IReadOnlyList<string> columns);
}
