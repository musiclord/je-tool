using System.Data.Common;
using System.Globalization;
using DuckDB.NET.Data;
using JET.AuditCore;
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// INF v2 canonical C# fixed vectors 與三個真實 SQL provider 的逐筆等價 gate；另以實際
/// InfSampleInsert 回放 legacy v1 golden membership，防止相容分支被新版 renderer 偷換。
/// </summary>
public sealed class InfSamplingPrfProviderTests
{
    private static readonly (long Seed, long SourceRow, long Expected)[] Vectors =
    [
        (1, 1, 392_932_026_216_200_272),
        (48_271, 1, 3_674_251_200_523_052_541),
        (48_271, 2, 1_415_422_971_068_456_559),
        (2_147_483_646, 2_147_483_646, 391_803_713_854_154_522),
        (20_260_801, 123_456_789, 251_248_875_147_623_848),
        (20_260_801, 2_147_483_648, 925_362_786_551_184_480),
        (20_260_801, 4_611_686_014_132_420_608, 2_382_869_688_066_897_686)
    ];

    [Fact]
    public async Task V2FixedVectors_SqliteSqlEqualsCanonicalCSharp()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await AssertV2VectorsAsync(connection, SqliteDialect.Instance);
        await AssertLegacyGoldenAsync(connection, SqliteDialect.Instance, schemaPrefix: "");
    }

    [Fact]
    public async Task V2FixedVectors_DuckDbSqlEqualsCanonicalCSharp()
    {
        await using var connection = new DuckDbConnectionAdapter(new DuckDBConnection("DataSource=:memory:"));
        await connection.OpenAsync();

        await AssertV2VectorsAsync(connection, DuckDbDialect.Instance);
        await AssertLegacyGoldenAsync(connection, DuckDbDialect.Instance, schemaPrefix: "");
    }

    [SqlServerFact]
    public async Task V2FixedVectors_SqlServerSqlEqualsCanonicalCSharp()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        if (project is null)
        {
            return;
        }

        await using var connection = project.Database.CreateConnection(project.ProjectId);
        await connection.OpenAsync();

        await AssertV2VectorsAsync(connection, SqlServerDialect.Instance);
        await SeedSqlServerLegacyRowsAsync(connection, project.ProjectId);
        await AssertLegacyGoldenAsync(
            connection,
            SqlServerDialect.Instance,
            SqlServerProjectSchema.QualifierFor(project.ProjectId),
            createTables: false);
    }

    private static async Task AssertV2VectorsAsync(DbConnection connection, ISqlDialect dialect)
    {
        foreach (var vector in Vectors)
        {
            var source = $"CAST({vector.SourceRow.ToString(CultureInfo.InvariantCulture)} AS BIGINT)";
            var seed = $"CAST({vector.Seed.ToString(CultureInfo.InvariantCulture)} AS BIGINT)";
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {dialect.InfSampleOrderingKey(source, seed)};";

            var actual = Convert.ToInt64(
                await command.ExecuteScalarAsync(CancellationToken.None),
                CultureInfo.InvariantCulture);

            Assert.Equal(vector.Expected, JetAuditProgram.ComputeInfSamplingOrderingKey(
                vector.Seed,
                vector.SourceRow));
            Assert.Equal(vector.Expected, actual);
        }
    }

    private static async Task AssertLegacyGoldenAsync(
        DbConnection connection,
        ISqlDialect dialect,
        string schemaPrefix,
        bool createTables = true)
    {
        if (createTables)
        {
            await ExecuteAsync(
                connection,
                """
                CREATE TABLE target_gl_entry (
                    entry_id BIGINT PRIMARY KEY,
                    source_row_number BIGINT NOT NULL,
                    document_number VARCHAR(32),
                    line_item VARCHAR(32),
                    post_date VARCHAR(32),
                    is_effective INTEGER);
                CREATE TABLE result_inf_sampling_test_sample (
                    run_id VARCHAR(64) NOT NULL,
                    entry_id BIGINT NOT NULL,
                    document_number VARCHAR(32),
                    line_item VARCHAR(32),
                    PRIMARY KEY (run_id, entry_id));
                """);

            for (var row = 1; row <= 8; row++)
            {
                await ExecuteAsync(
                    connection,
                    $"INSERT INTO target_gl_entry VALUES ({row}, {row}, 'JV-{row}', '{row}', '2025-01-01', 1);");
            }
        }

        await using (var sample = connection.CreateCommand())
        {
            sample.CommandText = ValidationProcedures.InfSampleInsert(
                schemaPrefix,
                dialect,
                JetAuditProgram.LegacyInfSamplingAlgorithmVersion);
            Add(sample, "@runId", "legacy-golden");
            Add(sample, "@seed", 1_987_654_321L);
            Add(sample, "@n", 5);
            await sample.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var read = connection.CreateCommand();
        read.CommandText =
            $"SELECT entry_id FROM {schemaPrefix}result_inf_sampling_test_sample "
            + "WHERE run_id = @runId ORDER BY entry_id;";
        Add(read, "@runId", "legacy-golden");
        await using var reader = await read.ExecuteReaderAsync(CancellationToken.None);
        var members = new List<long>();
        while (await reader.ReadAsync(CancellationToken.None))
        {
            members.Add(reader.GetInt64(0));
        }

        Assert.Equal([4L, 5L, 6L, 7L, 8L], members);
    }

    private static async Task SeedSqlServerLegacyRowsAsync(DbConnection connection, string projectId)
    {
        var prefix = SqlServerProjectSchema.QualifierFor(projectId);
        for (var row = 1; row <= 8; row++)
        {
            await ExecuteAsync(
                connection,
                $"INSERT INTO {prefix}target_gl_entry "
                + "(batch_id, source_row_number, document_number, line_item, post_date, "
                + "is_effective, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr) VALUES "
                + $"('legacy', {row}, 'JV-{row}', '{row}', '2025-01-01', 1, 0, 0, 0, 'DEBIT');");
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
