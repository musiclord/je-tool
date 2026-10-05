using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 使用者可複製的 app_message 精簡紀錄：耗時與最後進度只在操作結算時濃縮，
/// 不把逐筆 export.progress 寫成訊息。
/// </summary>
public sealed class OperationLogFrontendTests
{
    [Fact]
    public void Run_LogsOptInCompletion_AndAllFailuresIncludeElapsedAndLastProgress()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var run = ExtractFunction(core, "run");

        Assert.Contains("monotonicNowMilliseconds()", run, StringComparison.Ordinal);
        Assert.Contains("options.logCompletion", run, StringComparison.Ordinal);
        Assert.Contains("完成，耗時 ", run, StringComparison.Ordinal);
        Assert.Contains("formatElapsedMilliseconds", run, StringComparison.Ordinal);
        Assert.Contains("Store.getState().busyDetail", run, StringComparison.Ordinal);
        Assert.Contains("最後進度：", run, StringComparison.Ordinal);
        Assert.Contains("operation_cancelled", run, StringComparison.Ordinal);
        AssertOrder(
            run,
            "var lastProgress = Store.getState().busyDetail;",
            "Store.addMessage(message, level);",
            "Store.setBusy(false);");
    }

    [Fact]
    public void WorkflowLongOperations_ExplicitlyOptIntoOneCompletionSummary()
    {
        var validation = ReadFrontend("js", "steps", "validate-step.js");
        var import = ReadFrontend("js", "steps", "import-step.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var export = ReadFrontend("js", "steps", "export-step.js");
        var combined = validation + import + filter + export;

        foreach (var label in new[]
        {
            "匯入授權編製人員清單",
            "上傳假日檔",
            "上傳補班檔",
            "匯入科目配對",
            "重新產生空白科目配對範本",
            "產生驗證階段報告",
            "產生預篩選報告",
            "執行驗證並產生報告",
            "執行預篩選",
            "預覽篩選情境",
            // 2026-10-03 用語統一 W10：使用者裁定以「已儲存」為準，操作名稱改成「儲存」（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
            "儲存篩選情境",
            // 2026-10-02 整體複審 W13：按鈕與操作名稱改成「產生條件篩選報告」，不再重複「完成」。
            "產生條件篩選報告",
            "產生工作底稿",
            "產生工作底稿與預篩選報告"
        })
        {
            Assert.Contains($"Ui.run('{label}'", combined, StringComparison.Ordinal);
        }

        Assert.True(
            Regex.Matches(combined, @"logCompletion\s*:\s*true").Count >= 13,
            "關鍵長操作必須明示 opt-in；不得讓所有 Ui.run 無差別增加成功訊息。");
        Assert.True(
            Regex.Matches(combined, @"logCompletionWhen\s*:").Count >= 4,
            "含檔案選擇器的操作必須排除 resolved no-op，不能把取消選檔記成成功。");
    }

    [Fact]
    public void MessagePanel_CopiesRecentOneHundredWithProjectIdentityAsChronologicalTabSeparatedText()
    {
        var index = ReadFrontend("index.html");
        var app = ReadFrontend("js", "app.js");
        var css = ReadFrontend("css", "app.css");
        var formatter = ExtractFunction(app, "formatMessageLogForCopy");
        var cell = ExtractFunction(formatter, "cell");
        var copy = ExtractFunction(app, "copyRecentMessages");
        var clipboard = ExtractFunction(app, "writeClipboardText");

        Assert.Contains("data-action=\"messages-copy\"", index, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"複製最近 100 則操作紀錄\"", index, StringComparison.Ordinal);
        Assert.Contains(".messages__header-actions", css, StringComparison.Ordinal);
        Assert.Contains(".messages__copy", css, StringComparison.Ordinal);

        var headerMatch = Regex.Match(
            formatter,
            @"var\s+lines\s*=\s*\['(?<header>[^']*)'\]\s*;",
            RegexOptions.CultureInvariant);
        Assert.True(headerMatch.Success, "複製紀錄 formatter 必須宣告固定 TSV header。");
        Assert.Equal(
            new[] { "project_id", "project_code", "database_provider", "occurred_utc", "level", "text" },
            headerMatch.Groups["header"].Value.Split(new[] { "\\t" }, StringSplitOptions.None));

        var rowMatch = Regex.Match(
            formatter,
            @"lines\.push\(\s*\[\s*(?<cells>.*?)\s*\]\.join\('\\t'\)\s*\);",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        Assert.True(rowMatch.Success, "複製紀錄 formatter 必須以單一六欄陣列形成資料列。");
        var compactCells = Regex.Replace(rowMatch.Groups["cells"].Value, @"\s+", string.Empty);
        Assert.Equal(
            "cell(identity.projectId)," +
            "cell(identity.projectCode)," +
            "cell(identity.databaseProvider)," +
            "cell(entry.occurredUtc)," +
            "cell(entry.level)," +
            "cell(entry.text)",
            compactCells);
        Assert.Equal(6, Regex.Matches(compactCells, @"cell\(").Count);
        Assert.Single(Regex.Matches(formatter, @"lines\.push\(").Cast<Match>());

        Assert.Contains("operationLogIdentity(project)", formatter, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"\(entries\s*\|\|\s*\[\]\)\.slice\(0,\s*100\)\.reverse\(\)\.forEach\(function\s*\(entry\)",
                RegexOptions.CultureInvariant),
            formatter);
        Assert.Single(Regex.Matches(formatter, @"\.slice\(0,\s*100\)").Cast<Match>());
        Assert.Single(Regex.Matches(formatter, @"\.reverse\(\)").Cast<Match>());
        Assert.Contains(
            "return String(value == null ? '' : value).replace(/[\\t\\r\\n]+/g, ' ');",
            Regex.Replace(cell, @"\s+", " "),
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(cell, @"\.replace\(").Cast<Match>());

        Assert.Contains("logRecent({ limit: 100 })", copy, StringComparison.Ordinal);
        Assert.Contains("flushMessageWrites()", copy, StringComparison.Ordinal);
        AssertOrder(copy, "flushMessageWrites()", "logRecent({ limit: 100 })");
        Assert.Contains("formatMessageLogForCopy(messages, project)", copy, StringComparison.Ordinal);
        Assert.Contains("writeClipboardText", copy, StringComparison.Ordinal);
        Assert.Contains("copyRecentMessages(copyMessagesButton)", app, StringComparison.Ordinal);
        Assert.Contains("navigator.clipboard.writeText", clipboard, StringComparison.Ordinal);
        Assert.Contains("document.execCommand('copy')", clipboard, StringComparison.Ordinal);
    }

    [Fact]
    public void MessagePanel_CopyRequiresTheSameProjectSessionBeforeAndAfterRecentRead()
    {
        var app = ReadFrontend("js", "app.js");
        var sessionGuard = ExtractFunction(app, "operationLogSessionIsCurrent");
        var sessionRequirement = ExtractFunction(app, "requireOperationLogSession");
        var copy = ExtractFunction(app, "copyRecentMessages");

        Assert.Contains("var currentProject = Store.getState().project;", sessionGuard, StringComparison.Ordinal);
        Assert.Contains(
            "returnprojectId!=null&&projectId!==''&&!!currentProject&&" +
            "currentProject===project&&currentProject.projectId===projectId;",
            Regex.Replace(sessionGuard, @"\s+", string.Empty),
            StringComparison.Ordinal);
        Assert.Contains("if (operationLogSessionIsCurrent(project, projectId)) { return; }", sessionRequirement, StringComparison.Ordinal);
        Assert.Contains("operation_log_session_changed", sessionRequirement, StringComparison.Ordinal);
        Assert.Contains("throw error;", sessionRequirement, StringComparison.Ordinal);
        Assert.Contains("案件已切換，未複製", copy, StringComparison.Ordinal);

        Assert.Equal(
            3,
            Regex.Matches(
                copy,
                @"requireOperationLogSession\(project,\s*projectId\);",
                RegexOptions.CultureInvariant).Count);
        AssertOrder(
            copy,
            "var projectId = project.projectId;",
            "requireOperationLogSession(project, projectId);",
            "flushMessageWrites()",
            "requireOperationLogSession(project, projectId);",
            "global.JetApi.logRecent({ limit: 100 });",
            "requireOperationLogSession(project, projectId);",
            "formatMessageLogForCopy(messages, project)");
    }

    [Fact]
    public void MessagePanel_CopyWithoutActiveProject_LeavesIdentityCellsEmptyAndSkipsBackendRead()
    {
        var app = ReadFrontend("js", "app.js");
        var identity = ExtractFunction(app, "operationLogIdentity");
        var copy = ExtractFunction(app, "copyRecentMessages");
        var noProjectBranch = ExtractBlockAfterToken(copy, "if (!project)");

        // 案件名稱（projectId）是唯一必要識別；案件編號已是選填，留空時仍要能辨識這份紀錄屬於哪個案件。
        Assert.Contains(
            "project && project.projectId != null ? project.projectId : ''",
            identity,
            StringComparison.Ordinal);
        Assert.Contains(
            "project && project.projectCode != null ? project.projectCode : ''",
            identity,
            StringComparison.Ordinal);
        Assert.Contains(
            "project && project.databaseProvider != null ? project.databaseProvider : ''",
            identity,
            StringComparison.Ordinal);
        Assert.DoesNotContain("providerText", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("caseId", identity, StringComparison.Ordinal);

        Assert.Contains("尚未開啟案件", noProjectBranch, StringComparison.Ordinal);
        Assert.Contains("return Promise.resolve();", noProjectBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("logRecent", noProjectBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("writeClipboardText", noProjectBranch, StringComparison.Ordinal);
        Assert.Single(
            Regex.Matches(
                    copy,
                    @"global\.JetApi\.logRecent\(\{\s*limit:\s*100\s*\}\)",
                    RegexOptions.CultureInvariant)
                .Cast<Match>());
        AssertOrder(
            copy,
            "var project = Store.getState().project;",
            noProjectBranch,
            "var projectId = project.projectId;",
            "global.JetApi.logRecent({ limit: 100 });");
    }

    [Fact]
    public void MessagePersistence_IsOrderedRetriableAndOnlyAcknowledgedWritesAdvanceWatermark()
    {
        var app = ReadFrontend("js", "app.js");
        var persist = ExtractFunction(app, "persistNewMessages");
        var flush = ExtractFunction(app, "flushMessageWrites");

        Assert.Contains("pendingMessageWrites.push", persist, StringComparison.Ordinal);
        Assert.Contains("flushMessageWrites().catch", persist, StringComparison.Ordinal);
        Assert.Contains("messagePersistQueue", app, StringComparison.Ordinal);
        Assert.Contains(".catch(function () {})", flush, StringComparison.Ordinal);
        Assert.Contains("global.JetApi.logAppend", flush, StringComparison.Ordinal);
        AssertOrder(
            flush,
            "global.JetApi.logAppend",
            "pendingMessageWrites.shift();",
            "lastPersistedId = Math.max");
    }

    [Fact]
    public void CancellationRequest_PersistsTheLatestProgressBeforeTheOperationSettles()
    {
        var app = ReadFrontend("js", "app.js");

        Assert.Contains("if (!result.requested)", app, StringComparison.Ordinal);
        Assert.Contains("var snapshot = latest.busyDetail", app, StringComparison.Ordinal);
        Assert.Contains("已要求取消「", app, StringComparison.Ordinal);
        Assert.Contains("當時進度：", app, StringComparison.Ordinal);
        Assert.Contains("latest.busyLabel || '目前作業'", app, StringComparison.Ordinal);
    }

    private static void AssertOrder(string source, params string[] tokens)
    {
        var previous = -1;
        foreach (var token in tokens)
        {
            var current = source.IndexOf(token, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"找不到或順序錯誤：{token}");
            previous = current;
        }
    }

    private static string ExtractFunction(string source, string name)
    {
        var start = source.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {name} 函式。");

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

        throw new InvalidOperationException($"找不到 {name} 函式結尾。");
    }

    private static string ExtractBlockAfterToken(string source, string token)
    {
        var tokenStart = source.IndexOf(token, StringComparison.Ordinal);
        Assert.True(tokenStart >= 0, $"找不到區塊起點：{token}");

        var blockStart = source.IndexOf('{', tokenStart + token.Length);
        Assert.True(blockStart >= 0, $"找不到區塊左大括號：{token}");

        var depth = 0;
        for (var index = blockStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}' && --depth == 0)
            {
                return source[tokenStart..(index + 1)];
            }
        }

        throw new InvalidOperationException($"找不到區塊右大括號：{token}");
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(
            Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }.Concat(segments).ToArray()));

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
