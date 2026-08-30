namespace JET.Domain;

/// <summary>
/// 授權編製人員清單的欄位辨識（manifest import.authorizedPreparer.fromFile 細節）：
/// 單欄姓名清單——正規化標頭以關鍵字命中優先，無法命中時退回位次 1。
/// </summary>
public static class AuthorizedPreparerColumnResolver
{
    public static string Resolve(IReadOnlyList<string> columns)
    {
        if (columns.Count < 1)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectionFailed, "授權編製人員清單需至少一欄（姓名）。");
        }

        return FindByKeywords(columns, ["authorized_preparer", "preparer", "編製人員", "姓名", "name"])
            ?? columns[0];
    }

    private static string? FindByKeywords(IReadOnlyList<string> columns, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            foreach (var column in columns)
            {
                if (column.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return column;
                }
            }
        }

        return null;
    }
}

/// <summary>匯入結果（manifest import.authorizedPreparer.fromFile response 形狀的來源）。</summary>
public sealed record AuthorizedPreparerImportResult(
    string BatchId, int RowCount, string FileName, DateTimeOffset ImportedUtc);

/// <summary>
/// 授權清單的目前狀態（presence 查詢，供 project.load resume 顯示「已匯入(N 筆)」）。
/// 授權清單是 name 集合、不入 import_batch，故只持有 RowCount（無 fileName/importedUtc）；
/// 名單空時為 null（RowCount 永遠 &gt; 0）。
/// </summary>
public sealed record AuthorizedPreparerState(long RowCount);

/// <summary>
/// 授權編製人員清單的匯入與計數。授權清單就是一個 name 集合（target_authorized_preparer，name PK）；
/// 不入 import_batch dataset_kind 體系，故 store 只寫 staging + target。
/// </summary>
public interface IAuthorizedPreparerStore
{
    /// <summary>
    /// replace-only 匯入：清舊 staging/target、清依賴它的規則結果、串流寫 staging 並投影 target，
    /// 全在**同一 transaction**。姓名 TRIM 正規化、空白列略過、去重（name PK）。
    /// </summary>
    Task<AuthorizedPreparerImportResult> ImportAsync(
        string projectId,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken);

    /// <summary>授權清單筆數（供非授權編製人員預篩選閘控）。</summary>
    Task<long> CountAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>
    /// project.load resume 用：名單已匯入時回 <see cref="AuthorizedPreparerState"/>（RowCount = 名單筆數），
    /// 未匯入（0 筆）時回 null。rowCount 取自 target_authorized_preparer 的 COUNT，不持久化 fileName/importedUtc。
    /// </summary>
    Task<AuthorizedPreparerState?> FindStateAsync(string projectId, CancellationToken cancellationToken);
}
