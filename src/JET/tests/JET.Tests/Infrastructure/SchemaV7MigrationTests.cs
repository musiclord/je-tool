using System.Data.Common;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Schema v7 的本地 provider migration matrix。V6 fixture 是直接建立的 frozen shape，
/// 不以 current schema 降版冒充 legacy migration。
/// </summary>
public sealed class SchemaV7MigrationTests
{
    private const string FrozenV6SchemaAndData =
        """
        CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO schema_info VALUES ('schema_version', '6');

        CREATE TABLE config_field_mapping (
            dataset_kind TEXT PRIMARY KEY,
            mapping_json TEXT NOT NULL,
            mode_name TEXT NOT NULL,
            source_batch_id TEXT NOT NULL,
            committed_utc TEXT NOT NULL
        );
        INSERT INTO config_field_mapping VALUES
            ('gl', '{"docNum":"Doc"}', 'dualAmount', 'batch-v6', '2026-08-01T00:00:00+00:00');

        CREATE TABLE target_gl_entry (
            entry_id BIGINT PRIMARY KEY,
            batch_id TEXT NOT NULL,
            source_row_number BIGINT NOT NULL,
            document_number TEXT NULL,
            line_item TEXT NULL,
            line_item_numeric_sort_key TEXT NULL,
            post_date TEXT NULL,
            approval_date TEXT NULL,
            voucher_date TEXT NULL,
            account_code TEXT NULL,
            account_name TEXT NULL,
            document_description TEXT NULL,
            source_module TEXT NULL,
            created_by TEXT NULL,
            approved_by TEXT NULL,
            is_manual INTEGER NULL,
            amount_scaled BIGINT NOT NULL,
            debit_amount_scaled BIGINT NOT NULL,
            credit_amount_scaled BIGINT NOT NULL,
            dr_cr TEXT NOT NULL
        );
        INSERT INTO target_gl_entry
            (entry_id, batch_id, source_row_number, document_number, account_code,
             amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES (1, 'batch-v6', 2, 'DOC-V6', '4101', -10000, 0, 10000, 'CREDIT');

        CREATE TABLE gl_control_total (
            singleton INTEGER PRIMARY KEY,
            source_row_count BIGINT NOT NULL,
            target_row_count BIGINT NOT NULL,
            target_debit_scaled BIGINT NOT NULL,
            target_credit_scaled BIGINT NOT NULL
        );
        INSERT INTO gl_control_total VALUES (1, 1, 1, 0, 10000);

        CREATE TABLE target_account_mapping (
            mapping_id BIGINT PRIMARY KEY,
            batch_id TEXT NOT NULL,
            source_row_number INTEGER NOT NULL,
            account_code TEXT NOT NULL UNIQUE,
            account_name TEXT NULL,
            standardized_category TEXT NOT NULL
        );
        INSERT INTO target_account_mapping VALUES
            (1, 'batch-am-v6', 2, '4101', 'Revenue account', 'Revenue');

        CREATE TABLE config_filter_scenario (
            position INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            rationale TEXT NOT NULL,
            definition_json TEXT NOT NULL,
            saved_utc TEXT NOT NULL
        );
        INSERT INTO config_filter_scenario VALUES
            (1, 'legacy pair', 'migration oracle',
             '{"populationScope":"auditPeriod","logicVersion":"filter-2026-08-04-v6","groups":[{"rules":[{"type":"accountPair","debitCategory":"Cash","creditCategory":"Revenue"}]}]}',
             '2026-08-01T00:00:00+00:00');

        CREATE TABLE result_rule_run (
            run_id TEXT PRIMARY KEY,
            run_kind TEXT NOT NULL,
            generated_utc TEXT NOT NULL,
            summary_json TEXT NOT NULL
        );
        INSERT INTO result_rule_run VALUES
            ('validation-v6', 'validate', '2026-08-01T00:00:00+00:00', '{"resultRef":{"logicVersion":"validation-2026-07-13-v2"}}'),
            ('prescreen-v6', 'prescreen', '2026-08-01T00:00:00+00:00', '{"resultRef":{"logicVersion":"prescreen-2026-07-11-v2"}}');

        CREATE TABLE result_inf_sampling_test_sample (
            run_id TEXT NOT NULL,
            entry_id BIGINT NOT NULL,
            document_number TEXT NULL,
            line_item TEXT NULL,
            PRIMARY KEY (run_id, entry_id)
        );
        INSERT INTO result_inf_sampling_test_sample VALUES ('validation-v6', 1, 'DOC-V6', NULL);

        CREATE TABLE result_filter_run (
            scenario_position INTEGER NOT NULL,
            entry_id BIGINT NOT NULL,
            PRIMARY KEY (scenario_position, entry_id)
        );
        INSERT INTO result_filter_run VALUES (1, 1);
        """;

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task FreshSchema_IsV7AndContainsMinimumInventory(string provider)
    {
        using var env = new LocalEnv(provider, seedV6: false);

        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        Assert.Equal("9", await env.TextAsync(
            "SELECT value FROM schema_info WHERE key = 'schema_version';"));
        Assert.Equal(4, await env.V7TableCountAsync());
        Assert.Equal(
            AccountTaxonomyBuiltIns.All.ToArray(),
            (await env.TaxonomyAsync()).ToArray());
        Assert.Equal(1, await env.ScalarAsync("SELECT MAX(revision) FROM config_account_taxonomy;"));
        Assert.Equal(1, await env.ScalarAsync(
            "SELECT COUNT(*) FROM config_result_stale_state "
            + "WHERE singleton=1 AND validation_stale=0 AND prescreen_stale=0 AND filter_stale=0;"));
        Assert.Equal(3, await env.IndexCountAsync("target_gl_rde_value"));
    }

    [Fact]
    public async Task DuckDb_CurrentV7LoweredToV6_RetryPreservesV2MappingAndRemovesResidualReplacement()
    {
        using var env = new LocalEnv("duckdb", seedV6: false);
        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        var original = new CommittedMapping(
            DatasetKind.Gl,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.DocNum] = "Document Number",
                [GlMappingKeys.PostingStatus] = "Posting Status"
            },
            "signedAmount",
            "batch-v2",
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            MappingMetadataFormat.CurrentVersion,
            new GlMappingOptions(
                ApprovalDateModeNames.SameAsPostDate,
                new GlPostingStatusPolicy(["Posted"], IncludeBlank: true),
                new GlManualAutoPolicy(["M"], ["A"]),
                []));
        var store = new LocalMappingStateStore(env.Database);
        await store.SaveAsync(env.ProjectId, original, CancellationToken.None);
        var originalOptionsJson = await env.TextAsync(
            "SELECT options_json FROM config_field_mapping WHERE dataset_kind='gl';");

        // Simulate a shape-drift retry: the canonical table already contains v2 metadata, while a
        // replacement left by an interrupted/manual migration attempt also exists and the version
        // marker was lowered. The retry must prefer the canonical source and clean its scratch table.
        await env.ExecuteAsync(
            "CREATE TABLE config_field_mapping_v7 AS SELECT * FROM config_field_mapping; "
            + "UPDATE schema_info SET value='6' WHERE key='schema_version';");

        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        Assert.Equal("9", await env.TextAsync(
            "SELECT value FROM schema_info WHERE key='schema_version';"));
        Assert.Equal(0, await env.TableCountAsync("config_field_mapping_v7"));
        Assert.Equal(MappingMetadataFormat.CurrentVersion, await env.ScalarAsync(
            "SELECT format_version FROM config_field_mapping WHERE dataset_kind='gl';"));
        Assert.Equal(originalOptionsJson, await env.TextAsync(
            "SELECT options_json FROM config_field_mapping WHERE dataset_kind='gl';"));

        var reopened = env.CreateReopenedDatabase();
        var restored = await new LocalMappingStateStore(reopened).FindAsync(
            env.ProjectId,
            DatasetKind.Gl,
            CancellationToken.None);
        Assert.NotNull(restored);
        Assert.Equal(MappingMetadataFormat.CurrentVersion, restored.FormatVersion);
        Assert.Equal(ApprovalDateModeNames.SameAsPostDate, restored.GlOptions?.ApprovalDateMode);
        Assert.Equal(["Posted"], restored.GlOptions?.PostingStatusPolicy?.AcceptedValues);
        Assert.True(restored.GlOptions?.PostingStatusPolicy?.IncludeBlank);
        Assert.Equal(["M"], restored.GlOptions?.ManualAutoPolicy.ManualValues);
        Assert.Equal(["A"], restored.GlOptions?.ManualAutoPolicy.AutomaticValues);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task FrozenV6_MigratesAtomically_PreservesSourcesAndDefinitions_StalesDerivedResults(string provider)
    {
        using var env = new LocalEnv(provider, seedV6: true);

        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);
        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        Assert.Equal("9", await env.TextAsync(
            "SELECT value FROM schema_info WHERE key = 'schema_version';"));
        Assert.Equal(1, await env.ColumnCountAsync("target_gl_entry", "posting_status"));
        Assert.Equal(1, await env.ColumnCountAsync("target_gl_entry", "is_effective"));
        Assert.Equal(1, await env.ColumnCountAsync("target_gl_entry", "exclusion_reason"));
        Assert.Equal(1, await env.ColumnCountAsync("config_field_mapping", "format_version"));
        Assert.Equal(1, await env.ColumnCountAsync("config_field_mapping", "options_json"));
        Assert.Equal(1, await env.ScalarAsync(
            "SELECT COUNT(*) FROM config_field_mapping WHERE format_version=1 AND options_json IS NULL;"));
        Assert.Equal(1, await env.ScalarAsync(
            "SELECT COUNT(*) FROM target_gl_entry WHERE document_number='DOC-V6' "
            + "AND posting_status IS NULL AND is_effective IS NULL AND exclusion_reason IS NULL;"));
        Assert.Equal(1, await env.ScalarAsync(
            "SELECT COUNT(*) FROM target_account_mapping "
            + "WHERE standardized_category='Revenue' AND category_id='builtin.revenue';"));
        Assert.Equal(
            AccountTaxonomyBuiltIns.All.ToArray(),
            (await env.TaxonomyAsync()).ToArray());

        var scenario = await env.TextAsync(
            "SELECT definition_json FROM config_filter_scenario WHERE position=1;");
        Assert.Contains("\"debitCategory\":\"Cash\"", scenario, StringComparison.Ordinal);
        Assert.Contains("\"creditCategory\":\"Revenue\"", scenario, StringComparison.Ordinal);
        Assert.Contains("\"debitCategoryIds\":[\"builtin.cash\"]", scenario, StringComparison.Ordinal);
        Assert.Contains("\"creditCategoryIds\":[\"builtin.revenue\"]", scenario, StringComparison.Ordinal);
        Assert.Contains("\"logicVersion\":\"filter-2026-08-04-v6\"", scenario, StringComparison.Ordinal);
        Assert.False(RuleLogicVersions.IsCurrent(new SavedFilterScenario(
            1,
            "legacy pair",
            "migration oracle",
            scenario,
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero))));

        Assert.Equal(0, await env.ScalarAsync("SELECT COUNT(*) FROM result_rule_run;"));
        Assert.Equal(0, await env.ScalarAsync("SELECT COUNT(*) FROM result_inf_sampling_test_sample;"));
        Assert.Equal(0, await env.ScalarAsync("SELECT COUNT(*) FROM result_filter_run;"));
        Assert.Equal(1, await env.ScalarAsync(
            "SELECT COUNT(*) FROM config_result_stale_state "
            + "WHERE singleton=1 AND validation_stale=1 AND prescreen_stale=1 AND filter_stale=1;"));

        var reopened = env.CreateReopenedDatabase();
        await reopened.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);
        Assert.Equal("9", await env.TextAsync(
            "SELECT value FROM schema_info WHERE key = 'schema_version';"));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task FrozenV6_FaultAfterResultResetBeforeVersionBump_RollsBackEntireV7Transaction(string provider)
    {
        using var env = new LocalEnv(provider, seedV6: true);
        env.SetMigrationFault(stage =>
        {
            if (stage == "after-v7-result-reset")
            {
                throw new InvalidOperationException("injected v7 migration fault");
            }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None));

        Assert.Equal("6", await env.TextAsync(
            "SELECT value FROM schema_info WHERE key = 'schema_version';"));
        Assert.Equal(0, await env.ColumnCountAsync("target_gl_entry", "posting_status"));
        Assert.Equal(0, await env.ColumnCountAsync("config_field_mapping", "format_version"));
        Assert.Equal(0, await env.ColumnCountAsync("target_account_mapping", "category_id"));
        Assert.Equal(0, await env.TableCountAsync("config_account_taxonomy"));
        Assert.Equal(0, await env.TableCountAsync("config_result_stale_state"));
        Assert.Equal(0, await env.TableCountAsync("config_field_mapping_v7"));
        Assert.Equal(2, await env.ScalarAsync("SELECT COUNT(*) FROM result_rule_run;"));
        Assert.Equal(1, await env.ScalarAsync("SELECT COUNT(*) FROM result_inf_sampling_test_sample;"));
        Assert.Equal(1, await env.ScalarAsync("SELECT COUNT(*) FROM result_filter_run;"));
        Assert.DoesNotContain("CategoryIds", await env.TextAsync(
            "SELECT definition_json FROM config_filter_scenario WHERE position=1;"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MigratedV7_ClosedDatabaseColdCopiedToAnotherRoot_ReopensWithoutMigration(string provider)
    {
        using var source = new LocalEnv(provider, seedV6: true);
        await source.Database.EnsureCreatedAsync(source.ProjectId, CancellationToken.None);
        await source.PrepareColdCopyAsync();

        using var targetRoot = new TempProjectRoot();
        var targetFolder = new JetProjectFolder(targetRoot.Path);
        Directory.CreateDirectory(targetFolder.GetProjectDirectory(source.ProjectId));
        var targetDatabase = provider == "duckdb"
            ? (ILocalProjectDatabase)new DuckDbProjectDatabase(targetFolder)
            : new SqliteProjectDatabase(targetFolder);
        File.Copy(
            source.DatabasePath,
            provider == "duckdb"
                ? ((DuckDbProjectDatabase)targetDatabase).GetDatabasePath(source.ProjectId)
                : ((SqliteProjectDatabase)targetDatabase).GetDatabasePath(source.ProjectId));

        await targetDatabase.EnsureCreatedAsync(source.ProjectId, CancellationToken.None);
        await using var connection = targetDatabase.CreateConnection(source.ProjectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT value FROM schema_info WHERE key='schema_version'), "
            + "(SELECT COUNT(*) FROM target_gl_entry WHERE document_number='DOC-V6');";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("9", reader.GetString(0));
        Assert.Equal(1, reader.GetInt64(1));
    }

    [Fact]
    public void SqlServerColdCopy_IsNotAProjectDatabaseContract()
    {
        Assert.Equal(ProjectDocument.SqlServerDatabaseProvider, "sqlServer");
        Assert.NotEqual(DuckDbProjectDatabase.DatabaseFileName, ProjectDocument.SqlServerDatabaseProvider);
    }

    private sealed class LocalEnv : IDisposable
    {
        private readonly TempProjectRoot _root = new();
        private readonly string _provider;

        public LocalEnv(string provider, bool seedV6)
        {
            _provider = provider;
            Folder = new JetProjectFolder(_root.Path);
            ProjectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Folder.GetProjectDirectory(ProjectId));
            Database = CreateDatabase();
            if (seedV6)
            {
                using var connection = Database.CreateConnection(ProjectId);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = FrozenV6SchemaAndData;
                command.ExecuteNonQuery();
            }
        }

        public JetProjectFolder Folder { get; }
        public string ProjectId { get; }
        public ILocalProjectDatabase Database { get; }
        public string DatabasePath => Database is DuckDbProjectDatabase duckDb
            ? duckDb.GetDatabasePath(ProjectId)
            : Assert.IsType<SqliteProjectDatabase>(Database).GetDatabasePath(ProjectId);

        public ILocalProjectDatabase CreateReopenedDatabase() => CreateDatabase();

        public void SetMigrationFault(Action<string> hook)
        {
            if (Database is SqliteProjectDatabase sqlite)
            {
                sqlite.MigrationFaultHookForTests = hook;
            }
            else
            {
                Assert.IsType<DuckDbProjectDatabase>(Database).MigrationFaultHookForTests = hook;
            }
        }

        public Task<long> ColumnCountAsync(string table, string column) =>
            ScalarAsync($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}';");

        public Task<long> TableCountAsync(string table) => _provider == "sqlite"
            ? ScalarAsync($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';")
            : ScalarAsync($"SELECT COUNT(*) FROM information_schema.tables WHERE table_name='{table}';");

        public async Task<long> V7TableCountAsync()
        {
            long count = 0;
            foreach (var table in new[]
                     {
                         "config_gl_rde_field",
                         "target_gl_rde_value",
                         "config_account_taxonomy",
                         "config_result_stale_state"
                     })
            {
                count += await TableCountAsync(table);
            }

            return count;
        }

        public Task<long> IndexCountAsync(string table) => _provider == "sqlite"
            ? ScalarAsync($"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND tbl_name='{table}' AND name LIKE 'ix_%';")
            : ScalarAsync($"SELECT COUNT(*) FROM duckdb_indexes() WHERE table_name='{table}' AND index_name LIKE 'ix_%';");

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async Task<string> TextAsync(string sql)
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async Task<IReadOnlyList<AccountTaxonomyCategory>> TaxonomyAsync()
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT category_id, label, ordinal, semantic_role, is_builtin "
                + "FROM config_account_taxonomy ORDER BY ordinal, category_id;";
            await using var reader = await command.ExecuteReaderAsync();
            var categories = new List<AccountTaxonomyCategory>();
            while (await reader.ReadAsync())
            {
                categories.Add(new AccountTaxonomyCategory(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    Convert.ToBoolean(reader.GetValue(4))));
            }

            return categories;
        }

        public async Task PrepareColdCopyAsync()
        {
            if (Database is SqliteProjectDatabase)
            {
                await using (var connection = Database.CreateConnection(ProjectId))
                {
                    await connection.OpenAsync();
                    await using var checkpoint = connection.CreateCommand();
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    await checkpoint.ExecuteNonQueryAsync();
                }

                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                return;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        private ILocalProjectDatabase CreateDatabase() => _provider == "duckdb"
            ? new DuckDbProjectDatabase(Folder)
            : new SqliteProjectDatabase(Folder);

        public void Dispose() => _root.Dispose();
    }
}
