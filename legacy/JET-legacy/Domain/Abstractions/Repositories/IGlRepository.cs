using JET.Domain.Entities;

namespace JET.Domain.Abstractions.Repositories
{
    /// <summary>
    /// Persists raw GL rows streamed from a spreadsheet into <c>staging_gl_raw_row</c>.
    /// Introduced under plan.md Phase 3 §3.1.b to converge gap G1+G2 — the bridge
    /// must NEVER carry the full <c>rows[]</c> for 10万–500万 row GL files.
    /// </summary>
    /// <remarks>
    /// Implementations stream the input <see cref="IAsyncEnumerable{T}"/> with a
    /// prepared-statement transaction batch (5–20k rows per flush). The whole
    /// operation is one logical batch: a new <c>config_import_batch</c> row is
    /// created, the header row is written to <c>config_import_column</c>, and the
    /// raw data rows land in <c>staging_gl_raw_row</c> as JSON-encoded value arrays.
    /// </remarks>
    public interface IGlRepository
    {
        /// <summary>
        /// Bulk-inserts streamed GL rows into staging. Returns the new batch id and
        /// the number of data rows persisted (header excluded).
        /// </summary>
        /// <param name="projectId">Owning project id.</param>
        /// <param name="fileName">Display file name (free-form).</param>
        /// <param name="rows">
        /// Raw row stream. By convention <c>RowIndex == 0</c> is the header row;
        /// subsequent rows are data rows. The header is required.
        /// </param>
        /// <param name="mode">
        /// <c>"replace"</c> (default) deletes prior <c>gl</c> batches for the
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
