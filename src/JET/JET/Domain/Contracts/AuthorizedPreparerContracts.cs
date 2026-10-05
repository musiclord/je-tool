namespace JET.Domain;

/// <summary>
/// 授權編製人員清單的欄位辨識（import.authorizedPreparer.fromFile）：
/// 來源欄位由呼叫端明確指定，不猜姓名欄或第一欄。
/// </summary>
public static class AuthorizedPreparerColumnResolver
{
    public static string Resolve(IReadOnlyList<string> columns, string? sourceColumn = null)
    {
        if (columns.Count < 1)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectionFailed, "授權編製人員清單需至少一個人員識別欄位。");
        }

        if (string.IsNullOrWhiteSpace(sourceColumn))
            throw new JetActionException(JetErrorCodes.InvalidPayload,
                "請選擇與 GL 傳票建立人員一致的人員識別欄位，再重新匯入。", field: "sourceColumn");
        if (!columns.Contains(sourceColumn, StringComparer.Ordinal))
            throw new JetActionException(JetErrorCodes.InvalidPayload,
                "選取的人員識別欄位已不存在，請重新選擇來源欄位。", field: "sourceColumn");
        return sourceColumn;
    }
}
/// <summary>匯入結果（import.authorizedPreparer.fromFile response 形狀的來源）。</summary>
public sealed record AuthorizedPreparerImportResult(
    string BatchId, int RowCount, string FileName, DateTimeOffset ImportedUtc)
{
    public string? SourceColumn { get; init; }
    public int SourceRowCount { get; init; }
    public int BlankRowCount { get; init; }
    public int DuplicateRowCount { get; init; }
    public long? MatchedPreparerCount { get; init; }
}

/// <summary>
/// 授權清單的目前狀態（presence 查詢，供 project.load resume 顯示「已匯入(N 筆)」）。
/// 授權清單另保存匯入統計；舊名單缺少的統計為 null。未確認 GL 建立人員配對時比對數為 null。
/// 名單空時為 null（RowCount 永遠 &gt; 0）。
/// </summary>
public sealed record AuthorizedPreparerState(long RowCount)
{
    public string? SourceColumn { get; init; }
    public int? SourceRowCount { get; init; }
    public int? BlankRowCount { get; init; }
    public int? DuplicateRowCount { get; init; }
    public long? MatchedPreparerCount { get; init; }
}

/// <summary>
/// 授權編製人員清單的匯入與計數。授權清單就是一個 name 集合（target_authorized_preparer，name PK）；
/// 不入 import_batch dataset_kind 體系，故 store 只寫 staging + target。
/// </summary>
public interface IAuthorizedPreparerStore
{
    /// <summary>移除授權清單與相依結果；保留 GL、TB、驗證及情境定義。</summary>
    Task ClearAsync(string projectId, CancellationToken cancellationToken);

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

    /// <summary>授權清單筆數（清單為空時，非授權編製人員預篩選不執行）。</summary>
    Task<long> CountAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>
    /// project.load resume 用：名單已匯入時回 <see cref="AuthorizedPreparerState"/>（RowCount = 名單筆數），
    /// 未匯入（0 筆）時回 null。rowCount 取自 target_authorized_preparer 的 COUNT，不持久化 fileName/importedUtc。
    /// </summary>
    Task<AuthorizedPreparerState?> FindStateAsync(string projectId, CancellationToken cancellationToken);
}
