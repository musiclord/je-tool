namespace JET.Domain;

public sealed record DevTableInfo(string Name, long RowCount);

public sealed record DevDatabaseOverview(
    string DatabasePath,
    long FileSizeBytes,
    string EngineVersion,
    IReadOnlyList<DevTableInfo> Tables);

public sealed record DevTablePage(
    string TableName,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    long TotalCount,
    int Limit,
    int Offset);

/// <summary>
/// 開發階段 SQLite 檢視工具的抽象。GetTablePageAsync 回傳 null
/// 表示 tableName 不在白名單（呼叫端轉 table_not_allowed）。
/// </summary>
public interface IDevDatabaseInspector
{
    Task<DevDatabaseOverview> GetOverviewAsync(string projectId, CancellationToken cancellationToken);

    Task<DevTablePage?> GetTablePageAsync(
        string projectId,
        string tableName,
        int limit,
        int offset,
        CancellationToken cancellationToken);
}
