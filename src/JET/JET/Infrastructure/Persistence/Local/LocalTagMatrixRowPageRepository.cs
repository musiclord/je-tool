using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣行層 keyset 分頁(本地引擎)。兩段查詢委派給 provider 中立的
/// <see cref="TagMatrixRowPageReader"/>,本類別只負責開連線與帶入注入的 <see cref="ISqlDialect"/>
/// (LIMIT 由方言出)。鏡射 <see cref="LocalTagMatrixVoucherPageRepository"/> 的 keyset/游標/Dialect 範式。
/// </summary>
public sealed class LocalTagMatrixRowPageRepository(ILocalProjectDatabase database)
    : ITagMatrixRowPageRepository,
      IWorkpaperStep41PageRepository,
      IWorkpaperStep41PreparedSessionFactory
{
    public async Task<(PageResult<RowTagRow> Page, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> GetPageAsync(
        string projectId, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await TagMatrixRowPageReader.ReadAsync(
            connection, database.Dialect, context, request, scenarioPositions, cancellationToken);
    }

    async Task<WorkpaperStep41Page> IWorkpaperStep41PageRepository.GetPageAsync(
        string projectId,
        GlPopulationContext context,
        PageRequest request,
        IReadOnlyList<int> scenarioPositions,
        LegacyFieldKind lineItemKind,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await TagMatrixRowPageReader.ReadWorkpaperAsync(
            connection,
            database.Dialect,
            context,
            request,
            scenarioPositions,
            lineItemKind,
            cancellationToken);
    }

    Task<IWorkpaperStep41PreparedSession>
        IWorkpaperStep41PreparedSessionFactory.PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken) =>
        WorkpaperStep41PreparedSession.CreateAsync(
            CreatePreparedConnection(projectId),
            database.Dialect,
            schemaPrefix: string.Empty,
            context,
            scenarioPositions,
            lineItemKind,
            dedicatedConnection: true,
            cancellationToken);

    Task<IWorkpaperStep41PreparedSession>
        IWorkpaperStep41PreparedSessionFactory.PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken,
            IReadOnlyList<int> hitVoucherScenarioPositions) =>
        WorkpaperStep41PreparedSession.CreateAsync(
            CreatePreparedConnection(projectId),
            database.Dialect,
            schemaPrefix: string.Empty,
            context,
            scenarioPositions,
            lineItemKind,
            dedicatedConnection: true,
            cancellationToken,
            hitVoucherScenarioPositions);

    private DbConnection CreatePreparedConnection(string projectId)
    {
        var connection = database.CreateConnection(projectId);
        if (connection is not SqliteConnection sqlite)
        {
            // DuckDB.NET does not pool this physical connection.
            return connection;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder(sqlite.ConnectionString)
            {
                Pooling = false
            };
            // Keep the factory's SQL functions when disabling pooling for this session.
            sqlite.ConnectionString = builder.ConnectionString;
            return sqlite;
        }
        catch
        {
            sqlite.Dispose();
            throw;
        }
    }
}
