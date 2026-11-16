using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// import.holiday 與 import.makeupDay 的共同流程。
/// Payload 是 `{ dates: ["yyyy-MM-dd", …] }`。每次匯入整份取代同一種日期：先清掉原本的，再寫入這一批；重複的日期只存一次。
/// </summary>
public abstract class ImportCalendarHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly ILogger? logger;

    internal ImportCalendarHandler(
        IProjectStore projectStore,
        ProjectSession session,
        ILogger? logger = null)
    {
        this.projectStore = projectStore;
        this.session = session;
        this.logger = logger;
    }

    public abstract string Action { get; }

    protected abstract CalendarDayType DayType { get; }

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        var dates = PayloadReader.GetStringList(payload, "dates");
        var plan = JetAuditProgram.Plan(
            new CalendarInlineRequest(Action, projectId, DayType, dates));
        var entries = (plan.Dates
                ?? throw new InvalidOperationException("Inline 行事曆 plan 缺少正規化日期。"))
            .Select(date => new CalendarDayEntry(date, null))
            .ToList();
        var facts = await repositories.ReferenceDataFacts.ExecuteAsync(
            plan,
            entries,
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);

        await MarkCalendarImportedAsync(document);

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore, plan.Effects, logger);
        return new { count = result.Count,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reports = mutationState.Reports,
            reportWarning = mutationState.ReportWarning };
    }

    private async Task MarkCalendarImportedAsync(ProjectDocument document)
    {
        var updated = document with { CalendarImported = true };
        if (document.CalendarImported != true)
        {
            // 日期清單已經整份取代並提交。接著把 CalendarImported 記成 true，這一步不理會呼叫端的取消要求；
            // 否則匯入零筆的清單已經生效，案件卻因為取消而一直停在「從未匯入」。存檔失敗仍往外丟；重新匯入一次，結果相同。
            await projectStore.SaveAsync(updated, CancellationToken.None);
        }
    }
}

public sealed class ImportHolidayHandler : ImportCalendarHandler
{
    internal ImportHolidayHandler(
        IProjectStore projectStore,
        ProjectSession session,
        ILogger? logger = null)
        : base(projectStore, session, logger)
    {
    }

    public override string Action => "import.holiday";

    protected override CalendarDayType DayType => CalendarDayType.Holiday;
}

public sealed class ImportMakeupDayHandler : ImportCalendarHandler
{
    internal ImportMakeupDayHandler(
        IProjectStore projectStore,
        ProjectSession session,
        ILogger? logger = null)
        : base(projectStore, session, logger)
    {
    }

    public override string Action => "import.makeupDay";

    protected override CalendarDayType DayType => CalendarDayType.Makeup;
}

/// <summary>
/// import.holiday.fromFile 與 import.makeupDay.fromFile：匯入事務所的行事曆檔。
/// 只收 .xlsx，標頭在第 2 列（LeadingRowsToSkip=1）。哪一欄是日期、怎麼轉成日期清單，由 AuditCore 決定。
/// 每次匯入整份取代同一種日期，store 在同一個交易裡清掉受影響的規則結果。
/// </summary>
public abstract class ImportCalendarFromFileHandler : IApplicationActionHandler
{
    private readonly ITabularFileReader reader;
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly ILogger? logger;

    internal ImportCalendarFromFileHandler(
        ITabularFileReader reader,
        IProjectStore projectStore,
        ProjectSession session,
        ILogger? logger = null)
    {
        this.reader = reader;
        this.projectStore = projectStore;
        this.session = session;
        this.logger = logger;
    }

    public abstract string Action { get; }

    protected abstract CalendarDayType DayType { get; }

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        var filePath = PayloadReader.GetRequiredString(payload, "filePath");
        var sheetName = PayloadReader.GetOptionalString(payload, "sheetName");

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var plan = JetAuditProgram.Plan(
            new CalendarFileRequest(
                Action,
                projectId,
                DayType,
                filePath,
                File.Exists(filePath),
                extension));

        // 欄名固定在第 2 列，畫面不能改欄名列；找不到欄名時照舊說工作表是空的。
        var request = new TabularSourceRequest(filePath, SheetName: sheetName, LeadingRowsToSkip: 1, FixedLeadingRows: true);
        // Excel 開著的檔案照樣匯入最後一次存檔的內容，匯入完成後提醒。
        var fileName = ImportSourceFileName.Resolve(filePath, PayloadReader.GetOptionalString(payload, "fileName"));
        var openElsewhereWarnings = ImportMessages.SourceFilesOpenElsewhere(reader, [(filePath, fileName)]);

        var entries = await Task.Run(async () =>
        {
            var columns = await reader.ReadColumnsAsync(request, cancellationToken);
            var rows = reader.ReadRowsAsync(request, cancellationToken);
            return await JetAuditProgram.ProjectCalendarFileAsync(
                plan,
                columns,
                rows,
                cancellationToken);
        }, cancellationToken);

        var facts = await repositories.ReferenceDataFacts.ExecuteAsync(
            plan,
            entries,
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);

        await MarkCalendarImportedAsync(document);

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore, plan.Effects, logger);
        // 和其他匯入一樣：warnings 是全部提醒，開著的檔案那則另外列一份，畫面放在日曆卡片的匯入結果旁。
        return new { count = result.Count,
            warnings = openElsewhereWarnings,
            openElsewhereWarnings,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reports = mutationState.Reports,
            reportWarning = mutationState.ReportWarning };
    }

    private async Task MarkCalendarImportedAsync(ProjectDocument document)
    {
        var updated = document with { CalendarImported = true };
        if (document.CalendarImported != true)
        {
            // 檔案已經整份轉成日期清單，取代也已提交；和 ImportCalendarHandler 一樣，記下 CalendarImported 時不理會呼叫端的取消要求。
            await projectStore.SaveAsync(updated, CancellationToken.None);
        }
    }
}

public sealed class ImportHolidayFromFileHandler : ImportCalendarFromFileHandler
{
    internal ImportHolidayFromFileHandler(
        ITabularFileReader reader,
        IProjectStore projectStore,
        ProjectSession session,
        ILogger? logger = null)
        : base(reader, projectStore, session, logger)
    {
    }

    public override string Action => "import.holiday.fromFile";

    protected override CalendarDayType DayType => CalendarDayType.Holiday;
}

public sealed class ImportMakeupDayFromFileHandler : ImportCalendarFromFileHandler
{
    internal ImportMakeupDayFromFileHandler(
        ITabularFileReader reader,
        IProjectStore projectStore,
        ProjectSession session,
        ILogger? logger = null)
        : base(reader, projectStore, session, logger)
    {
    }

    public override string Action => "import.makeupDay.fromFile";

    protected override CalendarDayType DayType => CalendarDayType.Makeup;
}

/// <summary>
/// calendar.setNonWorkingDays：設定這個案件一週哪幾天是休息日，用 .NET DayOfWeek 的數字，週日是 0、週六是 6，
/// 預設週六與週日。設定存在 project.json，會影響週末過帳、週末核准兩條預篩選規則與週末的篩選條件。
/// Payload 是 `{ days: [int, …] }`；值要在 0 到 6 之間，否則回 invalid_payload。
/// </summary>
public sealed class CalendarSetNonWorkingDaysHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly ILogger? logger;

    internal CalendarSetNonWorkingDaysHandler(
        IProjectStore projectStore,
        ProjectSession session,
        ILogger? logger = null)
    {
        this.projectStore = projectStore;
        this.session = session;
        this.logger = logger;
    }

    public string Action => "calendar.setNonWorkingDays";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        // 先驗證 days，再讀案件：即使 session 指到已經不存在的案件，days 無效時仍然先回 invalid_payload。
        var requested = JetAuditProgram.ValidateNonWorkingDays(
            PayloadReader.GetIntList(payload, "days"));

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);
        var plan = JetAuditProgram.Plan(
            new NonWorkingDaysRequest(
                projectId,
                document.NonWorkingDays,
                requested));

        // 先清掉依休息日算出的結果，再存新設定。清除失敗時設定還是舊值，重試會再清一次；
        // 順序反過來的話，設定已經存好，重試時因為新舊值相同而跳過清除，舊的命中就留下來了。
        if (plan.ShouldExecute)
        {
            await repositories.ReferenceDataFacts.ExecuteAsync(plan, cancellationToken);
        }

        var result = JetAuditProgram.Finalize(plan);
        if (plan.ShouldExecute)
        {
            await projectStore.SaveAsync(document with { NonWorkingDays = result.NormalizedDays }, cancellationToken);
        }

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(
            projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore, plan.ShouldExecute ? plan.Effects : null, logger);
        return new { ok = true, nonWorkingDays = result.NormalizedDays,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reports = mutationState.Reports,
            reportWarning = mutationState.ReportWarning };
    }
}
