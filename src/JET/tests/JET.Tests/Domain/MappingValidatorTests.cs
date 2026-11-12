using System.Linq;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class MappingValidatorTests
{
    [Fact]
    public void UnknownMappingKeys_AreRejectedEvenWhenEmpty()
    {
        var result = MappingValidator.ValidateGl(new GlMappingSpec(
            new Dictionary<string, string> { ["obsoleteField"] = "" }, GlAmountMode.DualAmount), []);
        Assert.Contains("obsoleteField (unknown mapping key)", result.UnknownColumns);
    }

    private static readonly string[] JeColumns =
    [
        "日期", "傳票號碼", "會計項目", "項目名稱", "客供商代號", "客供商簡稱",
        "部門代號", "部門名稱", "摘要", "借方金額", "貸方金額"
    ];

    [Fact]
    public void DualMode_MissingCreditAmount_Reported()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.VoucherNumber] = "傳票號碼",
                [GlMappingKeys.GlDate] = "日期",
                [GlMappingKeys.AccountNumber] = "會計項目",
                [GlMappingKeys.AccountName] = "項目名稱",
                [GlMappingKeys.Description] = "摘要",
                [GlMappingKeys.DebitAmount] = "借方金額"
            },
            GlAmountMode.DualAmount);

        var result = MappingValidator.ValidateGl(spec, JeColumns);

        Assert.False(result.IsValid);
        Assert.Contains(GlMappingKeys.CreditAmount, result.MissingRequiredKeys);
    }

    [Fact]
    public void MappedColumnNotInBatch_Reported()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.VoucherNumber] = "不存在的欄",
                [GlMappingKeys.GlDate] = "日期",
                [GlMappingKeys.AccountNumber] = "會計項目",
                [GlMappingKeys.AccountName] = "項目名稱",
                [GlMappingKeys.Description] = "摘要",
                [GlMappingKeys.DebitAmount] = "借方金額",
                [GlMappingKeys.CreditAmount] = "貸方金額"
            },
            GlAmountMode.DualAmount);

        var result = MappingValidator.ValidateGl(spec, JeColumns);

        Assert.False(result.IsValid);
        Assert.Contains("不存在的欄", result.UnknownColumns);
    }

    [Fact]
    public void DrCrDebitCode_IsLiteralCode_ExemptFromColumnCheck()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.VoucherNumber] = "傳票號碼",
                [GlMappingKeys.GlDate] = "日期",
                [GlMappingKeys.AccountNumber] = "會計項目",
                [GlMappingKeys.AccountName] = "項目名稱",
                [GlMappingKeys.Description] = "摘要",
                [GlMappingKeys.Amount] = "借方金額",
                [GlMappingKeys.DrCr] = "部門代號",
                // 兩個方向代碼都是字面值。
                [GlMappingKeys.DrCrDebitCode] = "D",
                [GlMappingKeys.DrCrCreditCode] = "C"
            },
            GlAmountMode.AmountWithSide);

        var result = MappingValidator.ValidateGl(spec, JeColumns);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(GlAmountMode.AmountWithSide, GlMappingKeys.DrCr)]
    [InlineData(GlAmountMode.AmountWithSide, GlMappingKeys.DrCrDebitCode)]
    [InlineData(GlAmountMode.AmountWithSide, GlMappingKeys.DrCrCreditCode)]
    [InlineData(GlAmountMode.AmountWithFlag, GlMappingKeys.DrCr)]
    [InlineData(GlAmountMode.AmountWithFlag, GlMappingKeys.DrCrDebitCode)]
    [InlineData(GlAmountMode.AmountWithFlag, GlMappingKeys.DrCrCreditCode)]
    public void SideAndFlagModes_RequireTheDirectionColumnButOnlyOneDirectionCode(
        GlAmountMode mode,
        string missingKey)
    {
        var mapping = new Dictionary<string, string>
        {
            [GlMappingKeys.VoucherNumber] = "傳票號碼",
            [GlMappingKeys.GlDate] = "日期",
            [GlMappingKeys.AccountNumber] = "會計項目",
            [GlMappingKeys.AccountName] = "項目名稱",
            [GlMappingKeys.Description] = "摘要",
            [GlMappingKeys.Amount] = "借方金額",
            [GlMappingKeys.DrCr] = "部門代號",
            [GlMappingKeys.DrCrDebitCode] = "D",
            [GlMappingKeys.DrCrCreditCode] = "C"
        };
        mapping.Remove(missingKey);

        var result = MappingValidator.ValidateGl(
            new GlMappingSpec(mapping, mode),
            JeColumns);

        // 缺一側代碼可用補集；借貸別來源欄仍必填。
        Assert.Equal(missingKey != GlMappingKeys.DrCr, result.IsValid);
        Assert.Equal(missingKey == GlMappingKeys.DrCr ? new[] { missingKey } : [], result.MissingRequiredKeys);
        Assert.Empty(result.UnknownColumns);
    }

    [Fact]
    public void LineItemAbsence_IsValid()
    {
        // 來源可能沒有項次欄；lineItem 缺漏不可阻擋 commit（manifest: No／optional）
        var spec = new GlMappingSpec(
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

        var result = MappingValidator.ValidateGl(spec, JeColumns);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Tb_DebitCreditMode_RequiresBothColumns()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccountNumber] = "會計科目編號",
                [TbMappingKeys.AccountName] = "會計科目名稱",
                [TbMappingKeys.DebitAmount] = "借方金額"
            },
            TbChangeMode.DebitCredit);

        var result = MappingValidator.ValidateTb(
            spec,
            ["會計科目編號", "會計科目名稱", "COL3", "期初金額", "借方金額", "貸方金額", "期末金額"]);

        Assert.False(result.IsValid);
        Assert.Contains(TbMappingKeys.CreditAmount, result.MissingRequiredKeys);
    }

    [Fact]
    public void Tb_OpenCloseMode_MissingClosing_Reported()
    {
        // OpenClose 需要 openingBalance + closingBalance 兩欄；缺 closing → 擋下（legacy SA=2）。
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccountNumber] = "會計科目編號",
                [TbMappingKeys.AccountName] = "會計科目名稱",
                [TbMappingKeys.OpeningBalance] = "期初金額"
            },
            TbChangeMode.OpenClose);

        var result = MappingValidator.ValidateTb(
            spec,
            ["會計科目編號", "會計科目名稱", "期初金額", "期末金額"]);

        Assert.False(result.IsValid);
        Assert.Contains(TbMappingKeys.ClosingBalance, result.MissingRequiredKeys);
        Assert.DoesNotContain(TbMappingKeys.OpeningBalance, result.MissingRequiredKeys);
    }

    [Fact]
    public void Tb_OpenCloseBySideMode_MissingOneOfFour_Reported()
    {
        // OpenCloseBySide 需要期初借貸 + 期末借貸四欄；缺期末貸 → 擋下（legacy SA=4）。
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccountNumber] = "會計科目編號",
                [TbMappingKeys.AccountName] = "會計科目名稱",
                [TbMappingKeys.OpeningDebitBalance] = "期初借",
                [TbMappingKeys.OpeningCreditBalance] = "期初貸",
                [TbMappingKeys.ClosingDebitBalance] = "期末借"
            },
            TbChangeMode.OpenCloseBySide);

        var result = MappingValidator.ValidateTb(
            spec,
            ["會計科目編號", "會計科目名稱", "期初借", "期初貸", "期末借", "期末貸"]);

        Assert.False(result.IsValid);
        Assert.Contains(TbMappingKeys.ClosingCreditBalance, result.MissingRequiredKeys);
        Assert.Equal(3, new[] { TbMappingKeys.OpeningDebitBalance, TbMappingKeys.OpeningCreditBalance, TbMappingKeys.ClosingDebitBalance }
            .Count(k => !result.MissingRequiredKeys.Contains(k)));
    }

    [Theory]
    [InlineData(GlAmountMode.SignedAmount, new[] { "voucherNumber", "glDate", "accountNumber", "accountName", "description", "amount" })]
    [InlineData(GlAmountMode.AmountWithSide, new[] { "voucherNumber", "glDate", "accountNumber", "accountName", "description", "amount", "drCr", "drCrDebitCode", "drCrCreditCode" })]
    [InlineData(GlAmountMode.AmountWithFlag, new[] { "voucherNumber", "glDate", "accountNumber", "accountName", "description", "amount", "drCr", "drCrDebitCode", "drCrCreditCode" })]
    [InlineData(GlAmountMode.DualAmount, new[] { "voucherNumber", "glDate", "accountNumber", "accountName", "description", "debitAmount", "creditAmount" })]
    public void GlRequiredKeyMatrix_IsAppliedExactlyAndInOrder(
        GlAmountMode mode,
        string[] expected)
    {
        var result = MappingValidator.ValidateGl(
            new GlMappingSpec(new Dictionary<string, string>(), mode),
            []);

        Assert.Equal(expected, result.MissingRequiredKeys);
        Assert.Equal([], result.UnknownColumns);
    }

    [Theory]
    [InlineData(TbChangeMode.DirectChange, new[] { "accountNumber", "accountName", "changeAmount" })]
    [InlineData(TbChangeMode.DebitCredit, new[] { "accountNumber", "accountName", "debitAmount", "creditAmount" })]
    [InlineData(TbChangeMode.OpenClose, new[] { "accountNumber", "accountName", "openingBalance", "closingBalance" })]
    [InlineData(TbChangeMode.OpenCloseBySide, new[] { "accountNumber", "accountName", "openingDebitBalance", "openingCreditBalance", "closingDebitBalance", "closingCreditBalance" })]
    public void TbRequiredKeyMatrix_IsAppliedExactlyAndInOrder(
        TbChangeMode mode,
        string[] expected)
    {
        var result = MappingValidator.ValidateTb(
            new TbMappingSpec(new Dictionary<string, string>(), mode),
            []);

        Assert.Equal(expected, result.MissingRequiredKeys);
        Assert.Equal([], result.UnknownColumns);
    }

    [Fact]
    public void UnknownMappingKey_RemainsReportedWithoutTreatingItAsSourceColumn()
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.VoucherNumber] = "傳票號碼",
            [GlMappingKeys.GlDate] = "日期",
            [GlMappingKeys.AccountNumber] = "會計項目",
            [GlMappingKeys.AccountName] = "項目名稱",
            [GlMappingKeys.Description] = "摘要",
            [GlMappingKeys.Amount] = "借方金額",
            ["notAField"] = "確實存在的來源欄"
        };

        var result = MappingValidator.ValidateGl(
            new GlMappingSpec(mapping, GlAmountMode.SignedAmount),
            ["傳票號碼", "日期", "會計項目", "項目名稱", "摘要", "借方金額", "確實存在的來源欄"]);

        Assert.Equal([], result.MissingRequiredKeys);
        Assert.Equal(["notAField (unknown mapping key)"], result.UnknownColumns);
    }
}
