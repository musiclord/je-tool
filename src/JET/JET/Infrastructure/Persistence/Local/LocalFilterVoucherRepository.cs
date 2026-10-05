using JET.Domain;

namespace JET.Infrastructure;

public sealed class LocalFilterVoucherRepository(ILocalProjectDatabase database) : IFilterVoucherRepository
{
    public async Task<string> ReadRevisionAsync(string projectId, CancellationToken ct)
    {
        await database.EnsureReadyAsync(projectId, ct);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(ct);
        return await FilterVoucherPageReader.ReadRevisionAsync(connection, "", ct);
    }
    public async Task<FilterVoucherPage> ReadAsync(string projectId, FilterScenarioSpec scenario, FilterRuleContext context,
        string? documentNumber, PageRequest request, CancellationToken ct)
    {
        await database.EnsureReadyAsync(projectId, ct);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(ct);
        return await FilterVoucherPageReader.ReadAsync(connection, database.Dialect, "", scenario, context, documentNumber, request, ct);
    }
}
