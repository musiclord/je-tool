using JET.Domain.Entities;

namespace JET.Domain.Abstractions.Repositories
{
    /// <summary>
    /// Persists raw AccountMapping rows streamed from a spreadsheet into
    /// <c>staging_account_mapping_raw_row</c>. Introduced under plan.md Phase 3
    /// §3.1.d to converge gap G1+G2 — the bridge must NEVER carry the full
    /// <c>rows[]</c> for AccountMapping files.
    /// </summary>
    /// <remarks>
    /// Semantics mirror <see cref="IGlRepository.BulkInsertStagingAsync"/> with
    /// <c>dataset_kind = "accountMapping"</c>. Reuses <see cref="IGlFileReader"/>
    /// + <see cref="GlRawRow"/> + <see cref="BulkImportResult"/> across GL/TB/AccountMapping ingest.
    /// </remarks>
    public interface IAccountMappingRepository
    {
        /// <summary>
        /// Bulk-inserts streamed AccountMapping rows into staging. Returns the
        /// new batch id and the number of data rows persisted (header excluded).
        /// </summary>
        /// <param name="projectId">Owning project id.</param>
        /// <param name="fileName">Display file name (free-form).</param>
        /// <param name="rows">
        /// Raw row stream. By convention <c>RowIndex == 0</c> is the header row;
        /// subsequent rows are data rows. The header is required.
        /// </param>
        /// <param name="mode">
        /// <c>"replace"</c> (default) deletes prior <c>accountMapping</c> batches
        /// for the project before inserting; <c>"append"</c> leaves prior
        /// batches intact.
        /// </param>
        Task<BulkImportResult> BulkInsertStagingAsync(
            string projectId,
            string fileName,
            IAsyncEnumerable<GlRawRow> rows,
            string mode,
            CancellationToken cancellationToken);
    }
}
