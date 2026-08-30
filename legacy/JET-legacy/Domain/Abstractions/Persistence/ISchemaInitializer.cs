namespace JET.Domain.Abstractions.Persistence
{
    /// <summary>
    /// Result of a schema initialization attempt. <see cref="IsAvailable"/> mirrors the
    /// flag surfaced via <c>app.bootstrap</c>'s <c>DatabaseStatus</c>; <see cref="Mode"/>
    /// is a human-readable hint surfaced to the UI.
    /// </summary>
    public sealed record SchemaInitializationResult(bool IsAvailable, string Mode);

    /// <summary>
    /// Ensures the JET physical schema exists for the current provider. Idempotent.
    /// Implementations must not throw when the provider is reachable but unsupported;
    /// they should return <c>IsAvailable = false</c> instead.
    /// </summary>
    public interface ISchemaInitializer
    {
        Task<SchemaInitializationResult> EnsureAsync(CancellationToken cancellationToken);
    }
}
