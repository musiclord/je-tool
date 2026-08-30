using JET.Domain.Abstractions;
using JET.Domain.Abstractions.Repositories;

namespace JET.Application.Queries.RunPrescreen
{
    public sealed record QueryPreScreenPageQuery(string ProjectId, string Kind, long? Cursor, int PageSize);

    public sealed class QueryPreScreenPageQueryHandler
    {
        private readonly IProjectSessionStore _session;
        private readonly IPreScreenRepository _preScreenRepository;

        public QueryPreScreenPageQueryHandler(IProjectSessionStore session, IPreScreenRepository preScreenRepository)
        {
            _session = session;
            _preScreenRepository = preScreenRepository;
        }

        public async Task<PreScreenPageResult> HandleAsync(QueryPreScreenPageQuery query, CancellationToken cancellationToken)
        {
            var projectId = string.IsNullOrWhiteSpace(query.ProjectId) ? _session.CurrentProjectId : query.ProjectId;
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return new PreScreenPageResult(Array.Empty<PreScreenDetailRow>(), null);
            }

            return await _preScreenRepository.QueryPageAsync(projectId, query.Kind, query.Cursor, query.PageSize, cancellationToken).ConfigureAwait(false);
        }
    }
}
