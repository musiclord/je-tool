using JET.Domain;

namespace JET.Application;

internal interface IProjectSessionPublisher
{
    void Enter(string projectId, ProjectRepositories repositories);

    bool Leave(string expectedProjectId);
}

/// <summary>作用中案件的快照：案件編號連同建案或載入時選定的那一組資料存取物件。</summary>
internal sealed record ActiveProject(string ProjectId, ProjectRepositories Repositories);

/// <summary>
/// 執行期 session：記住目前載入的 projectId，以及建案或載入時依資料庫種類選定的那一組 repository，
/// 不保存資料內容。Infrastructure 不讀此狀態；handler 取得後以參數傳給 repository。
/// </summary>
public sealed class ProjectSession : IProjectSessionPublisher
{
    private readonly Lock _gate = new();
    private ActiveProject? _current;

    public string? CurrentProjectId
    {
        get
        {
            lock (_gate)
            {
                return _current?.ProjectId;
            }
        }
    }

    /// <summary>目前案件的快照；沒有作用中案件時為 null。只由 <see cref="Enter(string, ProjectRepositories)"/> 發布。</summary>
    internal ActiveProject? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>原子發布目前案件與它的資料庫組；建案與載入成功時使用。</summary>
    internal void Enter(string projectId, ProjectRepositories repositories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(repositories);
        var active = new ActiveProject(projectId, repositories);
        lock (_gate)
        {
            _current = active;
        }
    }

    void IProjectSessionPublisher.Enter(string projectId, ProjectRepositories repositories) =>
        Enter(projectId, repositories);

    /// <summary>
    /// 只有目前案件仍等於呼叫端捕捉的值時才離開。這個 compare-and-clear 防止延遲的 release/delete
    /// 清掉其間已由另一個成功動作切換的新案件。
    /// </summary>
    public bool Leave(string expectedProjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProjectId);
        lock (_gate)
        {
            if (!string.Equals(_current?.ProjectId, expectedProjectId, StringComparison.Ordinal))
            {
                return false;
            }

            _current = null;
            return true;
        }
    }

    public string RequireProjectId()
    {
        return CurrentProjectId ?? throw NoActiveProject();
    }

    /// <summary>
    /// 取得作用中案件的快照。handler 在一開始取一次，之後只用這份快照裡的 repository，
    /// 即使其間另一個請求切換了案件也不會混用資料庫。
    /// </summary>
    internal ActiveProject RequireActive()
    {
        return Current ?? throw NoActiveProject();
    }

    private static JetActionException NoActiveProject() =>
        new(
            JetErrorCodes.NoActiveProject,
            "尚未建立或載入任何專案，請先透過 project.create 或 project.load 選擇專案。");
}
