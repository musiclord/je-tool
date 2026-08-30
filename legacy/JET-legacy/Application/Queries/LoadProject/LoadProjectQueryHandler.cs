using JET.Domain.Abstractions;
using JET.Domain.Abstractions.Repositories;

namespace JET.Application.Queries.LoadProject
{
    public sealed record LoadProjectQuery(string ProjectId);

    /// <summary>
    /// Handler for the <c>project.load</c> action. Reads the project metadata and the
    /// most recent validation / prescreen / scenario <c>runId</c> from the repository,
    /// then sets <see cref="IProjectSessionStore.CurrentProjectId"/> so subsequent
    /// handlers can locate the project without rehydrating row data into memory
    /// (mission §1.5.5; collapse of <c>session.GlData</c>, plan §3.4).
    /// </summary>
    public sealed class LoadProjectQueryHandler
    {
        private readonly IProjectSessionStore _session;
        private readonly IProjectRepository _projectRepository;

        public LoadProjectQueryHandler(IProjectSessionStore session, IProjectRepository projectRepository)
        {
            _session = session;
            _projectRepository = projectRepository;
        }

        public async Task<object> HandleAsync(LoadProjectQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (string.IsNullOrWhiteSpace(query.ProjectId))
            {
                return Empty();
            }

            var snapshot = await _projectRepository.LoadAsync(query.ProjectId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                return Empty();
            }

            _session.SetCurrentProjectId(snapshot.ProjectId);
            _session.SetProject(snapshot.Project);

            return new
            {
                projectId = snapshot.ProjectId,
                project = snapshot.Project,
                mapping = new
                {
                    gl = _session.GlMapping,
                    tb = _session.TbMapping
                },
                latestRuns = new
                {
                    validationRunId = snapshot.LatestValidationRunId,
                    prescreenRunId = snapshot.LatestPrescreenRunId,
                    filterRunId = snapshot.LatestFilterRunId
                }
            };
        }

        private static object Empty() => new
        {
            projectId = string.Empty,
            project = (object?)null,
            mapping = new { gl = new Dictionary<string, string>(), tb = new Dictionary<string, string>() },
            latestRuns = new { validationRunId = (string?)null, prescreenRunId = (string?)null, filterRunId = (string?)null }
        };
    }
}
