namespace JET.Domain.Abstractions.Persistence
{
    /// <summary>
    /// Logical-to-physical table name mapping. Application/Domain code references logical
    /// keys via <c>JetTable.*</c>; provider implementations resolve to a physical name
    /// (SQLite: <c>staging_gl_raw_row</c>; SQL Server: <c>staging.gl_raw_row</c>).
    /// </summary>
    public interface ISchemaNames
    {
        /// <summary>Resolve a <see cref="JetTable"/> logical key to the provider-specific physical name.</summary>
        string Resolve(JetTable table);
    }
}
