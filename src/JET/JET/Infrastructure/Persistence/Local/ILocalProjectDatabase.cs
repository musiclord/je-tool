using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 本地檔引擎每專案資料庫的引擎中立介面。目前只有 SQLite 一種實作：DuckDB 隨裁定 2026-11-06-08 暫時移除，
/// 還原點是提交 7e88b7b。<c>Local*</c> 類別說明裡的「SQLite／DuckDB 共用」與 DuckDB 的寫法理由，是留給日後恢復時用的。
/// 引擎差異全部收進實作類：連線工廠、方言、匯入連線調校。<c>Local*</c> 家族只依賴
/// 這個抽象與 <see cref="ISqlDialect"/>，不感知具體 ADO 型別，因此同一套 repository
/// 與 SQL 文本可由多個本地引擎共用。<see cref="IProjectDatabaseInitializer"/> 與
/// <see cref="IProjectDatabaseDeleter"/> 為既有契約（建立／刪除每專案資料庫）；
/// <see cref="IProjectDatabaseCheckpoint"/> 是作業結束與離開案件時的收尾。
/// </summary>
public interface ILocalProjectDatabase : IProjectDatabaseInitializer, IProjectDatabaseDeleter, IProjectDatabaseCheckpoint
{
    /// <summary>該專案資料庫檔的實體路徑（dev 唯讀檢視定位檔案、回報檔案大小用）。</summary>
    string GetDatabasePath(string projectId);

    /// <summary>
    /// repository 每次讀寫前呼叫，不建立新的資料庫（裁定 2026-11-05-72）。SQLite 實作在同一程序內
    /// 對每個資料庫檔只做一次完整檢查（見 <see cref="LocalSchemaReadiness"/>）；未覆寫的實作沿用開案時的完整檢查。
    /// </summary>
    Task EnsureReadyAsync(string projectId, CancellationToken cancellationToken) =>
        EnsureExistingAsync(projectId, cancellationToken);

    /// <summary>讀寫連線（目錄不存在則建立、檔不存在則建檔）。回傳引擎中立的 <see cref="DbConnection"/>。</summary>
    DbConnection CreateConnection(string projectId);

    /// <summary>dev 唯讀檢視專用連線：唯讀、私有快取、不進連線池；DB 檔不存在則開啟即失敗（不建檔）。</summary>
    DbConnection CreateReadOnlyConnection(string projectId);

    /// <summary>本地引擎方言（LIMIT／週末判定／不分大小寫包含／參數命名）。</summary>
    IProviderSqlDialect Dialect { get; }

    /// <summary>
    /// 匯入連線層調校：只作用於匯入連線、於開啟後、交易開始前套用。
    /// SQLite 套 pragmas（synchronous/temp_store/cache_size）；其他引擎可 no-op。
    /// </summary>
    Task ApplyImportSessionSettingsAsync(DbConnection connection, CancellationToken cancellationToken);

    /// <summary>
    /// 建立一個批量列寫入器，用來加快大量匯入：把熱路徑的逐列 INSERT 迴圈交給引擎自適配的
    /// <see cref="IBulkRowWriter"/>——SQLite 包裝現行參數化 INSERT；原本 DuckDB 走原生 Appender。
    /// <paramref name="columns"/> 是呼叫端每列會依序給值的欄位名（可少於資料表欄位；缺欄／auto-id 由實作補齊）。
    /// 寫入器在傳入的 <paramref name="connection"/>／<paramref name="transaction"/> 上運作，
    /// <see cref="IBulkRowWriter.AppendAsync"/> 完成時已消費該次 values，呼叫端可在 await 後重用同一陣列；
    /// <see cref="IBulkRowWriter.CompleteAsync"/> 後同交易內可見。
    /// </summary>
    IBulkRowWriter CreateBulkRowWriter(
        DbConnection connection, DbTransaction transaction, string table, IReadOnlyList<string> columns);
}
