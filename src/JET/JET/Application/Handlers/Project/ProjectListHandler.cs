using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// project.list（雙來源雛形 2026-07-07）：先取得本機快照，再以同一個有界 deadline 並行查詢
/// <see cref="IProjectRegistry"/> 與 <see cref="ILockService"/>。任一遠端查詢失敗只降級自己的結果；
/// caller cancellation 則一律向上傳遞。排序維持 lastOpenedUtc ?? createdUtc 新→舊。
/// </summary>
public sealed class ProjectListHandler : IApplicationActionHandler
{
    private const string RegistryUnreachableMessage = "無法連線線上資料庫伺服器，僅顯示本機快取。";
    private static readonly TimeSpan DefaultRemoteDeadline = TimeSpan.FromSeconds(30);

    private readonly IProjectStore _projectStore;
    private readonly IProjectRegistry _registry;
    private readonly ILockService _lockService;
    private readonly CurrentPrincipal _principal;
    private readonly TimeSpan _remoteDeadline;

    public ProjectListHandler(
        IProjectStore projectStore,
        IProjectRegistry registry,
        ILockService lockService,
        CurrentPrincipal principal)
        : this(projectStore, registry, lockService, principal, DefaultRemoteDeadline)
    {
    }

    internal ProjectListHandler(
        IProjectStore projectStore,
        IProjectRegistry registry,
        ILockService lockService,
        CurrentPrincipal principal,
        TimeSpan remoteDeadline)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(lockService);
        ArgumentNullException.ThrowIfNull(principal);

        if (remoteDeadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(remoteDeadline),
                remoteDeadline,
                "Remote deadline must be positive.");
        }

        _projectStore = projectStore;
        _registry = registry;
        _lockService = lockService;
        _principal = principal;
        _remoteDeadline = remoteDeadline;
    }

    public string Action => "project.list";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        // 本機掃描不計入遠端 deadline：畫面先建立可保留的本機快照，再嘗試補上線上狀態。
        var localDocuments = await _projectStore.ListAsync(cancellationToken);

        using var remoteDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        remoteDeadline.CancelAfter(_remoteDeadline);
        var remoteToken = remoteDeadline.Token;

        // 兩個控制面查詢共用同一 token／deadline，彼此失敗時不互相改寫降級狀態。
        var registryTask = ReadRegistrySnapshotAsync(localDocuments, remoteToken, cancellationToken);
        var locksTask = ReadLocksSnapshotAsync(remoteToken, cancellationToken);

        // 不在 Task.WhenAll 外層再用 caller token 的 WaitAsync：CancellationToken callbacks 是 LIFO，
        // 外層 wait 若先醒來並離開 using scope，可能在 linked source 尚未轉送取消前就把它 Dispose，
        // 讓底層 remote I/O 繼續。兩個 wrapper 本身已以 linked remoteToken 做 hard bound。
        await Task.WhenAll(registryTask, locksTask);
        cancellationToken.ThrowIfCancellationRequested();

        var registrySnapshot = await registryTask;
        var locksByProject = await locksTask;
        var registeredById = registrySnapshot.Registered
            .ToDictionary(item => item.Document.ProjectId, StringComparer.Ordinal);
        var localIds = new HashSet<string>(
            localDocuments.Select(document => document.ProjectId),
            StringComparer.Ordinal);
        var entries = new List<ProjectListEntry>();

        // 本機條目：SQLite／DuckDB 無 syncStatus；SQL Server 依 registry 快照標記同步狀態。
        foreach (var document in localDocuments)
        {
            if (document.DatabaseProvider == ProjectDocument.SqlServerDatabaseProvider)
            {
                string status;
                if (!registrySnapshot.Reachable)
                {
                    status = "localOnly";
                }
                else if (registeredById.ContainsKey(document.ProjectId))
                {
                    status = "synced";
                }
                else
                {
                    status = registrySnapshot.NoAccessProjectIds.Contains(document.ProjectId)
                        ? "noAccess"
                        : "localOnly";
                }

                entries.Add(new ProjectListEntry(
                    document,
                    status,
                    document.LastOpenedUtc,
                    document.LastOpenedUtc ?? document.CreatedUtc,
                    locksByProject.GetValueOrDefault(document.ProjectId)));
            }
            else
            {
                entries.Add(new ProjectListEntry(
                    document,
                    SyncStatus: null,
                    document.LastOpenedUtc,
                    document.LastOpenedUtc ?? document.CreatedUtc));
            }
        }

        // 僅伺服器條目（serverOnly）：registry 可達且本機無此案；顯示與排序時間取自 registry。
        if (registrySnapshot.Reachable)
        {
            foreach (var item in registrySnapshot.Registered)
            {
                if (!localIds.Contains(item.Document.ProjectId))
                {
                    entries.Add(new ProjectListEntry(
                        item.Document,
                        "serverOnly",
                        item.LastOpenedUtc,
                        item.LastOpenedUtc ?? item.CreatedUtc,
                        locksByProject.GetValueOrDefault(item.Document.ProjectId)));
                }
            }
        }

        var projects = entries
            .OrderByDescending(entry => entry.SortKey)
            .Select(entry => entry.ToWire())
            .ToList();

        return new
        {
            projects,
            online = new
            {
                reachable = registrySnapshot.Reachable,
                principal = _principal.Name,
                message = registrySnapshot.Message
            }
        };
    }

    private async Task<RegistrySnapshot> ReadRegistrySnapshotAsync(
        IReadOnlyList<ProjectDocument> localDocuments,
        CancellationToken remoteToken,
        CancellationToken callerToken)
    {
        try
        {
            var registered = await _registry
                .ListVisibleAsync(_principal.Name, remoteToken)
                .WaitAsync(remoteToken);
            var registeredIds = new HashSet<string>(
                registered.Select(item => item.Document.ProjectId),
                StringComparer.Ordinal);
            var noAccessProjectIds = new HashSet<string>(StringComparer.Ordinal);

            // N+1 補問沿用同一個 remoteToken；不得為每一案重啟 deadline。
            foreach (var document in localDocuments)
            {
                if (document.DatabaseProvider == ProjectDocument.SqlServerDatabaseProvider
                    && !registeredIds.Contains(document.ProjectId)
                    && await ExistsInRegistrySafelyAsync(document.ProjectId, remoteToken, callerToken))
                {
                    noAccessProjectIds.Add(document.ProjectId);
                }
            }

            return new RegistrySnapshot(
                Reachable: true,
                Message: null,
                registered,
                noAccessProjectIds);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return RegistrySnapshot.Unreachable();
        }
        catch (Exception)
        {
            callerToken.ThrowIfCancellationRequested();
            return RegistrySnapshot.Unreachable();
        }
    }

    private async Task<bool> ExistsInRegistrySafelyAsync(
        string projectId,
        CancellationToken remoteToken,
        CancellationToken callerToken)
    {
        try
        {
            return await _registry.ExistsAsync(projectId, remoteToken).WaitAsync(remoteToken);
        }
        catch (OperationCanceledException)
        {
            // 交由外層區分 caller cancellation 與共用 deadline；deadline 必須使整份 registry 快照降級。
            throw;
        }
        catch (Exception)
        {
            callerToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private async Task<Dictionary<string, ProjectLockInfo>> ReadLocksSnapshotAsync(
        CancellationToken remoteToken,
        CancellationToken callerToken)
    {
        try
        {
            var locksByProject = new Dictionary<string, ProjectLockInfo>(StringComparer.Ordinal);
            var activeLocks = await _lockService
                .ListActiveAsync(remoteToken)
                .WaitAsync(remoteToken);
            foreach (var info in activeLocks)
            {
                locksByProject[info.ProjectId] = info;
            }

            return locksByProject;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new Dictionary<string, ProjectLockInfo>(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            callerToken.ThrowIfCancellationRequested();
            return new Dictionary<string, ProjectLockInfo>(StringComparer.Ordinal);
        }
    }

    private sealed record RegistrySnapshot(
        bool Reachable,
        string? Message,
        IReadOnlyList<RegisteredProject> Registered,
        IReadOnlySet<string> NoAccessProjectIds)
    {
        internal static RegistrySnapshot Unreachable() =>
            new(
                Reachable: false,
                Message: RegistryUnreachableMessage,
                Registered: [],
                NoAccessProjectIds: new HashSet<string>(StringComparer.Ordinal));
    }
}

/// <summary>
/// project.listLocal：只從本機 store 投影 SQLite／DuckDB 案件，不持有也不觸發 registry、lock 或同步狀態查詢。
/// </summary>
public sealed class ProjectListLocalHandler(IProjectStore projectStore) : IApplicationActionHandler
{
    public string Action => "project.listLocal";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projects = (await projectStore.ListAsync(cancellationToken))
            .Where(document =>
                document.DatabaseProvider == ProjectDocument.DefaultDatabaseProvider
                || document.DatabaseProvider == ProjectDocument.DuckDbDatabaseProvider)
            .OrderByDescending(document => document.LastOpenedUtc ?? document.CreatedUtc)
            .Select(document => new ProjectListEntry(
                document,
                SyncStatus: null,
                document.LastOpenedUtc,
                document.LastOpenedUtc ?? document.CreatedUtc).ToWire())
            .ToList();

        return new { projects };
    }
}

/// <summary>
/// project.list/project.listLocal 單一條目的中介表述：合併後統一排序，再投影成 wire 形狀。
/// syncStatus 僅 SQL Server 條目有；SQLite／DuckDB 本機條目投影時不含該鍵。
/// </summary>
internal sealed record ProjectListEntry(
    ProjectDocument Doc,
    string? SyncStatus,
    DateTimeOffset? DisplayLastOpened,
    DateTimeOffset SortKey,
    ProjectLockInfo? Lock = null)
{
    public object ToWire() => SyncStatus is null
        ? new
        {
            projectId = Doc.ProjectId,
            projectCode = Doc.ProjectCode,
            entityName = Doc.EntityName,
            periodStart = Doc.PeriodStart,
            periodEnd = Doc.PeriodEnd,
            createdUtc = Doc.CreatedUtc,
            currentStep = Doc.CurrentStep,
            databaseProvider = Doc.DatabaseProvider,
            lastOpenedUtc = DisplayLastOpened
        }
        : new
        {
            projectId = Doc.ProjectId,
            projectCode = Doc.ProjectCode,
            entityName = Doc.EntityName,
            periodStart = Doc.PeriodStart,
            periodEnd = Doc.PeriodEnd,
            createdUtc = Doc.CreatedUtc,
            currentStep = Doc.CurrentStep,
            databaseProvider = Doc.DatabaseProvider,
            lastOpenedUtc = DisplayLastOpened,
            syncStatus = SyncStatus,
            @lock = Lock is null
                ? null
                : (object)new { lockedBy = Lock.LockedBy, machineName = Lock.MachineName, lockedUtc = Lock.LockedUtc }
        };
}
