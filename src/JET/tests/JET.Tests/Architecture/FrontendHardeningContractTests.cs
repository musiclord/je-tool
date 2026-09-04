using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 2026-07-14 本地收尾第三批的前端靜態接線守衛。這些測試只鎖原語、消費點與先後順序；
/// 真實慢回應與可見性仍由 Windows GUI handoff 驗收。
/// </summary>
public sealed class FrontendHardeningContractTests
{
    [Fact]
    public void Picker_HasVisibleInlineFeedback_AndAllFailureEntrypointsUseIt()
    {
        var state = ReadFrontend("js", "state.js");
        var core = ReadFrontend("js", "ui-core.js");
        var app = ReadFrontend("js", "app.js");
        var css = ReadFrontend("css", "app.css");

        Assert.Contains("pickerFeedback: null", state, StringComparison.Ordinal);
        Assert.Contains("pickerFeedbackContext: null", state, StringComparison.Ordinal);
        Assert.Contains("setPickerFeedback: function", state, StringComparison.Ordinal);
        Assert.Contains(
            "pickerFeedbackHtml(state.pickerFeedback, state.pickerFeedbackContext)",
            app,
            StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", app, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", app, StringComparison.Ordinal);
        Assert.Contains("state.pickerFeedback", ExtractFunction(app, "renderPicker"), StringComparison.Ordinal);
        Assert.Contains(".picker-feedback", css, StringComparison.Ordinal);

        const string errorHook = "onError: function (message) { Store.setPickerFeedback(message); }";
        Assert.Contains(errorHook, ExtractFunction(core, "loadProjects"), StringComparison.Ordinal);
        Assert.Contains(errorHook, ExtractFunction(core, "syncOnlineProjects"), StringComparison.Ordinal);

        var openProject = ExtractFunction(core, "openProject");
        Assert.Contains("onError: function (message, error)", openProject, StringComparison.Ordinal);
        Assert.Contains("projectId: projectId", openProject, StringComparison.Ordinal);
        Assert.Contains("errorCode: error && error.code", openProject, StringComparison.Ordinal);
        Assert.Contains("correlationId: error && error.correlationId", openProject, StringComparison.Ordinal);

        var picker = ExtractFunction(app, "bindPicker");
        Assert.Contains("onError: function (message, error)", picker, StringComparison.Ordinal);
        Assert.Contains("projectId: target.id", picker, StringComparison.Ordinal);
        Assert.Contains("errorCode: error && error.code", picker, StringComparison.Ordinal);
        Assert.Contains("correlationId: error && error.correlationId", picker, StringComparison.Ordinal);
        Assert.Contains("data-action=\"picker-copy-error\"", app, StringComparison.Ordinal);
        Assert.Contains("data-action=\"picker-support-export\"", app, StringComparison.Ordinal);
        Assert.Contains("supportLogExport", picker, StringComparison.Ordinal);
    }

    [Fact]
    public void LatestResponseGuard_IsSharedAcrossPreviewMappingAndFilterConsumers()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var preview = ReadFrontend("js", "data-preview.js");
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        var primitive = ExtractFunction(core, "createLatestResponseGuard");
        Assert.Contains("var issuedRevision = ++revision", primitive, StringComparison.Ordinal);
        Assert.Contains("issue: function", primitive, StringComparison.Ordinal);
        Assert.Contains(
            "return issuedRevision === revision && (!stillCurrent || stillCurrent());",
            primitive,
            StringComparison.Ordinal);
        Assert.Contains("invalidate: function () { revision++; }", primitive, StringComparison.Ordinal);
        Assert.Contains("createLatestResponseGuard: createLatestResponseGuard", core, StringComparison.Ordinal);

        Assert.Contains("previewResponseGuard.issue", preview, StringComparison.Ordinal);
        Assert.Contains("projectId", ExtractFunction(preview, "refresh"), StringComparison.Ordinal);
        Assert.Contains("dataGeneration", ExtractFunction(preview, "refresh"), StringComparison.Ordinal);
        Assert.Contains("Ui.registerWorkflowReset(resetDataPreviewState)", preview, StringComparison.Ordinal);
        var previewReset = ExtractFunction(preview, "resetDataPreviewState");
        Assert.Contains("previewResponseGuard.invalidate", previewReset, StringComparison.Ordinal);
        Assert.Contains("elBody.innerHTML", previewReset, StringComparison.Ordinal);
        Assert.Contains("elNote.innerHTML", previewReset, StringComparison.Ordinal);
        AssertOrder(
            ExtractFunction(preview, "refresh"),
            "previewResponseGuard.issue",
            "if (acceptResponse())",
            "renderPreview(dataset, data)");

        Assert.Contains("sourceResponseGuards.gl", mapping, StringComparison.Ordinal);
        Assert.Contains("sourceResponseGuards.tb", mapping, StringComparison.Ordinal);
        Assert.Contains("sourceResponseGuards[kind].issue", mapping, StringComparison.Ordinal);
        Assert.Contains("sourceResponseGuards.gl.invalidate", mapping, StringComparison.Ordinal);
        Assert.Contains("sourceResponseGuards.tb.invalidate", mapping, StringComparison.Ordinal);
        var sourcePreview = ExtractFunction(mapping, "ensureSourcePreview");
        Assert.Equal(2, Regex.Matches(
            sourcePreview,
            @"if \(!acceptResponse\(\)\) \{ return; \}").Count);
        Assert.Matches(
            new Regex(@"\.then\(function \(data\) \{\s*if \(!acceptResponse\(\)\) \{ return; \}\s*sourceCache\[kind\]", RegexOptions.Singleline),
            sourcePreview);
        Assert.Matches(
            new Regex(@"\.catch\(function \(\) \{\s*if \(!acceptResponse\(\)\) \{ return; \}\s*sourceCache\[kind\]", RegexOptions.Singleline),
            sourcePreview);

        Assert.Contains("draftResponseGuard.issue", filter, StringComparison.Ordinal);
        Assert.Contains("scenarioResponseGuard(index).issue", filter, StringComparison.Ordinal);
        Assert.Contains("matrixResponseGuard.issue", filter, StringComparison.Ordinal);
        Assert.Contains("invalidateScenarioResponseGuards", filter, StringComparison.Ordinal);
        AssertOrder(
            ExtractFunction(filter, "ensureScenarioPreview"),
            "scenarioResponseGuard(index).issue",
            "if (!acceptResponse())",
            "viewState.scenarioPreviews[index] = data.scenario");
        AssertOrder(
            ExtractFunction(filter, "ensureMatrixContent"),
            "matrixResponseGuard.issue",
            "if (!acceptResponse())",
            "viewState.matrixData = data.scenarios || []");
        Assert.Contains("draftResponseGuard.invalidate", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterBackgroundReaders_GuardErrorsBeforeSharedFeedbackAndRemainRetryable()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        AssertGuardedBackgroundFailure(ExtractFunction(filter, "ensureScenarioPreview"));
        AssertGuardedBackgroundFailure(ExtractFunction(filter, "ensureMatrixContent"));
    }

    [Fact]
    public void FilterAsyncReaders_RunConcurrently_AndMutationsReachCentralBusyFeedback()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var scenario = ExtractFunction(filter, "ensureScenarioPreview");
        var matrix = ExtractFunction(filter, "ensureMatrixContent");

        Assert.Contains("Ui.runBackground", scenario, StringComparison.Ordinal);
        Assert.DoesNotContain("Ui.run('", scenario, StringComparison.Ordinal);
        Assert.Contains("Ui.runBackground", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("Ui.run('", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("if (Store.getState().busy) { return; }", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressSave_MergesLatestValue_AndDeparturesOnlyLeaveAfterReleaseSucceeds()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var merger = ExtractFunction(core, "createLatestSaveMerger");

        Assert.Contains("pendingValue", merger, StringComparison.Ordinal);
        Assert.Contains("inFlight", merger, StringComparison.Ordinal);
        Assert.Contains(".then(flush)", merger, StringComparison.Ordinal);
        AssertOrder(merger, "pendingValue = value;", "hasPending = true;", "if (!inFlight)");
        var flush = ExtractFunction(merger, "flush");
        AssertOrder(
            flush,
            "var value = pendingValue;",
            "hasPending = false;",
            ".then(function () { return save(value); })");
        Assert.Contains("createLatestSaveMerger(", core, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(core, @"JetApi\.projectSaveProgress\(").Cast<Match>());

        var release = ExtractFunction(core, "releaseProjectLease");
        Assert.DoesNotContain("runBackground", release, StringComparison.Ordinal);
        AssertOrder(release, "JetApi.projectReleaseLock", "stopHeartbeat()");
        Assert.Contains("if (!Store.getState().project)", release, StringComparison.Ordinal);
        Assert.Contains("Promise.reject(unavailable)", release, StringComparison.Ordinal);

        var retryMessage = ExtractFunction(core, "departureRetryMessage");
        Assert.Contains("state.activeRequestId", retryMessage, StringComparison.Ordinal);
        Assert.Contains("取消作業", retryMessage, StringComparison.Ordinal);
        Assert.Contains("等待作業完成後再重試", retryMessage, StringComparison.Ordinal);
        var visibleGuidance = ExtractFunction(core, "showBusyDepartureGuidance");
        Assert.Contains("Store.setBusyDetail(message)", visibleGuidance, StringComparison.Ordinal);
        Assert.Contains("Store.addMessage(message, 'warn')", visibleGuidance, StringComparison.Ordinal);

        var departureFailure = ExtractFunction(core, "handleProjectDepartureFailure");
        Assert.Contains("operation_in_progress", departureFailure, StringComparison.Ordinal);
        Assert.Contains("state.activeRequestId", departureFailure, StringComparison.Ordinal);
        Assert.Contains("Store.setBusyDetail(message)", departureFailure, StringComparison.Ordinal);
        Assert.Contains("Store.setBusy(false)", departureFailure, StringComparison.Ordinal);
        Assert.Contains("已保留目前畫面", departureFailure, StringComparison.Ordinal);

        var exit = ExtractFunction(core, "exitApp");
        Assert.Contains("if (state.busy)", exit, StringComparison.Ordinal);
        Assert.Contains("showBusyDepartureGuidance('儲存並結束')", exit, StringComparison.Ordinal);
        AssertOrder(exit, "persistProgress", "releaseProjectLease", "hostExitApp");
        AssertOrder(exit, "hostExitApp", "goBackToPicker", "setPickerFeedback");
        Assert.Contains(
            "handleProjectDepartureFailure(error, '儲存並結束')",
            exit,
            StringComparison.Ordinal);

        var back = ExtractFunction(core, "backToPickerFromHeader");
        Assert.Contains("if (state.busy)", back, StringComparison.Ordinal);
        Assert.Contains("showBusyDepartureGuidance('回專案選擇')", back, StringComparison.Ordinal);
        AssertOrder(
            back,
            "if (state.busy)",
            "Store.setBusy(true, '回專案選擇')",
            "persistProgress",
            "releaseProjectLease",
            "Store.setBusy(false)",
            "goBackToPicker");
        Assert.DoesNotContain(".finally(", back, StringComparison.Ordinal);
        Assert.Contains(
            "handleProjectDepartureFailure(error, '回專案選擇')",
            back,
            StringComparison.Ordinal);

        Assert.DoesNotContain("releaseProjectLease", ExtractFunction(core, "goBackToPicker"), StringComparison.Ordinal);
        Assert.Contains("persistProgress(index)", ExtractFunction(core, "gotoStep"), StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWindowClose_RoutesThroughFrontendDepartureAndSharedGateFallback()
    {
        var form = File.ReadAllText(Path.Combine(RepoRoot(), "JET", "Form1.cs"));
        var closing = ExtractCSharpMethod(form, "OnFormClosing");
        var frontendExit = ExtractCSharpMethod(form, "RequestFrontendExitAsync");
        var busyFallback = ExtractCSharpMethod(form, "NotifyBusyNativeExitAsync");

        AssertOrder(
            closing,
            "CloseReason.UserClosing",
            "e.Cancel = true",
            "RequestFrontendExitAsync()");
        Assert.Contains("window.JetUi.exitApp()", frontendExit, StringComparison.Ordinal);
        AssertOrder(
            closing,
            "TryAcquireShutdownExecutionLease",
            "_runtime?.Dispose()");
        Assert.Contains("NotifyBusyNativeExitAsync", closing, StringComparison.Ordinal);
        Assert.Contains("state.activeRequestId", busyFallback, StringComparison.Ordinal);
        Assert.Contains("setBusyDetail(message)", busyFallback, StringComparison.Ordinal);
        Assert.Contains("等待作業完成後再重試關閉視窗", busyFallback, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingEditors_ShareCommitEligibility_AndClassicShowsMissingRail()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var eligibility = ExtractFunction(mapping, "mappingCommitEligibility");
        var grid = ExtractFunction(mapping, "gridEditSection");
        var classic = ExtractFunction(mapping, "classicEditSection");

        Assert.Contains("missingRequired", eligibility, StringComparison.Ordinal);
        Assert.Contains("mappingCommitEligibility", grid, StringComparison.Ordinal);
        Assert.Contains("mappingCommitEligibility", classic, StringComparison.Ordinal);
        Assert.Contains("requiredRailHtml", classic, StringComparison.Ordinal);
        Assert.Contains("disabled", classic, StringComparison.Ordinal);
        Assert.Contains("eligibility.canCommit", grid, StringComparison.Ordinal);
        Assert.Contains("eligibility.canCommit", classic, StringComparison.Ordinal);
    }

    [Fact]
    public void JetApi_RejectsPendingOnUnload_AndProjectLoadIsCancellable()
    {
        var api = ReadFrontend("js", "jet-api.js");
        var app = ReadFrontend("js", "app.js");
        var rejectAll = ExtractFunction(api, "rejectAllPending");

        Assert.Contains("Object.keys(pending)", rejectAll, StringComparison.Ordinal);
        Assert.Contains("delete pending[requestId]", rejectAll, StringComparison.Ordinal);
        Assert.Contains("callbacks.reject", rejectAll, StringComparison.Ordinal);
        Assert.Contains("global.addEventListener('pagehide', rejectAllPending)", api, StringComparison.Ordinal);
        Assert.Contains("'project.load': true", app, StringComparison.Ordinal);
        Assert.Contains("err.correlationId = typeof message.correlationId === 'string'", api, StringComparison.Ordinal);
        Assert.Contains("'support.log.export'", api, StringComparison.Ordinal);
    }

    [Fact]
    public void NonWorkingDayEditor_InvalidatesFrontendResultsOnlyWhenCanonicalValueChanges()
    {
        var import = ReadFrontend("js", "steps", "import-step.js");
        var comparer = ExtractFunction(import, "sameNonWorkingDays");
        var binding = ExtractFunction(import, "bindCalendarCard");

        Assert.Contains("Array.isArray(value) ? value : [0, 6]", comparer, StringComparison.Ordinal);
        Assert.Contains("all.indexOf(day) === index", comparer, StringComparison.Ordinal);
        Assert.Contains("sort(function (a, b) { return a - b; })", comparer, StringComparison.Ordinal);
        var nonWorkingBinding = binding[binding.IndexOf("calendarSetNonWorkingDays", StringComparison.Ordinal)..];
        AssertOrder(
            nonWorkingBinding,
            "var changed = !sameNonWorkingDays",
            "if (changed)",
            "Store.setCalendarState");
    }

    [Fact]
    public void CalendarTask_UsesPersistedCompletionMarkersInsteadOfPositiveRowCounts()
    {
        var import = ReadFrontend("js", "steps", "import-step.js");
        var task = ExtractFunction(import, "calendarTask");
        var card = ExtractFunction(import, "calendarCard");
        var binding = ExtractFunction(import, "bindCalendarCard");

        Assert.Contains(
            "calendar.calendarImported || calendar.nonWorkingDaysConfigured",
            task,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "calendar.holidayCount || calendar.makeupDayCount",
            task,
            StringComparison.Ordinal);
        Assert.Contains("calendar.calendarImported", card, StringComparison.Ordinal);
        Assert.Contains("calendar.nonWorkingDaysConfigured", card, StringComparison.Ordinal);
        Assert.Contains("calendarImported: true", binding, StringComparison.Ordinal);
        Assert.Contains("nonWorkingDaysConfigured: true", binding, StringComparison.Ordinal);
        Assert.Contains(
            "非工作日 無（整週皆工作日）",
            ExtractFunction(import, "nonWorkingSummary"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeFrontend_DoesNotExposeTestCaseLoadersInAnyConfiguration()
    {
        var frontendRoot = Path.Combine(RepoRoot(), "JET", "wwwroot");
        var runtime = string.Join('\n',
            Directory.EnumerateFiles(frontendRoot, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) is ".html" or ".css" or ".js")
                .Select(File.ReadAllText));

        Assert.DoesNotContain("套用測試案件", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("data-action=\"mock-load\"", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("btn--mock", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("mockButtonHtml", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("bindMockButton", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("registerMockLoader", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseUiStrings_UseAuditorVocabulary()
    {
        var frontendRoot = Path.Combine(RepoRoot(), "JET", "wwwroot");
        var releaseSources = Directory.EnumerateFiles(frontendRoot, "*.js", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("dev-panel.js", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("dev-log-panel.js", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("jet-api.js", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText);
        var uiLiterals = string.Join('\n', releaseSources.Select(ExtractJavaScriptStringLiterals));

        var forbiddenDisplayPhrases = new[]
        {
            "已投影",
            "後端權威",
            "Artifact ID",
            "kind 未提供",
            "目前 catalog",
            "wire 或前端 state",
            "資料庫 Provider",
            "操作使用者 ID",
            "WebView2 host",
            "Bridge 連線",
            "Host 未連線",
            "請 DBA",
            "逐行 tag",
            "step4：",
            "step4-1：",
            "AUTHORIZED_PREPARER（",
            "DATE_DIMENSION（",
            "（stale）",
            "（retention）"
        };

        Assert.All(forbiddenDisplayPhrases, phrase =>
            Assert.DoesNotContain(phrase, uiLiterals, StringComparison.Ordinal));

        foreach (var requiredDisplayPhrase in new[]
        {
            "標準化",
            "資料儲存方式",
            "應用程式連線",
        })
        {
            Assert.Contains(requiredDisplayPhrase, uiLiterals, StringComparison.Ordinal);
        }

        var index = ReadFrontend("index.html");
        Assert.DoesNotContain("system.whoAmI", VisibleHtml(index), StringComparison.Ordinal);
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        Assert.DoesNotContain(">key<", ExtractJavaScriptStringLiterals(mapping), StringComparison.Ordinal);
    }

    [Fact]
    public void FilterScenarioActions_StylePreviewAsSecondaryAndSaveAsPrimary()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        Assert.Contains(
            "class=\"btn btn--ghost\" data-action=\"preview-scenario\"",
            filter,
            StringComparison.Ordinal);
        Assert.Contains(
            "class=\"btn\" data-action=\"save-scenario\"",
            filter,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderProjectFolderAction_IsWorkflowOnlyAndUsesServerResolvedTarget()
    {
        var index = ReadFrontend("index.html");
        var app = ReadFrontend("js", "app.js");

        Assert.Contains("data-action=\"open-project-folder\"", index, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"data-action=""open-project-folder""[^>]*hidden", RegexOptions.CultureInvariant),
            index);
        Assert.Contains("openProjectFolder.hidden = true", app, StringComparison.Ordinal);
        Assert.Contains("openProjectFolder.hidden = false", app, StringComparison.Ordinal);
        Assert.Contains(
            "global.JetApi.hostOpenFolder({ target: 'projectFolder' })",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain("hostOpenFolder({ path:", app, StringComparison.Ordinal);
    }

    private static void AssertGuardedBackgroundFailure(string source)
    {
        Assert.Contains(".catch(function (error)", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(
            source,
            @"if \(!acceptResponse\(\)\) \{ return; \}").Count);
        Assert.Matches(
            new Regex(
                @"\.catch\(function \(error\) \{[\s\S]*?if \(!acceptResponse\(\)\) \{ return; \}[\s\S]*?載入失敗[\s\S]*?throw error;\s*\}",
                RegexOptions.CultureInvariant),
            source);
    }

    private static string ExtractJavaScriptStringLiterals(string source)
    {
        var withoutComments = Regex.Replace(
            source,
            @"/\*[\s\S]*?\*/|(?m)^\s*//.*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        return string.Join('\n', Regex.Matches(
                withoutComments,
                @"(['""])(?:\\.|(?!\1).)*\1",
                RegexOptions.Singleline | RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Value));
    }

    private static string VisibleHtml(string source)
        => Regex.Replace(
            source,
            @"<!--[\s\S]*?-->",
            string.Empty,
            RegexOptions.CultureInvariant);

    private static void AssertOrder(string source, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(current > previous, $"預期 '{marker}' 位於前一個離場步驟之後。\n{source}");
            previous = current;
        }
    }

    private static string ExtractFunction(string source, string name)
    {
        var start = source.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {name} 函式。");

        var depth = 0;
        var opened = false;
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
                opened = true;
            }
            else if (source[i] == '}' && opened && --depth == 0)
            {
                return source[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"找不到 {name} 函式結尾。");
    }

    private static string ExtractCSharpMethod(string source, string name)
    {
        var start = source.IndexOf("void " + name + "(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {name} 方法。");

        var bodyStart = source.IndexOf('{', start);
        Assert.True(bodyStart >= 0, $"找不到 {name} 方法本體。");
        var depth = 0;
        for (var i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"找不到 {name} 方法結尾。");
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }.Concat(segments).ToArray()));

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
}
