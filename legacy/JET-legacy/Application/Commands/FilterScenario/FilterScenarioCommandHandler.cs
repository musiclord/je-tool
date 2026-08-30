using System.Text.Json;
using JET.Application.Commands.FilterScenario.Rules;
using JET.Domain.Abstractions;
using JET.Domain.Abstractions.Repositories;

namespace JET.Application.Commands.FilterScenario
{
    public sealed class FilterScenarioCommandHandler
    {
        private readonly IProjectSessionStore _session;
        private readonly IScenarioRepository? _scenarioRepository;

        public FilterScenarioCommandHandler(IProjectSessionStore session)
            : this(session, null) { }

        public FilterScenarioCommandHandler(IProjectSessionStore session, IScenarioRepository? scenarioRepository)
        {
            _session = session;
            _scenarioRepository = scenarioRepository;
        }

        public async Task<object> HandlePreviewAsync(object scenarioPayload, CancellationToken cancellationToken)
        {
            var scenario = ParseScenario(scenarioPayload);
            if (_scenarioRepository is null || string.IsNullOrWhiteSpace(_session.CurrentProjectId))
                return EmptyPreview(scenario);

            var result = await _scenarioRepository.PreviewAsync(_session.CurrentProjectId!, scenario, cancellationToken).ConfigureAwait(false);
            return new
            {
                scenario = new
                {
                    label = result.Label,
                    count = result.Count,
                    voucherCount = result.VoucherCount,
                    summary = result.Summary,
                    previewRows = result.PreviewRows,
                    resultRef = new { runId = result.RunId }
                }
            };
        }

        public Task<object> HandleCommitAsync(object scenarios, CancellationToken cancellationToken)
            => Task.FromResult<object>(new { ok = true });

        private static object EmptyPreview(ScenarioDefinition scenario)
        {
            var label = string.IsNullOrWhiteSpace(scenario.Name) ? "未命名情境" : scenario.Name;
            return new
            {
                scenario = new
                {
                    label,
                    count = 0,
                    voucherCount = 0,
                    summary = Array.Empty<string>(),
                    previewRows = Array.Empty<object>(),
                    resultRef = new { runId = string.Empty }
                }
            };
        }

        private static ScenarioDefinition ParseScenario(object payload)
        {
            if (payload is not JsonElement json || json.ValueKind != JsonValueKind.Object)
                return new ScenarioDefinition();

            var name = json.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
            var groups = new List<ScenarioGroup>();
            if (json.TryGetProperty("groups", out var groupArray) && groupArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var groupJson in groupArray.EnumerateArray())
                {
                    var rules = new List<ScenarioRule>();
                    if (groupJson.TryGetProperty("rules", out var ruleArray) && ruleArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ruleJson in ruleArray.EnumerateArray())
                        {
                            rules.Add(new ScenarioRule
                            {
                                Join = ReadString(ruleJson, "join", "AND"),
                                Type = ReadString(ruleJson, "type", string.Empty),
                                PrescreenKey = ReadString(ruleJson, "prescreenKey", string.Empty),
                                Field = ReadString(ruleJson, "field", string.Empty),
                                Keywords = ReadString(ruleJson, "keywords", string.Empty),
                                Mode = ReadString(ruleJson, "mode", "contains"),
                                From = ReadString(ruleJson, "from", string.Empty),
                                To = ReadString(ruleJson, "to", string.Empty),
                                DebitCategory = ReadString(ruleJson, "debitCategory", string.Empty),
                                CreditCategory = ReadString(ruleJson, "creditCategory", string.Empty),
                                DrCr = ReadString(ruleJson, "drCr", "debit"),
                                IsManual = ReadString(ruleJson, "isManual", "true"),
                            });
                        }
                    }
                    groups.Add(new ScenarioGroup { Join = ReadString(groupJson, "join", "AND"), Rules = rules });
                }
            }
            return new ScenarioDefinition { Name = name, Groups = groups };
        }

        private static string ReadString(JsonElement element, string property, string fallback)
            => element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString() ?? fallback : fallback;
    }
}
