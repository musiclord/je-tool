using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class GlRowProjectorTests
{
    private const int Scale = 10_000;

    // 照舊版保留來源金額的正負號：借方代碼的列照原金額，其他列乘以 -1，借貸衍生欄再依結果的正負號決定。
    // 第二列是貸方代碼、來源 -12.3456，結果是 123_456 借方；
    // 第三列是借方代碼、來源 -12.3456，結果是 -123_456 貸方。
    [Theory]
    [InlineData(
        GlAmountMode.SignedAmount,
        "-12.3456", null, null, null, null,
        -123_456L, 0L, 123_456L, "CREDIT")]
    [InlineData(
        GlAmountMode.AmountWithSide,
        "-12.3456", " C ", "D", null, null,
        123_456L, 123_456L, 0L, "DEBIT")]
    [InlineData(
        GlAmountMode.AmountWithFlag,
        "-12.3456", " 1 ", "1", null, null,
        -123_456L, 0L, 123_456L, "CREDIT")]
    [InlineData(
        GlAmountMode.DualAmount,
        null, null, null, "0.00005", "0.00004",
        0L, 0L, 0L, "DEBIT")]
    public void AmountModes_HandCalculatedDecisionTable_PreservesSignAndFinalScaling(
        GlAmountMode mode,
        string? amount,
        string? dcValue,
        string? drCrDebitCode,
        string? debit,
        string? credit,
        long expectedAmountScaled,
        long expectedDebitScaled,
        long expectedCreditScaled,
        string expectedDrCr)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (mode)
        {
            case GlAmountMode.SignedAmount:
                mapping[GlMappingKeys.Amount] = "amount";
                break;
            case GlAmountMode.AmountWithSide:
            case GlAmountMode.AmountWithFlag:
                mapping[GlMappingKeys.Amount] = "amount";
                mapping[GlMappingKeys.DrCr] = "dc";
                mapping[GlMappingKeys.DrCrDebitCode] = drCrDebitCode!;
                // 本合成表明定 D/C 與 1/0。
                mapping[GlMappingKeys.DrCrCreditCode] = mode == GlAmountMode.AmountWithFlag ? "0" : "C";
                break;
            case GlAmountMode.DualAmount:
                mapping[GlMappingKeys.DebitAmount] = "debit";
                mapping[GlMappingKeys.CreditAmount] = "credit";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        AddIfPresent(values, "amount", amount);
        AddIfPresent(values, "dc", dcValue);
        AddIfPresent(values, "debit", debit);
        AddIfPresent(values, "credit", credit);

        var row = new StagingRow(17, values);
        var spec = new GlMappingSpec(mapping, mode);

        Assert.True(
            GlRowProjector.TryProject(row, spec, Scale, out var projected, out var error),
            error?.ToString());
        Assert.Null(error);
        Assert.NotNull(projected);
        Assert.Equal(expectedAmountScaled, projected.AmountScaled);
        Assert.Equal(expectedDebitScaled, projected.DebitAmountScaled);
        Assert.Equal(expectedCreditScaled, projected.CreditAmountScaled);
        Assert.Equal(expectedDrCr, projected.DrCr);
    }

    private static GlMappingSpec DualSpec() => new(
        new Dictionary<string, string>
        {
            [GlMappingKeys.VoucherNumber] = "傳票號碼",
            [GlMappingKeys.GlDate] = "日期",
            [GlMappingKeys.AccountNumber] = "會計項目",
            [GlMappingKeys.AccountName] = "項目名稱",
            [GlMappingKeys.Description] = "摘要",
            [GlMappingKeys.DebitAmount] = "借方金額",
            [GlMappingKeys.CreditAmount] = "貸方金額"
        },
        GlAmountMode.DualAmount);

    [Fact]
    public void DualAmount_DebitRow_MissingCreditCell_ProjectsPositive()
    {
        // 稀疏列：借方列完全沒有貸方 key（對應允許缺少空值儲存格的來源形狀）
        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["傳票號碼"] = "0000000-2",
            ["日期"] = "2024-01-01",
            ["會計項目"] = "5100",
            ["項目名稱"] = "進貨",
            ["摘要"] = "期初存貨轉入",
            ["借方金額"] = "100.50"
        });

        Assert.True(GlRowProjector.TryProject(row, DualSpec(), Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.NotNull(projected);
        Assert.Equal(1_005_000L, projected.AmountScaled);
        Assert.Equal(1_005_000L, projected.DebitAmountScaled);
        Assert.Equal(0L, projected.CreditAmountScaled);
        Assert.Equal("DEBIT", projected.DrCr);
        Assert.Equal("0000000-2", projected.VoucherNumber);
        Assert.Equal("2024-01-01", projected.GlDate);
        Assert.Null(projected.LineItem);
        Assert.Null(projected.ApprovalDate);
    }

    [Fact]
    public void DualAmount_CreditRow_ProjectsNegative()
    {
        var row = new StagingRow(6, new Dictionary<string, string>
        {
            ["傳票號碼"] = "0000000-3",
            ["日期"] = "2024-01-02",
            ["會計項目"] = "1100",
            ["項目名稱"] = "現金",
            ["摘要"] = "收款",
            ["貸方金額"] = "200"
        });

        Assert.True(GlRowProjector.TryProject(row, DualSpec(), Scale, out var projected, out _));
        Assert.NotNull(projected);
        Assert.Equal(-2_000_000L, projected.AmountScaled);
        Assert.Equal(0L, projected.DebitAmountScaled);
        Assert.Equal(2_000_000L, projected.CreditAmountScaled);
        Assert.Equal("CREDIT", projected.DrCr);
    }

    [Fact]
    public void SignedAmount_NegativeIsCredit()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.VoucherNumber] = "doc",
                [GlMappingKeys.GlDate] = "date",
                [GlMappingKeys.AccountNumber] = "acc",
                [GlMappingKeys.AccountName] = "name",
                [GlMappingKeys.Description] = "desc",
                [GlMappingKeys.Amount] = "amt"
            },
            GlAmountMode.SignedAmount);

        var row = new StagingRow(3, new Dictionary<string, string>
        {
            ["doc"] = "D1",
            ["date"] = "2024-06-30",
            ["acc"] = "1101",
            ["name"] = "現金",
            ["desc"] = "x",
            ["amt"] = "-50"
        });

        Assert.True(GlRowProjector.TryProject(row, spec, Scale, out var projected, out _));
        Assert.NotNull(projected);
        Assert.Equal(-500_000L, projected.AmountScaled);
        Assert.Equal("CREDIT", projected.DrCr);
    }

    [Fact]
    public void AmountWithSide_ComparesDebitCodeTrimmedCaseInsensitive()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.VoucherNumber] = "doc",
                [GlMappingKeys.GlDate] = "date",
                [GlMappingKeys.AccountNumber] = "acc",
                [GlMappingKeys.AccountName] = "name",
                [GlMappingKeys.Description] = "desc",
                [GlMappingKeys.Amount] = "amt",
                [GlMappingKeys.DrCr] = "借貸別",
                [GlMappingKeys.DrCrDebitCode] = "D",
                [GlMappingKeys.DrCrCreditCode] = "C" // 同一合成案例的 creditRow 明確使用 C。
            },
            GlAmountMode.AmountWithSide);

        var debitRow = new StagingRow(2, new Dictionary<string, string>
        {
            ["doc"] = "D1", ["date"] = "2024-01-01", ["acc"] = "1", ["name"] = "n", ["desc"] = "d",
            ["amt"] = "100",
            ["借貸別"] = " d "
        });

        Assert.True(GlRowProjector.TryProject(debitRow, spec, Scale, out var debit, out _));
        Assert.Equal(1_000_000L, debit!.AmountScaled);

        var creditRow = new StagingRow(3, new Dictionary<string, string>
        {
            ["doc"] = "D1", ["date"] = "2024-01-01", ["acc"] = "2", ["name"] = "n", ["desc"] = "d",
            ["amt"] = "100",
            ["借貸別"] = "C"
        });

        Assert.True(GlRowProjector.TryProject(creditRow, spec, Scale, out var credit, out _));
        Assert.Equal(-1_000_000L, credit!.AmountScaled);
    }

    [Fact]
    public void BadAmount_ReturnsRowErrorWithExcelRowNumberAndSourceColumn()
    {
        var row = new StagingRow(423, new Dictionary<string, string>
        {
            ["傳票號碼"] = "X",
            ["日期"] = "2024-01-01",
            ["會計項目"] = "1",
            ["項目名稱"] = "n",
            ["摘要"] = "d",
            ["借方金額"] = "12..3"
        });

        Assert.False(GlRowProjector.TryProject(row, DualSpec(), Scale, out var projected, out var error));
        Assert.Null(projected);
        Assert.NotNull(error);
        Assert.Equal(423, error.SourceRowNumber);
        Assert.Equal("借方金額", error.Field);
        Assert.Equal("12..3", error.RawValue);
        // 這句的用字已定案。
        Assert.Equal("不是有效的金額。請確認來源的金額格式，或改選正確的金額欄", error.Reason);
    }

    [Theory]
    [InlineData("2024-01-01", "2024-01-01")] // ISO 直通
    [InlineData("2024/06/30", "2024-06-30")] // 一般日期格式 fallback
    public void DateProjection_NormalizesVariants(string raw, string expected)
    {
        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["傳票號碼"] = "X",
            ["日期"] = raw,
            ["會計項目"] = "1",
            ["項目名稱"] = "n",
            ["摘要"] = "d",
            ["借方金額"] = "1"
        });

        Assert.True(GlRowProjector.TryProject(row, DualSpec(), Scale, out var projected, out _));
        Assert.Equal(expected, projected!.GlDate);
    }

    [Fact]
    public void DateProjection_GarbageFails_MissingIsNull()
    {
        var garbage = new StagingRow(9, new Dictionary<string, string>
        {
            ["傳票號碼"] = "X",
            ["日期"] = "not-a-date",
            ["會計項目"] = "1",
            ["項目名稱"] = "n",
            ["摘要"] = "d",
            ["借方金額"] = "1"
        });

        Assert.False(GlRowProjector.TryProject(garbage, DualSpec(), Scale, out _, out var error));
        Assert.Equal(9, error!.SourceRowNumber);
        Assert.Equal("日期", error.Field);
        // 這句的用字已定案。
        Assert.Equal("無法辨識為日期。請確認來源的日期格式，或改選正確的日期欄", error.Reason);

        var missing = new StagingRow(10, new Dictionary<string, string>
        {
            ["傳票號碼"] = "X",
            ["會計項目"] = "1",
            ["項目名稱"] = "n",
            ["摘要"] = "d",
            ["借方金額"] = "1"
        });

        Assert.True(GlRowProjector.TryProject(missing, DualSpec(), Scale, out var projected, out _));
        Assert.Null(projected!.GlDate);
    }

    // 總帳日期帶千分位時和其他無法辨識的日期相同，確認配對列出列號，不換成 1903 年。
    [Fact]
    public void DateProjection_NumberWithThousandsSeparator_FailsWithRowNumber()
    {
        var row = new StagingRow(12, new Dictionary<string, string>
        {
            ["傳票號碼"] = "X",
            ["日期"] = "1,234",
            ["會計項目"] = "1",
            ["項目名稱"] = "n",
            ["摘要"] = "d",
            ["借方金額"] = "1"
        });

        Assert.False(GlRowProjector.TryProject(row, DualSpec(), Scale, out _, out var error));
        Assert.Equal(12, error!.SourceRowNumber);
        Assert.Equal("日期", error.Field);
        Assert.Equal("1,234", error.RawValue);
        Assert.Equal("無法辨識為日期。請確認來源的日期格式，或改選正確的日期欄", error.Reason);
    }

    // 總帳日期是純數字時不當成 Excel 序列值，和其他無法辨識的日期相同，
    // 確認配對列出列號。
    [Theory]
    [InlineData("2025")]     // 當成 Excel 序列值會是 1905-07-17
    [InlineData("2025.12")]  // 當成 Excel 序列值也會落在 1905 年
    [InlineData("45292")]    // 當成 Excel 序列值會是 2024-01-01
    public void DateProjection_PlainNumber_FailsWithRowNumber(string raw)
    {
        var row = new StagingRow(13, new Dictionary<string, string>
        {
            ["傳票號碼"] = "X",
            ["日期"] = raw,
            ["會計項目"] = "1",
            ["項目名稱"] = "n",
            ["摘要"] = "d",
            ["借方金額"] = "1"
        });

        Assert.False(GlRowProjector.TryProject(row, DualSpec(), Scale, out _, out var error));
        Assert.Equal(13, error!.SourceRowNumber);
        Assert.Equal("日期", error.Field);
        Assert.Equal(raw, error.RawValue);
        Assert.Equal("無法辨識為日期。請確認來源的日期格式，或改選正確的日期欄", error.Reason);
    }

    [Fact]
    public void TryProject_ScaledAmountExceedsLongRange_ReturnsRowProjectionError()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.Amount] = "amt"
            },
            GlAmountMode.SignedAmount);
        var row = new StagingRow(88, new Dictionary<string, string>
        {
            ["amt"] = "922337203685477.5808"
        });

        Assert.False(GlRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(projected);
        Assert.NotNull(error);
        Assert.Equal(88, error.SourceRowNumber);
        Assert.Equal("amt", error.Field);
        Assert.Equal("922337203685477.5808", error.RawValue);
        // 這句的用字已定案；訊息不叫審計員調整小數位數，因為案件沒有這個設定。
        Assert.Equal("金額超出可儲存的範圍。請確認來源金額是否正確，或改選正確的金額欄", error.Reason);
    }

    [Fact]
    public void TryProject_LongMinScaledAmount_ReturnsControlTotalProjectionError()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.Amount] = "amt"
            },
            GlAmountMode.SignedAmount);
        var row = new StagingRow(89, new Dictionary<string, string>
        {
            ["amt"] = "-922337203685477.5808"
        });

        Assert.False(GlRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(projected);
        Assert.NotNull(error);
        Assert.Equal(89, error.SourceRowNumber);
        Assert.Equal("amt", error.Field);
        Assert.Equal("-922337203685477.5808", error.RawValue);
        // 這句的用字已定案；訊息不叫審計員調整小數位數，因為案件沒有這個設定。
        Assert.Equal("加總後的金額超出可儲存的範圍。請確認來源金額是否正確，或改選正確的金額欄", error.Reason);
    }

    [Fact]
    public void ProjectManualFlag_UnrecognizedText_FailsStrictProjection()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.Amount] = "amt",
                [GlMappingKeys.IsManual] = "manual"
            },
            GlAmountMode.SignedAmount);
        var row = new StagingRow(12, new Dictionary<string, string>
        {
            ["amt"] = "1",
            ["manual"] = "manual-posting"
        });

        Assert.False(GlRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(projected);
        Assert.NotNull(error);
        Assert.Equal("manual", error.Field);
        Assert.Contains("未歸類為人工或自動", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SameAsGlDate_DerivesApprovalDateFromNormalizedGlDate()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.Amount] = "amt",
                [GlMappingKeys.GlDate] = "post"
            },
            GlAmountMode.SignedAmount)
        {
            Options = GlMappingOptions.NormalizeLegacy(new Dictionary<string, string>()) with
            {
                ApprovalDateMode = ApprovalDateModeNames.SameAsGlDate
            }
        };
        var row = new StagingRow(7, new Dictionary<string, string>
        {
            ["amt"] = "1",
            ["post"] = "2025/02/03"
        });

        Assert.True(GlRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.Equal("2025-02-03", projected!.GlDate);
        Assert.Equal(projected.GlDate, projected.ApprovalDate);
    }

    [Fact]
    public void CustomManualPolicy_TrimsAndClassifiesCaseInsensitive()
    {
        var mapping = new Dictionary<string, string>
        {
            [GlMappingKeys.Amount] = "amt",
            [GlMappingKeys.IsManual] = "manual"
        };
        var spec = new GlMappingSpec(mapping, GlAmountMode.SignedAmount)
        {
            Options = GlMappingOptions.NormalizeLegacy(mapping) with
            {
                ManualAutoPolicy = new GlManualAutoPolicy(["manual"], ["system"])
            }
        };
        var row = new StagingRow(8, new Dictionary<string, string>
        {
            ["amt"] = "1",
            ["manual"] = " MANUAL "
        });

        Assert.True(GlRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.True(projected!.IsManual);
    }

    [Fact]
    public void TypedRde_BlankIsAbsentAndMoneyUsesProjectScale()
    {
        var mapping = new Dictionary<string, string> { [GlMappingKeys.Amount] = "amt" };
        var spec = new GlMappingSpec(mapping, GlAmountMode.SignedAmount)
        {
            Options = GlMappingOptions.NormalizeLegacy(mapping) with
            {
                RdeFields =
                [
                    new GlRdeFieldMetadata("rde.00000000000000000000000000000001", "blank", "Blank", "text"),
                    new GlRdeFieldMetadata("rde.00000000000000000000000000000002", "date", "Date", "date"),
                    new GlRdeFieldMetadata("rde.00000000000000000000000000000003", "money", "Money", "amount")
                ]
            }
        };
        var row = new StagingRow(9, new Dictionary<string, string>
        {
            ["amt"] = "1",
            ["blank"] = "   ",
            ["date"] = "2025/03/04",
            ["money"] = "12.34567"
        });

        Assert.True(GlRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.Collection(
            projected!.RdeValues,
            value => Assert.Equal("2025-03-04", value.DateValue),
            value => Assert.Equal(123_457L, value.AmountScaled));
    }

    [Theory]
    // 這句的用字已定案。
    [InlineData("date", "2025/13/40", "無法辨識為日期。請確認來源的日期格式，或將此攸關資料元素欄位改為文字")]
    // 超過四位小數會四捨五入，不會失敗，案件也沒有小數位數設定；所以這句寫金額無效或超出可儲存的範圍。
    [InlineData("amount", "12.abc", "不是有效的金額，或超出可儲存的範圍。請確認來源資料，或將此攸關資料元素欄位改為文字")]
    public void TypedRde_InvalidValue_ReturnsChineseReasonWithNextStep(string valueType, string raw, string expectedReason)
    {
        var mapping = new Dictionary<string, string> { [GlMappingKeys.Amount] = "amt" };
        var spec = new GlMappingSpec(mapping, GlAmountMode.SignedAmount)
        {
            Options = GlMappingOptions.NormalizeLegacy(mapping) with
            {
                RdeFields =
                [
                    new GlRdeFieldMetadata("rde.00000000000000000000000000000004", "custom", "Custom", valueType)
                ]
            }
        };
        var row = new StagingRow(12, new Dictionary<string, string>
        {
            ["amt"] = "1",
            ["custom"] = raw
        });

        Assert.False(GlRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(projected);
        Assert.Equal(12, error!.SourceRowNumber);
        Assert.Equal("custom", error.Field);
        Assert.Equal(raw, error.RawValue);
        Assert.Equal(expectedReason, error.Reason);
    }

    [Theory]
    [InlineData(450, true)]
    [InlineData(451, false)]
    public void TextRde_UsesCommonUtf16LengthLimit(int length, bool expectedSuccess)
    {
        var mapping = new Dictionary<string, string> { [GlMappingKeys.Amount] = "amt" };
        var spec = new GlMappingSpec(mapping, GlAmountMode.SignedAmount)
        {
            Options = GlMappingOptions.NormalizeLegacy(mapping) with
            {
                RdeFields =
                [
                    new GlRdeFieldMetadata("rde.00000000000000000000000000000001", "custom", "Custom", "text")
                ]
            }
        };
        var row = new StagingRow(10, new Dictionary<string, string>
        {
            ["amt"] = "1",
            ["custom"] = new string('x', length)
        });

        var success = GlRowProjector.TryProject(row, spec, Scale, out var projected, out var error);

        Assert.Equal(expectedSuccess, success);
        if (expectedSuccess)
        {
            Assert.Equal(length, projected!.RdeValues.Single().TextValue!.Length);
        }
        else
        {
            Assert.Equal("custom", error!.Field);
            // 這句的用字已定案。
            Assert.Equal("文字超過 450 個字。請縮短來源資料，或取消保留此欄位", error.Reason);
        }
    }

    private static void AddIfPresent(
        IDictionary<string, string> values,
        string key,
        string? value)
    {
        if (value is not null)
        {
            values[key] = value;
        }
    }
}
