using JET.Domain.Entities;

namespace JET.Domain.Abstractions.Repositories
{
    /// <summary>
    /// Persists project metadata. The first repository introduced under Phase 3 §6.1
    /// step 1 of <c>plan.md</c>; writes <c>config_project</c> + <c>config_project_state</c>
    /// in a single transaction and returns the generated project id.
    /// </summary>
    public interface IProjectRepository
    {
        /// <summary>
        /// Inserts a new project row plus its initial state row (current step = 1).
        /// Returns the server-generated project id (opaque string; currently a GUID).
        /// </summary>
        Task<string> CreateAsync(ProjectInfo project, CancellationToken cancellationToken);

        /// <summary>
        /// Loads the project metadata plus the latest <c>runId</c> for validation,
        /// prescreen and scenario runs. Returns <c>null</c> if the project does not exist.
        /// Powers the <c>project.load</c> bridge action so the UI can rehydrate after a
        /// process restart without keeping rows in <see cref="IProjectSessionStore"/>.
        /// </summary>
        Task<ProjectStateSnapshot?> LoadAsync(string projectId, CancellationToken cancellationToken);
    }

    public sealed record ProjectStateSnapshot(
        string ProjectId,
        ProjectInfo Project,
        string? LatestValidationRunId,
        string? LatestPrescreenRunId,
        string? LatestFilterRunId);
}
