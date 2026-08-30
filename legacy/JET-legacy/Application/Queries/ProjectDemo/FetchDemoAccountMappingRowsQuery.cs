using JET.Application.Contracts;
using JET.Application.DemoData;

namespace JET.Application.Queries.ProjectDemo
{
    public sealed record FetchDemoAccountMappingRowsQuery;

    public sealed class FetchDemoAccountMappingRowsQueryHandler
    {
        private readonly IDemoProjectDataGenerator _generator;

        public FetchDemoAccountMappingRowsQueryHandler()
            : this(new DeterministicDemoProjectDataGenerator())
        {
        }

        public FetchDemoAccountMappingRowsQueryHandler(IDemoProjectDataGenerator generator)
        {
            _generator = generator;
        }

        public Task<DemoAccountMappingRowsDto> HandleAsync(FetchDemoAccountMappingRowsQuery query, CancellationToken cancellationToken)
        {
            return Task.FromResult(_generator.Generate().AccountMapping);
        }
    }
}
