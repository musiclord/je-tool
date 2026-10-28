namespace JET.Domain;

/// <summary>其他程式需要指名的動作名稱。handler 的 Action、執行政策與支援日誌的讀取端都引用這裡，不各自寫字串。</summary>
public static class JetActionNames
{
    public const string ProjectReleaseLock = "project.releaseLock";
}
