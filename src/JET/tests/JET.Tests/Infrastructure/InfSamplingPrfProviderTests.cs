using System.Data.Common;
using System.Globalization;
using JET.AuditCore;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// INF v2 抽樣排序鍵：SQLite 算出的值要和 C# 標準實作的固定向量逐筆相同。
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
    public async Task FixedVectors_SqliteSqlEqualsCanonicalCSharp()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await AssertFixedVectorsAsync(connection, SqliteDialect.Instance);
    }

    private static async Task AssertFixedVectorsAsync(DbConnection connection, ISqlDialect dialect)
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

            Assert.Equal(vector.Expected, InfSamplingPrf.ComputeOrderingKey(
                vector.Seed,
                vector.SourceRow));
            Assert.Equal(vector.Expected, actual);
        }
    }
}
