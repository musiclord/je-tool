using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class TbRowProjectorTests
{
    private const int Scale = 10_000;

    [Fact]
    public void DebitCredit_ComputesChangeFromRealTbShape()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "會計科目編號",
                [TbMappingKeys.AccName] = "會計科目名稱",
                [TbMappingKeys.DebitAmt] = "借方金額",
                [TbMappingKeys.CreditAmt] = "貸方金額"
            },
            TbChangeMode.DebitCredit);

        // 合成餘額列：期初與期末借貸相抵後，變動為 0。
        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["會計科目編號"] = "110201",
            ["會計科目名稱"] = "現金-台幣",
            ["借方金額"] = "418509",
            ["貸方金額"] = "418509"
        });

        Assert.True(TbRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.NotNull(projected);
        Assert.Equal("110201", projected.AccountCode);
        Assert.Equal(0L, projected.ChangeAmountScaled);

        var movedRow = new StagingRow(3, new Dictionary<string, string>
        {
            ["會計科目編號"] = "110202",
            ["會計科目名稱"] = "現金-美元",
            ["借方金額"] = "107228",
            ["貸方金額"] = "108719"
        });

        Assert.True(TbRowProjector.TryProject(movedRow, spec, Scale, out var moved, out _));
        Assert.Equal(-14_910_000L, moved!.ChangeAmountScaled); // (107228-108719) × 10000
    }

    [Fact]
    public void DirectChange_ParsesSingleColumn()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.Amount] = "change"
            },
            TbChangeMode.DirectChange);

        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["acc"] = "1101",
            ["name"] = "現金",
            ["change"] = "-1234.56"
        });

        Assert.True(TbRowProjector.TryProject(row, spec, Scale, out var projected, out _));
        Assert.Equal(-12_345_600L, projected!.ChangeAmountScaled);
    }

    [Fact]
    public void DebitCredit_AccountingDashIsZero()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "總帳科目",
                [TbMappingKeys.AccName] = "短文",
                [TbMappingKeys.DebitAmt] = "報表期間借項餘額",
                [TbMappingKeys.CreditAmt] = "報表期間的貸項餘額"
            },
            TbChangeMode.DebitCredit);

        // 真實 PBC TB（會計格式輸出，guide §3.1.2）：零以單獨連字號顯示，
        // 借 "-"、貸 "100" → 變動 = 0 - 100 = -100。
        var row = new StagingRow(5, new Dictionary<string, string>
        {
            ["總帳科目"] = "70110000",
            ["短文"] = "利息收入",
            ["報表期間借項餘額"] = "-",
            ["報表期間的貸項餘額"] = "100"
        });

        Assert.True(TbRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.Equal(-1_000_000L, projected!.ChangeAmountScaled); // (0-100) × 10000
    }

    [Fact]
    public void DebitCredit_SubScaleInputs_RoundsOnlyFinalDifference()
    {
        // BVA：0.00005 在單欄定標時會進位，0.00004 不會；先做 decimal 差額 0.00001 後才定標應為 0。
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.DebitAmt] = "debit",
                [TbMappingKeys.CreditAmt] = "credit"
            },
            TbChangeMode.DebitCredit);
        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["debit"] = "0.00005",
            ["credit"] = "0.00004"
        });

        Assert.True(TbRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.Equal(0L, projected!.ChangeAmountScaled);
    }

    [Fact]
    public void BadAmount_ReturnsRowError()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.Amount] = "change"
            },
            TbChangeMode.DirectChange);

        var row = new StagingRow(7, new Dictionary<string, string>
        {
            ["acc"] = "1101",
            ["name"] = "現金",
            ["change"] = "oops"
        });

        Assert.False(TbRowProjector.TryProject(row, spec, Scale, out _, out var error));
        Assert.Equal(7, error!.SourceRowNumber);
        Assert.Equal("change", error.Field);
    }

    [Fact]
    public void TryProject_DebitCreditWithInvalidCreditAmount_ReturnsRowError()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.DebitAmt] = "debit",
                [TbMappingKeys.CreditAmt] = "credit"
            },
            TbChangeMode.DebitCredit);

        var row = new StagingRow(9, new Dictionary<string, string>
        {
            ["acc"] = "1101",
            ["name"] = "現金",
            ["debit"] = "100",
            ["credit"] = "oops"
        });

        Assert.False(TbRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(projected);
        Assert.Equal(9, error!.SourceRowNumber);
        Assert.Equal("credit", error.Field);
        Assert.Equal("oops", error.RawValue);
        Assert.Equal("is not a valid amount", error.Reason);
    }


    [Fact]
    public void TryProject_ChangeModeIsUnknown_ThrowsArgumentOutOfRangeException()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.Amount] = "change"
            },
            (TbChangeMode)999);

        var row = new StagingRow(10, new Dictionary<string, string>
        {
            ["change"] = "1"
        });

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            TbRowProjector.TryProject(row, spec, Scale, out _, out _));

        Assert.Equal("spec", exception.ParamName);
        Assert.Equal((TbChangeMode)999, exception.ActualValue);
    }

    // OpenClose（legacy SA=2）：本期變動 = 期末 − 期初。BVA/等價分割：正變動、負變動、
    // 零期初、零期末、零變動。oracle：規格手算（closing − opening）× scale。
    [Theory]
    [InlineData("1000", "1500", 5_000_000L)]    // 正變動 +500
    [InlineData("800", "300", -5_000_000L)]     // 負變動 −500
    [InlineData("0", "1500", 15_000_000L)]      // 零期初
    [InlineData("1000", "0", -10_000_000L)]     // 零期末
    [InlineData("1234.56", "1234.56", 0L)]      // 零變動（期初＝期末）
    public void OpenClose_ComputesClosingMinusOpening(string opening, string closing, long expectedScaled)
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.OpeningBalance] = "opening",
                [TbMappingKeys.ClosingBalance] = "closing"
            },
            TbChangeMode.OpenClose);

        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["acc"] = "1101",
            ["name"] = "現金",
            ["opening"] = opening,
            ["closing"] = closing
        });

        Assert.True(TbRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.Equal(expectedScaled, projected!.ChangeAmountScaled);
    }

    [Fact]
    public void OpenClose_SubScaleInputs_RoundsOnlyFinalDifference()
    {
        // BVA：先算期末 0.00005 − 期初 0.00004，再做一次 away-from-zero 定標。
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.OpeningBalance] = "opening",
                [TbMappingKeys.ClosingBalance] = "closing"
            },
            TbChangeMode.OpenClose);
        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["opening"] = "0.00004",
            ["closing"] = "0.00005"
        });

        Assert.True(TbRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.Equal(0L, projected!.ChangeAmountScaled);
    }

    // OpenCloseBySide（legacy SA=4）：本期變動 = (期末借 − 期末貸) − (期初借 − 期初貸)。
    // 決策表：借方科目、貸方科目、借貸交叉、全零、負變動。oracle：規格手算 × scale。
    [Theory]
    [InlineData("1000", "0", "1500", "0", 5_000_000L)]     // 借方科目 +500
    [InlineData("0", "800", "0", "300", 5_000_000L)]       // 貸方科目 (0-300)-(0-800)=+500
    [InlineData("0", "1000", "500", "0", 15_000_000L)]     // 借貸交叉 (500-0)-(0-1000)=+1500
    [InlineData("0", "0", "0", "0", 0L)]                   // 全零
    [InlineData("1000", "0", "0", "500", -15_000_000L)]    // 負變動 (0-500)-(1000-0)=−1500
    public void OpenCloseBySide_ComputesNetSideChange(
        string openingDr, string openingCr, string closingDr, string closingCr, long expectedScaled)
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.OpeningDebit] = "odr",
                [TbMappingKeys.OpeningCredit] = "ocr",
                [TbMappingKeys.ClosingDebit] = "cdr",
                [TbMappingKeys.ClosingCredit] = "ccr"
            },
            TbChangeMode.OpenCloseBySide);

        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["acc"] = "1101",
            ["name"] = "現金",
            ["odr"] = openingDr,
            ["ocr"] = openingCr,
            ["cdr"] = closingDr,
            ["ccr"] = closingCr
        });

        Assert.True(TbRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.Equal(expectedScaled, projected!.ChangeAmountScaled);
    }

    [Fact]
    public void OpenCloseBySide_SubScaleInputs_RoundsOnlyFinalNetChange()
    {
        // BVA：四欄先以 decimal 組成淨變動 0.00001，最後一次定標才不會製造一個 scaled unit。
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.OpeningDebit] = "odr",
                [TbMappingKeys.OpeningCredit] = "ocr",
                [TbMappingKeys.ClosingDebit] = "cdr",
                [TbMappingKeys.ClosingCredit] = "ccr"
            },
            TbChangeMode.OpenCloseBySide);
        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["odr"] = "0.00004",
            ["ocr"] = "0",
            ["cdr"] = "0.00005",
            ["ccr"] = "0"
        });

        Assert.True(TbRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.Equal(0L, projected!.ChangeAmountScaled);
    }

    // 正負號語意一致性（metamorphic）：OpenClose(opening=A, closing=A+delta) 與 DirectChange(delta)
    // 產生相同的 ChangeAmountScaled（借正貸負的本期變動同一基準，guide §2.2）。
    [Theory]
    [InlineData("500")]
    [InlineData("-500")]
    [InlineData("0")]
    public void OpenClose_SignConsistentWithDirectChange(string delta)
    {
        var directSpec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.Amount] = "chg"
            },
            TbChangeMode.DirectChange);
        var directRow = new StagingRow(2, new Dictionary<string, string>
        {
            ["acc"] = "1101", ["name"] = "現金", ["chg"] = delta
        });

        var openCloseSpec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.OpeningBalance] = "opening",
                [TbMappingKeys.ClosingBalance] = "closing"
            },
            TbChangeMode.OpenClose);
        // 期初固定 1000、期末 = 1000 + delta → 本期變動恰為 delta。
        var closing = (1000m + decimal.Parse(delta, System.Globalization.CultureInfo.InvariantCulture))
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var openCloseRow = new StagingRow(2, new Dictionary<string, string>
        {
            ["acc"] = "1101", ["name"] = "現金", ["opening"] = "1000", ["closing"] = closing
        });

        Assert.True(TbRowProjector.TryProject(directRow, directSpec, Scale, out var direct, out _));
        Assert.True(TbRowProjector.TryProject(openCloseRow, openCloseSpec, Scale, out var openClose, out _));
        Assert.Equal(direct!.ChangeAmountScaled, openClose!.ChangeAmountScaled);
    }

    // OpenCloseBySide 任一欄非法金額 → 逐列錯誤（鏡射 DebitCredit 的負向處理）。
    [Fact]
    public void OpenCloseBySide_InvalidClosingCredit_ReturnsRowError()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.OpeningDebit] = "odr",
                [TbMappingKeys.OpeningCredit] = "ocr",
                [TbMappingKeys.ClosingDebit] = "cdr",
                [TbMappingKeys.ClosingCredit] = "ccr"
            },
            TbChangeMode.OpenCloseBySide);

        var row = new StagingRow(8, new Dictionary<string, string>
        {
            ["acc"] = "1101", ["name"] = "現金",
            ["odr"] = "100", ["ocr"] = "0", ["cdr"] = "200", ["ccr"] = "oops"
        });

        Assert.False(TbRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(projected);
        Assert.Equal(8, error!.SourceRowNumber);
        Assert.Equal("ccr", error.Field);
        Assert.Equal("oops", error.RawValue);
        Assert.Equal("is not a valid amount", error.Reason);
    }

    [Fact]
    public void TryProject_ScaledDirectChangeExceedsInt64Range_ReturnsRowError()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.Amount] = "change"
            },
            TbChangeMode.DirectChange);

        var row = new StagingRow(11, new Dictionary<string, string>
        {
            ["acc"] = "1101",
            ["name"] = "現金",
            ["change"] = "79228162514264337593543950335"
        });

        Assert.False(TbRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(projected);
        Assert.Equal(11, error!.SourceRowNumber);
        Assert.Equal("change", error.Field);
        Assert.Equal("79228162514264337593543950335", error.RawValue);
        Assert.Equal("scaled amount exceeds 64-bit range", error.Reason);
    }

}
