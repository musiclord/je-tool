using JET.Domain.Abstractions;
using JET.Domain.Abstractions.Repositories;

namespace JET.Application.Commands.CommitMapping
{
    public sealed class CommitMappingCommandHandler
    {
        private readonly IProjectSessionStore _session;
        private readonly IGlProjectionRepository? _glProjectionRepository;
        private readonly ITbProjectionRepository? _tbProjectionRepository;

        public CommitMappingCommandHandler(IProjectSessionStore session)
            : this(session, null, null)
        {
        }

        public CommitMappingCommandHandler(
            IProjectSessionStore session,
            IGlProjectionRepository? glProjectionRepository,
            ITbProjectionRepository? tbProjectionRepository)
        {
            _session = session;
            _glProjectionRepository = glProjectionRepository;
            _tbProjectionRepository = tbProjectionRepository;
        }

        public async Task<object> HandleCommitGlAsync(Dictionary<string, string> mapping, CancellationToken cancellationToken)
        {
            _session.SetGlMapping(mapping);
            var result = await ProjectGlAsync(mapping, cancellationToken).ConfigureAwait(false);
            return new { ok = true, mapping, batchId = result.BatchId, projectedRowCount = result.ProjectedRowCount };
        }

        public async Task<object> HandleCommitTbAsync(Dictionary<string, string> mapping, CancellationToken cancellationToken)
        {
            _session.SetTbMapping(mapping);
            var result = await ProjectTbAsync(mapping, cancellationToken).ConfigureAwait(false);
            return new { ok = true, mapping, batchId = result.BatchId, projectedRowCount = result.ProjectedRowCount };
        }

        private async Task<ProjectionResult> ProjectGlAsync(Dictionary<string, string> mapping, CancellationToken cancellationToken)
        {
            if (_glProjectionRepository is null || string.IsNullOrWhiteSpace(_session.CurrentProjectId))
            {
                return new ProjectionResult(null, 0);
            }

            return await _glProjectionRepository.ProjectLatestBatchAsync(_session.CurrentProjectId, mapping, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ProjectionResult> ProjectTbAsync(Dictionary<string, string> mapping, CancellationToken cancellationToken)
        {
            if (_tbProjectionRepository is null || string.IsNullOrWhiteSpace(_session.CurrentProjectId))
            {
                return new ProjectionResult(null, 0);
            }

            return await _tbProjectionRepository.ProjectLatestBatchAsync(_session.CurrentProjectId, mapping, cancellationToken).ConfigureAwait(false);
        }
    }
}
