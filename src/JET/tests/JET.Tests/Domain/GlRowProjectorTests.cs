using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class GlRowProjectorTests
{
    private const int Scale = 10_000;

    [Theory]
    [InlineData(
        GlAmountMode.SignedAmount,
        "-12.3456", null, null, null, null,
        -123_456L, 0L, 123_456L, "CREDIT")]
    [InlineData(
        GlAmountMode.AmountWithSide,
        "-12.3456", " C ", "D", null, null,
        -123_456L, 0L, 123_456L, "CREDIT")]
    [InlineData(
        GlAmountMode.AmountWithFlag,
        "-12.3456", " 1 ", "1", null, null,
        123_456L, 123_456L, 0L, "DEBIT")]
    [InlineData(
        GlAmountMode.DualAmount,
        null, null, null, "0.00005", "0.00004",
        0L, 0L, 0L, "DEBIT")]
    public void AmountModes_HandCalculatedDecisionTable_PreservesSignAndFinalScaling(
        GlAmountMode mode,
        string? amount,
        string? dcValue,
        string? dcDebitCode,
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
                mapping[GlMappingKeys.DcField] = "dc";
                mapping[GlMappingKeys.DcDebitCode] = dcDebitCode!;
                // 2026-10-04 R9：本合成表明定 D/C 與 1/0，完整金額與借貸斷言不變。
                // 首次失敗：Public 20261004-100911120-57efb95a0cae44beb892ec3c2d058592。
                mapping[GlMappingKeys.DcCreditCode] = mode == GlAmountMode.AmountWithFlag ? "0" : "C";
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
            [GlMappingKeys.DocNum] = "傳票號碼",
            [GlMappingKeys.PostDate] = "日期",
            [GlMappingKeys.AccNum] = "會計項目",
            [GlMappingKeys.AccName] = "項目名稱",
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
        Assert.Equal("0000000-2", projected.DocumentNumber);
        Assert.Equal("2024-01-01", projected.PostDate);
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
                [GlMappingKeys.DocNum] = "doc",
                [GlMappingKeys.PostDate] = "date",
                [GlMappingKeys.AccNum] = "acc",
                [GlMappingKeys.AccName] = "name",
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
                [GlMappingKeys.DocNum] = "doc",
                [GlMappingKeys.PostDate] = "date",
                [GlMappingKeys.AccNum] = "acc",
                [GlMappingKeys.AccName] = "name",
                [GlMappingKeys.Description] = "desc",
                [GlMappingKeys.Amount] = "amt",
                [GlMappingKeys.DcField] = "借貸別",
                [GlMappingKeys.DcDebitCode] = "D",
                [GlMappingKeys.DcCreditCode] = "C" // 同一合成案例的 creditRow 明確使用 C。
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
        // 使用者 2026-10-13 勾選頁確認改字，預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
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
        Assert.Equal(expected, projected!.PostDate);
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
        // 使用者 2026-10-13 勾選頁確認改字，預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
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
        Assert.Null(projected!.PostDate);
    }

    // 使用者 2026-10-19 裁定：總帳日期帶千分位時和其他無法辨識的日期相同，確認配對列出列號，不再換成 1903 年。
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

    // 預篩選收斂計畫階段七（使用者 2026-10-22 裁定）：總帳日期是純數字時不再當成 Excel 序列值，和其他無法辨識的日期相同，
    // 確認配對列出列號。「45292」原本在 DateProjection_NormalizesVariants 期待 2024-01-01；第一次失敗：收據 20261024-044716893-6a6900c538364667949441681dcd3b6d。
    [Theory]
    [InlineData("2025")]     // 以前換成 1905-07-17
    [InlineData("2025.12")]  // 以前也是 1905 年
    [InlineData("45292")]    // 以前換成 2024-01-01
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
        // 使用者 2026-10-13 勾選頁確認改字，預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
        // 使用者 2026-10-22 裁定一併改寫叫審計員調整小數位數的訊息，案件沒有這個設定（預篩選收斂計畫階段五）；
        // 第一次失敗：Focused 收據 20261023-112033228-d81054521d85414bb9578fa2420d72e4。
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
        // 使用者 2026-10-13 勾選頁確認改字，預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
        // 使用者 2026-10-22 裁定一併改寫叫審計員調整小數位數的訊息，案件沒有這個設定（預篩選收斂計畫階段五）；
        // 第一次失敗：Focused 收據 20261023-112033228-d81054521d85414bb9578fa2420d72e4。
        Assert.Equal("加總後的金額超出可儲存的範圍。請確認來源金額是否正確，或改選正確的金額欄", error.Reason);
    }

    [Fact]
    public void ProjectManualFlag_UnrecognizedText_FailsStrictProjection()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.Amount] = "amt",
                [GlMappingKeys.Manual] = "manual"
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
    public void SameAsPostDate_DerivesApprovalDateFromNormalizedPostDate()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.Amount] = "amt",
                [GlMappingKeys.PostDate] = "post"
            },
            GlAmountMode.SignedAmount)
        {
            Options = GlMappingOptions.NormalizeLegacy(new Dictionary<string, string>()) with
            {
                ApprovalDateMode = ApprovalDateModeNames.SameAsPostDate
            }
        };
        var row = new StagingRow(7, new Dictionary<string, string>
        {
            ["amt"] = "1",
            ["post"] = "2025/02/03"
        });

        Assert.True(GlRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.Equal("2025-02-03", projected!.PostDate);
        Assert.Equal(projected.PostDate, projected.ApprovalDate);
    }

    [Fact]
    public void CustomManualPolicy_TrimsAndClassifiesCaseInsensitive()
    {
        var mapping = new Dictionary<string, string>
        {
            [GlMappingKeys.Amount] = "amt",
            [GlMappingKeys.Manual] = "manual"
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
                    new GlRdeFieldMetadata("rde.00000000000000000000000000000003", "money", "Money", "money")
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
// 使用者 2026-10-13 勾選頁確認改字，預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
        [InlineData("date", "2025/13/40", "無法辨識為日期。請確認來源的日期格式，或將此攸關資料元素欄位改為文字")]
    // 使用者 2026-10-22 裁定一併改寫：超過四位小數會四捨五入，不會失敗，案件也沒有小數位數設定；這句改寫成金額無效或超出可儲存的範圍
    // （預篩選收斂計畫階段五）。第一次失敗：Focused 收據 20261023-112033228-d81054521d85414bb9578fa2420d72e4。
    [InlineData("money", "12.abc", "不是有效的金額，或超出可儲存的範圍。請確認來源資料，或將此攸關資料元素欄位改為文字")]
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
            // 使用者 2026-10-13 勾選頁確認改字，預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
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
