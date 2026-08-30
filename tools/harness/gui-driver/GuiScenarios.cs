using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

internal static class GuiScenarios
{
    private const int MaximumProjectDocumentBytes = 1_048_576;

    private const string CreateFormProbeScript = """
        (function () {
          function visible(element) {
            if (!element || element.hidden) { return false; }
            var style = window.getComputedStyle(element);
            var rect = element.getBoundingClientRect();
            return style.display !== 'none' && style.visibility !== 'hidden'
              && Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
          }
          function field(name) {
            var element = document.querySelector('[data-bind="create-form"] [name="' + name + '"]');
            var rect = element ? element.getBoundingClientRect() : null;
            return {
              visible: visible(element),
              value: element ? element.value : '',
              x: rect ? rect.left + (rect.width / 2) : 0,
              y: rect ? rect.top + (rect.height / 2) : 0
            };
          }
          var form = document.querySelector('[data-bind="create-form"]');
          var submit = form ? form.querySelector('[type="submit"]') : null;
          var submitRect = submit ? submit.getBoundingClientRect() : null;
          return {
            formVisible: visible(form),
            caseName: field('caseName'),
            projectCode: field('projectCode'),
            entityName: field('entityName'),
            operatorId: field('operatorId'),
            periodStart: field('periodStart'),
            periodEnd: field('periodEnd'),
            databaseProvider: field('databaseProvider'),
            submitVisible: visible(submit),
            submitX: submitRect ? submitRect.left + (submitRect.width / 2) : 0,
            submitY: submitRect ? submitRect.top + (submitRect.height / 2) : 0
          };
        })()
        """;

    private const string CreatedProjectProbeScript = """
        (function () {
          function visible(element) {
            if (!element || element.hidden) { return false; }
            var style = window.getComputedStyle(element);
            var rect = element.getBoundingClientRect();
            return style.display !== 'none' && style.visibility !== 'hidden'
              && Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
          }
          var form = document.querySelector('[data-bind="create-form"]');
          var caseStep = document.querySelector('[data-bind="case-step"]');
          var caseId = document.querySelector('[data-bind="case-id"]');
          var importStep = document.querySelector('[data-bind="step-nav"] [data-step-index="1"][aria-current="step"]');
          var exitButton = document.querySelector('[data-action="app-exit"]');
          var exitRect = exitButton ? exitButton.getBoundingClientRect() : null;
          return {
            formAbsent: !form,
            caseStepVisible: visible(caseStep),
            caseStepText: caseStep ? caseStep.textContent.trim() : '',
            caseIdVisible: visible(caseId),
            caseIdText: caseId ? caseId.textContent.trim() : '',
            importStepVisible: visible(importStep),
            exitButtonVisible: visible(exitButton),
            exitX: exitRect ? exitRect.left + (exitRect.width / 2) : 0,
            exitY: exitRect ? exitRect.top + (exitRect.height / 2) : 0
          };
        })()
        """;

    internal static Task ExecuteAsync(
        GuiScenarioDefinition scenario,
        CdpSession cdp,
        OwnedGuiRun ownedRun,
        Process process,
        GuiRunner.UiProbe initialUi,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        return scenario.Name switch
        {
            GuiScenarioCatalog.StartupSmoke => ExecuteStartupSmokeAsync(
                cdp, initialUi, outcome, cancellationToken),
            GuiScenarioCatalog.SyntheticSqliteCreate => ExecuteSyntheticSqliteCreateAsync(
                cdp, ownedRun, process, initialUi, outcome, cancellationToken),
            _ => throw new GuiInfrastructureException("scenario_not_implemented")
        };
    }

    private static async Task ExecuteStartupSmokeAsync(
        CdpSession cdp,
        GuiRunner.UiProbe initialUi,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        await ClickAsync(cdp, initialUi.ExitX, initialUi.ExitY, outcome, cancellationToken)
            .ConfigureAwait(false);
        outcome.Assertions.ExitRequested = true;
    }

    private static async Task ExecuteSyntheticSqliteCreateAsync(
        CdpSession cdp,
        OwnedGuiRun ownedRun,
        Process process,
        GuiRunner.UiProbe initialUi,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        var suffix = ownedRun.RunId[..12];
        var caseName = "AgentGui-" + suffix;
        var projectCode = "GUI-" + suffix;
        const string entityName = "AgentGuiEntity";
        const string operatorId = "agent-gui";
        const string periodStart = "2025-01-01";
        const string periodEnd = "2025-12-31";

        await ClickAsync(
            cdp,
            initialUi.NewProjectX,
            initialUi.NewProjectY,
            outcome,
            cancellationToken).ConfigureAwait(false);

        var form = await WaitForCreateFormAsync(cdp, process, cancellationToken).ConfigureAwait(false);
        outcome.Assertions.CreateFormVisible = true;

        form = await FillTextFieldAsync(
            cdp, process, form, CreateField.CaseName, caseName, outcome, cancellationToken)
            .ConfigureAwait(false);
        form = await FillTextFieldAsync(
            cdp, process, form, CreateField.ProjectCode, projectCode, outcome, cancellationToken)
            .ConfigureAwait(false);
        form = await FillTextFieldAsync(
            cdp, process, form, CreateField.EntityName, entityName, outcome, cancellationToken)
            .ConfigureAwait(false);
        form = await FillTextFieldAsync(
            cdp, process, form, CreateField.OperatorId, operatorId, outcome, cancellationToken)
            .ConfigureAwait(false);
        form = await FillDateFieldAsync(
            cdp, process, form, CreateField.PeriodStart, periodStart, outcome, cancellationToken)
            .ConfigureAwait(false);
        form = await FillDateFieldAsync(
            cdp, process, form, CreateField.PeriodEnd, periodEnd, outcome, cancellationToken)
            .ConfigureAwait(false);

        outcome.Assertions.RequiredFieldsEntered =
            form.CaseName.Value == caseName
            && form.ProjectCode.Value == projectCode
            && form.EntityName.Value == entityName
            && form.OperatorId.Value == operatorId
            && form.PeriodStart.Value == periodStart
            && form.PeriodEnd.Value == periodEnd;
        outcome.Assertions.SqliteSelected = form.DatabaseProvider.Value == "sqlite";
        if (!outcome.Assertions.RequiredFieldsEntered || !outcome.Assertions.SqliteSelected)
        {
            throw new GuiCheckException("create_form_values_invalid");
        }

        await ClickAsync(cdp, form.SubmitX, form.SubmitY, outcome, cancellationToken)
            .ConfigureAwait(false);
        var created = await WaitForCreatedProjectAsync(cdp, process, projectCode, cancellationToken)
            .ConfigureAwait(false);
        outcome.Assertions.ProjectCreated = created.FormAbsent;
        outcome.Assertions.ImportStepVisible = created.ImportStepVisible
            && created.CaseStepText == "匯入資料";
        outcome.Assertions.ProjectCodeVisible = created.CaseIdVisible
            && created.CaseIdText == projectCode;
        if (!outcome.Assertions.ProjectCreated
            || !outcome.Assertions.ImportStepVisible
            || !outcome.Assertions.ProjectCodeVisible)
        {
            throw new GuiCheckException("created_project_ui_invalid");
        }

        ValidateSyntheticProjectFiles(ownedRun, caseName, projectCode, outcome.Assertions);

        await ClickAsync(cdp, created.ExitX, created.ExitY, outcome, cancellationToken)
            .ConfigureAwait(false);
        outcome.Assertions.ExitRequested = true;
    }

    private static async Task<CreateFormProbe> FillTextFieldAsync(
        CdpSession cdp,
        Process process,
        CreateFormProbe form,
        CreateField field,
        string expected,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        var target = form.Get(field);
        EnsureEmptyVisibleField(target);
        await ClickAsync(cdp, target.X, target.Y, outcome, cancellationToken).ConfigureAwait(false);
        outcome.RecordAction();
        await cdp.TypeTextAsync(expected, cancellationToken).ConfigureAwait(false);
        return await WaitForFieldValueAsync(cdp, process, field, expected, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<CreateFormProbe> FillDateFieldAsync(
        CdpSession cdp,
        Process process,
        CreateFormProbe form,
        CreateField field,
        string expected,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        var target = form.Get(field);
        EnsureEmptyVisibleField(target);
        await ClickAsync(cdp, target.X, target.Y, outcome, cancellationToken).ConfigureAwait(false);
        outcome.RecordAction();
        await cdp.TypeDateAsync(expected, cancellationToken).ConfigureAwait(false);
        return await WaitForFieldValueAsync(cdp, process, field, expected, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ClickAsync(
        CdpSession cdp,
        double x,
        double y,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x <= 0 || y <= 0)
        {
            throw new GuiInfrastructureException("click_target_invalid");
        }

        outcome.RecordAction();
        await cdp.ClickAsync(x, y, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CreateFormProbe> WaitForCreateFormAsync(
        CdpSession cdp,
        Process process,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, "application_exited_before_create_form");
            var probe = ReadCreateFormProbe(
                await cdp.EvaluateAsync(CreateFormProbeScript, cancellationToken).ConfigureAwait(false));
            if (probe.IsReady)
            {
                return probe;
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<CreateFormProbe> WaitForFieldValueAsync(
        CdpSession cdp,
        Process process,
        CreateField field,
        string expected,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, "application_exited_during_create_form");
            var probe = ReadCreateFormProbe(
                await cdp.EvaluateAsync(CreateFormProbeScript, cancellationToken).ConfigureAwait(false));
            if (probe.IsReady && probe.Get(field).Value == expected)
            {
                return probe;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<CreatedProjectProbe> WaitForCreatedProjectAsync(
        CdpSession cdp,
        Process process,
        string projectCode,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, "application_exited_before_project_created");
            var value = await cdp.EvaluateAsync(CreatedProjectProbeScript, cancellationToken)
                .ConfigureAwait(false);
            var probe = new CreatedProjectProbe(
                ReadBoolean(value, "formAbsent"),
                ReadBoolean(value, "caseStepVisible"),
                ReadString(value, "caseStepText"),
                ReadBoolean(value, "caseIdVisible"),
                ReadString(value, "caseIdText"),
                ReadBoolean(value, "importStepVisible"),
                ReadBoolean(value, "exitButtonVisible"),
                ReadDouble(value, "exitX"),
                ReadDouble(value, "exitY"));
            if (probe.FormAbsent
                && probe.CaseStepVisible
                && probe.CaseIdVisible
                && probe.CaseIdText == projectCode
                && probe.ImportStepVisible
                && probe.ExitButtonVisible
                && double.IsFinite(probe.ExitX)
                && double.IsFinite(probe.ExitY)
                && probe.ExitX > 0
                && probe.ExitY > 0)
            {
                return probe;
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateSyntheticProjectFiles(
        OwnedGuiRun ownedRun,
        string caseName,
        string projectCode,
        GuiAssertions assertions)
    {
        var projectsRoot = Path.GetFullPath(ownedRun.ProjectsRootPath);
        var projectRoot = Path.GetFullPath(Path.Combine(projectsRoot, caseName));
        if (!Path.GetDirectoryName(projectRoot)!.Equals(projectsRoot, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(projectRoot))
        {
            throw new GuiCheckException("synthetic_project_directory_missing");
        }

        OwnedGuiRun.RejectReparsePoint(projectsRoot);
        OwnedGuiRun.RejectReparsePoint(projectRoot);
        var documentPath = Path.Combine(projectRoot, "project.json");
        var databasePath = Path.Combine(projectRoot, "jet.db");
        assertions.ProjectJsonExists = File.Exists(documentPath);
        assertions.SqliteDatabaseExists = File.Exists(databasePath);
        if (!assertions.ProjectJsonExists || !assertions.SqliteDatabaseExists)
        {
            throw new GuiCheckException("synthetic_project_files_missing");
        }

        OwnedGuiRun.RejectReparsePoint(documentPath);
        OwnedGuiRun.RejectReparsePoint(databasePath);
        var documentInfo = new FileInfo(documentPath);
        var databaseInfo = new FileInfo(databasePath);
        if (documentInfo.Length is < 1 or > MaximumProjectDocumentBytes || databaseInfo.Length < 1)
        {
            throw new GuiCheckException("synthetic_project_files_invalid");
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(documentPath));
        var root = document.RootElement;
        assertions.StoredProjectMatches =
            ReadRequiredDocumentString(root, "projectId") == caseName
            && ReadRequiredDocumentString(root, "projectCode") == projectCode
            && ReadRequiredDocumentString(root, "databaseProvider") == "sqlite";
        if (!assertions.StoredProjectMatches)
        {
            throw new GuiCheckException("synthetic_project_metadata_invalid");
        }
    }

    private static CreateFormProbe ReadCreateFormProbe(JsonElement value)
    {
        return new CreateFormProbe(
            ReadBoolean(value, "formVisible"),
            ReadField(value, "caseName"),
            ReadField(value, "projectCode"),
            ReadField(value, "entityName"),
            ReadField(value, "operatorId"),
            ReadField(value, "periodStart"),
            ReadField(value, "periodEnd"),
            ReadField(value, "databaseProvider"),
            ReadBoolean(value, "submitVisible"),
            ReadDouble(value, "submitX"),
            ReadDouble(value, "submitY"));
    }

    private static FieldProbe ReadField(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(name, out var field)
            || field.ValueKind != JsonValueKind.Object)
        {
            return new FieldProbe(false, string.Empty, double.NaN, double.NaN);
        }

        return new FieldProbe(
            ReadBoolean(field, "visible"),
            ReadString(field, "value"),
            ReadDouble(field, "x"),
            ReadDouble(field, "y"));
    }

    private static bool ReadBoolean(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.True;

    private static double ReadDouble(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.TryGetDouble(out var number)
            ? number
            : double.NaN;

    private static string ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadRequiredDocumentString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new GuiCheckException("synthetic_project_metadata_missing");
        }

        return property.GetString()!;
    }

    private static void EnsureEmptyVisibleField(FieldProbe field)
    {
        if (!field.IsReady || field.Value.Length != 0)
        {
            throw new GuiCheckException("create_field_not_empty");
        }
    }

    private static void ThrowIfExited(Process process, string code)
    {
        if (process.HasExited)
        {
            throw new GuiCheckException(code);
        }
    }

    private enum CreateField
    {
        CaseName,
        ProjectCode,
        EntityName,
        OperatorId,
        PeriodStart,
        PeriodEnd
    }

    private sealed record FieldProbe(bool Visible, string Value, double X, double Y)
    {
        internal bool IsReady => Visible
            && double.IsFinite(X)
            && double.IsFinite(Y)
            && X > 0
            && Y > 0;
    }

    private sealed record CreateFormProbe(
        bool FormVisible,
        FieldProbe CaseName,
        FieldProbe ProjectCode,
        FieldProbe EntityName,
        FieldProbe OperatorId,
        FieldProbe PeriodStart,
        FieldProbe PeriodEnd,
        FieldProbe DatabaseProvider,
        bool SubmitVisible,
        double SubmitX,
        double SubmitY)
    {
        internal bool IsReady => FormVisible
            && CaseName.IsReady
            && ProjectCode.IsReady
            && EntityName.IsReady
            && OperatorId.IsReady
            && PeriodStart.IsReady
            && PeriodEnd.IsReady
            && DatabaseProvider.IsReady
            && SubmitVisible
            && double.IsFinite(SubmitX)
            && double.IsFinite(SubmitY)
            && SubmitX > 0
            && SubmitY > 0;

        internal FieldProbe Get(CreateField field) => field switch
        {
            CreateField.CaseName => CaseName,
            CreateField.ProjectCode => ProjectCode,
            CreateField.EntityName => EntityName,
            CreateField.OperatorId => OperatorId,
            CreateField.PeriodStart => PeriodStart,
            CreateField.PeriodEnd => PeriodEnd,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };
    }

    private sealed record CreatedProjectProbe(
        bool FormAbsent,
        bool CaseStepVisible,
        string CaseStepText,
        bool CaseIdVisible,
        string CaseIdText,
        bool ImportStepVisible,
        bool ExitButtonVisible,
        double ExitX,
        double ExitY);
}
