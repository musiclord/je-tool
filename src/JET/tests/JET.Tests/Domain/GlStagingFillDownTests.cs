using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 預篩選收斂計畫階段六「往下填補空白」的串流狀態與欄位規則。這兩個類別是本階段新加的，沒有可保存的第一次失敗；
/// 對外行為（確認配對的回應、投影結果、重開案件）由 GlFillDownWorkflowTests 保存第一次失敗。
/// </summary>
public sealed class GlStagingFillDownTests
{
    private static GlMappingSpec Spec(params string[] fillDownFields) => new(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.DocNum] = "傳票號碼",
            [GlMappingKeys.PostDate] = "傳票日期",
            [GlMappingKeys.DocDate] = "傳票日期",
            [GlMappingKeys.AccNum] = "科目代號",
            [GlMappingKeys.Amount] = "金額",
            [GlMappingKeys.DcField] = "借方旗標",
            [GlMappingKeys.DcDebitCode] = "1",
            [GlMappingKeys.DcCreditCode] = "0"
        },
        GlAmountMode.AmountWithFlag)
    {
        Options = new GlMappingOptions(
            ApprovalDateModeNames.Mapped,
            null,
            new GlManualAutoPolicy([], []),
            [])
        {
            FillDownFields = fillDownFields
        }
    };

    // 讀取器只保留有內容的格，所以缺鍵就是空白；這裡用 null 代表那一格沒讀到。
    private static Dictionary<string, string> Row(params (string Column, string? Value)[] cells)
    {
        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (column, value) in cells)
        {
            if (value is not null)
            {
                row[column] = value;
            }
        }

        return row;
    }

    [Fact]
    public void Apply_FillsBlanksOfChosenColumnsFromThePreviousRow_AndLeavesOtherColumnsAlone()
    {
        var fillDown = GlStagingFillDown.Create(Spec(GlMappingKeys.DocNum, GlMappingKeys.PostDate));
        Assert.NotNull(fillDown);

        var first = Row(("傳票號碼", "JV-1"), ("傳票日期", "2025-03-01"), ("科目代號", "1000"), ("金額", "10"));
        var second = Row(("科目代號", "4000"), ("金額", "10"));
        var third = Row(("傳票號碼", "JV-2"), ("科目代號", "1000"), ("金額", "20"));
        var fourth = Row(("傳票號碼", " "), ("傳票日期", ""), ("金額", "15"));

        // 沒有填到任何格時回傳原本那一份。
        Assert.Same(first, fillDown.Apply(1, first));

        var filledSecond = fillDown.Apply(1, second);
        Assert.Equal("JV-1", filledSecond["傳票號碼"]);
        Assert.Equal("2025-03-01", filledSecond["傳票日期"]);
        Assert.Equal("4000", filledSecond["科目代號"]);
        // 交進來的那一列（PBC-GL 的值）不動。
        Assert.False(second.ContainsKey("傳票號碼"));

        var filledThird = fillDown.Apply(1, third);
        Assert.Equal("JV-2", filledThird["傳票號碼"]);
        Assert.Equal("2025-03-01", filledThird["傳票日期"]);

        // 只有空白的格也當空白；沒勾選的欄位空著就空著。
        var filledFourth = fillDown.Apply(1, fourth);
        Assert.Equal("JV-2", filledFourth["傳票號碼"]);
        Assert.Equal("2025-03-01", filledFourth["傳票日期"]);
        Assert.False(filledFourth.ContainsKey("科目代號"));

        Assert.Equal(5, fillDown.FilledCellCount);
    }

    [Fact]
    public void Apply_DoesNotCarryValuesIntoTheNextSource()
    {
        var fillDown = GlStagingFillDown.Create(Spec(GlMappingKeys.DocNum))!;
        fillDown.Apply(1, Row(("傳票號碼", "JV-1")));

        // 追加的第二個來源從頭開始：第一列的空白就是空白。
        var firstOfNextSource = Row(("科目代號", "1000"));
        Assert.Same(firstOfNextSource, fillDown.Apply(2, firstOfNextSource));
        Assert.Equal("JV-9", fillDown.Apply(2, Row(("傳票號碼", "JV-9")))["傳票號碼"]);
        Assert.Equal("JV-9", fillDown.Apply(2, Row())["傳票號碼"]);
        Assert.Equal(1, fillDown.FilledCellCount);
    }

    [Fact]
    public void SourceColumns_TwoFieldsOnOneColumn_FillThatColumnOnce()
    {
        var spec = Spec(GlMappingKeys.PostDate, GlMappingKeys.DocDate);
        Assert.Equal(["傳票日期"], GlStagingFillDown.SourceColumns(spec));

        var fillDown = GlStagingFillDown.Create(spec)!;
        fillDown.Apply(1, Row(("傳票日期", "2025-03-01")));
        Assert.Equal("2025-03-01", fillDown.Apply(1, Row())["傳票日期"]);
        Assert.Equal(1, fillDown.FilledCellCount);
    }

    [Fact]
    public void Create_WithoutChosenFields_ReturnsNull()
    {
        Assert.Null(GlStagingFillDown.Create(Spec()));
    }

    [Fact]
    public void NormalizeFillDownFields_OrdersByCatalog_AndRejectsFieldsThatCannotBeFilled()
    {
        var mapping = Spec().Mapping;

        // 使用者 2026-10-28 裁定依來源欄成組：過帳日期與傳票日期都配到「傳票日期」，勾過帳日期時傳票日期也一起列入。
        // 這行原本預期只有勾選的三個欄位，寫在裁定之前；結案前的完整 Public 才跑到它而失敗，預期值依裁定改寫。
        Assert.Equal(
            [GlMappingKeys.DocNum, GlMappingKeys.PostDate, GlMappingKeys.DocDate, GlMappingKeys.AccNum],
            GlMappingOptionsRules.NormalizeFillDownFields(
                mapping,
                [GlMappingKeys.AccNum, GlMappingKeys.PostDate, GlMappingKeys.DocNum]));
        Assert.Empty(GlMappingOptionsRules.NormalizeFillDownFields(mapping, []));

        // 沒有來源欄、字面值、重複、不是 GL 欄位，都不能填。
        Assert.Throws<ArgumentException>(() => GlMappingOptionsRules.NormalizeFillDownFields(mapping, [GlMappingKeys.ApproveBy]));
        Assert.Throws<ArgumentException>(() => GlMappingOptionsRules.NormalizeFillDownFields(mapping, [GlMappingKeys.DcDebitCode]));
        Assert.Throws<ArgumentException>(() => GlMappingOptionsRules.NormalizeFillDownFields(mapping, [GlMappingKeys.DocNum, GlMappingKeys.DocNum]));
        Assert.Throws<ArgumentException>(() => GlMappingOptionsRules.NormalizeFillDownFields(mapping, ["notAField"]));
    }
}
