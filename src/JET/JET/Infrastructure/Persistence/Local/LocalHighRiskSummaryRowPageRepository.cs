using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

/// <summary>
/// 高風險條件彙總行層 keyset 分頁(本地引擎)。兩段查詢委派給 provider 中立的
/// <see cref="HighRiskSummaryRowPageReader"/>,本類別只負責開連線與帶入注入的 <see cref="ISqlDialect"/>
/// (LIMIT 由方言出)。鏡射 <see cref="LocalHighRiskSummaryVoucherPageRepository"/> 的 keyset/游標/Dialect 範式。
/// </summary>
public sealed class LocalHighRiskSummaryRowPageRepository(ILocalProjectDatabase database)
    : IHighRiskSummaryRowPageRepository,
      IWorkingPaperStep41PageRepository,
      IWorkingPaperStep41PreparedSessionFactory
{
    public async Task<(PageResult<RowTagRow> Page, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> GetPageAsync(
        string projectId, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await HighRiskSummaryRowPageReader.ReadAsync(
            connection, database.Dialect, context, request, scenarioPositions, cancellationToken);
    }

    async Task<WorkingPaperStep41Page> IWorkingPaperStep41PageRepository.GetPageAsync(
        string projectId,
        GlPopulationContext context,
        PageRequest request,
        IReadOnlyList<int> scenarioPositions,
        LegacyFieldKind lineItemKind,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await HighRiskSummaryRowPageReader.ReadWorkingPaperAsync(
            connection,
            database.Dialect,
            context,
            request,
            scenarioPositions,
            lineItemKind,
            cancellationToken);
    }

    Task<IWorkingPaperStep41PreparedSession>
        IWorkingPaperStep41PreparedSessionFactory.PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken) =>
        WorkingPaperStep41PreparedSession.CreateAsync(
            CreatePreparedConnection(projectId),
            database.Dialect,
            schemaPrefix: string.Empty,
            context,
            scenarioPositions,
            lineItemKind,
            dedicatedConnection: true,
            cancellationToken);

    Task<IWorkingPaperStep41PreparedSession>
        IWorkingPaperStep41PreparedSessionFactory.PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken,
            IReadOnlyList<int> hitVoucherScenarioPositions) =>
        WorkingPaperStep41PreparedSession.CreateAsync(
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
