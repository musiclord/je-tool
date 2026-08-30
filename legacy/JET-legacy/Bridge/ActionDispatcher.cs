using System.Text.Json;
using JET.Application.Commands.CommitMapping;
using JET.Application.Commands.CreateProject;
using JET.Application.Commands.ExportReport;
using JET.Application.Commands.FilterScenario;
using JET.Application.Commands.ImportData;
using JET.Application.Queries.AppBootstrap;
using JET.Application.Queries.AutoSuggestMapping;
using JET.Application.Queries.LoadProject;
using JET.Application.Queries.ProjectDemo;
using JET.Application.Queries.RunPrescreen;
using JET.Application.Queries.RunValidation;
using JET.Application.Queries.SystemPing;
using JET.Domain.Abstractions;
using JET.Domain.Abstractions.Files;
using JET.Domain.Abstractions.Repositories;
using JET.Infrastructure.Configuration;

namespace JET.Bridge
{
    public sealed class ActionDispatcher : IActionDispatcher
    {
        private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> _routes;
        private readonly IReadOnlyCollection<string> _supportedActions = Array.Empty<string>();

        public ActionDispatcher(
            JetAppOptions appOptions,
            IAppStateStore appStateStore,
            IProjectSessionStore sessionStore,
            IProjectRepository projectRepository,
            IDateDimensionRepository dateDimensionRepository,
            IGlFileReader glFileReader,
            IGlRepository glRepository,
            ITbRepository tbRepository,
            IAccountMappingRepository accountMappingRepository,
            IGlProjectionRepository? glProjectionRepository = null,
            ITbProjectionRepository? tbProjectionRepository = null,
            IValidationRepository? validationRepository = null,
            IPreScreenRepository? preScreenRepository = null,
            IScenarioRepository? scenarioRepository = null)
        {
            var pingQueryHandler = new SystemPingQueryHandler();
            var bootstrapQueryHandler = new GetAppBootstrapQueryHandler(appOptions, appStateStore);
            var demoQueryHandler = new GetProjectDemoQueryHandler();
            var demoGlFileHandler = new ExportDemoGlFileQueryHandler();
            var demoTbFileHandler = new ExportDemoTbFileQueryHandler();
            var demoAccountMappingFileHandler = new ExportDemoAccountMappingFileQueryHandler();
            var demoGlRowsHandler = new FetchDemoGlRowsQueryHandler();
            var demoTbRowsHandler = new FetchDemoTbRowsQueryHandler();
            var demoAccountMappingRowsHandler = new FetchDemoAccountMappingRowsQueryHandler();
            var createProjectHandler = new CreateProjectCommandHandler(sessionStore, projectRepository);
            var importDataHandler = new ImportDataCommandHandler(sessionStore, dateDimensionRepository);
            var importGlFromFileHandler = new ImportGlFromFileCommandHandler(sessionStore, glFileReader, glRepository);
            var importTbFromFileHandler = new ImportTbFromFileCommandHandler(sessionStore, glFileReader, tbRepository);
            var importAccountMappingFromFileHandler = new ImportAccountMappingFromFileCommandHandler(sessionStore, glFileReader, accountMappingRepository);
            var autoSuggestHandler = new AutoSuggestMappingQueryHandler();
            var commitMappingHandler = new CommitMappingCommandHandler(sessionStore, glProjectionRepository, tbProjectionRepository);
            var validationHandler = new RunValidationQueryHandler(sessionStore, validationRepository);
            var validationDetailsPageHandler = validationRepository is null ? null : new QueryValidationDetailsPageQueryHandler(sessionStore, validationRepository);
            var prescreenHandler = new RunPrescreenQueryHandler(sessionStore, preScreenRepository);
            var prescreenPageHandler = preScreenRepository is null ? null : new QueryPreScreenPageQueryHandler(sessionStore, preScreenRepository);
            var filterHandler = new FilterScenarioCommandHandler(sessionStore, scenarioRepository);
            var loadProjectHandler = new LoadProjectQueryHandler(sessionStore, projectRepository);
            var exportHandler = new ExportReportCommandHandler();

            _routes = new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(StringComparer.OrdinalIgnoreCase)
            {
                ["system.ping"] = async (_, ct) => await pingQueryHandler.HandleAsync(new SystemPingQuery(), ct),
                ["app.bootstrap"] = async (_, ct) => await bootstrapQueryHandler.HandleAsync(new GetAppBootstrapQuery(_supportedActions), ct),
                ["project.loadDemo"] = async (_, ct) => await demoQueryHandler.HandleAsync(new GetProjectDemoQuery(), ct),
                ["demo.exportGlFile"] = async (_, ct) => await demoGlFileHandler.HandleAsync(new ExportDemoGlFileQuery(), ct),
                ["demo.exportTbFile"] = async (_, ct) => await demoTbFileHandler.HandleAsync(new ExportDemoTbFileQuery(), ct),
                ["demo.exportAccountMappingFile"] = async (_, ct) => await demoAccountMappingFileHandler.HandleAsync(new ExportDemoAccountMappingFileQuery(), ct),
                ["demo.fetchGlRows"] = async (_, ct) => await demoGlRowsHandler.HandleAsync(new FetchDemoGlRowsQuery(), ct),
                ["demo.fetchTbRows"] = async (_, ct) => await demoTbRowsHandler.HandleAsync(new FetchDemoTbRowsQuery(), ct),
                ["demo.fetchAccountMappingRows"] = async (_, ct) => await demoAccountMappingRowsHandler.HandleAsync(new FetchDemoAccountMappingRowsQuery(), ct),

                ["project.create"] = async (payload, ct) =>
                {
                    var cmd = new CreateProjectCommand(
                        GetString(payload, "projectCode"),
                        GetString(payload, "entityName"),
                        GetString(payload, "operatorId"),
                        GetString(payload, "industry"),
                        GetString(payload, "periodStart"),
                        GetString(payload, "periodEnd"),
                        GetString(payload, "lastPeriodStart"));
                    return await createProjectHandler.HandleAsync(cmd, ct);
                },

                ["project.load"] = async (payload, ct) => await loadProjectHandler.HandleAsync(new LoadProjectQuery(GetString(payload, "projectId")), ct),

                ["import.gl"] = (payload, ct) =>
                {
                    // Deprecated: kept only as a no-op echo so legacy small-file callers do not
                    // fail outright. New code must use import.gl.fromFile (plan §1.5.1).
                    var fileName = GetString(payload, "fileName");
                    var rows = DeserializeRows(payload, "rows");
                    var columns = DeserializeStringList(payload, "columns");
                    return Task.FromResult<object?>(new { fileName, rows, columns });
                },

                ["import.gl.fromFile"] = async (payload, ct) =>
                {
                    var filePath = GetString(payload, "filePath");
                    var fileName = payload.TryGetProperty("fileName", out var fn) ? fn.GetString() : null;
                    var mode = payload.TryGetProperty("mode", out var md) ? md.GetString() : null;
                    return await importGlFromFileHandler.HandleAsync(filePath, fileName, mode, ct);
                },

                ["import.tb"] = (payload, ct) =>
                {
                    // Deprecated: see import.gl note.
                    var fileName = GetString(payload, "fileName");
                    var rows = DeserializeRows(payload, "rows");
                    var columns = DeserializeStringList(payload, "columns");
                    return Task.FromResult<object?>(new { fileName, rows, columns });
                },

                ["import.tb.fromFile"] = async (payload, ct) =>
                {
                    var filePath = GetString(payload, "filePath");
                    var fileName = payload.TryGetProperty("fileName", out var fn) ? fn.GetString() : null;
                    var mode = payload.TryGetProperty("mode", out var md) ? md.GetString() : null;
                    return await importTbFromFileHandler.HandleAsync(filePath, fileName, mode, ct);
                },

                ["import.accountMapping"] = (payload, ct) =>
                {
                    // Deprecated: see import.gl note.
                    var fileName = GetString(payload, "fileName");
                    var rows = DeserializeRows(payload, "rows");
                    return Task.FromResult<object?>(new { fileName, rows });
                },

                ["import.accountMapping.fromFile"] = async (payload, ct) =>
                {
                    var filePath = GetString(payload, "filePath");
                    var fileName = payload.TryGetProperty("fileName", out var fn) ? fn.GetString() : null;
                    var mode = payload.TryGetProperty("mode", out var md) ? md.GetString() : null;
                    return await importAccountMappingFromFileHandler.HandleAsync(filePath, fileName, mode, ct);
                },

                ["import.holiday"] = async (payload, ct) =>
                {
                    var dates = DeserializeStringList(payload, "dates");
                    return await importDataHandler.HandleImportHolidayAsync(dates, ct);
                },

                ["import.makeupDay"] = async (payload, ct) =>
                {
                    var dates = DeserializeStringList(payload, "dates");
                    return await importDataHandler.HandleImportMakeupDayAsync(dates, ct);
                },

                ["mapping.autoSuggest"] = async (payload, ct) =>
                {
                    var fields = DeserializeFieldDefinitions(payload);
                    var columns = DeserializeStringList(payload, "columns");
                    return await autoSuggestHandler.HandleAsync(new AutoSuggestMappingQuery(fields, columns), ct);
                },

                ["mapping.commit.gl"] = async (payload, ct) =>
                {
                    var mapping = DeserializeMapping(payload, "mapping");
                    return await commitMappingHandler.HandleCommitGlAsync(mapping, ct);
                },

                ["mapping.commit.tb"] = async (payload, ct) =>
                {
                    var mapping = DeserializeMapping(payload, "mapping");
                    return await commitMappingHandler.HandleCommitTbAsync(mapping, ct);
                },

                ["validate.run"] = async (_, ct) => await validationHandler.HandleAsync(ct),

                ["query.validationDetailsPage"] = async (payload, ct) =>
                {
                    if (validationDetailsPageHandler is null)
                    {
                        return new ValidationDetailsPageResult(Array.Empty<ValidationDetailRow>(), null);
                    }

                    var query = new QueryValidationDetailsPageQuery(
                        GetString(payload, "projectId"),
                        GetString(payload, "kind"),
                        payload.TryGetProperty("cursor", out var cursor) && cursor.ValueKind == JsonValueKind.Number ? cursor.GetInt64() : null,
                        payload.TryGetProperty("pageSize", out var pageSize) && pageSize.ValueKind == JsonValueKind.Number ? pageSize.GetInt32() : 100);
                    return await validationDetailsPageHandler.HandleAsync(query, ct);
                },

                ["prescreen.run"] = async (_, ct) => await prescreenHandler.HandleAsync(ct),

                ["query.prescreenPage"] = async (payload, ct) =>
                {
                    if (prescreenPageHandler is null)
                    {
                        return new PreScreenPageResult(Array.Empty<PreScreenDetailRow>(), null);
                    }

                    var query = new QueryPreScreenPageQuery(
                        GetString(payload, "projectId"),
                        GetString(payload, "kind"),
                        payload.TryGetProperty("cursor", out var cursor) && cursor.ValueKind == JsonValueKind.Number ? cursor.GetInt64() : null,
                        payload.TryGetProperty("pageSize", out var pageSize) && pageSize.ValueKind == JsonValueKind.Number ? pageSize.GetInt32() : 100);
                    return await prescreenPageHandler.HandleAsync(query, ct);
                },

                ["filter.preview"] = async (payload, ct) =>
                {
                    var scenario = payload.GetProperty("scenario");
                    return await filterHandler.HandlePreviewAsync(scenario, ct);
                },

                ["query.filterPage"] = async (payload, ct) =>
                {
                    if (scenarioRepository is null)
                    {
                        return new ScenarioPageResult(Array.Empty<ScenarioFilterRow>(), null);
                    }

                    var projectId = GetString(payload, "projectId");
                    if (string.IsNullOrWhiteSpace(projectId)) projectId = sessionStore.CurrentProjectId ?? string.Empty;
                    var runId = payload.TryGetProperty("runId", out var rid) ? rid.GetString() : null;
                    var cursor = payload.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : (long?)null;
                    var pageSize = payload.TryGetProperty("pageSize", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetInt32() : 100;
                    return await scenarioRepository.QueryPageAsync(projectId, runId, cursor, pageSize, ct);
                },

                ["filter.commit"] = async (payload, ct) =>
                {
                    var scenarios = payload.GetProperty("scenarios");
                    return await filterHandler.HandleCommitAsync(scenarios, ct);
                },

                ["export.validation"] = async (_, ct) => await exportHandler.HandleExportValidationAsync(ct),
                ["export.prescreen"] = async (_, ct) => await exportHandler.HandleExportPrescreenAsync(ct),
                ["export.criteria"] = async (_, ct) => await exportHandler.HandleExportCriteriaAsync(ct),
                ["export.workpaper"] = async (payload, ct) =>
                {
                    var selected = payload.GetProperty("selected");
                    return await exportHandler.HandleExportWorkpaperAsync(selected, ct);
                },
            };

            _supportedActions = _routes.Keys.OrderBy(static action => action).ToArray();
        }

        public IReadOnlyCollection<string> SupportedActions => _supportedActions;

        public async Task<object?> DispatchAsync(string action, JsonElement payload, CancellationToken cancellationToken)
        {
            if (!_routes.TryGetValue(action, out var routeHandler))
            {
                throw new KeyNotFoundException($"Unsupported action: {action}");
            }

            return await routeHandler(payload, cancellationToken);
        }

        private static string GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var prop) ? prop.GetString() ?? string.Empty : string.Empty;
        }

        private static List<Dictionary<string, object?>> DeserializeRows(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            var rows = new List<Dictionary<string, object?>>();
            foreach (var item in arr.EnumerateArray())
            {
                var row = new Dictionary<string, object?>();
                foreach (var prop in item.EnumerateObject())
                {
                    row[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object)l : prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetString()
                    };
                }
                rows.Add(row);
            }
            return rows;
        }

        private static List<string> DeserializeStringList(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            return arr.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
        }

        private static List<FieldDefinition> DeserializeFieldDefinitions(JsonElement element)
        {
            if (!element.TryGetProperty("fields", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            return arr.EnumerateArray().Select(e => new FieldDefinition(
                e.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "",
                e.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                e.TryGetProperty("req", out var r) && r.GetBoolean(),
                e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : ""
            )).ToList();
        }

        private static Dictionary<string, string> DeserializeMapping(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var obj) || obj.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, string>();

            var dict = new Dictionary<string, string>();
            foreach (var prop in obj.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
            return dict;
        }
    }
}

