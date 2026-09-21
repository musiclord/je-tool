using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class FilterNestedVoucherMigrationTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task FrozenV10Taxonomy_AddsRootPathsWithoutChangingDefinitions(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == "sqlite" ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(id));
        await using var connection = database.CreateConnection(id);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // Frozen v10 columns, independent of the current schema factory.
        command.CommandText = """
            CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO schema_info VALUES ('schema_version','10');
            CREATE TABLE config_account_taxonomy (category_id TEXT PRIMARY KEY, label TEXT NOT NULL,
                ordinal INTEGER NOT NULL, semantic_role TEXT NOT NULL, is_builtin INTEGER NOT NULL, revision INTEGER NOT NULL);
            INSERT INTO config_account_taxonomy VALUES ('builtin.cash','Cash',0,'cash',1,7),
                ('custom.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','Local cash',1,'cash',0,7);
            CREATE TABLE config_filter_scenario (definition_json TEXT NOT NULL);
            INSERT INTO config_filter_scenario VALUES ('{"groups":[{"matchScope":"sameVoucher","rules":[{"type":"accountSide","categoryIds":["builtin.cash"]}]}]}');
            """;
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT definition_json FROM config_filter_scenario";
        var original = await command.ExecuteScalarAsync();
        await AccountTaxonomyHierarchy.UpgradeLocalAsync(connection, CancellationToken.None);
        await AccountTaxonomyHierarchy.UpgradeLocalAsync(connection, CancellationToken.None);
        command.CommandText = "SELECT definition_json FROM config_filter_scenario";
        Assert.Equal(original, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT value FROM schema_info WHERE key='schema_version'";
        Assert.Equal("11", await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM config_account_taxonomy WHERE parent_category_id IS NULL AND revision=7 AND semantic_role='cash'";
        Assert.Equal(2, Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT COUNT(*) FROM config_account_taxonomy_path WHERE ancestor_id=descendant_id";
        Assert.Equal(2, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }
}
