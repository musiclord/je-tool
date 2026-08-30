using JET.Application.Contracts;
using JET.Domain.Abstractions;
using JET.Infrastructure.Configuration;

namespace JET.Application.Queries.AppBootstrap
{
    public sealed class GetAppBootstrapQueryHandler
    {
        private readonly JetAppOptions _appOptions;
        private readonly IAppStateStore _appStateStore;

        public GetAppBootstrapQueryHandler(JetAppOptions appOptions, IAppStateStore appStateStore)
        {
            _appOptions = appOptions;
            _appStateStore = appStateStore;
        }

        public async Task<AppBootstrapDto> HandleAsync(GetAppBootstrapQuery query, CancellationToken cancellationToken)
        {
            var databaseStatus = await _appStateStore.GetStatusAsync(cancellationToken);

            return new AppBootstrapDto(
                _appOptions.Host.Title,
                _appOptions.Host.StartPageUrl,
                query.SupportedActions.OrderBy(static action => action).ToArray(),
                new DatabaseBootstrapDto(
                    databaseStatus.Provider.ToString(),
                    databaseStatus.IsAvailable,
                    databaseStatus.ConnectionTarget,
                    databaseStatus.Mode),
                new DemoBootstrapDto(_appOptions.Demo.Enabled));
        }
    }
}
