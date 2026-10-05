using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class JetFieldCatalogTests
{
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
        var glDocNum = typeof(GlMappingKeys).GetField(nameof(GlMappingKeys.DocNum));
        var glAll = typeof(GlMappingKeys).GetField(nameof(GlMappingKeys.All));
        var tbAmount = typeof(TbMappingKeys).GetField(nameof(TbMappingKeys.Amount));
        var tbAll = typeof(TbMappingKeys).GetField(nameof(TbMappingKeys.All));
        var glCanonical = typeof(GlCanonicalNames).GetProperty(nameof(GlCanonicalNames.Gl));
        var tbCanonical = typeof(GlCanonicalNames).GetProperty(nameof(GlCanonicalNames.Tb));

        Assert.NotNull(glDocNum);
        Assert.True(glDocNum.IsLiteral);
        Assert.Equal(typeof(string), glDocNum.FieldType);
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

        Assert.NotNull(glCanonical);
        Assert.Equal(typeof(IReadOnlyDictionary<string, string>), glCanonical.PropertyType);
        Assert.NotNull(glCanonical.GetMethod);
        Assert.Null(glCanonical.SetMethod);
        Assert.NotNull(tbCanonical);
        Assert.Equal(typeof(IReadOnlyDictionary<string, string>), tbCanonical.PropertyType);
        Assert.NotNull(tbCanonical.GetMethod);
        Assert.Null(tbCanonical.SetMethod);
    }

    [Fact]
    public void GlDefinitions_MatchV2ContractMatrix()
    {
        var expected = new[]
        {
            Slot("docNum", "傳票號碼", 0, true, [], false, true,
                "docNum", JetFieldValueKind.Text, "document_number", true, "傳票號碼_JE", true),
            Slot("lineID", "傳票文件項次", 1, false, [], false, true,
                "lineID", JetFieldValueKind.Text, "line_item", true, "傳票文件項次_JE_S", true),
            Slot("postDate", "總帳入帳日", 2, true, [], false, true,
                "postDate", JetFieldValueKind.Date, "post_date", true, "總帳日期_JE", true),
            Slot("docDate", "傳票核准日", 3, false, [], false, true,
                "docDate", JetFieldValueKind.Date, "approval_date", true, null, true),
            Slot("voucherDate", "傳票日期", 4, false, [], false, true,
                "voucherDate", JetFieldValueKind.Date, "voucher_date", true, null, true),
            Slot("accNum", "會計科目編號", 5, true, [], false, true,
                "accNum", JetFieldValueKind.Text, "account_code", true, "會計科目編號_JE", true),
            Slot("accName", "會計科目名稱", 6, true, [], false, true,
                "accName", JetFieldValueKind.Text, "account_name", true, "會計科目名稱_JE", true),
            Slot("description", "傳票摘要", 7, true, [], false, true,
                "description", JetFieldValueKind.Text, "document_description", true, "傳票摘要_JE", true),
            Slot("jeSource", "分錄來源模組", 8, false, [], false, true,
                "jeSource", JetFieldValueKind.Text, "source_module", true, null, true),
            Slot("createBy", "傳票建立人員", 9, false, [], false, true,
                "createBy", JetFieldValueKind.Text, "created_by", true, "傳票建立人員_JE", true),
            Slot("approveBy", "傳票核准人員", 10, false, [], false, true,
                "approveBy", JetFieldValueKind.Text, "approved_by", true, "傳票核准人員_JE", true),
            Slot("manual", "人工/自動分錄", 11, false, [], false, false,
                "manual", JetFieldValueKind.Boolean, "is_manual", true, null, false),
            Slot("amount", "分錄金額（單欄）", 12, false, ["signed", "side", "flag"], false, true,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            Slot("debitAmount", "借方金額", 13, false, ["dual"], false, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            Slot("creditAmount", "貸方金額", 14, false, ["dual"], false, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            Slot("dcField", "借貸別欄位", 15, false, ["side", "flag"], false, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            // 2026-10-02 使用者裁定 W5：欄位配對名稱「借方標識代碼」改成「借方代碼」。
            Slot("dcDebitCode", "借方代碼", 16, false, ["side", "flag"], true, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            // 2026-10-04 R9 加入必填貸方代碼；新增 slot，不改任何既有欄位的儲存語意。
            // 首次失敗：Public 20261004-100911120-57efb95a0cae44beb892ec3c2d058592。
            Slot("dcCreditCode", "貸方代碼", 17, false, ["side", "flag"], true, false,
                "amount", JetFieldValueKind.Amount, "amount_scaled", false, "傳票金額_JE", true),
            Slot("postingStatus", "過帳狀態", 18, false, [], false, false,
                "postingStatus", JetFieldValueKind.Text, "posting_status", true, null, false)
        };

        Assert.Equal(expected, Flatten(JetFieldCatalog.GlFields, JetFieldCatalog.GlMappingSlots));
    }

    [Fact]
    public void TbDefinitions_MatchPreCutoverContractMatrix()
    {
        var expected = new[]
        {
            Slot("accNum", "會計科目編號", 0, true, [], false, true,
                "accNum", JetFieldValueKind.Text, "account_code", true, "會計科目編號_TB", false),
            Slot("accName", "會計科目名稱", 1, true, [], false, true,
                "accName", JetFieldValueKind.Text, "account_name", true, "會計科目名稱_TB", false),
            Slot("amount", "年度變動金額", 2, false, ["direct"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("debitAmt", "借方金額", 3, false, ["debitCredit"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("creditAmt", "貸方金額", 4, false, ["debitCredit"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("openingBalance", "期初餘額", 5, false, ["openClose"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("closingBalance", "期末餘額", 6, false, ["openClose"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("openingDebit", "期初借方", 7, false, ["openCloseBySide"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("openingCredit", "期初貸方", 8, false, ["openCloseBySide"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("closingDebit", "期末借方", 9, false, ["openCloseBySide"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false),
            Slot("closingCredit", "期末貸方", 10, false, ["openCloseBySide"], false, true,
                "changeAmount", JetFieldValueKind.Amount, "change_amount_scaled", false, "試算表變動金額_TB", false)
        };

        Assert.Equal(expected, Flatten(JetFieldCatalog.TbFields, JetFieldCatalog.TbMappingSlots));
    }

    [Fact]
    public void CompatibilityViews_AreCatalogProjectionsWithExactValuesAndOrder()
    {
        Assert.Same(JetFieldCatalog.GlMappingKeys, GlMappingKeys.All);
        Assert.Same(JetFieldCatalog.TbMappingKeys, TbMappingKeys.All);
        Assert.Same(JetFieldCatalog.GlCanonicalNames, GlCanonicalNames.Gl);
        Assert.Same(JetFieldCatalog.TbCanonicalNames, GlCanonicalNames.Tb);

        Assert.Equal(
            [
                "docNum", "lineID", "postDate", "docDate", "voucherDate", "accNum", "accName",
                "description", "jeSource", "createBy", "approveBy", "manual", "amount",
                "debitAmount", "creditAmount", "dcField", "dcDebitCode", "dcCreditCode", "postingStatus"
            ],
            GlMappingKeys.All);
        Assert.Equal(
            [
                "accNum", "accName", "amount", "debitAmt", "creditAmt", "openingBalance",
                "closingBalance", "openingDebit", "openingCredit", "closingDebit", "closingCredit"
            ],
            TbMappingKeys.All);

        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["docNum"] = "傳票號碼_JE",
                ["lineID"] = "傳票文件項次_JE_S",
                ["postDate"] = "總帳日期_JE",
                ["createBy"] = "傳票建立人員_JE",
                ["approveBy"] = "傳票核准人員_JE",
                ["accNum"] = "會計科目編號_JE",
                ["accName"] = "會計科目名稱_JE",
                ["amount"] = "傳票金額_JE",
                ["description"] = "傳票摘要_JE"
            },
            GlCanonicalNames.Gl);
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["accNum"] = "會計科目編號_TB",
                ["accName"] = "會計科目名稱_TB",
                ["changeAmount"] = "試算表變動金額_TB"
            },
            GlCanonicalNames.Tb);
        Assert.False(GlCanonicalNames.Tb.ContainsKey(TbMappingKeys.Amount));
    }

    [Theory]
    [InlineData(GlAmountMode.SignedAmount, new[] { "docNum", "postDate", "accNum", "accName", "description", "amount" })]
    [InlineData(GlAmountMode.AmountWithSide, new[] { "docNum", "postDate", "accNum", "accName", "description", "amount", "dcField", "dcDebitCode", "dcCreditCode" })]
    [InlineData(GlAmountMode.AmountWithFlag, new[] { "docNum", "postDate", "accNum", "accName", "description", "amount", "dcField", "dcDebitCode", "dcCreditCode" })]
    [InlineData(GlAmountMode.DualAmount, new[] { "docNum", "postDate", "accNum", "accName", "description", "debitAmount", "creditAmount" })]
    public void GlRequiredKeyMatrix_IsExactAndOrdered(GlAmountMode mode, string[] expected)
    {
        Assert.Equal(expected, JetFieldCatalog.RequiredGlMappingKeys(mode));
    }

    [Theory]
    [InlineData(TbChangeMode.DirectChange, new[] { "accNum", "accName", "amount" })]
    [InlineData(TbChangeMode.DebitCredit, new[] { "accNum", "accName", "debitAmt", "creditAmt" })]
    [InlineData(TbChangeMode.OpenClose, new[] { "accNum", "accName", "openingBalance", "closingBalance" })]
    [InlineData(TbChangeMode.OpenCloseBySide, new[] { "accNum", "accName", "openingDebit", "openingCredit", "closingDebit", "closingCredit" })]
    public void TbRequiredKeyMatrix_IsExactAndOrdered(TbChangeMode mode, string[] expected)
    {
        Assert.Equal(expected, JetFieldCatalog.RequiredTbMappingKeys(mode));
    }

    [Fact]
    public void UnknownModes_PreserveOnlyAlwaysRequiredKeys()
    {
        Assert.Equal(
            ["docNum", "postDate", "accNum", "accName", "description"],
            JetFieldCatalog.RequiredGlMappingKeys((GlAmountMode)int.MaxValue));
        Assert.Equal(
            ["accNum", "accName"],
            JetFieldCatalog.RequiredTbMappingKeys((TbChangeMode)int.MaxValue));
    }

    [Fact]
    public void MappingRequiredness_IsIndependentFromStorageNullability()
    {
        var documentNumber = JetFieldCatalog.FindGlSemanticFieldByMappingKey(GlMappingKeys.DocNum);
        var postDate = JetFieldCatalog.FindGlSemanticFieldByMappingKey(GlMappingKeys.PostDate);
        var accountCode = JetFieldCatalog.FindTbSemanticFieldByMappingKey(TbMappingKeys.AccNum);
        var glAmount = JetFieldCatalog.FindGlSemanticFieldByMappingKey(GlMappingKeys.Amount);
        var tbAmount = JetFieldCatalog.FindTbSemanticFieldByMappingKey(TbMappingKeys.Amount);

        Assert.True(JetFieldCatalog.FindGlMappingSlot(GlMappingKeys.DocNum).IsAlwaysRequired);
        Assert.True(documentNumber.StorageNullable);
        Assert.True(JetFieldCatalog.FindGlMappingSlot(GlMappingKeys.PostDate).IsAlwaysRequired);
        Assert.True(postDate.StorageNullable);
        Assert.True(JetFieldCatalog.FindTbMappingSlot(TbMappingKeys.AccNum).IsAlwaysRequired);
        Assert.True(accountCode.StorageNullable);

        Assert.False(JetFieldCatalog.FindGlMappingSlot(GlMappingKeys.Amount).IsAlwaysRequired);
        Assert.False(glAmount.StorageNullable);
        Assert.False(JetFieldCatalog.FindTbMappingSlot(TbMappingKeys.Amount).IsAlwaysRequired);
        Assert.False(tbAmount.StorageNullable);
    }

    [Fact]
    public void LiteralAndFieldInfoCompatibility_AreExact()
    {
        Assert.Equal(
            [GlMappingKeys.DcDebitCode, GlMappingKeys.DcCreditCode],
            JetFieldCatalog.GlMappingSlots.Where(static slot => slot.IsLiteral).Select(static slot => slot.Key));
        Assert.DoesNotContain(JetFieldCatalog.TbMappingSlots, static slot => slot.IsLiteral);

        Assert.Equal(
            [
                "docNum", "lineID", "postDate", "docDate", "voucherDate", "accNum", "accName",
                "description", "jeSource", "createBy", "approveBy", "amount"
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
        var slot = JetFieldCatalog.FindGlMappingSlot(GlMappingKeys.PostingStatus);
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
                field.CanonicalName,
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
