using JET.Application.Contracts;
using JET.Application.DemoData;

namespace JET.Application.Queries.ProjectDemo
{
    public sealed record FetchDemoTbRowsQuery;

    public sealed class FetchDemoTbRowsQueryHandler
    {
        private readonly IDemoProjectDataGenerator _generator;

        public FetchDemoTbRowsQueryHandler()
            : this(new DeterministicDemoProjectDataGenerator())
        {
        }

        public FetchDemoTbRowsQueryHandler(IDemoProjectDataGenerator generator)
        {
            _generator = generator;
        }

        public Task<DemoTbRowsDto> HandleAsync(FetchDemoTbRowsQuery query, CancellationToken cancellationToken)
        {
            return Task.FromResult(_generator.Generate().Tb);
        }
    }
}
