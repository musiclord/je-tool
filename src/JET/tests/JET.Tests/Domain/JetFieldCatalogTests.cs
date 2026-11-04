using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class JetFieldCatalogTests
{
    // 2026-10-22 清理技術債：目錄只給測試用的 FindGlMappingSlot 與 FindTbMappingSlot 已刪除，測試直接從槽位清單找。
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
        // 照樣本範本定的 GlCanonicalNames 依裁定 2026-11-03-11 刪除，輸出欄名只剩欄位目錄的 LegacyFieldName 一份；
        // 這裡原本對樣本欄名表的檢查一起拿掉。第一次失敗：Build 收據 20261103-064752145-f0bd5fc4ade8407b8955b91d0b0bd760。

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
        // 矩陣倒數第二欄原本是樣本欄名 CanonicalName，依裁定 2026-11-03-11 改成輸出欄名 LegacyFieldName，
        // 沒有樣本名的傳票核准日、分錄來源模組與人工/自動分錄因此有值。第一次失敗：Build 收據 20261103-064752145-f0bd5fc4ade8407b8955b91d0b0bd760。
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
            // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，原本 order 4 的 voucherDate 拿掉，其餘順序值不重排。
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
            // 裁定 2026-10-13-18 把分錄金額改名為金額；第一次失敗：20261013-092449164-3847782f6c50488b86a0ac3856e606ca。
            Slot("amount", "金額", 12, false, ["signed", "side", "flag"], false, true,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            Slot("debitAmount", "借方金額", 13, false, ["dual"], false, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            Slot("creditAmount", "貸方金額", 14, false, ["dual"], false, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            Slot("drCr", "借貸別", 15, false, ["side", "flag"], false, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            // 裁定 2026-10-02-02：欄位配對名稱「借方標識代碼」改成「借方代碼」。
            Slot("drCrDebitCode", "借方代碼", 16, false, ["side", "flag"], true, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            // 2026-10-04 加入必填貸方代碼；新增 slot，不改任何既有欄位的儲存語意。
            // 首次失敗：Public 20261004-100911120-57efb95a0cae44beb892ec3c2d058592。
            Slot("drCrCreditCode", "貸方代碼", 17, false, ["side", "flag"], true, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            // 過帳碼_JE 照舊版補回（裁定 2026-11-03-15），過帳狀態欄因此有輸出欄名。第一次失敗：Build 收據 20261103-064752145-f0bd5fc4ade8407b8955b91d0b0bd760。
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
        // 照樣本範本定的 GlCanonicalNames 依裁定 2026-11-03-11 刪除，輸出欄名只剩欄位目錄的 LegacyFieldName 一份；
        // 這裡原本對樣本欄名表的檢查一起拿掉。第一次失敗：Build 收據 20261103-064752145-f0bd5fc4ade8407b8955b91d0b0bd760。

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

        // 「前端工作流整合」已接上過帳狀態的配對控制項與 posting policy 編輯器，
        // 故它不再是 contract-only slot；欄位語意（SQL target、nullability、非通用篩選欄）不變。
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
