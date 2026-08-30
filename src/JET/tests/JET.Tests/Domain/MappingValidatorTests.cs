using System.Linq;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class MappingValidatorTests
{
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
                [GlMappingKeys.DocNum] = "傳票號碼",
                [GlMappingKeys.PostDate] = "日期",
                [GlMappingKeys.AccNum] = "會計項目",
                [GlMappingKeys.AccName] = "項目名稱",
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
                [GlMappingKeys.DocNum] = "不存在的欄",
                [GlMappingKeys.PostDate] = "日期",
                [GlMappingKeys.AccNum] = "會計項目",
                [GlMappingKeys.AccName] = "項目名稱",
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
    public void DcDebitCode_IsLiteralCode_ExemptFromColumnCheck()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.DocNum] = "傳票號碼",
                [GlMappingKeys.PostDate] = "日期",
                [GlMappingKeys.AccNum] = "會計項目",
                [GlMappingKeys.AccName] = "項目名稱",
                [GlMappingKeys.Description] = "摘要",
                [GlMappingKeys.Amount] = "借方金額",
                [GlMappingKeys.DcField] = "部門代號",
                [GlMappingKeys.DcDebitCode] = "D" // 字面值，不是欄位名
            },
            GlAmountMode.AmountWithSide);

        var result = MappingValidator.ValidateGl(spec, JeColumns);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(GlAmountMode.AmountWithSide, GlMappingKeys.DcField)]
    [InlineData(GlAmountMode.AmountWithSide, GlMappingKeys.DcDebitCode)]
    [InlineData(GlAmountMode.AmountWithFlag, GlMappingKeys.DcField)]
    [InlineData(GlAmountMode.AmountWithFlag, GlMappingKeys.DcDebitCode)]
    public void SideAndFlagModes_MissingDirectionInput_ReportsExactRequiredKey(
        GlAmountMode mode,
        string missingKey)
    {
        var mapping = new Dictionary<string, string>
        {
            [GlMappingKeys.DocNum] = "傳票號碼",
            [GlMappingKeys.PostDate] = "日期",
            [GlMappingKeys.AccNum] = "會計項目",
            [GlMappingKeys.AccName] = "項目名稱",
            [GlMappingKeys.Description] = "摘要",
            [GlMappingKeys.Amount] = "借方金額",
            [GlMappingKeys.DcField] = "部門代號",
            [GlMappingKeys.DcDebitCode] = "D"
        };
        mapping.Remove(missingKey);

        var result = MappingValidator.ValidateGl(
            new GlMappingSpec(mapping, mode),
            JeColumns);

        Assert.False(result.IsValid);
        Assert.Equal([missingKey], result.MissingRequiredKeys);
        Assert.Empty(result.UnknownColumns);
    }

    [Fact]
    public void LineIdAbsence_IsValid()
    {
        // 來源可能沒有項次欄；lineID 缺漏不可阻擋 commit（manifest: No／optional）
        var spec = new GlMappingSpec(
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

        var result = MappingValidator.ValidateGl(spec, JeColumns);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Tb_DebitCreditMode_RequiresBothColumns()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "會計科目編號",
                [TbMappingKeys.AccName] = "會計科目名稱",
                [TbMappingKeys.DebitAmt] = "借方金額"
            },
            TbChangeMode.DebitCredit);

        var result = MappingValidator.ValidateTb(
            spec,
            ["會計科目編號", "會計科目名稱", "COL3", "期初金額", "借方金額", "貸方金額", "期末金額"]);

        Assert.False(result.IsValid);
        Assert.Contains(TbMappingKeys.CreditAmt, result.MissingRequiredKeys);
    }

    [Fact]
    public void Tb_OpenCloseMode_MissingClosing_Reported()
    {
        // OpenClose 需要 openingBalance + closingBalance 兩欄；缺 closing → 擋下（legacy SA=2）。
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "會計科目編號",
                [TbMappingKeys.AccName] = "會計科目名稱",
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
                [TbMappingKeys.AccNum] = "會計科目編號",
                [TbMappingKeys.AccName] = "會計科目名稱",
                [TbMappingKeys.OpeningDebit] = "期初借",
                [TbMappingKeys.OpeningCredit] = "期初貸",
                [TbMappingKeys.ClosingDebit] = "期末借"
            },
            TbChangeMode.OpenCloseBySide);

        var result = MappingValidator.ValidateTb(
            spec,
            ["會計科目編號", "會計科目名稱", "期初借", "期初貸", "期末借", "期末貸"]);

        Assert.False(result.IsValid);
        Assert.Contains(TbMappingKeys.ClosingCredit, result.MissingRequiredKeys);
        Assert.Equal(3, new[] { TbMappingKeys.OpeningDebit, TbMappingKeys.OpeningCredit, TbMappingKeys.ClosingDebit }
            .Count(k => !result.MissingRequiredKeys.Contains(k)));
    }

    [Theory]
    [InlineData(GlAmountMode.SignedAmount, new[] { "docNum", "postDate", "accNum", "accName", "description", "amount" })]
    [InlineData(GlAmountMode.AmountWithSide, new[] { "docNum", "postDate", "accNum", "accName", "description", "amount", "dcField", "dcDebitCode" })]
    [InlineData(GlAmountMode.AmountWithFlag, new[] { "docNum", "postDate", "accNum", "accName", "description", "amount", "dcField", "dcDebitCode" })]
    [InlineData(GlAmountMode.DualAmount, new[] { "docNum", "postDate", "accNum", "accName", "description", "debitAmount", "creditAmount" })]
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
    [InlineData(TbChangeMode.DirectChange, new[] { "accNum", "accName", "amount" })]
    [InlineData(TbChangeMode.DebitCredit, new[] { "accNum", "accName", "debitAmt", "creditAmt" })]
    [InlineData(TbChangeMode.OpenClose, new[] { "accNum", "accName", "openingBalance", "closingBalance" })]
    [InlineData(TbChangeMode.OpenCloseBySide, new[] { "accNum", "accName", "openingDebit", "openingCredit", "closingDebit", "closingCredit" })]
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
            [GlMappingKeys.DocNum] = "傳票號碼",
            [GlMappingKeys.PostDate] = "日期",
            [GlMappingKeys.AccNum] = "會計項目",
            [GlMappingKeys.AccName] = "項目名稱",
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
