namespace JET.Domain;

/// <summary>
/// 案件資料夾定位埠：正式報告一律由後端報告 store 寫進目前案件資料夾，
/// action payload 不接受實體路徑，wire 也不回傳絕對路徑。
/// 實作為 Infrastructure 的 <c>JetProjectFolder</c>：案件資料夾的路徑只在這裡決定，並檢查 id 是否合法、防止 path traversal。
/// </summary>
public interface IProjectExportLocator
{
    string GetProjectDirectory(string projectId);
}
