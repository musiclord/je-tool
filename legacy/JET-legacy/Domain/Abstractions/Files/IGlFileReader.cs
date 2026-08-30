using JET.Domain.Entities;

namespace JET.Domain.Abstractions.Files
{
    /// <summary>
    /// Streaming reader for tabular GL/TB/AccountMapping source files.
    /// Introduced under plan.md Phase 3 §3.1.a as the foundation for the
    /// <c>import.gl.fromFile</c> / <c>import.tb.fromFile</c> /
    /// <c>import.accountMapping.fromFile</c> pipelines (G1 + G2).
    /// </summary>
    /// <remarks>
    /// Mission constraint (docs/jet-guide.md §1.5.4): implementations MUST stream
    /// row-by-row and never load the entire workbook into memory. Targets are
    /// 10万–500万 row GL files; an in-memory load would OOM.
    /// </remarks>
    public interface IGlFileReader
    {
        /// <summary>
        /// Reads the file at <paramref name="filePath"/> and yields rows lazily.
        /// The first yielded row (<c>RowIndex == 0</c>) is the header row; data
        /// rows follow with monotonically increasing <c>RowIndex</c>.
        /// </summary>
        IAsyncEnumerable<GlRawRow> ReadAsync(string filePath, CancellationToken cancellationToken);
    }
}
