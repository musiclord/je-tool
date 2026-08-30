using System.Text.RegularExpressions;
using JET.Domain;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 前後端 action 清單的漂移守衛（2026-07-03 複審測試債）：前端 <c>jet-api.js</c> 的
/// <c>SUPPORTED_ACTIONS</c> 必須涵蓋後端 dispatcher 的 <c>RegisteredActions</c>。
/// 前端沒有自動測試——action 改名/新增只改一邊時，前端會默默拿到 unknown action 的 bridge_error；
/// 這條守衛把該缺口升級為 CI 可擋。兩邊的共同 source of truth 是 docs/action-contract-manifest.md
/// （新增順序：manifest → SUPPORTED_ACTIONS → handler）。
/// 後端以 enableDevTools:true 組裝（dev.* 只在 Debug 註冊；前端清單含 dev.*，由 system.ping 的
/// devToolsEnabled 在 Release 隱藏面板，而非從清單移除）。五個 demo facade method 同樣靜態保留，
/// 但 Release composition 不註冊其 handler；Release 的前端單邊差異必須精確等於這五項。
/// </summary>
public sealed class SupportedActionsParityTests
{
    [Fact]
    public void FrontendSupportedActions_MatchDispatcherForBuildConfiguration()
    {
        var frontendActions = ReadFrontendSupportedActions();
        Assert.NotEmpty(frontendActions); // 空集合守門：解析失敗不得真空通過

        using var host = new HandlerTestHost(enableDevTools: true);
        var backendActions = host.Dispatcher.RegisteredActions
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

#if DEBUG || JET_AGENT_GUI_TEST
        Assert.Equal(frontendActions, backendActions);
#else
        string[] buildConditionalDemoActions =
        [
            "project.loadDemo",
            "demo.exportGlFile",
            "demo.exportTbFile",
            "demo.exportAccountMappingFile",
            "demo.exportAuthorizedPreparerFile"
        ];
        var frontendOnly = frontendActions
            .Except(backendActions, StringComparer.Ordinal)
            .OrderBy(static action => action, StringComparer.Ordinal)
            .ToArray();
        var backendOnly = backendActions
            .Except(frontendActions, StringComparer.Ordinal)
            .OrderBy(static action => action, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            buildConditionalDemoActions
                .OrderBy(static action => action, StringComparer.Ordinal)
                .ToArray(),
            frontendOnly);
        Assert.Empty(backendOnly);
#endif
    }

    [Fact]
    public void FrontendCancellableActions_AreRegisteredAndClassified()
    {
        var appPath = Path.Combine(RepoRoot(), "JET", "wwwroot", "js", "app.js");
        var source = File.ReadAllText(appPath);
        var objectMatch = Regex.Match(
            source, @"CANCELLABLE_ACTIONS\s*=\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(objectMatch.Success, "app.js 內找不到 CANCELLABLE_ACTIONS 物件。");

        var cancellable = Regex.Matches(objectMatch.Groups["body"].Value, @"'(?<action>[^']+)'\s*:\s*true")
            .Select(match => match.Groups["action"].Value)
            .ToArray();
        Assert.NotEmpty(cancellable);

        var formalExports = new[]
        {
            "export.validationArtifacts",
            "export.accountMappingTemplate",
            "export.prescreenReport",
            "export.criteriaSelectionReport",
            "export.workpaperStream"
        };
        Assert.All(formalExports, action => Assert.Contains(action, cancellable));

        using var host = new HandlerTestHost(enableDevTools: true);
        foreach (var action in cancellable)
        {
            Assert.Contains(action, host.Dispatcher.RegisteredActions);
            Assert.True(ActionExecutionPolicy.IsClassified(action), $"取消鈕 action 未受 execution policy 管理：{action}");
            Assert.NotEqual("operation.cancel", action);
        }
    }

    /// <summary>從 wwwroot/js/jet-api.js 解析 SUPPORTED_ACTIONS 陣列的字串常值（排序後回傳）。</summary>
    private static List<string> ReadFrontendSupportedActions()
    {
        var jetApiPath = Path.Combine(RepoRoot(), "JET", "wwwroot", "js", "jet-api.js");
        Assert.True(File.Exists(jetApiPath), $"找不到前端 API 檔：{jetApiPath}");

        var source = File.ReadAllText(jetApiPath);
        var arrayMatch = Regex.Match(
            source, @"SUPPORTED_ACTIONS\s*=\s*\[(?<body>[^\]]*)\]", RegexOptions.Singleline);
        Assert.True(arrayMatch.Success, "jet-api.js 內找不到 SUPPORTED_ACTIONS 陣列（宣告形狀改了就同步更新本守衛）。");

        return Regex.Matches(arrayMatch.Groups["body"].Value, @"'(?<action>[^']+)'")
            .Select(m => m.Groups["action"].Value)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>由測試組件位置向上尋 `JET.slnx`，定位 repo 根（同 SchemaIsolationGuardTests 的慣例）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JET.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
    }
}
