using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class FormalWorkbookTypographyTests
{
    /// <summary>
    /// Excel 一列最高 409 點。11 點字每行 16.5 點：24 行是 396 點，照行數加高；25 行算起來 412.5 點，超過上限，寫成 409 點。
    /// </summary>
    [Theory]
    [InlineData(11D, 24, 396D)]
    [InlineData(11D, 25, 409D)]
    [InlineData(10D, 41, 409D)]
    public void RowHeight_ManyWrappedLines_StopsAtExcelRowHeightLimit(double fontSize, int lines, double expected)
    {
        Assert.Equal(expected, FormalWorkbookTypography.RowHeight(fontSize, lines));
    }
}
