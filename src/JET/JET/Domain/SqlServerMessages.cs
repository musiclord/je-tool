namespace JET.Domain;

/// <summary>
/// 只在線上案件出現的錯誤句（使用者 2026-10-14 第三輪勾選頁確認 R3-66、R3-37）。同一種情況全程式只用這裡的一句，
/// 不寫環境變數、程式名稱或引擎原文。2026-10-19 的 Provider 中，<c>DualSourceProjectTests</c> 在 SQL Server 核對了
/// <see cref="ProjectMissingOnServer"/> 與 <see cref="LocalCaseWithSameName"/>；<see cref="DuplicateKey"/> 由不連線的
/// <c>SqlServerEngineErrorsTests</c> 核對。其餘句子沒有測試核對文字。
/// </summary>
public static class SqlServerMessages
{
    public const string NotConfigured = "尚未設定 SQL Server 連線，目前無法使用線上案件；本機案件不受影響。";

    public const string Deadlock = "SQL Server 同時處理其他作業，本次操作未完成。請稍候再試一次。";

    public const string LoginFailed = "無法登入 SQL Server，目前無法使用線上案件；本機案件不受影響。";

    public const string DuplicateKey = "資料與既有紀錄重複，本次操作未完成。";

    public const string Timeout = "SQL Server 未在時限內回應，本次操作未完成。請稍候再試一次。";

    public const string ExpressUnsupported =
        "目前連線的 SQL Server 是 Express 版，無法建立線上案件所需的資料庫；線上案件需要 SQL Server 2022 Developer 或 Standard 版。本機案件不受影響。";

    public const string ProjectMissingOnServer = "這個線上案件已不在 SQL Server 上，無法開啟。";

    public static string CaseNameTakenOnServer(string caseName) =>
        $"SQL Server 上已有名為「{caseName}」的案件資料，請改用其他案件名稱。";

    public static string LocalCaseWithSameName(string caseName) =>
        $"本機已有同名案件「{caseName}」，無法開啟這個線上案件。";
}
