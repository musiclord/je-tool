using JET.Application;
using JET.Bridge;

namespace JET;

/// <summary>
/// Gui 測試版的組裝：照正式的組裝方式建立 runtime，再套上這次啟用的夾具，並在啟動後建立測試案件。
/// </summary>
public static partial class AppCompositionRoot
{
    internal static JetApplicationRuntime CreateAgentGuiTestRuntime(
        IHostShell hostShell,
        AgentGuiTestProfile profile,
        IJetEventPublisher eventPublisher)
    {
        var fixtures = new AgentGuiTestFixtures(profile);
        var releaseVisibleSurface = profile.FixtureIds.Contains(
            AgentGuiTestFixtures.ReleaseVisibleSurfaceId,
            StringComparer.Ordinal);
        return CreateRuntime(
            hostShell,
            profile.ProjectsRootPath,
            enableDevTools: !releaseVisibleSurface,
            eventPublisher: fixtures.DecorateEventPublisher(eventPublisher),
            diagnosticLoggerProvider: null,
            diagnosticLogDirectory: profile.DiagnosticLogDirectory,
            principalName: AgentGuiTestProfile.IsolatedPrincipal,
            userProfileDirectory: profile.UserProfileDirectory,
            decorateHandler: fixtures.DecorateHandler,
            startupWork: fixtures.SeedsProjectState ? fixtures.SeedProjectStateAsync : null);
    }
}
