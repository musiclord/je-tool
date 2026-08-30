using ClosedXML.Excel;
using JET.Application;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class DemoWorkbookWriterTests
{
    [Fact]
    public async Task WriteAuthorizedPreparer_WritesSingleColumnNames()
    {
        var writer = new DemoWorkbookWriter();
        var data = DemoDataFactory.Create();

        var file = await writer.WriteAuthorizedPreparerAsync(data, CancellationToken.None);

        Assert.Equal(data.AuthorizedPreparerFileName, file.FileName);
        Assert.True(File.Exists(file.FilePath));

        using var workbook = new XLWorkbook(file.FilePath);
        var ws = workbook.Worksheets.First();
        Assert.Equal(data.AuthorizedPreparerColumns[0], ws.Cell(1, 1).GetString());

        var names = ws.RowsUsed().Skip(1).Select(r => r.Cell(1).GetString()).ToList();
        Assert.Equal(data.AuthorizedPreparers.Count, names.Count);
        Assert.Equal(data.AuthorizedPreparers.OrderBy(x => x), names.OrderBy(x => x));
    }

    [Fact]
    public async Task WriteGl_IsMemoizedAcrossCalls()
    {
        var writer = new DemoWorkbookWriter();
        var data = DemoDataFactory.Create();

        var first = await writer.WriteGlAsync(data, CancellationToken.None);
        var second = await writer.WriteGlAsync(data, CancellationToken.None);

        Assert.Equal(first.FilePath, second.FilePath); // 同一行程只寫一次,重用同檔
        Assert.True(File.Exists(first.FilePath));
    }
}
