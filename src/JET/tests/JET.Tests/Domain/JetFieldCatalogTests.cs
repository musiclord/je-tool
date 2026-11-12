using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class JetFieldCatalogTests
{
    // 測試直接從槽位清單找槽位。
    private static JetMappingSlot GlSlot(string key) => JetFieldCatalog.GlMappingSlots.Single(slot => slot.Key == key);

    private static JetMappingSlot TbSlot(string key) => JetFieldCatalog.TbMappingSlots.Single(slot => slot.Key == key);

    [Fact]
    public void CatalogTypes_AreInternal()
    {
        Assert.False(typeof(JetFieldCatalog).IsPublic);
        Assert.False(typeof(JetFieldDefinition).IsPublic);
        Assert.False(typeof(JetMappingSlot).IsPublic);
        Assert.False(typeof(JetFieldValueKind).IsPublic);
    }

    [Fact]
    public void CompatibilityMembers_PreservePublicShapes()
    {
        var glVoucherNumber = typeof(GlMappingKeys).GetField(nameof(GlMappingKeys.VoucherNumber));
        var glAll = typeof(GlMappingKeys).GetField(nameof(GlMappingKeys.All));
        var tbAmount = typeof(TbMappingKeys).GetField(nameof(TbMappingKeys.ChangeAmount));
        var tbAll = typeof(TbMappingKeys).GetField(nameof(TbMappingKeys.All));
        // 輸出欄名只有欄位目錄的 LegacyFieldName 一份。

        Assert.NotNull(glVoucherNumber);
        Assert.True(glVoucherNumber.IsLiteral);
        Assert.Equal(typeof(string), glVoucherNumber.FieldType);
        Assert.NotNull(tbAmount);
        Assert.True(tbAmount.IsLiteral);
        Assert.Equal(typeof(string), tbAmount.FieldType);

        Assert.NotNull(glAll);
        Assert.True(glAll.IsStatic);
        Assert.True(glAll.IsInitOnly);
        Assert.Equal(typeof(IReadOnlyList<string>), glAll.FieldType);
        Assert.NotNull(tbAll);
        Assert.True(tbAll.IsStatic);
        Assert.True(tbAll.IsInitOnly);
        Assert.Equal(typeof(IReadOnlyList<string>), tbAll.FieldType);
    }

    [Fact]
    public void GlDefinitions_MatchV2ContractMatrix()
    {
        // 矩陣倒數第二欄是輸出欄名 LegacyFieldName，傳票核准日、分錄來源模組與人工/自動分錄也有值。
        var expected = new[]
        {
            Slot("voucherNumber", "傳票號碼", 0, true, [], false, true,
                "voucherNumber", JetFieldValueKind.Text, "voucher_number", true, "傳票號碼_JE", true),
            Slot("lineItem", "傳票文件項次", 1, false, [], false, true,
                "lineItem", JetFieldValueKind.Text, "line_item", true, "傳票文件項次_JE_S", true),
            Slot("glDate", "總帳日期", 2, true, [], false, true,
                "glDate", JetFieldValueKind.Date, "gl_date", true, "總帳日期_JE", true),
            Slot("approvalDate", "傳票核准日", 3, false, [], false, true,
                "approvalDate", JetFieldValueKind.Date, "approval_date", true, "傳票核准日_JE", true),
            // 順序值 4 空著不用，其餘順序值不重排。
            Slot("accountNumber", "會計科目編號", 5, true, [], false, true,
                "accountNumber", JetFieldValueKind.Text, "account_number", true, "會計科目編號_JE", true),
            Slot("accountName", "會計科目名稱", 6, true, [], false, true,
                "accountName", JetFieldValueKind.Text, "account_name", true, "會計科目名稱_JE", true),
            Slot("description", "傳票摘要", 7, true, [], false, true,
                "description", JetFieldValueKind.Text, "description", true, "傳票摘要_JE", true),
            Slot("sourceModule", "分錄來源模組", 8, false, [], false, true,
                "sourceModule", JetFieldValueKind.Text, "source_module", true, "分錄來源模組_JE", true),
            Slot("createdBy", "傳票建立人員", 9, false, [], false, true,
                "createdBy", JetFieldValueKind.Text, "created_by", true, "傳票建立人員_JE", true),
            Slot("approvedBy", "傳票核准人員", 10, false, [], false, true,
                "approvedBy", JetFieldValueKind.Text, "approved_by", true, "傳票核准人員_JE", true),
            Slot("isManual", "人工/自動分錄", 11, false, [], false, false,
                "isManual", JetFieldValueKind.Boolean, "is_manual", true, "人工傳票否_JE_S", false),
            Slot("amount", "金額", 12, false, ["signed", "side", "flag"], false, true,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            Slot("debitAmount", "借方金額", 13, false, ["dual"], false, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            Slot("creditAmount", "貸方金額", 14, false, ["dual"], false, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            Slot("drCr", "借貸別", 15, false, ["side", "flag"], false, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            Slot("drCrDebitCode", "借方代碼", 16, false, ["side", "flag"], true, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            // 必填的貸方代碼；這個 slot 不改任何既有欄位的儲存語意。
            Slot("drCrCreditCode", "貸方代碼", 17, false, ["side", "flag"], true, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            // 過帳碼_JE 照舊版補回，過帳狀態欄因此有輸出欄名。
            Slot("postingStatus", "過帳狀態", 18, false, [], false, false,
                "postingStatus", JetFieldValueKind.Text, "posting_status", true, "過帳碼_JE", false)
        };

        Assert.Equal(expected, Flatten(JetFieldCatalog.GlFields, JetFieldCatalog.GlMappingSlots));
    }

    [Fact]
    public void TbDefinitions_MatchPreCutoverContractMatrix()
    {
        var expected = new[]
        {
            Slot("accountNumber", "會計科目編號", 0, true, [], false, true,
                "accountNumber", JetFieldValueKind.Text, "account_number", true, "會計科目編號_TB", false),
            Slot("accountName", "會計科目名稱", 1, true, [], false, true,
                "accountName", JetFieldValueKind.Text, "account_name", true, "會計科目名稱_TB", false),
            Slot("changeAmount", "年度變動金額", 2, false, ["direct"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("debitAmount", "借方金額", 3, false, ["debitCredit"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("creditAmount", "貸方金額", 4, false, ["debitCredit"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("openingBalance", "期初餘額", 5, false, ["openClose"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("closingBalance", "期末餘額", 6, false, ["openClose"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("openingDebitBalance", "期初借方", 7, false, ["openCloseBySide"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("openingCreditBalance", "期初貸方", 8, false, ["openCloseBySide"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("closingDebitBalance", "期末借方", 9, false, ["openCloseBySide"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("closingCreditBalance", "期末貸方", 10, false, ["openCloseBySide"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false)
        };

        Assert.Equal(expected, Flatten(JetFieldCatalog.TbFields, JetFieldCatalog.TbMappingSlots));
    }

    [Fact]
    public void CompatibilityViews_AreCatalogProjectionsWithExactValuesAndOrder()
    {
        Assert.Same(JetFieldCatalog.GlMappingKeys, GlMappingKeys.All);
        Assert.Same(JetFieldCatalog.TbMappingKeys, TbMappingKeys.All);
        // 輸出欄名只有欄位目錄的 LegacyFieldName 一份。

        Assert.Equal(
            [
                "voucherNumber", "lineItem", "glDate", "approvalDate", "accountNumber", "accountName",
                "description", "sourceModule", "createdBy", "approvedBy", "isManual", "amount",
                "debitAmount", "creditAmount", "drCr", "drCrDebitCode", "drCrCreditCode", "postingStatus"
            ],
            GlMappingKeys.All);
        Assert.Equal(
            [
                "accountNumber", "accountName", "changeAmount", "debitAmount", "creditAmount", "openingBalance",
                "closingBalance", "openingDebitBalance", "openingCreditBalance", "closingDebitBalance", "closingCreditBalance"
            ],
            TbMappingKeys.All);

        Assert.Equal(JetFieldCatalog.TbChangeAmount, TbMappingKeys.ChangeAmount);
        Assert.Equal("試算表變動金額_TB", JetFieldCatalog.FindTbSemanticFieldByMappingKey(TbMappingKeys.ChangeAmount).LegacyFieldName);
    }

    [Theory]
    [InlineData(GlAmountMode.SignedAmount, new[] { "voucherNumber", "glDate", "accountNumber", "accountName", "description", "amount" })]
    [InlineData(GlAmountMode.AmountWithSide, new[] { "voucherNumber", "glDate", "accountNumber", "accountName", "description", "amount", "drCr", "drCrDebitCode", "drCrCreditCode" })]
    [InlineData(GlAmountMode.AmountWithFlag, new[] { "voucherNumber", "glDate", "accountNumber", "accountName", "description", "amount", "drCr", "drCrDebitCode", "drCrCreditCode" })]
    [InlineData(GlAmountMode.DualAmount, new[] { "voucherNumber", "glDate", "accountNumber", "accountName", "description", "debitAmount", "creditAmount" })]
    public void GlRequiredKeyMatrix_IsExactAndOrdered(GlAmountMode mode, string[] expected)
    {
        Assert.Equal(expected, JetFieldCatalog.RequiredGlMappingKeys(mode));
    }

    [Theory]
    [InlineData(TbChangeMode.DirectChange, new[] { "accountNumber", "accountName", "changeAmount" })]
    [InlineData(TbChangeMode.DebitCredit, new[] { "accountNumber", "accountName", "debitAmount", "creditAmount" })]
    [InlineData(TbChangeMode.OpenClose, new[] { "accountNumber", "accountName", "openingBalance", "closingBalance" })]
    [InlineData(TbChangeMode.OpenCloseBySide, new[] { "accountNumber", "accountName", "openingDebitBalance", "openingCreditBalance", "closingDebitBalance", "closingCreditBalance" })]
    public void TbRequiredKeyMatrix_IsExactAndOrdered(TbChangeMode mode, string[] expected)
    {
        Assert.Equal(expected, JetFieldCatalog.RequiredTbMappingKeys(mode));
    }

    [Fact]
    public void UnknownModes_PreserveOnlyAlwaysRequiredKeys()
    {
        Assert.Equal(
            ["voucherNumber", "glDate", "accountNumber", "accountName", "description"],
            JetFieldCatalog.RequiredGlMappingKeys((GlAmountMode)int.MaxValue));
        Assert.Equal(
            ["accountNumber", "accountName"],
            JetFieldCatalog.RequiredTbMappingKeys((TbChangeMode)int.MaxValue));
    }

    [Fact]
    public void MappingRequiredness_IsIndependentFromStorageNullability()
    {
        var voucherNumber = JetFieldCatalog.FindGlSemanticFieldByMappingKey(GlMappingKeys.VoucherNumber);
        var glDate = JetFieldCatalog.FindGlSemanticFieldByMappingKey(GlMappingKeys.GlDate);
        var accountNumber = JetFieldCatalog.FindTbSemanticFieldByMappingKey(TbMappingKeys.AccountNumber);
        var glAmount = JetFieldCatalog.FindGlSemanticFieldByMappingKey(GlMappingKeys.Amount);
        var tbAmount = JetFieldCatalog.FindTbSemanticFieldByMappingKey(TbMappingKeys.ChangeAmount);

        Assert.True(GlSlot(GlMappingKeys.VoucherNumber).IsAlwaysRequired);
        Assert.True(voucherNumber.StorageNullable);
        Assert.True(GlSlot(GlMappingKeys.GlDate).IsAlwaysRequired);
        Assert.True(glDate.StorageNullable);
        Assert.True(TbSlot(TbMappingKeys.AccountNumber).IsAlwaysRequired);
        Assert.True(accountNumber.StorageNullable);

        Assert.False(GlSlot(GlMappingKeys.Amount).IsAlwaysRequired);
        Assert.False(glAmount.StorageNullable);
        Assert.False(TbSlot(TbMappingKeys.ChangeAmount).IsAlwaysRequired);
        Assert.False(tbAmount.StorageNullable);
    }

    [Fact]
    public void LiteralAndFieldInfoCompatibility_AreExact()
    {
        Assert.Equal(
            [GlMappingKeys.DrCrDebitCode, GlMappingKeys.DrCrCreditCode],
            JetFieldCatalog.GlMappingSlots.Where(static slot => slot.IsLiteral).Select(static slot => slot.Key));
        Assert.DoesNotContain(JetFieldCatalog.TbMappingSlots, static slot => slot.IsLiteral);

        Assert.Equal(
            [
                "voucherNumber", "lineItem", "glDate", "approvalDate", "accountNumber", "accountName",
                "description", "sourceModule", "createdBy", "approvedBy", "amount"
            ],
            JetFieldCatalog.GlMappingSlots
                .Where(static slot => slot.IncludeInFieldInfo)
                .Select(static slot => slot.Key));
        Assert.Equal(TbMappingKeys.All, JetFieldCatalog.TbMappingSlots
            .Where(static slot => slot.IncludeInFieldInfo)
            .Select(static slot => slot.Key));
    }

    [Fact]
    public void PostingStatus_IsAMappingUiSlotWithSemanticSqlTarget()
    {
        Assert.Contains(GlMappingKeys.PostingStatus, GlMappingKeys.All);
        var slot = GlSlot(GlMappingKeys.PostingStatus);
        var field = JetFieldCatalog.FindGlSemanticFieldByMappingKey(GlMappingKeys.PostingStatus);

        // 前端已接上過帳狀態的配對控制項與 posting policy 編輯器，
        // 故它不是 contract-only slot；欄位語意（SQL target、nullability、非通用篩選欄）不變。
        Assert.True(slot.IncludeInMappingUi);
        Assert.Contains(slot, JetFieldCatalog.GlMappingUiSlots);
        Assert.Equal(
            JetFieldCatalog.GlMappingSlots.Where(static item => item.IncludeInMappingUi),
            JetFieldCatalog.GlMappingUiSlots);
        Assert.Equal("posting_status", field.SemanticSqlTarget);
        Assert.True(field.StorageNullable);
        Assert.False(field.IsGenericFilterField);
    }

    private static ExpectedSlot Slot(
        string key,
        string label,
        int order,
        bool isAlwaysRequired,
        IEnumerable<string> requiredModes,
        bool isLiteral,
        bool includeInFieldInfo,
        string semanticIdentity,
        JetFieldValueKind kind,
        string sqlTarget,
        bool storageNullable,
        string? canonicalName,
        bool isGenericFilterField,
        bool includeInMappingUi = true) =>
        new(
            key,
            label,
            order,
            isAlwaysRequired,
            string.Join("|", requiredModes),
            isLiteral,
            includeInFieldInfo,
            semanticIdentity,
            kind,
            sqlTarget,
            storageNullable,
            canonicalName,
            isGenericFilterField,
            includeInMappingUi);

    private static IReadOnlyList<ExpectedSlot> Flatten(
        IReadOnlyList<JetFieldDefinition> fields,
        IReadOnlyList<JetMappingSlot> slots) =>
        slots.Select(slot =>
        {
            var field = fields.Single(field => field.MappingSlots.Contains(slot));
            return Slot(
                slot.Key,
                slot.Label,
                slot.Order,
                slot.IsAlwaysRequired,
                slot.RequiredModes,
                slot.IsLiteral,
                slot.IncludeInFieldInfo,
                field.SemanticIdentity,
                field.Kind,
                field.SemanticSqlTarget,
                field.StorageNullable,
                field.LegacyFieldName,
                field.IsGenericFilterField,
                slot.IncludeInMappingUi);
        }).ToArray();

    private sealed record ExpectedSlot(
        string Key,
        string Label,
        int Order,
        bool IsAlwaysRequired,
        string RequiredModes,
        bool IsLiteral,
        bool IncludeInFieldInfo,
        string SemanticIdentity,
        JetFieldValueKind Kind,
        string SemanticSqlTarget,
        bool StorageNullable,
        string? CanonicalName,
        bool IsGenericFilterField,
        bool IncludeInMappingUi);
}
