using JET.Domain;

namespace JET.Application;

internal interface IProjectSessionPublisher
{
    void Enter(string projectId);

    bool Leave(string expectedProjectId);
}

/// <summary>
/// 執行期 session：只記住目前載入的 projectId（guide §1.5.6 輕量指標）。
/// Infrastructure 不讀此狀態；handler 解析後以參數傳給 repository。
/// </summary>
public sealed class ProjectSession : IProjectSessionPublisher
{
    private readonly Lock _gate = new();
    private string? _currentProjectId;

    public string? CurrentProjectId
    {
        get
        {
            lock (_gate)
            {
                return _currentProjectId;
            }
        }
    }

    /// <summary>原子發布目前案件；所有 handler 的 session 寫入都收斂到此入口。</summary>
    public void Enter(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (_gate)
        {
            _currentProjectId = projectId;
        }
    }

    /// <summary>
    /// 只有目前案件仍等於呼叫端捕捉的值時才離開。這個 compare-and-clear 防止延遲的 release/delete
    /// 清掉其間已由另一個成功動作切換的新案件。
    /// </summary>
    public bool Leave(string expectedProjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProjectId);
        lock (_gate)
        {
            if (!string.Equals(_currentProjectId, expectedProjectId, StringComparison.Ordinal))
            {
                return false;
            }

            _currentProjectId = null;
            return true;
        }
    }

    public string RequireProjectId()
    {
        return CurrentProjectId
            ?? throw new JetActionException(
                JetErrorCodes.NoActiveProject,
                "尚未建立或載入任何專案，請先透過 project.create 或 project.load 選擇專案。");
    }
}
