namespace JET.Application;

/// <summary>把 provider 的持鎖資訊轉為可採信的 project_locked 訊息。</summary>
internal static class ProjectLockErrorMessage
{
    private const string ExternalProcessMessage =
        "此案件正由另一個 JET 執行個體開啟中，請先在該視窗離開案件後再試。";

    public static string Format(string lockedBy, string machineName, DateTimeOffset lockedUtc)
    {
        // 本地 OS 排他 handle 只告訴我們「另一程序持有」，無法可靠回讀它的機器與起始時間。
        // LocalFileLockService 以空 machine／default time 表示未知；不得拿目前程序資訊冒充持有人。
        if (string.IsNullOrWhiteSpace(machineName) || lockedUtc == default)
        {
            return ExternalProcessMessage;
        }

        return $"此案件由 {lockedBy}（{machineName}）於 {lockedUtc:yyyy-MM-dd HH:mm} 開啟中，請稍後再試。";
    }
}
