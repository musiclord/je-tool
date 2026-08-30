namespace JET.Domain.Entities
{
    /// <summary>
    /// A raw row read from a GL/TB/AccountMapping spreadsheet before any column-map
    /// or schema validation is applied. Used by <c>IGlFileReader</c> streaming
    /// pipeline (plan.md Phase 3 §3.1.a).
    /// </summary>
    /// <param name="RowIndex">
    /// 0-based row index inside the source sheet. By convention, row 0 is the
    /// header row; data rows start at index 1.
    /// </param>
    /// <param name="Values">
    /// Cell values in column order, normalised to nullable strings. Empty cells
    /// surface as <c>null</c>; downstream handlers parse to typed values when
    /// projecting into <c>staging_*</c> rows.
    /// </param>
    public sealed record GlRawRow(int RowIndex, IReadOnlyList<string?> Values);
}
