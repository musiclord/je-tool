using JET.AuditCore;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class InfSamplingPrfTests
{
    [Theory]
    [InlineData(1L, 1L, 392_932_026_216_200_272L)]
    [InlineData(48_271L, 1L, 3_674_251_200_523_052_541L)]
    [InlineData(48_271L, 2L, 1_415_422_971_068_456_559L)]
    [InlineData(2_147_483_646L, 2_147_483_646L, 391_803_713_854_154_522L)]
    [InlineData(20_260_801L, 123_456_789L, 251_248_875_147_623_848L)]
    [InlineData(20_260_801L, 2_147_483_648L, 925_362_786_551_184_480L)]
    [InlineData(20_260_801L, 4_611_686_014_132_420_608L, 2_382_869_688_066_897_686L)]
    public void StableOrderingKey_FixedVectorsMatchCanonicalValues(
        long seed,
        long sourceRowNumber,
        long expected)
    {
        Assert.Equal(
            expected,
            JetAuditProgram.ComputeInfSamplingOrderingKey(seed, sourceRowNumber));
    }

    [Fact]
    public void StableOrderingKey_FixedSyntheticPopulationPassesBucketChiSquareSanity()
    {
        // 固定 oracle 實測 χ² = 31.54368（df=31）；60 是寬鬆的迴歸上界，
        // 用來抓回線性／集中排序，不把這項 sanity guard 誤當正式統計證明。
        const int populationSize = 100_000;
        const int bucketCount = 32;
        const double chiSquareUpperBound = 60.0;
        const long seed = 20_260_801;
        var counts = new int[bucketCount];

        for (var sourceRowNumber = 1L; sourceRowNumber <= populationSize; sourceRowNumber++)
        {
            var key = JetAuditProgram.ComputeInfSamplingOrderingKey(seed, sourceRowNumber);
            var bucket = (int)((decimal)key * bucketCount
                / JetAuditProgram.InfSamplingOrderingDomainSize);
            counts[bucket]++;
        }

        var expectedPerBucket = (double)populationSize / bucketCount;
        var chiSquare = counts.Sum(count =>
            Math.Pow(count - expectedPerBucket, 2) / expectedPerBucket);

        Assert.All(counts, count => Assert.True(count > 0));
        Assert.InRange(chiSquare, 0, chiSquareUpperBound);
    }

    // 舊版案件的三列（缺版本、缺種子、第 1 版）已隨固定種子與舊排序法一起移除，改由下一個測試鎖定拒絕行為。
    [Theory]
    [InlineData(123_456L, 2, true, 123_456L, 2)]
    [InlineData(null, 1, false, 0L, 0)]
    [InlineData(null, 2, false, 0L, 0)]
    [InlineData(0L, 2, false, 0L, 0)]
    [InlineData(2_147_483_647L, 2, false, 0L, 0)]
    [InlineData(123_456L, 99, false, 0L, 0)]
    public void ResolveSeedVersion_RejectsCorruption(
        long? persistedSeed,
        int? persistedVersion,
        bool expectedValid,
        long expectedSeed,
        int expectedVersion)
    {
        var result = JetAuditProgram.ResolveInfSamplingSeed(persistedSeed, persistedVersion);

        Assert.Equal(expectedValid, result.IsValid);
        Assert.Equal(expectedSeed, result.Seed);
        Assert.Equal(expectedVersion, result.AlgorithmVersion);
        Assert.Equal(expectedValid, result.Error is null);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(123_456L, null)]
    [InlineData(123_456L, 1)]
    [InlineData(null, 1)]
    public void ResolveSeedVersion_LegacyProject_IsRejectedWithoutFallback(
        long? persistedSeed,
        int? persistedVersion)
    {
        var result = JetAuditProgram.ResolveInfSamplingSeed(persistedSeed, persistedVersion);

        Assert.False(result.IsValid);
        Assert.True(result.IsLegacyProject);
        Assert.Equal(0L, result.Seed);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData(0L, 2)]
    [InlineData(123_456L, 99)]
    [InlineData(null, 2)]
    public void ResolveSeedVersion_Corruption_IsNotReportedAsLegacyProject(
        long? persistedSeed,
        int? persistedVersion)
    {
        var result = JetAuditProgram.ResolveInfSamplingSeed(persistedSeed, persistedVersion);

        Assert.False(result.IsValid);
        Assert.False(result.IsLegacyProject);
    }
}
