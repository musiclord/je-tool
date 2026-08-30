using JET.Application.Commands.FilterScenario.Rules;
using JET.Domain.Abstractions.Repositories;
using JET.Infrastructure.Configuration;

namespace JET.Infrastructure.Persistence.SqlServer.Repositories
{
    public sealed class SqlServerScenarioRepository : IScenarioRepository
    {
        public SqlServerScenarioRepository(DatabaseOptions databaseOptions)
        {
            _ = databaseOptions;
        }

        public Task<ScenarioPreviewResult> PreviewAsync(string projectId, ScenarioDefinition scenario, CancellationToken cancellationToken)
        {
            throw new NotImplementedException("SqlServer scenario filter SQL pushdown is scheduled for a later round (plan.md §6).");
        }

        public Task<ScenarioPageResult> QueryPageAsync(string projectId, string? runId, long? cursor, int pageSize, CancellationToken cancellationToken)
        {
            throw new NotImplementedException("SqlServer scenario filter paging is scheduled for a later round (plan.md §6).");
        }
    }
}
