using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class WorkpaperStep4StreamRepositoryTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task LocalProviders_StreamHighVoucherCardinalityInOneOrderedEnumeration(
        string provider)
    {
        const int voucherCount = 757;
        await using var engine = await PaginationBoundaryEngine.CreateLocalAsync(
            provider,
            voucherCount);

        var rows = await ReadAllAsync(
            Assert.IsAssignableFrom<IWorkpaperStep4StreamRepository>(
                engine.VoucherMatrix),
            engine.ProjectId);

        AssertRows(rows, voucherCount);
    }

    [SqlServerFact]
    public async Task SqlServer_StreamsHighVoucherCardinalityWithSameProjection()
    {
        const int voucherCount = 757;
        await using var engine = await PaginationBoundaryEngine.CreateSqlServerAsync(
            voucherCount);

        var rows = await ReadAllAsync(
            Assert.IsAssignableFrom<IWorkpaperStep4StreamRepository>(
                engine.VoucherMatrix),
            engine.ProjectId);

        AssertRows(rows, voucherCount);
    }

    private static async Task<IReadOnlyList<WorkpaperStep4VoucherRow>> ReadAllAsync(
        IWorkpaperStep4StreamRepository repository,
        string projectId)
    {
        var rows = new List<WorkpaperStep4VoucherRow>();
        await foreach (var row in repository.StreamAsync(
                           projectId,
                           new(
                               GlPopulationScope.AuditPeriod,
                               "2025-01-01",
                               "2025-12-31"),
                           [1],
                           CancellationToken.None))
        {
            rows.Add(row);
        }
        return rows;
    }

    private static void AssertRows(
        IReadOnlyList<WorkpaperStep4VoucherRow> rows,
        int voucherCount)
    {
        Assert.Equal(voucherCount, rows.Count);
        Assert.Equal(
            Enumerable.Range(1, voucherCount)
                .Select(index => $"DOC-{index:000}"),
            rows.Select(row => row.Voucher.DocumentNumber));
        Assert.All(rows, row => Assert.Equal([1], row.MatchedPositions));
        Assert.All(rows, row => Assert.Equal(10_000, row.Voucher.VoucherTotalScaled));
        Assert.All(rows, row => Assert.Equal("2025-06-01", row.Voucher.PostDate));
    }
}
