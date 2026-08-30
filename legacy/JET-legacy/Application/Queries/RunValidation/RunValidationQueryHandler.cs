using JET.Domain.Abstractions;
using JET.Domain.Abstractions.Repositories;

namespace JET.Application.Queries.RunValidation
{
    public sealed class RunValidationQueryHandler
    {
        private readonly IProjectSessionStore _session;
        private readonly IValidationRepository? _validationRepository;

        public RunValidationQueryHandler(IProjectSessionStore session)
            : this(session, null)
        {
        }

        public RunValidationQueryHandler(IProjectSessionStore session, IValidationRepository? validationRepository)
        {
            _session = session;
            _validationRepository = validationRepository;
        }

        public async Task<object> HandleAsync(CancellationToken cancellationToken)
        {
            if (_validationRepository is null || string.IsNullOrWhiteSpace(_session.CurrentProjectId))
            {
                return EmptyResult();
            }

            var result = await _validationRepository.RunAsync(_session.CurrentProjectId, cancellationToken).ConfigureAwait(false);
            return new
            {
                stats = result.Stats,
                summary = result.Summary,
                v1 = result.V1,
                v2 = result.V2,
                v3 = result.V3,
                v4 = result.V4,
                diffAccounts = result.DiffAccounts,
                resultRef = new { runId = result.RunId }
            };
        }

        private static object EmptyResult()
        {
            return new
            {
                stats = new ValidationStats(0, 0, 0, 0, 0),
                summary = new ValidationSummary(0, 0, 0, 0),
                v1 = 0,
                v2 = 0,
                v3 = 0,
                v4 = 0,
                diffAccounts = Array.Empty<ValidationDiffAccount>(),
                resultRef = new { runId = string.Empty }
            };
        }
    }
}
