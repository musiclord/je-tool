using System.Text.Json;
using JET.Domain;

namespace JET.Application;

public sealed class DevDbOverviewHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "dev.db.overview";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        var overview = await repositories.DevDatabaseInspector.GetOverviewAsync(projectId, cancellationToken);

        return new
        {
            databasePath = overview.DatabasePath,
            databaseProvider = document.DatabaseProvider,
            fileSizeBytes = overview.FileSizeBytes,
            engineVersion = overview.EngineVersion,
            tables = overview.Tables.Select(t => new { name = t.Name, rowCount = t.RowCount }).ToList()
        };
    }
}

public sealed class DevDbTableDataHandler(ProjectSession session) : IApplicationActionHandler
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public string Action => "dev.db.tableData";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();

        var tableName = PayloadReader.GetRequiredString(payload, "tableName");
        var limit = Math.Clamp(PayloadReader.GetOptionalInt(payload, "limit") ?? DefaultLimit, 1, MaxLimit);
        var offset = Math.Max(PayloadReader.GetOptionalInt(payload, "offset") ?? 0, 0);

        var page = await repositories.DevDatabaseInspector.GetTablePageAsync(projectId, tableName, limit, offset, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.TableNotAllowed,
                $"資料表 '{tableName}' 不存在或不允許查詢。");

        return new
        {
            tableName = page.TableName,
            columns = page.Columns,
            rows = page.Rows,
            totalCount = page.TotalCount,
            limit = page.Limit,
            offset = page.Offset
        };
    }
}

/// <summary>
/// dev.db.reconcile（只供開發診斷）：資料庫漂移檢查，比對單庫 sys.schemas、專案登錄與本機資料夾，
/// 回報 orphanSchemas／ghostRegistrations／zombieFolders 三種漂移供人工裁決（只列建議、不自動清理）。
/// 不需 active project（跨專案）。僅 Debug 組建註冊（比照 dev.db.overview）。
/// </summary>
public sealed class DevDbReconcileHandler(IControlPlaneReconciler reconciler) : IApplicationActionHandler
{
    public string Action => "dev.db.reconcile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var report = await reconciler.ReconcileAsync(cancellationToken);

        return new
        {
            orphanSchemas = report.OrphanSchemas,
            ghostRegistrations = report.GhostRegistrations
                .Select(g => new { projectId = g.ProjectId, schemaName = g.SchemaName })
                .ToArray(),
            zombieFolders = report.ZombieFolders
        };
    }
}
