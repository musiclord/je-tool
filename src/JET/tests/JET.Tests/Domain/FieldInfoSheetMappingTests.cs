using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 檔案沒有 JET 記下的隱藏配對時，從「自動化工具-檔案欄位資訊」看得見的對照表推得草稿。
/// 舊版 IDEA JET 與新版 JET 兩種寫法都要認得；欄名都是合成的。
/// </summary>
public sealed class FieldInfoSheetMappingTests
{
    private const string Text = "文字型態";
    private const string Number = "數字型態";
    private const string Date = "日期型態";

    private static readonly string[] LegacyGlColumns =
    [
        "Doc No", "Line", "Post Date", "Acct", "Acct Name", "Memo", "Module", "Maker", "Checker", "Manual Flag", "Amount"
    ];

    [Fact]
    public void Infer_LegacyStandardNames_MapBackToTheMappingKeys()
    {
        var result = Infer(Gl(
            Row("Doc No", Text, "傳票號碼_JE"),
            Row("Line", Number, "傳票文件項次_JE_S"),
            Row("Post Date", Date, "總帳日期_JE"),
            Row("Acct", Text, "會計科目編號_JE"),
            Row("Acct Name", Text, "會計科目名稱_JE"),
            Row("Memo", Text, "傳票摘要_JE"),
            Row("Module", Text, "分錄來源模組_JE"),
            Row("Maker", Text, "傳票建立人員_JE"),
            Row("Checker", Text, "傳票核准人員_JE"),
            Row("Manual Flag", Number, "人工傳票否_JE_S"),
            Row("Amount", Number),
            Row("傳票金額_JE 由系統產生 : Amount", Number, "傳票金額_JE")), LegacyGlColumns);

        Assert.Equal(ExpectedLegacyGlMapping(), result.Draft.Gl.Mapping);
        Assert.Equal(GlAmountModeNames.Signed, result.Draft.Gl.AmountMode);
        Assert.Equal(ApprovalDateModeNames.Unmapped, result.Draft.Gl.ApprovalDateMode);
        Assert.Null(result.Draft.Gl.PostingStatusPolicy);
        Assert.Equal(["1"], result.Draft.Gl.ManualAutoPolicy.ManualValues);
        Assert.Equal(["0"], result.Draft.Gl.ManualAutoPolicy.AutomaticValues);
        Assert.Empty(result.Draft.Gl.RdeFields);
        Assert.Empty(result.Draft.Gl.FillDownFields);
        Assert.Null(result.Draft.Tb);
        Assert.Empty(result.Unmatched);
        AssertDraftIsValid(result, LegacyGlColumns);
    }

    [Fact]
    public void Infer_NewVersionMarkedNames_MapBackLikeTheLegacyNames()
    {
        var result = Infer(Gl(
            Row("【Doc No】", Text, "傳票號碼_JE"),
            Row("【Line】", Number, "傳票文件項次_JE_S"),
            Row("【Post Date】", Date, "總帳日期_JE"),
            Row("【Acct】", Text, "會計科目編號_JE"),
            Row("【Acct Name】", Text, "會計科目名稱_JE"),
            Row("【Memo】", Text, "傳票摘要_JE"),
            Row("【Module】", Text, "分錄來源模組_JE"),
            Row("【Maker】", Text, "傳票建立人員_JE"),
            Row("【Checker】", Text, "傳票核准人員_JE"),
            Row("【Manual Flag】", Number, "人工傳票否_JE_S"),
            Row("Amount", Number),
            Row("【傳票金額_JE】由系統產生 : 【Amount】", Number, "傳票金額_JE")), LegacyGlColumns);

        Assert.Equal(ExpectedLegacyGlMapping(), result.Draft.Gl.Mapping);
        Assert.Equal(GlAmountModeNames.Signed, result.Draft.Gl.AmountMode);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Infer_SystemGeneratedTempAndBlankRows_AreNotMappings()
    {
        var result = Infer(Gl(
            // 數字型的傳票號碼：_Temp 原欄的 E 欄空白，改名後的文字欄才寫標準欄名。
            Row("Doc No", Number),
            Row("【Doc No】", Number),
            Row("Doc No", Text, "傳票號碼_JE"),
            // 系統產生的欄位：A 欄和 E 欄相同，或 A 欄空白。
            Row("傳票文件項次_JE_S", Number, "傳票文件項次_JE_S"),
            Row("人工傳票否_JE_S", Number, "人工傳票否_JE_S"),
            Row("", Text, "傳票摘要_JE"),
            Row("   ", Text, "會計科目名稱_JE"),
            // 非標準的 _JE_S 與其他結尾都略過。
            Row("Flag", Number, "特殊_JE_S"),
            Row("Other", Text, "Other_TB")), ["Doc No", "Flag", "Other"]);

        Assert.Equal(
            new Dictionary<string, string> { [GlMappingKeys.VoucherNumber] = "Doc No" },
            result.Draft.Gl.Mapping);
        Assert.Empty(result.Draft.Gl.RdeFields);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Infer_SameStandardNameTwice_KeepsTheFirstValidRow()
    {
        var result = Infer(Gl(
            Row("", Text, "傳票摘要_JE"),
            Row("Memo", Text, "傳票摘要_JE"),
            Row("Memo 2", Text, "傳票摘要_JE")), ["Memo", "Memo 2"]);

        Assert.Equal("Memo", result.Draft.Gl.Mapping[GlMappingKeys.Description]);
    }

    [Fact]
    public void Infer_AmountRowWithoutGeneratedPrefix_IsASignedAmount()
    {
        var result = Infer(Gl(Row("Net", Number, "傳票金額_JE")), ["Net"]);

        Assert.Equal(GlAmountModeNames.Signed, result.Draft.Gl.AmountMode);
        Assert.Equal("Net", result.Draft.Gl.Mapping[GlMappingKeys.Amount]);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Infer_LegacyDualAmount_SplitsAtTheDash()
    {
        var result = Infer(Gl(Row("傳票金額_JE 由系統產生 : Debit-Credit", Number, "傳票金額_JE")), ["Debit", "Credit"]);

        AssertDual(result, "Debit", "Credit");
    }

    [Fact]
    public void Infer_NewVersionDualAmount_ReadsBothMarkedNames()
    {
        var result = Infer(
            Gl(Row("【傳票金額_JE】由系統產生 : 【Dr-Amt】-【Cr-Amt】", Number, "傳票金額_JE")),
            ["Dr-Amt", "Cr-Amt"]);

        AssertDual(result, "Dr-Amt", "Cr-Amt");
    }

    [Fact]
    public void Infer_LegacyDualAmountWithDashesInNames_UsesTheOnlySplitMatchingCurrentColumns()
    {
        var result = Infer(
            Gl(Row("傳票金額_JE 由系統產生 : Dr-Amt-Cr-Amt", Number, "傳票金額_JE")),
            ["Dr-Amt", "Cr-Amt", "Amt"]);

        AssertDual(result, "Dr-Amt", "Cr-Amt");
    }

    [Fact]
    public void Infer_LegacyDualAmountWithOneDashAndUnknownNames_KeepsTheFileNamesForReselection()
    {
        var result = Infer(
            Gl(Row("傳票金額_JE 由系統產生 : Old Debit-Old Credit", Number, "傳票金額_JE")),
            ["Debit", "Credit"]);

        AssertDual(result, "Old Debit", "Old Credit");
    }

    // 舊版說明句的「-」一律是借方欄與貸方欄的分隔：
    // 整句即使和目前某個欄名相同，或正規化後像另一個欄名，也不當成單一欄。對不上的欄名留著，由 handler 列出重選。
    [Theory]
    [InlineData("Net-Amount", new[] { "Net-Amount", "Net", "Amount" }, "Net", "Amount")]
    [InlineData("Dr-Cr", new[] { "Dr", "Cr", "Dr/Cr" }, "Dr", "Cr")]
    [InlineData("Debit-Credit", new[] { "Amount", "Debit/Credit" }, "Debit", "Credit")]
    [InlineData("Debit-Credit", new[] { "Debit", "Credit Amt", "Debit/Credit" }, "Debit", "Credit")]
    [InlineData("Net-Amount", new[] { "NET AMOUNT" }, "Net", "Amount")]
    public void Infer_LegacyAmountWithADash_IsDualEvenWhenTheWholeTextLooksLikeOneColumn(
        string expression,
        string[] columns,
        string debit,
        string credit)
    {
        var result = Infer(Gl(Row("傳票金額_JE 由系統產生 : " + expression, Number, "傳票金額_JE")), columns);

        AssertDual(result, debit, credit);
    }

    [Theory]
    [InlineData("傳票金額_JE 由系統產生 :")]
    [InlineData("【傳票金額_JE】由系統產生 :   ")]
    public void Infer_AmountSentenceWithoutAColumn_ListsTheAmount(string sentence)
    {
        var result = Infer(Gl(Row(sentence, Number, "傳票金額_JE")), ["Amount"]);

        Assert.Empty(result.Draft.Gl.Mapping);
        Assert.Equal([new UnmatchedMappingField("gl", GlMappingKeys.Amount, "金額", "")], result.Unmatched);
    }

    [Theory]
    [InlineData("兩種切法都對得上", new[] { "A", "B-C", "A-B", "C" })]
    [InlineData("好幾個「-」但沒有一種切法對得上", new string[0])]
    public void Infer_LegacyAmountThatCannotBeSplit_LeavesTheAmountForTheAuditor(string situation, string[] columns)
    {
        Assert.NotEmpty(situation);
        var sentence = "傳票金額_JE 由系統產生 : A-B-C";
        var result = Infer(Gl(Row(sentence, Number, "傳票金額_JE")), columns);

        Assert.Equal(GlAmountModeNames.Signed, result.Draft.Gl.AmountMode);
        Assert.Empty(result.Draft.Gl.Mapping);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(new UnmatchedMappingField("gl", GlMappingKeys.Amount, "金額", "A-B-C"), unmatched);
    }

    [Fact]
    public void Infer_LegacyAmountWithoutDash_IsSignedWithTheFileName()
    {
        var result = Infer(Gl(Row("傳票金額_JE 由系統產生 : Old Amount", Number, "傳票金額_JE")), ["Amount"]);

        Assert.Equal(GlAmountModeNames.Signed, result.Draft.Gl.AmountMode);
        Assert.Equal("Old Amount", result.Draft.Gl.Mapping[GlMappingKeys.Amount]);
        Assert.Empty(result.Unmatched);
    }

    [Theory]
    [InlineData(Text, GlAmountModeNames.Side)]
    [InlineData(Number, GlAmountModeNames.Flag)]
    public void Infer_LegacyAmountWithDebitCreditColumn_TypeOfTheJudgingColumnChoosesSideOrFlag(string drCrType, string expectedMode)
    {
        var result = Infer(Gl(
            Row("Amount", Number),
            Row("DC", drCrType),
            Row("傳票金額_JE 由系統產生 : 金額欄位【Amount】、借貸方判斷欄位【DC】，借方為【D】", Number, "傳票金額_JE")),
            ["Amount", "DC"]);

        Assert.Equal(expectedMode, result.Draft.Gl.AmountMode);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [GlMappingKeys.Amount] = "Amount",
                [GlMappingKeys.DrCr] = "DC",
                [GlMappingKeys.DrCrDebitCode] = "D"
            },
            result.Draft.Gl.Mapping);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Infer_NewVersionAmountWithDebitCreditColumn_ReadsTheQuotedDebitCode()
    {
        var result = Infer(Gl(
            Row("DC", Number),
            Row("【傳票金額_JE】由系統產生 : 金額欄位【Amount】、借貸方判斷欄位【DC】，借方為「1」", Number, "傳票金額_JE")),
            ["Amount", "DC"]);

        Assert.Equal(GlAmountModeNames.Flag, result.Draft.Gl.AmountMode);
        Assert.Equal("1", result.Draft.Gl.Mapping[GlMappingKeys.DrCrDebitCode]);
        Assert.Empty(result.Unmatched);
    }

    [Theory]
    [InlineData("傳票金額_JE 由系統產生 : 金額欄位【Amount】、借貸方判斷欄位【DC】")]
    [InlineData("【傳票金額_JE】由系統產生 : 金額欄位【Amount】、借貸方判斷欄位【DC】，借方為「drCrDebitCode」")]
    [InlineData("傳票金額_JE 由系統產生 : 金額欄位【Amount】、借貸方判斷欄位【DC】，借方為【】")]
    public void Infer_AmountWithoutDebitCode_ListsTheDebitCodeWithoutASourceColumn(string sentence)
    {
        var result = Infer(Gl(Row("DC", Text), Row(sentence, Number, "傳票金額_JE")), ["Amount", "DC"]);

        Assert.Equal(GlAmountModeNames.Side, result.Draft.Gl.AmountMode);
        Assert.False(result.Draft.Gl.Mapping.ContainsKey(GlMappingKeys.DrCrDebitCode));
        Assert.Equal("DC", result.Draft.Gl.Mapping[GlMappingKeys.DrCr]);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(new UnmatchedMappingField("gl", GlMappingKeys.DrCrDebitCode, "借方代碼", ""), unmatched);
    }

    [Theory]
    [InlineData("Status ：過帳判斷值【Y】", new[] { "Y" }, false)]
    [InlineData("Status：過帳判斷值【 Y 】", new[] { "Y" }, false)]
    [InlineData("Status ：過帳判斷值【XXXX】", new string[0], true)] // 舊版填 XXXX 表示空白
    [InlineData("【Status】過帳判斷值「P」「A」「p」", new[] { "P", "A" }, false)]
    [InlineData("【Status】過帳判斷值「P」，空白也算已過帳", new[] { "P" }, true)]
    [InlineData("【Status】過帳判斷值，空白也算已過帳", new string[0], true)]
    public void Infer_PostingStatus_ReadsTheColumnAndTheAcceptedValues(string sentence, string[] values, bool includeBlank)
    {
        var result = Infer(Gl(Row("Status", Text), Row(sentence, Text, "過帳碼_JE")), ["Status"]);

        Assert.Equal("Status", result.Draft.Gl.Mapping[GlMappingKeys.PostingStatus]);
        Assert.NotNull(result.Draft.Gl.PostingStatusPolicy);
        Assert.Equal(values, result.Draft.Gl.PostingStatusPolicy.AcceptedValues);
        Assert.Equal(includeBlank, result.Draft.Gl.PostingStatusPolicy.IncludeBlank);
        Assert.Empty(result.Unmatched);
    }

    [Theory]
    [InlineData("Status ：過帳判斷值【】", "Status")]
    [InlineData("【Status】過帳判斷值", "Status")]
    [InlineData("過帳碼看不懂的說明", "過帳碼看不懂的說明")]
    public void Infer_PostingStatusWithoutValues_ListsThePostingStatus(string sentence, string expectedSource)
    {
        var result = Infer(Gl(Row(sentence, Text, "過帳碼_JE")), ["Status"]);

        Assert.False(result.Draft.Gl.Mapping.ContainsKey(GlMappingKeys.PostingStatus));
        Assert.Null(result.Draft.Gl.PostingStatusPolicy);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(new UnmatchedMappingField("gl", GlMappingKeys.PostingStatus, "過帳狀態", expectedSource), unmatched);
    }

    [Theory]
    [InlineData("系統產生 : 同總帳日期_JE來源")]
    [InlineData("【傳票核准日_JE】由系統產生 : 同【總帳日期_JE】來源")]
    [InlineData("傳票核准日_JE 由系統產生 : 同總帳日期_JE來源")]
    public void Infer_ApprovalDateSameAsGlDate_SetsTheModeWithoutAColumn(string sentence)
    {
        var result = Infer(Gl(Row(sentence, Date, "傳票核准日_JE")), ["Post Date"]);

        Assert.Equal(ApprovalDateModeNames.SameAsGlDate, result.Draft.Gl.ApprovalDateMode);
        Assert.False(result.Draft.Gl.Mapping.ContainsKey(GlMappingKeys.ApprovalDate));
        Assert.Empty(result.Unmatched);
    }

    [Theory]
    [InlineData("Approve Date")]
    [InlineData("【Approve Date】")]
    public void Infer_ApprovalDateFromASourceColumn_IsMapped(string before)
    {
        var result = Infer(Gl(Row(before, Date, "傳票核准日_JE")), ["Approve Date"]);

        Assert.Equal(ApprovalDateModeNames.Mapped, result.Draft.Gl.ApprovalDateMode);
        Assert.Equal("Approve Date", result.Draft.Gl.Mapping[GlMappingKeys.ApprovalDate]);
    }

    [Fact]
    public void Infer_ManualColumn_UsesOneForManualAndZeroForAutomatic()
    {
        var result = Infer(Gl(Row("Manual Flag", Number, "人工傳票否_JE_S")), ["Manual Flag"]);

        Assert.Equal("Manual Flag", result.Draft.Gl.Mapping[GlMappingKeys.IsManual]);
        Assert.Equal(["1"], result.Draft.Gl.ManualAutoPolicy.ManualValues);
        Assert.Equal(["0"], result.Draft.Gl.ManualAutoPolicy.AutomaticValues);
    }

    [Fact]
    public void Infer_OtherJeRows_BecomeRelevantDataElementsTypedByTheFieldType()
    {
        var result = Infer(Gl(
            Row("Region", Text, "地區_JE"),
            Row("Due", Date, "到期日_JE"),
            Row("Cost", Number, "成本_JE"),
            Row("【Dept】", Text, "部門（Dept）_JE"),
            Row("Note", "時間型態", "Note_JE")), ["Region", "Due", "Cost", "Dept", "Note"]);

        // 「部門（Dept）」沒有和其他攸關資料元素同名，新版不會在後面補來源欄名，所以名稱本身就是「部門（Dept）」。
        // 後綴照寫出端的規則反推。
        Assert.Equal(
            [
                new GlRdeFieldMetadata(FieldId(1), "Region", "地區", RdeFieldValueTypeNames.Text),
                new GlRdeFieldMetadata(FieldId(2), "Due", "到期日", RdeFieldValueTypeNames.Date),
                new GlRdeFieldMetadata(FieldId(3), "Cost", "成本", RdeFieldValueTypeNames.Amount),
                new GlRdeFieldMetadata(FieldId(4), "Dept", "部門（Dept）", RdeFieldValueTypeNames.Text),
                new GlRdeFieldMetadata(FieldId(5), "Note", "Note", RdeFieldValueTypeNames.Text)
            ],
            result.Draft.Gl.RdeFields);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Infer_RelevantDataElementsThatCannotBeKept_AreListedForTheAuditor()
    {
        var tooLong = new string('長', GlRdeStorageLimits.LabelUtf16CodeUnits + 1);
        var result = Infer(Gl(
            Row("Memo", Text, "傳票摘要_JE"),
            Row("Region", Text, "地區_JE"),
            Row("Region 2", Text, "地區_JE"),
            Row("Region", Text, "區域_JE"),
            Row("Memo", Text, "備註_JE"),
            Row("Long", Text, tooLong + "_JE"),
            Row("Blank", Text, " _JE")), ["Memo", "Region", "Region 2", "Long", "Blank"]);

        // 名稱相同但來源欄不同時照樣帶入，輸出欄名會在名稱後面加上來源欄名區分。
        Assert.Equal(
            [
                new GlRdeFieldMetadata(FieldId(1), "Region", "地區", RdeFieldValueTypeNames.Text),
                new GlRdeFieldMetadata(FieldId(2), "Region 2", "地區", RdeFieldValueTypeNames.Text)
            ],
            result.Draft.Gl.RdeFields);
        Assert.Equal(
            [
                new UnmatchedMappingField("gl", FieldId(3), "區域", "Region"),
                new UnmatchedMappingField("gl", FieldId(4), "備註", "Memo"),
                new UnmatchedMappingField("gl", FieldId(5), tooLong, "Long"),
                new UnmatchedMappingField("gl", FieldId(6), "", "Blank")
            ],
            result.Unmatched);
    }

    [Fact]
    public void Infer_NewVersionRelevantDataElementsWithTheSameName_KeepBothWithTheirOwnSourceColumns()
    {
        var result = Infer(Gl(
            Row("【Region】", Text, "地區（Region）_JE"),
            Row("【Region 2】", Text, "地區（Region 2）_JE")), ["Region", "Region 2"]);

        Assert.Equal(
            [
                new GlRdeFieldMetadata(FieldId(1), "Region", "地區", RdeFieldValueTypeNames.Text),
                new GlRdeFieldMetadata(FieldId(2), "Region 2", "地區", RdeFieldValueTypeNames.Text)
            ],
            result.Draft.Gl.RdeFields);
        Assert.Empty(result.Unmatched);
    }

    // 舊版 A 欄寫原欄名，來源欄本來就叫 JET 欄名時 A 欄和 E 欄相同，算同名配對；目前資料沒有這一欄時照樣寫進草稿，由 handler 列出重選。
    [Fact]
    public void Infer_LegacySourceAlreadyNamedLikeTheJetField_IsASameNameMapping()
    {
        var result = Infer(
            Sheet(
                [
                    Row("會計科目編號_TB", Text, "會計科目編號_TB"),
                    Row("試算表變動金額_TB", Number, "試算表變動金額_TB")
                ],
                [
                    Row("傳票號碼_JE", Text, "傳票號碼_JE"),
                    Row("傳票摘要_JE", Text, "傳票摘要_JE"),
                    Row("傳票文件項次_JE_S", Number, "傳票文件項次_JE_S"),
                    Row("傳票核准日_JE", Date, "傳票核准日_JE"),
                    Row("傳票金額_JE", Number, "傳票金額_JE"),
                    Row("地區_JE", Text, "地區_JE")
                ]),
            ["傳票號碼_JE", "Memo", "傳票文件項次_JE_S", "傳票核准日_JE", "傳票金額_JE", "地區_JE"],
            ["會計科目編號_TB", "試算表變動金額_TB"]);

        Assert.Equal(
            new Dictionary<string, string>
            {
                [GlMappingKeys.VoucherNumber] = "傳票號碼_JE",
                [GlMappingKeys.Description] = "傳票摘要_JE",
                [GlMappingKeys.LineItem] = "傳票文件項次_JE_S",
                [GlMappingKeys.ApprovalDate] = "傳票核准日_JE",
                [GlMappingKeys.Amount] = "傳票金額_JE"
            },
            result.Draft.Gl.Mapping);
        Assert.Equal(ApprovalDateModeNames.Mapped, result.Draft.Gl.ApprovalDateMode);
        Assert.Equal([new GlRdeFieldMetadata(FieldId(1), "地區_JE", "地區", RdeFieldValueTypeNames.Text)], result.Draft.Gl.RdeFields);
        Assert.NotNull(result.Draft.Tb);
        Assert.Equal(TbChangeModeNames.Direct, result.Draft.Tb.ChangeMode);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccountNumber] = "會計科目編號_TB",
                [TbMappingKeys.ChangeAmount] = "試算表變動金額_TB"
            },
            result.Draft.Tb.Mapping);
        Assert.Empty(result.Unmatched);
    }

    // 新版同名改名的 A 欄寫成「【X】」，算配對；沒有括號、A 欄和 E 欄相同的是系統產生，例如自動編號的傳票文件項次_JE_S。
    [Fact]
    public void Infer_NewVersionSameNameRenameIsAMappingButTheAutoNumberIsNot()
    {
        var result = Infer(
            Gl(
                Row("【Doc】", Text, "傳票號碼_JE"),
                Row("【人工傳票否_JE_S】", Number, "人工傳票否_JE_S"),
                Row("傳票文件項次_JE_S", Number, "傳票文件項次_JE_S")),
            ["Doc", "人工傳票否_JE_S"]);

        Assert.Equal(
            new Dictionary<string, string>
            {
                [GlMappingKeys.VoucherNumber] = "Doc",
                [GlMappingKeys.IsManual] = "人工傳票否_JE_S"
            },
            result.Draft.Gl.Mapping);
        Assert.Empty(result.Unmatched);
    }

    // 新版說明句的欄名本身有「】」時：單一欄名去掉最外層括號再找；金額加借貸別的句子有固定的分隔，照樣讀得出來。
    // 兩種都不套舊版「-」的規則。借貸別句的欄名可以有「】」。
    [Fact]
    public void Infer_NewVersionAmountWithBracketsInsideTheName_ReadsTheNamesWithoutTheLegacyDashRule()
    {
        var single = Infer(
            Gl(Row("【傳票金額_JE】由系統產生 : 【【調整】A-B】", Number, "傳票金額_JE")),
            ["【調整】A-B"]);
        var sided = Infer(
            Gl(Row("【傳票金額_JE】由系統產生 : 金額欄位【【調整】金額】、借貸方判斷欄位【DC】，借方為「D」", Number, "傳票金額_JE")),
            ["【調整】金額", "DC"]);

        Assert.Equal(GlAmountModeNames.Signed, single.Draft.Gl.AmountMode);
        Assert.Equal(new Dictionary<string, string> { [GlMappingKeys.Amount] = "【調整】A-B" }, single.Draft.Gl.Mapping);
        Assert.Empty(single.Unmatched);
        Assert.Equal(GlAmountModeNames.Side, sided.Draft.Gl.AmountMode);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [GlMappingKeys.Amount] = "【調整】金額",
                [GlMappingKeys.DrCr] = "DC",
                [GlMappingKeys.DrCrDebitCode] = "D"
            },
            sided.Draft.Gl.Mapping);
        Assert.Empty(sided.Unmatched);
    }

    // 只填貸方代碼時，新版寫「貸方為「C」」；讀回貸方代碼，不要求填借方代碼。
    [Fact]
    public void Infer_AmountWithOnlyACreditCode_ReadsTheCreditCode()
    {
        var result = Infer(
            Gl(Row("DC", Text), Row("【傳票金額_JE】由系統產生 : 金額欄位【Amount】、借貸方判斷欄位【DC】，貸方為「C」", Number, "傳票金額_JE")),
            ["Amount", "DC"]);

        Assert.Equal(GlAmountModeNames.Side, result.Draft.Gl.AmountMode);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [GlMappingKeys.Amount] = "Amount",
                [GlMappingKeys.DrCr] = "DC",
                [GlMappingKeys.DrCrCreditCode] = "C"
            },
            result.Draft.Gl.Mapping);
        Assert.Empty(result.Unmatched);
    }

    // 來源欄名本身有括號時，新版的配對前欄位名稱是「【【舊】地區】」：外層括號裡的括號成對，去掉最外層；
    // 過帳狀態的說明句也照樣讀出欄名。
    [Fact]
    public void Infer_SourceColumnNamesWithBrackets_AreReadWithoutTheOuterBrackets()
    {
        var result = Infer(Gl(
            Row("【Doc】", Text, "傳票號碼_JE"),
            Row("【【舊】地區】", Text, "地區（【舊】地區）_JE"),
            Row("【Region】", Text, "地區（Region）_JE"),
            Row("【舊】狀態", Text),
            Row("【【舊】狀態】過帳判斷值「Y」", Text, "過帳碼_JE")), ["Doc", "【舊】地區", "Region", "【舊】狀態"]);

        Assert.Equal("【舊】狀態", result.Draft.Gl.Mapping[GlMappingKeys.PostingStatus]);
        Assert.Equal(["Y"], result.Draft.Gl.PostingStatusPolicy!.AcceptedValues);
        Assert.Equal(
            [
                new GlRdeFieldMetadata(FieldId(1), "【舊】地區", "地區", RdeFieldValueTypeNames.Text),
                new GlRdeFieldMetadata(FieldId(2), "Region", "地區", RdeFieldValueTypeNames.Text)
            ],
            result.Draft.Gl.RdeFields);
        Assert.Empty(result.Unmatched);
    }

    // 來源欄名有儲存格裡的換行時，說明句照樣讀得出欄名：過帳狀態、借貸兩欄、金額加借貸別。
    [Fact]
    public void Infer_SourceColumnNamesWithLineBreaks_AreReadFromTheSentences()
    {
        var dualAndPosting = Infer(Gl(
            Row("Status\nCode", Text),
            Row("【Status\nCode】過帳判斷值「Y」", Text, "過帳碼_JE"),
            Row("【傳票金額_JE】由系統產生 : 【借方\n金額】-【貸方\n金額】", Number, "傳票金額_JE")),
            ["Status\nCode", "借方\n金額", "貸方\n金額"]);
        var sided = Infer(
            Gl(Row("借\n貸", Text), Row("【傳票金額_JE】由系統產生 : 金額欄位【金\n額】、借貸方判斷欄位【借\n貸】，借方為「D」", Number, "傳票金額_JE")),
            ["金\n額", "借\n貸"]);

        Assert.Equal("Status\nCode", dualAndPosting.Draft.Gl.Mapping[GlMappingKeys.PostingStatus]);
        Assert.Equal(["Y"], dualAndPosting.Draft.Gl.PostingStatusPolicy!.AcceptedValues);
        Assert.Equal(GlAmountModeNames.Dual, dualAndPosting.Draft.Gl.AmountMode);
        Assert.Equal("借方\n金額", dualAndPosting.Draft.Gl.Mapping[GlMappingKeys.DebitAmount]);
        Assert.Equal("貸方\n金額", dualAndPosting.Draft.Gl.Mapping[GlMappingKeys.CreditAmount]);
        Assert.Empty(dualAndPosting.Unmatched);
        Assert.Equal(GlAmountModeNames.Side, sided.Draft.Gl.AmountMode);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [GlMappingKeys.Amount] = "金\n額",
                [GlMappingKeys.DrCr] = "借\n貸",
                [GlMappingKeys.DrCrDebitCode] = "D"
            },
            sided.Draft.Gl.Mapping);
        Assert.Empty(sided.Unmatched);
    }

    // 新版改名列的 A 欄一律寫「【來源欄名】」。來源欄名本身的括號不成對時（「A】」「【舊地區」），照樣只去掉最外層，
    // 不靠模糊比對；目前資料另有正規化後相同的欄（「A」「舊地區」）時也對得回去。
    [Fact]
    public void Infer_RenamedSourceWithUnpairedBrackets_DropsOnlyTheOuterBrackets()
    {
        var result = Infer(Gl(
            Row("【Doc】", Text, "傳票號碼_JE"),
            Row("【A】】", Text, "傳票摘要_JE"),
            Row("【Region】", Text, "地區（Region）_JE"),
            Row("【【舊地區】", Text, "地區（【舊地區）_JE")), ["Doc", "A】", "A", "Region", "【舊地區", "舊地區"]);

        Assert.Equal("A】", result.Draft.Gl.Mapping[GlMappingKeys.Description]);
        Assert.Equal(
            [
                new GlRdeFieldMetadata(FieldId(1), "Region", "地區", RdeFieldValueTypeNames.Text),
                new GlRdeFieldMetadata(FieldId(2), "【舊地區", "地區", RdeFieldValueTypeNames.Text)
            ],
            result.Draft.Gl.RdeFields);
        Assert.Empty(result.Unmatched);
    }

    // 借貸方判斷欄的欄名本身整格加了【】時，原樣留著的那一列 A 欄是「【DC】」；依它的欄位型態判斷借貸標誌。
    // 第二份另有來源欄「DC」改名成數字型態的攸關資料元素，A 欄也寫「【DC】」，仍依原樣留著、文字型態的那一列判斷。
    [Fact]
    public void Infer_DrCrColumnNamedWithBrackets_UsesItsFieldType()
    {
        var result = Infer(
            Gl(Row("【DC】", Number), Row("【傳票金額_JE】由系統產生 : 金額欄位【Amount】、借貸方判斷欄位【【DC】】，借方為「1」", Number, "傳票金額_JE")),
            ["Amount", "【DC】"]);
        var withRenamedDc = Infer(
            Gl(
                Row("【DC】", Number, "代碼_JE"),
                Row("【DC】", Text),
                Row("【傳票金額_JE】由系統產生 : 金額欄位【Amount】、借貸方判斷欄位【【DC】】，借方為「D」", Number, "傳票金額_JE")),
            ["DC", "【DC】", "Amount"]);

        Assert.Equal(GlAmountModeNames.Flag, result.Draft.Gl.AmountMode);
        Assert.Equal("【DC】", result.Draft.Gl.Mapping[GlMappingKeys.DrCr]);
        Assert.Equal("1", result.Draft.Gl.Mapping[GlMappingKeys.DrCrDebitCode]);
        Assert.Empty(result.Unmatched);
        Assert.Equal(GlAmountModeNames.Side, withRenamedDc.Draft.Gl.AmountMode);
        Assert.Equal("【DC】", withRenamedDc.Draft.Gl.Mapping[GlMappingKeys.DrCr]);
        Assert.Equal([new GlRdeFieldMetadata(FieldId(1), "DC", "代碼", RdeFieldValueTypeNames.Amount)], withRenamedDc.Draft.Gl.RdeFields);
        Assert.Empty(withRenamedDc.Unmatched);
    }

    // 攸關資料元素的「（來源欄名）」照寫出端的規則反推：名稱有兩列以上相同，或加 _JE 剛好是標準欄名，才是補上的後綴。
    [Fact]
    public void Infer_RelevantDataElementSourceSuffix_IsRemovedOnlyWhenTheWriterWouldHaveAddedIt()
    {
        var result = Infer(Gl(
            Row("【Memo 2】", Text, "傳票摘要（Memo 2）_JE"),
            Row("【Region】", Text, "地區（Region）_JE")), ["Memo 2", "Region"]);

        Assert.Equal(
            [
                new GlRdeFieldMetadata(FieldId(1), "Memo 2", "傳票摘要", RdeFieldValueTypeNames.Text),
                new GlRdeFieldMetadata(FieldId(2), "Region", "地區（Region）", RdeFieldValueTypeNames.Text)
            ],
            result.Draft.Gl.RdeFields);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Infer_TbSectionWithoutAnyMappedRow_ReturnsNoTbDraft()
    {
        var result = Infer(
            Sheet([Row("Account", Text), Row("Amount", Number)], [Row("Doc No", Text, "傳票號碼_JE")]),
            ["Doc No"],
            ["Account", "Amount"]);

        Assert.Null(result.Draft.Tb);
        Assert.Empty(result.Unmatched);
    }

    [Theory]
    [InlineData("試算表變動金額_TB 由系統產生 : (End Dr - End Cr) - (Begin Dr-Begin Cr)")]
    [InlineData("【試算表變動金額_TB】由系統產生 : (【End Dr】 - 【End Cr】) - (【Begin Dr】-【Begin Cr】)")]
    public void Infer_TbOpeningAndClosingBySide_MapsTheFourColumns(string sentence)
    {
        var tbColumns = new[] { "Account", "Account Name", "End Dr", "End Cr", "Begin Dr", "Begin Cr" };
        var result = Infer(
            Sheet(
                [
                    Row("Account", Text, "會計科目編號_TB"),
                    Row("【Account Name】", Text, "會計科目名稱_TB"),
                    Row(sentence, Number, "試算表變動金額_TB")
                ],
                []),
            [],
            tbColumns);

        Assert.NotNull(result.Draft.Tb);
        Assert.Equal(TbChangeModeNames.OpenCloseBySide, result.Draft.Tb.ChangeMode);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccountNumber] = "Account",
                [TbMappingKeys.AccountName] = "Account Name",
                [TbMappingKeys.ClosingDebitBalance] = "End Dr",
                [TbMappingKeys.ClosingCreditBalance] = "End Cr",
                [TbMappingKeys.OpeningDebitBalance] = "Begin Dr",
                [TbMappingKeys.OpeningCreditBalance] = "Begin Cr"
            },
            result.Draft.Tb.Mapping);
        Assert.Empty(result.Unmatched);
    }

    [Theory]
    [InlineData("試算表變動金額_TB 由系統產生 : End - Begin", "End - Begin")]
    [InlineData("【試算表變動金額_TB】由系統產生 : 【Debit】 - 【Credit】", "【Debit】 - 【Credit】")]
    public void Infer_TbTwoColumnDifference_ListsTheChangeAmountBecauseTheMeaningIsUnknown(string sentence, string expression)
    {
        var result = Infer(
            Sheet([Row("Account", Text, "會計科目編號_TB"), Row(sentence, Number, "試算表變動金額_TB")], []),
            [],
            ["Account", "End", "Begin", "Debit", "Credit"]);

        Assert.NotNull(result.Draft.Tb);
        Assert.Equal(TbChangeModeNames.Direct, result.Draft.Tb.ChangeMode);
        Assert.Equal(
            new Dictionary<string, string> { [TbMappingKeys.AccountNumber] = "Account" },
            result.Draft.Tb.Mapping);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(new UnmatchedMappingField("tb", TbMappingKeys.ChangeAmount, "年度變動金額", expression), unmatched);
    }

    [Theory]
    [InlineData("Movement")]
    [InlineData("【Movement】")]
    public void Infer_TbChangeAmountRenamedInPlace_IsADirectChange(string before)
    {
        var result = Infer(Sheet([Row(before, Number, "試算表變動金額_TB")], []), [], ["Movement"]);

        Assert.NotNull(result.Draft.Tb);
        Assert.Equal(TbChangeModeNames.Direct, result.Draft.Tb.ChangeMode);
        Assert.Equal("Movement", result.Draft.Tb.Mapping[TbMappingKeys.ChangeAmount]);
    }

    [Fact]
    public void Infer_EmptyTbSectionOrNoTbImport_ReturnsNoTbDraft()
    {
        var withoutRows = Infer(Sheet([], [Row("Doc No", Text, "傳票號碼_JE")]), ["Doc No"], ["Account"]);
        var withoutImport = Infer(
            Sheet([Row("試算表變動金額_TB 由系統產生 : End - Begin", Number, "試算表變動金額_TB")], []),
            [],
            tbColumns: null);

        Assert.Null(withoutRows.Draft.Tb);
        Assert.Null(withoutImport.Draft.Tb);
        Assert.Empty(withoutImport.Unmatched);
    }

    [Fact]
    public void Infer_SourceNamesDifferingOnlyInWidthCaseOrSymbols_UseTheUniqueCurrentColumn()
    {
        var result = Infer(Gl(
            Row("Doc No", Text, "傳票號碼_JE"),
            Row("ＡＣＣＴ", Text, "會計科目編號_JE"),
            Row("Memo", Text, "傳票摘要_JE")), ["DOC_NO", "acct", "Memo", "MEMO"]);

        Assert.Equal("DOC_NO", result.Draft.Gl.Mapping[GlMappingKeys.VoucherNumber]);
        Assert.Equal("acct", result.Draft.Gl.Mapping[GlMappingKeys.AccountNumber]);
        // 完全相同的欄名優先，不看正規化後是否唯一。
        Assert.Equal("Memo", result.Draft.Gl.Mapping[GlMappingKeys.Description]);
    }

    [Fact]
    public void Infer_SourceNameMatchingSeveralCurrentColumnsAfterNormalizing_KeepsTheFileName()
    {
        var result = Infer(Gl(Row("Doc No", Text, "傳票號碼_JE")), ["DOC-NO", "doc_no"]);

        Assert.Equal("Doc No", result.Draft.Gl.Mapping[GlMappingKeys.VoucherNumber]);
    }

    [Fact]
    public void Infer_FullLegacyDraft_PassesTheMappingOptionRules()
    {
        string[] columns = [.. LegacyGlColumns, "DC", "Status", "Approve Date", "Region"];
        var result = Infer(Gl(
            Row("Doc No", Text, "傳票號碼_JE"),
            Row("Post Date", Date, "總帳日期_JE"),
            Row("Approve Date", Date, "傳票核准日_JE"),
            Row("Acct", Text, "會計科目編號_JE"),
            Row("Acct Name", Text, "會計科目名稱_JE"),
            Row("Memo", Text, "傳票摘要_JE"),
            Row("Manual Flag", Number, "人工傳票否_JE_S"),
            Row("Region", Text, "地區_JE"),
            Row("DC", Text),
            Row("Status", Text),
            Row("傳票金額_JE 由系統產生 : 金額欄位【Amount】、借貸方判斷欄位【DC】，借方為【D】", Number, "傳票金額_JE"),
            Row("Status ：過帳判斷值【Y】", Text, "過帳碼_JE")), columns);

        Assert.Empty(result.Unmatched);
        AssertDraftIsValid(result, columns);
    }

    private static void AssertDual(FieldInfoSheetMappingResult result, string debit, string credit)
    {
        Assert.Equal(GlAmountModeNames.Dual, result.Draft.Gl.AmountMode);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [GlMappingKeys.DebitAmount] = debit,
                [GlMappingKeys.CreditAmount] = credit
            },
            result.Draft.Gl.Mapping);
        Assert.Empty(result.Unmatched);
    }

    private static void AssertDraftIsValid(FieldInfoSheetMappingResult result, IReadOnlyList<string> columns)
    {
        var options = GlMappingOptionsRules.NormalizeAndValidate(
            result.Draft.Gl.Mapping,
            columns,
            result.Draft.Gl.ToOptions());
        GlMappingOptionsJsonCodec.Validate(options, result.Draft.Gl.Mapping);
    }

    private static Dictionary<string, string> ExpectedLegacyGlMapping() => new()
    {
        [GlMappingKeys.VoucherNumber] = "Doc No",
        [GlMappingKeys.LineItem] = "Line",
        [GlMappingKeys.GlDate] = "Post Date",
        [GlMappingKeys.AccountNumber] = "Acct",
        [GlMappingKeys.AccountName] = "Acct Name",
        [GlMappingKeys.Description] = "Memo",
        [GlMappingKeys.SourceModule] = "Module",
        [GlMappingKeys.CreatedBy] = "Maker",
        [GlMappingKeys.ApprovedBy] = "Checker",
        [GlMappingKeys.IsManual] = "Manual Flag",
        [GlMappingKeys.Amount] = "Amount"
    };

    private static FieldInfoSheetMappingResult Infer(
        FieldInfoSheet sheet,
        IReadOnlyList<string> glColumns,
        IReadOnlyList<string>? tbColumns = null)
    {
        var next = 0;
        return FieldInfoSheetMapping.Infer(sheet, glColumns, tbColumns, () => FieldId(++next));
    }

    private static string FieldId(int index) => "rde." + index.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

    private static FieldInfoSheet Gl(params FieldInfoSheetRow[] rows) => Sheet([], rows);

    private static FieldInfoSheet Sheet(FieldInfoSheetRow[] tb, FieldInfoSheetRow[] gl) => new(tb, gl);

    private static FieldInfoSheetRow Row(string before, string? fieldType, string? after = null) =>
        new(before, fieldType, after);
}
