using JET.Domain.Abstractions;
using JET.Domain.Abstractions.Repositories;

namespace JET.Application.Queries.RunPrescreen
{
    public sealed class RunPrescreenQueryHandler
    {
        private readonly IProjectSessionStore _session;
        private readonly IPreScreenRepository? _preScreenRepository;

        public RunPrescreenQueryHandler(IProjectSessionStore session)
            : this(session, null) { }

        public RunPrescreenQueryHandler(IProjectSessionStore session, IPreScreenRepository? preScreenRepository)
        {
            _session = session;
            _preScreenRepository = preScreenRepository;
        }

        public async Task<object> HandleAsync(CancellationToken cancellationToken)
        {
            if (_preScreenRepository is null || string.IsNullOrWhiteSpace(_session.CurrentProjectId))
                return EmptyResult();

            var result = await _preScreenRepository.RunAsync(_session.CurrentProjectId, cancellationToken).ConfigureAwait(false);
            return new
            {
                r1 = result.R1, r2 = result.R2, r3 = result.R3, r4 = result.R4,
                r4ZerosThreshold = result.R4ZerosThreshold,
                r5Summary = result.R5Summary,
                r6 = result.R6, r7 = result.R7, r8 = result.R8,
                a2 = result.A2, a3 = result.A3, a4 = result.A4,
                descNullCount = result.DescNullCount,
                resultRef = new { runId = result.RunId }
            };
        }

        private static object EmptyResult() => new
        {
            r1 = 0, r2 = 0, r3 = 0, r4 = 0,
            r4ZerosThreshold = 3,
            r5Summary = Array.Empty<PreScreenCreatorSummary>(),
            r6 = 0, r7 = 0, r8 = 0,
            a2 = 0, a3 = 0, a4 = 0,
            descNullCount = 0,
            resultRef = new { runId = string.Empty }
        };
    }
}
