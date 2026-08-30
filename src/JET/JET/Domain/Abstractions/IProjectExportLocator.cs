namespace JET.Domain;

/// <summary>
/// 專案資料夾定位埠：正式報告一律由後端 artifact store 寫進目前專案目錄，
/// action payload 不接受實體路徑，wire 也不回傳絕對路徑。
/// 實作為 Infrastructure 的 <c>JetProjectFolder</c>(專案路徑的單一事實來源,含 id 合法性 / path traversal 防護)。
/// </summary>
public interface IProjectExportLocator
{
    string GetProjectDirectory(string projectId);
}
