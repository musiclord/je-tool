using JET.Application.Contracts;
using JET.Application.DemoData;

namespace JET.Application.Queries.ProjectDemo
{
    public sealed record FetchDemoGlRowsQuery;

    public sealed class FetchDemoGlRowsQueryHandler
    {
        private readonly IDemoProjectDataGenerator _generator;

        public FetchDemoGlRowsQueryHandler()
            : this(new DeterministicDemoProjectDataGenerator())
        {
        }

        public FetchDemoGlRowsQueryHandler(IDemoProjectDataGenerator generator)
        {
            _generator = generator;
        }

        public Task<DemoGlRowsDto> HandleAsync(FetchDemoGlRowsQuery query, CancellationToken cancellationToken)
        {
            return Task.FromResult(_generator.Generate().Gl);
        }
    }
}
