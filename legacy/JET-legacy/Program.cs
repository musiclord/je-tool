using JET.Bridge;
using JET.Domain.Abstractions;
using JET.Domain.Abstractions.Files;
using JET.Domain.Abstractions.Persistence;
using JET.Domain.Abstractions.Repositories;
using JET.Domain.Enums;
using JET.Infrastructure.Configuration;
using JET.Infrastructure.IO.Excel;
using JET.Infrastructure.Persistence;
using JET.Infrastructure.Persistence.SqlServer;
using JET.Infrastructure.Persistence.SqlServer.Repositories;
using JET.Infrastructure.Persistence.Sqlite;
using JET.Infrastructure.Persistence.Sqlite.Repositories;

namespace JET
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            var options = JetAppOptionsLoader.LoadWithEnvironment(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), null);
            var schemaInitializer = CreateSchemaInitializer(options);
            TryEnsureSchema(schemaInitializer);
            var appStateStore = CreateAppStateStore(options);
            var sessionStore = new InMemoryProjectSessionStore();
            var projectRepository = CreateProjectRepository(options);
            var dateDimensionRepository = CreateDateDimensionRepository(options);
            var glRepository = CreateGlRepository(options);
            var tbRepository = CreateTbRepository(options);
            var accountMappingRepository = CreateAccountMappingRepository(options);
            var glProjectionRepository = CreateGlProjectionRepository(options);
            var tbProjectionRepository = CreateTbProjectionRepository(options);
            var validationRepository = CreateValidationRepository(options);
            var preScreenRepository = CreatePreScreenRepository(options);
            var scenarioRepository = CreateScenarioRepository(options);
            IGlFileReader glFileReader = new SylvanGlFileReader();
            var actionDispatcher = new ActionDispatcher(options, appStateStore, sessionStore, projectRepository, dateDimensionRepository, glFileReader, glRepository, tbRepository, accountMappingRepository, glProjectionRepository, tbProjectionRepository, validationRepository, preScreenRepository, scenarioRepository);

            System.Windows.Forms.Application.Run(new Form1(options, actionDispatcher));
        }

        private static IAppStateStore CreateAppStateStore(JetAppOptions options)
        {
            return options.Database.Provider switch
            {
                DatabaseProvider.SqlServer => new SqlServerAppStateStore(options.Database),
                _ => new SqliteAppStateStore(options.Database)
            };
        }

        private static ISchemaInitializer CreateSchemaInitializer(JetAppOptions options)
        {
            return options.Database.Provider switch
            {
                DatabaseProvider.SqlServer => new SqlServerSchemaInitializer(options.Database),
                _ => new SqliteSchemaInitializer(options.Database)
            };
        }

        private static IProjectRepository CreateProjectRepository(JetAppOptions options)
        {
            return options.Database.Provider switch
            {
                DatabaseProvider.SqlServer => new SqlServerProjectRepository(options.Database),
                _ => new SqliteProjectRepository(options.Database)
            };
        }

        private static IDateDimensionRepository CreateDateDimensionRepository(JetAppOptions options)
        {
            return options.Database.Provider switch
            {
                DatabaseProvider.SqlServer => new SqlServerDateDimensionRepository(options.Database),
                _ => new SqliteDateDimensionRepository(options.Database)
            };
        }

        private static IGlRepository CreateGlRepository(JetAppOptions options)
        {
            return options.Database.Provider switch
            {
                DatabaseProvider.SqlServer => new SqlServerGlRepository(options.Database),
                _ => new SqliteGlRepository(options.Database)
            };
        }

        private static ITbRepository CreateTbRepository(JetAppOptions options)
        {
            return options.Database.Provider switch
            {
                DatabaseProvider.SqlServer => new SqlServerTbRepository(options.Database),
                _ => new SqliteTbRepository(options.Database)
            };
        }

        private static IAccountMappingRepository CreateAccountMappingRepository(JetAppOptions options)
        {
            return options.Database.Provider switch
            {
                DatabaseProvider.SqlServer => new SqlServerAccountMappingRepository(options.Database),
                _ => new SqliteAccountMappingRepository(options.Database)
            };
        }

        private static IGlProjectionRepository CreateGlProjectionRepository(JetAppOptions options)
        {
            return options.Database.Provider switch
            {
                DatabaseProvider.SqlServer => new SqlServerGlProjectionRepository(options.Database),
                _ => new SqliteGlProjectionRepository(options.Database)
            };
        }

        private static ITbProjectionRepository CreateTbProjectionRepository(JetAppOptions options)
        {
            return options.Database.Provider switch
            {
                DatabaseProvider.SqlServer => new SqlServerTbProjectionRepository(options.Database),
                _ => new SqliteTbProjectionRepository(options.Database)
            };
        }

        private static IValidationRepository CreateValidationRepository(JetAppOptions options)
        {
            return options.Database.Provider switch
            {
                DatabaseProvider.SqlServer => new SqlServerValidationRepository(options.Database),
                _ => new SqliteValidationRepository(options.Database)
            };
        }

        private static IPreScreenRepository CreatePreScreenRepository(JetAppOptions options)
        {
            return options.Database.Provider switch
            {
                DatabaseProvider.SqlServer => new SqlServerPreScreenRepository(options.Database),
                _ => new SqlitePreScreenRepository(options.Database)
            };
        }

        private static IScenarioRepository CreateScenarioRepository(JetAppOptions options)
        {
            return options.Database.Provider switch
            {
                DatabaseProvider.SqlServer => new SqlServerScenarioRepository(options.Database),
                _ => new SqliteScenarioRepository(options.Database)
            };
        }

        private static void TryEnsureSchema(ISchemaInitializer initializer)
        {
            try
            {
                // Synchronous wait at startup is intentional: schema must exist before
                // any handler runs. Per plan.md §5.3, an unavailable provider must not
                // crash startup; app.bootstrap surfaces it via DatabaseStatus.IsAvailable.
                initializer.EnsureAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Schema initialization failed: {ex.Message}");
            }
        }
    }
}

