namespace JET.Tests.TestInfrastructure;

internal static partial class TestProfileTraits
{
    // 讀方案資料夾（JET.slnx 所在的資料夾）以外檔案的測試：範本原檔、舊版腳本、Git 忽略規則與追蹤清單、
    // 桌面情境程式與 action 契約文件。這些測試放在開發儲存庫的驗證框架底下，只有方案資料夾時不會編進測試專案。
    internal const string DevelopmentRepository = "DevelopmentRepository";
}
