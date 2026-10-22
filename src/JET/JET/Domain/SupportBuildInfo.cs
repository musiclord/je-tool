using System.Reflection;

namespace JET.Domain;

/// <summary>
/// 支援日誌寫出的 JET 版本：匯出檔開頭、案件開啟事件與案件紀錄裡每個程序的標頭共用這一組。
/// 維護者依版本、模組代號與組建設定取得相應版本的程式，對回例外裡的方法位置（2026-10-21）。
/// </summary>
public static class SupportBuildInfo
{
    private static readonly Assembly JetAssembly = typeof(SupportBuildInfo).Assembly;

    public static string AppVersion { get; } = JetAssembly.GetName().Version?.ToString() ?? "unknown";

    public static string InformationalVersion { get; } =
        JetAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static string ModuleVersionId { get; } = JetAssembly.ManifestModule.ModuleVersionId.ToString("N");

    public static string BuildConfiguration { get; } =
#if JET_AGENT_GUI_TEST
        "AgentGuiTest";
#elif DEBUG
        "Debug";
#else
        "Release";
#endif
}
