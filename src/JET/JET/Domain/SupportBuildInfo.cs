using System.Reflection;

namespace JET.Domain;

/// <summary>
/// 支援日誌寫出的 JET 版本：匯出檔開頭、案件開啟事件與案件紀錄裡每個程序的標頭共用這一組。
/// 維護者依版本、模組代號與組建設定取得相應版本的程式，對回例外裡的方法位置。
/// </summary>
public static class SupportBuildInfo
{
    private static readonly Assembly JetAssembly = typeof(SupportBuildInfo).Assembly;

    public static string AppVersion { get; } = JetAssembly.GetName().Version?.ToString() ?? "unknown";

    public static string InformationalVersion { get; } =
        JetAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static string ModuleVersionId { get; } = JetAssembly.ManifestModule.ModuleVersionId.ToString("N");

    /// <summary>建置時的組建設定名稱，例如 Debug 或 Release。</summary>
    public static string BuildConfiguration { get; } =
        JetAssembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";
}
