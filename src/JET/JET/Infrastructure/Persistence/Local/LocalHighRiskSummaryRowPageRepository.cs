using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

/// <summary>
/// 分頁讀出高風險條件彙總的分錄明細，分頁用 keyset 方式：用上一頁最後一筆的排序鍵接下一頁。
/// 兩段查詢都交給 <see cref="HighRiskSummaryRowPageReader"/>，本類別只負責開連線，並把 <see cref="ISqlDialect"/> 傳進去；
/// LIMIT 的寫法由它提供。分頁、cursor 與方言的用法和 <see cref="LocalHighRiskSummaryVoucherPageRepository"/> 相同。
/// </summary>
public sealed class LocalHighRiskSummaryRowPageRepository(ILocalProjectDatabase database)
    : IHighRiskSummaryRowPageRepository,
      IHighRiskSummaryRowStreamRepository,
      IWorkingPaperStep41PageRepository,
      IWorkingPaperStep41PreparedSessionFactory
{
    async IAsyncEnumerable<(long EntryId, bool IsHit)> IHighRiskSummaryRowStreamRepository.StreamAsync(
        string projectId,
        GlPopulationContext context,
        int scenarioPosition,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await foreach (var row in HighRiskSummaryRowPageReader.StreamAsync(
                           connection,
                           database.Dialect,
                           context,
                           scenarioPosition,
                           cancellationToken))
        {
            yield return row;
        }
    }

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
            // 直接改這條連線的連線字串來關掉連線池，不另外建立連線；建立連線時註冊的 SQL 函式才會留著。
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
