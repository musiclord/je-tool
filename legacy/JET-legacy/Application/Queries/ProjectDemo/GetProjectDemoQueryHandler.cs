using JET.Application.Contracts;
using JET.Application.DemoData;

namespace JET.Application.Queries.ProjectDemo
{
    public sealed class GetProjectDemoQueryHandler
    {
        private readonly IDemoProjectDataGenerator _generator;

        public GetProjectDemoQueryHandler()
            : this(new DeterministicDemoProjectDataGenerator())
        {
        }

        public GetProjectDemoQueryHandler(IDemoProjectDataGenerator generator)
        {
            _generator = generator;
        }

        public Task<DemoProjectDto> HandleAsync(GetProjectDemoQuery query, CancellationToken cancellationToken)
        {
            return Task.FromResult(_generator.Generate().Project);
        }
    }
}
