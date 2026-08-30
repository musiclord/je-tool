using JET.Domain.Entities;

namespace JET.Domain.Abstractions.Repositories
{
    /// <summary>
    /// Persists raw TB rows streamed from a spreadsheet into <c>staging_tb_raw_row</c>.
    /// Introduced under plan.md Phase 3 §3.1.c to converge gap G1+G2 — the bridge
    /// must NEVER carry the full <c>rows[]</c> for large TB files.
    /// </summary>
    /// <remarks>
    /// Semantics mirror <see cref="IGlRepository.BulkInsertStagingAsync"/> with
    /// <c>dataset_kind = "tb"</c>. The shared <see cref="IGlFileReader"/> + <see cref="GlRawRow"/>
    /// abstractions are reused across GL/TB/AccountMapping ingest.
    /// </remarks>
    public interface ITbRepository
    {
        /// <summary>
        /// Bulk-inserts streamed TB rows into staging. Returns the new batch id and
        /// the number of data rows persisted (header excluded).
        /// </summary>
        /// <param name="projectId">Owning project id.</param>
        /// <param name="fileName">Display file name (free-form).</param>
        /// <param name="rows">
        /// Raw row stream. By convention <c>RowIndex == 0</c> is the header row;
        /// subsequent rows are data rows. The header is required.
        /// </param>
        /// <param name="mode">
        /// <c>"replace"</c> (default) deletes prior <c>tb</c> batches for the
        /// project before inserting; <c>"append"</c> leaves prior batches intact.
        /// </param>
        Task<BulkImportResult> BulkInsertStagingAsync(
            string projectId,
            string fileName,
            IAsyncEnumerable<GlRawRow> rows,
            string mode,
            CancellationToken cancellationToken);
    }
}
