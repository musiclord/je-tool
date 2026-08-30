namespace JET.Domain.Abstractions.Repositories
{
    /// <summary>
    /// Outcome of a streaming staging-table bulk insert (GL / TB / AccountMapping).
    /// Returned by <see cref="IGlRepository"/>, <see cref="ITbRepository"/>, and the
    /// future <c>IAccountMappingRepository</c>. Per plan.md §3.1, Bridge responses
    /// MUST NOT carry full <c>rows[]</c>; only this summary travels.
    /// </summary>
    /// <param name="BatchId">New <c>config_import_batch.batch_id</c>.</param>
    /// <param name="RowCount">Number of data rows persisted (header excluded).</param>
    /// <param name="Columns">Header column names in source order.</param>
    public sealed record BulkImportResult(string BatchId, int RowCount, IReadOnlyList<string> Columns);
}
