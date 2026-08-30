using JET.Domain.Abstractions;
using JET.Domain.Abstractions.Repositories;

namespace JET.Application.Queries.RunValidation
{
    public sealed record QueryValidationDetailsPageQuery(
        string ProjectId,
        string Kind,
        long? Cursor,
        int PageSize);

    public sealed class QueryValidationDetailsPageQueryHandler
    {
        private readonly IProjectSessionStore _session;
        private readonly IValidationRepository _validationRepository;

        public QueryValidationDetailsPageQueryHandler(IProjectSessionStore session, IValidationRepository validationRepository)
        {
            _session = session;
            _validationRepository = validationRepository;
        }

        public async Task<ValidationDetailsPageResult> HandleAsync(QueryValidationDetailsPageQuery query, CancellationToken cancellationToken)
        {
            var projectId = string.IsNullOrWhiteSpace(query.ProjectId) ? _session.CurrentProjectId : query.ProjectId;
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return new ValidationDetailsPageResult(Array.Empty<ValidationDetailRow>(), null);
            }

            return await _validationRepository.QueryDetailsPageAsync(projectId, query.Kind, query.Cursor, query.PageSize, cancellationToken).ConfigureAwait(false);
        }
    }
}
