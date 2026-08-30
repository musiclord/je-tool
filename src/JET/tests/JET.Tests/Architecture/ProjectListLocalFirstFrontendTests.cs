using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>Picker local-first／manual-online workflow mirror guards.</summary>
public sealed class ProjectListLocalFirstFrontendTests
{
    [Fact]
    public void Picker_UsesLocalActionForBootstrapAndRefresh_AndOnlineActionOnlyForManualSync()
    {
        var api = ReadFrontend("jet-api.js");
        var core = ReadFrontend("ui-core.js");
        var app = ReadFrontend("app.js");

        Assert.Contains("'project.listLocal'", api, StringComparison.Ordinal);

        var loadLocal = ExtractFunction(core, "loadProjects");
        Assert.Contains("JetApi.projectListLocal({})", loadLocal, StringComparison.Ordinal);
        Assert.DoesNotContain("JetApi.projectList({})", loadLocal, StringComparison.Ordinal);
        Assert.Contains("Store.setProjects(data.projects || [], null)", loadLocal, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(loadLocal, Regex.Escape("if (!acceptResponse()) { return; }")).Count);
        AssertGuardPrecedesPublicationAndStaleFailureIsSwallowed(loadLocal);

        var syncOnline = ExtractFunction(core, "syncOnlineProjects");
        Assert.Contains("JetApi.projectList({})", syncOnline, StringComparison.Ordinal);
        Assert.Contains("pickerProjectResponseGuard.issue", syncOnline, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(syncOnline, Regex.Escape("if (!acceptResponse()) { return; }")).Count);
        Assert.Contains("Store.setProjects(data.projects || [], data.online)", syncOnline, StringComparison.Ordinal);
        AssertGuardPrecedesPublicationAndStaleFailureIsSwallowed(syncOnline);

        Assert.Contains("data-action=\"picker-refresh-local\"", app, StringComparison.Ordinal);
        Assert.Contains(">重新整理本機</button>", app, StringComparison.Ordinal);
        Assert.Contains("data-action=\"picker-sync-online\"", app, StringComparison.Ordinal);
        Assert.Contains(">同步線上案件</button>", app, StringComparison.Ordinal);
        Assert.Contains("'project.list': true", app, StringComparison.Ordinal);
    }

    private static void AssertGuardPrecedesPublicationAndStaleFailureIsSwallowed(string function)
    {
        const string guard = "if (!acceptResponse()) { return; }";
        const string initialFeedbackReset = "Store.setPickerFeedback(null);";
        var feedbackReset = function.IndexOf(initialFeedbackReset, StringComparison.Ordinal);
        var factoryStart = function.IndexOf("var acceptResponse =", StringComparison.Ordinal);
        var thenStart = function.IndexOf(".then(function (data)", StringComparison.Ordinal);
        var successGuard = function.IndexOf(guard, thenStart, StringComparison.Ordinal);
        var publication = function.IndexOf("Store.setProjects(", thenStart, StringComparison.Ordinal);
        var catchStart = function.IndexOf(".catch(function (error)", publication, StringComparison.Ordinal);
        var failureGuard = function.IndexOf(guard, catchStart, StringComparison.Ordinal);
        var rethrow = function.IndexOf("throw error;", failureGuard, StringComparison.Ordinal);

        Assert.True(feedbackReset >= 0 && feedbackReset < factoryStart && factoryStart < thenStart,
            "Response guard ticket must be issued before the request promise chain.");
        Assert.True(thenStart < successGuard && successGuard < publication,
            "Accepted-response guard must run before Store publication.");
        Assert.True(publication < catchStart && catchStart < failureGuard && failureGuard < rethrow,
            "Stale failure must return before it can reach Ui.run's shared catch.");

        AssertNoStorePublication(
            function[..feedbackReset],
            "before the intentional request-start feedback reset");
        AssertNoStorePublication(
            function[(feedbackReset + initialFeedbackReset.Length)..thenStart],
            "after the request-start feedback reset and before the success callback");
        AssertNoStorePublication(
            function[thenStart..successGuard],
            "inside the success callback before its guard");
        AssertNoStorePublication(
            function[catchStart..failureGuard],
            "inside the failure callback before its guard");
        Assert.DoesNotContain(
            "Store.setProjects(",
            function[catchStart..],
            StringComparison.Ordinal);
    }

    private static void AssertNoStorePublication(string segment, string stage)
    {
        var publications = Regex.Matches(
                segment,
                @"\bStore\.(?<member>[A-Za-z_$][\w$]*)")
            .Cast<Match>()
            .Select(match => match.Groups["member"].Value)
            .Where(member => !string.Equals(member, "getState", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            publications.Length == 0,
            $"Picker list response published through Store {stage}: {string.Join(", ", publications)}");
    }

    [Fact]
    public void Picker_InvalidatesLateListResponsesBeforeLeavingOrOpeningAProject()
    {
        var core = ReadFrontend("ui-core.js");
        var app = ReadFrontend("app.js");

        Assert.Contains(
            "var pickerProjectResponseGuard = createLatestResponseGuard();",
            core,
            StringComparison.Ordinal);
        Assert.Contains(
            "pickerProjectResponseGuard.invalidate();",
            ExtractFunction(core, "openProject"),
            StringComparison.Ordinal);
        Assert.Contains(
            "pickerProjectResponseGuard.invalidate();",
            ExtractFunction(core, "goBackToPicker"),
            StringComparison.Ordinal);
        Assert.Contains(
            "Ui.invalidateProjectListRequests();",
            ExtractFunction(app, "bindPicker"),
            StringComparison.Ordinal);
    }

    private static string ReadFrontend(string fileName) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "JET", "wwwroot", "js", fileName));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }

    private static string ExtractFunction(string source, string name)
    {
        var start = source.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {name} 函式。\n{Regex.Escape(name)}");

        var depth = 0;
        var opened = false;
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
                opened = true;
            }
            else if (source[index] == '}' && opened && --depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new Xunit.Sdk.XunitException($"無法擷取 {name} 函式。");
    }
}
