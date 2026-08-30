using JET.Domain.Abstractions;
using JET.Domain.Abstractions.Repositories;
using JET.Domain.Entities;

namespace JET.Application.Commands.CreateProject
{
    public sealed class CreateProjectCommandHandler
    {
        private readonly IProjectSessionStore _session;
        private readonly IProjectRepository _repository;

        public CreateProjectCommandHandler(IProjectSessionStore session, IProjectRepository repository)
        {
            _session = session;
            _repository = repository;
        }

        public async Task<object> HandleAsync(CreateProjectCommand command, CancellationToken cancellationToken)
        {
            var project = new ProjectInfo
            {
                ProjectCode = command.ProjectCode,
                EntityName = command.EntityName,
                OperatorId = command.OperatorId,
                Industry = command.Industry,
                PeriodStart = command.PeriodStart,
                PeriodEnd = command.PeriodEnd,
                LastPeriodStart = command.LastPeriodStart,
            };

            // Additive change per plan.md §6.1 step 1: persist to the repository while
            // keeping the in-memory session pointer for handlers that have not yet
            // migrated. Phase 3 §6.4 will shrink the session store once GL/TB rows
            // also live in the database.
            var projectId = await _repository.CreateAsync(project, cancellationToken);
            _session.SetProject(project);
            _session.SetCurrentProjectId(projectId);

            return new { projectId, ok = true };
        }
    }
}
