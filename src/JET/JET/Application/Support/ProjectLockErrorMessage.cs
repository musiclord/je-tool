using System.Globalization;

namespace JET.Application;

/// <summary>把案件鎖回報的持有人資訊寫成 project_locked 的訊息；持有人資訊不明時，只說案件已在另一個 JET 視窗開啟。</summary>
internal static class ProjectLockErrorMessage
{
    private const string ExternalProcessMessage =
        "此案件已在另一個 JET 視窗中開啟，請先在該視窗離開案件後再試一次。";

    public static string Format(string lockedBy, string machineName, DateTimeOffset lockedUtc)
    {
        // 本機的檔案鎖只知道鎖被另一個程序拿著，讀不到可靠的電腦名稱與開始時間。
        // LocalFileLockService 用空的電腦名稱或預設時間表示不知道；這時不能拿目前程序的資訊當成持有人。
        if (string.IsNullOrWhiteSpace(machineName) || lockedUtc == default)
        {
            return ExternalProcessMessage;
        }

        return string.Create(CultureInfo.InvariantCulture, $"此案件由 {lockedBy}（{machineName}）於 {lockedUtc:yyyy-MM-dd HH:mm} 開啟中，請稍後再試。");
    }
}
