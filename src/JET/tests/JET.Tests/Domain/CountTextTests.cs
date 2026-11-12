using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 畫面上的筆數、列數這類數量加千分位；Excel 列號、第幾組第幾條與頁碼這類位置不加。
/// 後端產生、顯示在畫面上的訊息都經過同一個格式函式。預期文字都是手寫的固定答案。
/// </summary>
public sealed class CountTextTests
{
    [Theory]
    [InlineData(1234567L, "1,234,567")]
    [InlineData(12345L, "12,345")]
    [InlineData(1000L, "1,000")]
    [InlineData(999L, "999")]
    [InlineData(0L, "0")]
    [InlineData(-1234L, "-1,234")]
    public void Format_AddsThousandsSeparatorsToCounts(long value, string expected)
    {
        Assert.Equal(expected, CountText.Format(value));
    }

    [Fact]
    public void ProjectionFailure_FormatsTheCountButNotTheSourceRowNumber()
    {
        var error = ProjectionFailureText.Create(new ProjectionResult(0,
            [new RowProjectionError(12345, "金額", "abc", "不是有效金額。請確認來源資料的金額格式")])
        {
            TotalErrorCount = 12345
        });

        // 開頭的 12,345 是無法讀取的列數，要加千分位；「第 12345 列」是來源列號，不加。
        Assert.Equal(
            "有 12,345 列的值無法讀取，本次配對未儲存。以下僅列出部分的值與列號。" +
            "來源欄「金額」有 1 列不是有效金額：「abc」在第 12345 列。請確認來源資料的金額格式。",
            error.Message);
    }
}
