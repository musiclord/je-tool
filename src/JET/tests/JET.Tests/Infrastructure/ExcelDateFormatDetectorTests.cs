using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Excel 數字格式的日期/時間判定（guide §3.1.5）。
/// oracle：ECMA-376 §18.8.30 內建格式登錄表（id 0–49 標準、50–58 東亞地區變體）。
/// 設計技術：內建 id 邊界值分析（日期區段的下鄰/邊界/上鄰）+ 自訂格式碼等價分割。
/// </summary>
public sealed class ExcelDateFormatDetectorTests
{
    private static readonly IReadOnlyDictionary<int, string> NoCustomFormats =
        new Dictionary<int, string>();

    // 內建 id BVA：13(分數)否 | 14–17 日期 | 18–21 時間 | 22 日期時間 | 23(保留)否 |
    // 26(保留)否 | 27–36 東亞日期 | 44(會計)否 | 45–47 時間 | 48(科學)否 | 49(文字)否 |
    // 50–58 東亞日期 | 59(超界)否
    [Theory]
    [InlineData(0, ExcelNumberKind.None)]   // General
    [InlineData(13, ExcelNumberKind.None)]
    [InlineData(14, ExcelNumberKind.Date)]  // 真實 PBC 檔的日期欄樣式
    [InlineData(17, ExcelNumberKind.Date)]
    [InlineData(18, ExcelNumberKind.Time)]
    [InlineData(21, ExcelNumberKind.Time)]
    [InlineData(22, ExcelNumberKind.Date)]  // 日期時間 → 投影只取日期
    [InlineData(23, ExcelNumberKind.None)]
    [InlineData(26, ExcelNumberKind.None)]
    [InlineData(27, ExcelNumberKind.Date)]
    [InlineData(36, ExcelNumberKind.Date)]
    [InlineData(43, ExcelNumberKind.None)]  // 會計千分位（真實 TB 的金額樣式）
    [InlineData(44, ExcelNumberKind.None)]
    [InlineData(45, ExcelNumberKind.Time)]
    [InlineData(47, ExcelNumberKind.Time)]
    [InlineData(48, ExcelNumberKind.None)]
    [InlineData(49, ExcelNumberKind.None)]
    [InlineData(50, ExcelNumberKind.Date)]
    [InlineData(58, ExcelNumberKind.Date)]
    [InlineData(59, ExcelNumberKind.None)]
    public void Classify_BuiltinIds(int numFmtId, ExcelNumberKind expected)
    {
        Assert.Equal(expected, ExcelDateFormatDetector.Classify(numFmtId, NoCustomFormats));
    }

    // 自訂格式碼等價分割：日期記號 / 時間記號 / 引號字面值內的記號不算 /
    // [..] 條件與色彩區段不算、但 [h] 經過時間區段算 / 反斜線跳脫不算 / 純數字格式
    [Theory]
    [InlineData("yyyy/m/d", ExcelNumberKind.Date)]
    [InlineData("yyyy\"年\"m\"月\"d\"日\"", ExcelNumberKind.Date)]   // 引號內的「年月日」是字面值，y/m/d 在引號外
    [InlineData("mmm-yy", ExcelNumberKind.Date)]
    [InlineData("mmmm", ExcelNumberKind.Date)]                        // 純月份名 → 日期
    [InlineData("h:mm:ss", ExcelNumberKind.Time)]
    [InlineData("[h]:mm", ExcelNumberKind.Time)]                      // 經過時間 token [h]
    [InlineData("mm:ss", ExcelNumberKind.Time)]
    [InlineData("0_);[Red]\\(0\\)", ExcelNumberKind.None)]            // 真實 PBC 檔的自訂 176：[Red] 區段 + \( 跳脫
    [InlineData("_-* #,##0.00_-;\\-* #,##0.00_-;_-* \"-\"??_-;_-@_-", ExcelNumberKind.None)] // 會計格式 43 的格式碼
    [InlineData("\"day: \"0.0", ExcelNumberKind.None)]                // 引號內的 day 不算日期記號
    [InlineData("0.00E+00", ExcelNumberKind.None)]
    [InlineData("@", ExcelNumberKind.None)]
    [InlineData("", ExcelNumberKind.None)]
    public void ClassifyFormatCode_TokenScan(string formatCode, ExcelNumberKind expected)
    {
        Assert.Equal(expected, ExcelDateFormatDetector.ClassifyFormatCode(formatCode));
    }

    [Fact]
    public void Classify_CustomIdUsesFormatCode()
    {
        var custom = new Dictionary<int, string>
        {
            [176] = "0_);[Red]\\(0\\)",
            [177] = "yyyy/m/d"
        };

        Assert.Equal(ExcelNumberKind.None, ExcelDateFormatDetector.Classify(176, custom));
        Assert.Equal(ExcelNumberKind.Date, ExcelDateFormatDetector.Classify(177, custom));

        // 未登錄的自訂 id（理論上不該出現）→ None，不猜測
        Assert.Equal(ExcelNumberKind.None, ExcelDateFormatDetector.Classify(200, custom));
    }
}
