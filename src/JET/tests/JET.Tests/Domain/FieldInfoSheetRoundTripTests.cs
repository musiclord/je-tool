using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 新版 JET 實際寫出的「自動化工具-檔案欄位資訊」，交給「由底稿匯入欄位」的推得規則（FieldInfoSheetMapping.Infer）後，
/// 推回的欄位配對要和當初確認的設定一致。每個案例照正式流程走一遍：
/// <list type="number">
/// <item>用合成的來源欄位定義（FieldInfoState，匯入時記下的每一欄欄名與型態）和確認配對的請求，
/// 經 JetAuditProgram.Plan 組出正式流程投影時用的 spec（GlMappingSpec、TbMappingSpec，也就是確認配對時選的來源欄與計算方式）。</item>
/// <item>FieldInfoProjector.ProjectGl、ProjectTb 投影成配對後的欄位定義，再照 SQLite 存進去再讀回的方式轉成 FieldInfo。</item>
/// <item>JetAuditProgram.ProjectFieldInfo 產生欄位資訊表的每一列，只取看得見的 A、B、E 欄組成 FieldInfoSheet。</item>
/// <item>FieldInfoSheetMapping.Infer 推得配對草稿（第三步帶入、還沒確認的配對），目前匯入的欄名用來源欄名清單。</item>
/// </list>
/// 預期值一律從輸入的 spec 推，不取 Infer 的輸出。刻意的差異照規則寫進預期，並在用到的地方註明。
/// 欄名與資料都是程式生成的合成值。
/// </summary>
public sealed class FieldInfoSheetRoundTripTests
{
    private const int MoneyScale = 100;
    private const LegacyFieldKind Text = LegacyFieldKind.Text;
    private const LegacyFieldKind Number = LegacyFieldKind.Number;
    private const LegacyFieldKind Date = LegacyFieldKind.Date;

    /// <summary>
    /// GL 四種金額方式：單一帶號金額、借貸兩欄、金額加借貸別、金額加借貸標誌。
    /// 借貸別與借貸標誌由「借貸方判斷欄位」的欄位型態決定：
    /// 借貸別欄的「欄位型態」是數字型態時算借貸標誌（flag），其他算借貸別（side）；兩者的計算相同。
    /// 後兩列是這條規則造成的刻意差異：判斷欄的型態和當初選的方式不一致時，推回的是依型態決定的方式。
    /// </summary>
    [Theory]
    [InlineData(GlAmountMode.SignedAmount, false, GlAmountModeNames.Signed)]
    [InlineData(GlAmountMode.DualAmount, false, GlAmountModeNames.Dual)]
    [InlineData(GlAmountMode.AmountWithSide, false, GlAmountModeNames.Side)]
    [InlineData(GlAmountMode.AmountWithFlag, true, GlAmountModeNames.Flag)]
    [InlineData(GlAmountMode.AmountWithSide, true, GlAmountModeNames.Flag)]
    [InlineData(GlAmountMode.AmountWithFlag, false, GlAmountModeNames.Side)]
    public void AmountModes_ComeBackWithTheSameAmountColumns(GlAmountMode mode, bool numericDrCrColumn, string expectedMode)
    {
        var source = Columns(StandardGlColumns(drCrKind: numericDrCrColumn ? Number : Text));
        var mapping = CoreGlMapping();
        switch (mode)
        {
            case GlAmountMode.SignedAmount:
                mapping[GlMappingKeys.Amount] = "Amount";
                break;
            case GlAmountMode.DualAmount:
                mapping[GlMappingKeys.DebitAmount] = "Debit";
                mapping[GlMappingKeys.CreditAmount] = "Credit";
                break;
            case GlAmountMode.AmountWithSide:
            case GlAmountMode.AmountWithFlag:
                // 借方、貸方代碼兩側都填；看得見的表只寫借方代碼，推回時沒有貸方代碼，見 AssertGlDraftMatchesSpec。
                mapping[GlMappingKeys.Amount] = "Amount";
                mapping[GlMappingKeys.DrCr] = "DC";
                mapping[GlMappingKeys.DrCrDebitCode] = mode == GlAmountMode.AmountWithFlag ? "1" : "D";
                mapping[GlMappingKeys.DrCrCreditCode] = mode == GlAmountMode.AmountWithFlag ? "2" : "C";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        var spec = PlanGl(source, mapping, mode);

        var outcome = WriteAndInfer(source, spec);

        AssertGlDraftMatchesSpec(outcome.Result.Draft.Gl, spec, expectedMode);
        Assert.Null(outcome.Result.Draft.Tb);
        Assert.Empty(outcome.Result.Unmatched);
    }

    /// <summary>
    /// 金額加借貸別只填一側代碼：只填借方或只填貸方，推回的都是當初填的那一側。
    /// 判斷欄與金額欄的名稱本身有括號時也一樣。
    /// </summary>
    [Theory]
    [InlineData("D", null, "Amount", "DC")]
    [InlineData(null, "C", "Amount", "DC")]
    [InlineData("D", null, "【調整】金額", "【舊】借貸")]
    public void SidedAmountWithOneCode_ComesBackWithThatCode(string? debitCode, string? creditCode, string amountColumn, string drCrColumn)
    {
        var source = Columns([.. StandardGlColumns(), (amountColumn, Number), (drCrColumn, Text)]);
        var mapping = CoreGlMapping();
        mapping[GlMappingKeys.Amount] = amountColumn;
        mapping[GlMappingKeys.DrCr] = drCrColumn;
        if (debitCode is not null)
        {
            mapping[GlMappingKeys.DrCrDebitCode] = debitCode;
        }

        if (creditCode is not null)
        {
            mapping[GlMappingKeys.DrCrCreditCode] = creditCode;
        }

        var spec = PlanGl(source, mapping, GlAmountMode.AmountWithSide);

        var outcome = WriteAndInfer(source, spec);

        AssertGlDraftMatchesSpec(outcome.Result.Draft.Gl, spec, GlAmountModeNames.Side);
        Assert.Empty(outcome.Result.Unmatched);
    }

    [Theory]
    [InlineData(GlAmountMode.AmountWithSide, "D", "C")]
    [InlineData(GlAmountMode.AmountWithFlag, "1", "2")]
    // 實際代碼剛好是配對鍵 drCrDebitCode 時（較早版本的 JET 在沒有借方代碼時，說明句的借方寫的就是這個配對鍵），明確寫出的雙側代碼仍須完整保留。
    [InlineData(GlAmountMode.AmountWithSide, "drCrDebitCode", "C")]
    public void SidedAmountWithBothCodes_ComesBackWithBothCodes(GlAmountMode mode, string debitCode, string creditCode)
    {
        var source = Columns(StandardGlColumns(drCrKind: mode == GlAmountMode.AmountWithFlag ? Number : Text));
        var mapping = CoreGlMapping();
        mapping[GlMappingKeys.Amount] = "Amount";
        mapping[GlMappingKeys.DrCr] = "DC";
        mapping[GlMappingKeys.DrCrDebitCode] = debitCode;
        mapping[GlMappingKeys.DrCrCreditCode] = creditCode;
        var spec = PlanGl(source, mapping, mode);

        var outcome = WriteAndInfer(source, spec);

        Assert.Equal(debitCode, outcome.Result.Draft.Gl.Mapping[GlMappingKeys.DrCrDebitCode]);
        Assert.Equal(creditCode, outcome.Result.Draft.Gl.Mapping[GlMappingKeys.DrCrCreditCode]);
        Assert.Empty(outcome.Result.Unmatched);
    }

    /// <summary>
    /// 來源欄名本身有括號：過帳狀態的來源欄「【舊】狀態」，以及兩個名稱都叫「地區」、其中一個來源欄是「【舊】地區」的攸關資料元素。
    /// 新版的配對前欄位名稱與說明句會出現兩層括號，推回的仍是原本的來源欄與名稱。
    /// </summary>
    [Fact]
    public void SourceColumnNamesWithBrackets_ComeBackTheSame()
    {
        var source = Columns([.. StandardGlColumns(), ("【舊】狀態", Text), ("【舊】地區", Text), ("Region", Text)]);
        var mapping = CoreGlMapping();
        mapping[GlMappingKeys.Amount] = "Amount";
        mapping[GlMappingKeys.PostingStatus] = "【舊】狀態";
        var options = GlMappingOptions.NormalizeLegacy(mapping) with
        {
            RdeFields =
            [
                new GlRdeFieldMetadata(SpecFieldId(1), "【舊】地區", "地區", RdeFieldValueTypeNames.Text),
                new GlRdeFieldMetadata(SpecFieldId(2), "Region", "地區", RdeFieldValueTypeNames.Text)
            ]
        };
        var spec = PlanGl(source, mapping, GlAmountMode.SignedAmount, new GlPostingStatusPolicy(["Y"], false), options);

        var outcome = WriteAndInfer(source, spec);

        Assert.Contains(outcome.Sheet.Gl, static row => row.BeforeName == "【【舊】地區】" && row.AfterName == "地區（【舊】地區）_JE");
        AssertGlDraftMatchesSpec(outcome.Result.Draft.Gl, spec, GlAmountModeNames.Signed);
        Assert.Empty(outcome.Result.Unmatched);
    }

    /// <summary>
    /// 攸關資料元素的名稱本身就以「（來源欄名）」結尾時，新版不補後綴；去掉這段後的名稱沒有和其他列相同、加 _JE 也不是標準欄名，
    /// 推回的名稱照原樣。符合其中一種情形時推回的是去掉後的名稱，這是已知限制。
    /// </summary>
    [Fact]
    public void RelevantDataElementNamedWithItsSourceInBrackets_ComesBackWithTheSameName()
    {
        var source = Columns([.. StandardGlColumns(), ("Region", Text)]);
        var mapping = CoreGlMapping();
        mapping[GlMappingKeys.Amount] = "Amount";
        var options = GlMappingOptions.NormalizeLegacy(mapping) with
        {
            RdeFields = [new GlRdeFieldMetadata(SpecFieldId(1), "Region", "地區（Region）", RdeFieldValueTypeNames.Text)]
        };
        var spec = PlanGl(source, mapping, GlAmountMode.SignedAmount, options: options);

        var outcome = WriteAndInfer(source, spec);

        AssertGlDraftMatchesSpec(outcome.Result.Draft.Gl, spec, GlAmountModeNames.Signed);
        Assert.Empty(outcome.Result.Unmatched);
    }

    /// <summary>
    /// 來源欄名有儲存格裡的換行（匯入時只去頭尾空白）：過帳狀態、借貸兩欄與金額加借貸別的說明句都照樣推回來源欄。
    /// </summary>
    [Theory]
    [InlineData(GlAmountMode.DualAmount)]
    [InlineData(GlAmountMode.AmountWithSide)]
    public void SourceColumnNamesWithLineBreaks_ComeBackTheSame(GlAmountMode mode)
    {
        var source = Columns([.. StandardGlColumns(), ("Status\nCode", Text), ("借方\n金額", Number), ("貸方\n金額", Number), ("借\n貸", Text)]);
        var mapping = CoreGlMapping();
        mapping[GlMappingKeys.PostingStatus] = "Status\nCode";
        if (mode == GlAmountMode.DualAmount)
        {
            mapping[GlMappingKeys.DebitAmount] = "借方\n金額";
            mapping[GlMappingKeys.CreditAmount] = "貸方\n金額";
        }
        else
        {
            mapping[GlMappingKeys.Amount] = "借方\n金額";
            mapping[GlMappingKeys.DrCr] = "借\n貸";
            mapping[GlMappingKeys.DrCrDebitCode] = "D";
        }

        var spec = PlanGl(source, mapping, mode, new GlPostingStatusPolicy(["Y"], true));

        var outcome = WriteAndInfer(source, spec);

        AssertGlDraftMatchesSpec(
            outcome.Result.Draft.Gl,
            spec,
            mode == GlAmountMode.DualAmount ? GlAmountModeNames.Dual : GlAmountModeNames.Side);
        Assert.Empty(outcome.Result.Unmatched);
    }

    /// <summary>
    /// 來源欄名本身的括號不成對：「A】」配給傳票摘要，兩個名稱都叫「地區」的攸關資料元素，其中一個來源欄是「【舊地區」。
    /// 目前資料另有「A」「舊地區」，模糊比對分不出來，要靠去掉最外層括號對回原本的來源欄。
    /// 另有一個借貸方判斷欄整格加【】、型態是數字的「【DC】」，推回的是借貸標誌。
    /// </summary>
    [Fact]
    public void SourceColumnNamesWithUnpairedOrWholeBrackets_ComeBackTheSame()
    {
        var source = Columns([
            .. StandardGlColumns(),
            ("A】", Text),
            ("A", Text),
            ("【舊地區", Text),
            ("舊地區", Text),
            ("Region", Text),
            ("【DC】", Number)
        ]);
        var mapping = CoreGlMapping();
        mapping[GlMappingKeys.Description] = "A】";
        mapping[GlMappingKeys.Amount] = "Amount";
        mapping[GlMappingKeys.DrCr] = "【DC】";
        mapping[GlMappingKeys.DrCrDebitCode] = "1";
        var options = GlMappingOptions.NormalizeLegacy(mapping) with
        {
            RdeFields =
            [
                new GlRdeFieldMetadata(SpecFieldId(1), "【舊地區", "地區", RdeFieldValueTypeNames.Text),
                new GlRdeFieldMetadata(SpecFieldId(2), "Region", "地區", RdeFieldValueTypeNames.Text)
            ]
        };
        var spec = PlanGl(source, mapping, GlAmountMode.AmountWithFlag, options: options);

        var outcome = WriteAndInfer(source, spec);

        Assert.Contains(outcome.Sheet.Gl, static row => row.BeforeName == "【【舊地區】" && row.AfterName == "地區（【舊地區）_JE");
        AssertGlDraftMatchesSpec(outcome.Result.Draft.Gl, spec, GlAmountModeNames.Flag);
        Assert.Empty(outcome.Result.Unmatched);
    }

    /// <summary>
    /// 過帳狀態：來源欄照原樣留著，另外複製出「過帳碼_JE」，說明句寫來源欄與過帳判斷值（算已過帳的過帳狀態值）；
    /// 審計員勾了「空白也算已過帳」時句尾寫明。最後一列是只有空白算已過帳、沒有其他值。
    /// </summary>
    [Theory]
    [InlineData(new[] { "Y" }, false)]
    [InlineData(new[] { "P", "A" }, false)]
    [InlineData(new[] { "Y" }, true)]
    [InlineData(new string[0], true)]
    public void PostingStatus_ComesBackWithTheSameColumnValuesAndBlankChoice(string[] acceptedValues, bool includeBlank)
    {
        var source = Columns(StandardGlColumns());
        var mapping = CoreGlMapping();
        mapping[GlMappingKeys.Amount] = "Amount";
        mapping[GlMappingKeys.PostingStatus] = "Status";
        var spec = PlanGl(source, mapping, GlAmountMode.SignedAmount, new GlPostingStatusPolicy(acceptedValues, includeBlank));

        var outcome = WriteAndInfer(source, spec);

        // 確認這個案例真的寫出了系統產生的過帳碼_JE。
        Assert.Contains(outcome.Sheet.Gl, row => row.AfterName == "過帳碼_JE" && row.BeforeName.StartsWith("【Status】過帳判斷值", StringComparison.Ordinal));
        AssertGlDraftMatchesSpec(outcome.Result.Draft.Gl, spec, GlAmountModeNames.Signed);
        Assert.Empty(outcome.Result.Unmatched);
    }

    /// <summary>
    /// 傳票核准日三種模式：配對來源欄、與總帳日期相同、不配對。第二列是核准日和總帳日期選同一個來源欄
    /// （同一個來源欄可以配給多個欄位），投影時核准日改成追加在後面的欄位。
    /// </summary>
    [Theory]
    [InlineData(ApprovalDateModeNames.Mapped, "Approve Date")]
    [InlineData(ApprovalDateModeNames.Mapped, "Post Date")]
    [InlineData(ApprovalDateModeNames.SameAsGlDate, null)]
    [InlineData(ApprovalDateModeNames.Unmapped, null)]
    public void ApprovalDateModes_ComeBackTheSame(string approvalDateMode, string? approvalColumn)
    {
        var source = Columns(StandardGlColumns());
        var mapping = CoreGlMapping();
        mapping[GlMappingKeys.Amount] = "Amount";
        if (approvalColumn is not null)
        {
            mapping[GlMappingKeys.ApprovalDate] = approvalColumn;
        }

        var options = GlMappingOptions.NormalizeLegacy(mapping) with { ApprovalDateMode = approvalDateMode };
        var spec = PlanGl(source, mapping, GlAmountMode.SignedAmount, options: options);

        var outcome = WriteAndInfer(source, spec);

        Assert.Equal(approvalDateMode, spec.Options.ApprovalDateMode);
        Assert.Equal(approvalDateMode != ApprovalDateModeNames.Unmapped, outcome.Sheet.Gl.Any(static row => row.AfterName == "傳票核准日_JE"));
        AssertGlDraftMatchesSpec(outcome.Result.Draft.Gl, spec, GlAmountModeNames.Signed);
        Assert.Empty(outcome.Result.Unmatched);
    }

    /// <summary>
    /// 數字型的傳票號碼與會計科目編號：原欄照 IDEA 的叫法留成「_Temp 原欄」（配對後欄位名稱空白），
    /// 另外追加轉成文字的欄位寫標準欄名。推回時只看追加的那一列，配對仍是原本的來源欄。
    /// </summary>
    [Fact]
    public void NumericVoucherAndAccountNumbers_LeaveTempColumnsAndComeBackAsTheSourceColumns()
    {
        var source = Columns(StandardGlColumns(voucherKind: Number, accountKind: Number));
        var mapping = CoreGlMapping();
        mapping[GlMappingKeys.Amount] = "Amount";
        var spec = PlanGl(source, mapping, GlAmountMode.SignedAmount);

        var outcome = WriteAndInfer(source, spec);

        Assert.Contains(outcome.GlTarget, static state => state.FieldName == "傳票號碼_JE_Temp" && state.Origin == FieldInfoOrigin.Temp);
        Assert.Contains(outcome.GlTarget, static state => state.FieldName == "會計科目編號_JE_Temp" && state.Origin == FieldInfoOrigin.Temp);
        Assert.Contains(new FieldInfoSheetRow("【Doc No】", "文字型態", null), outcome.Sheet.Gl);
        Assert.Contains(new FieldInfoSheetRow("【Doc No】", "文字型態", "傳票號碼_JE"), outcome.Sheet.Gl);
        Assert.Contains(new FieldInfoSheetRow("【Acct】", "文字型態", null), outcome.Sheet.Gl);
        Assert.Contains(new FieldInfoSheetRow("【Acct】", "文字型態", "會計科目編號_JE"), outcome.Sheet.Gl);
        AssertGlDraftMatchesSpec(outcome.Result.Draft.Gl, spec, GlAmountModeNames.Signed);
        Assert.Empty(outcome.Result.Unmatched);
    }

    /// <summary>
    /// 傳票文件項次：有配對時推回原來源欄；沒有配對時系統依列序自動編號，寫出一列 A 欄和 E 欄都是
    /// 「傳票文件項次_JE_S」、沒有【】的系統產生欄位，新版這種列不算配對。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LineItem_MappedComesBackAndTheAutoNumberIsNotAMapping(bool mapped)
    {
        var source = Columns(StandardGlColumns());
        var mapping = CoreGlMapping();
        mapping[GlMappingKeys.Amount] = "Amount";
        if (mapped)
        {
            mapping[GlMappingKeys.LineItem] = "Line";
        }

        var spec = PlanGl(source, mapping, GlAmountMode.SignedAmount);

        var outcome = WriteAndInfer(source, spec);

        var autoNumberRow = new FieldInfoSheetRow("傳票文件項次_JE_S", "數字型態", "傳票文件項次_JE_S");
        Assert.Equal(!mapped, outcome.Sheet.Gl.Contains(autoNumberRow));
        AssertGlDraftMatchesSpec(outcome.Result.Draft.Gl, spec, GlAmountModeNames.Signed);
        Assert.Empty(outcome.Result.Unmatched);
    }

    /// <summary>
    /// 人工傳票否：來源欄照樣推回。代碼照舊版，「1」是人工、「0」是自動；
    /// 第二列當初選了其他代碼，推回時仍是 1 與 0，這是刻意的差異（欄位資訊表上沒有寫人工與自動的代碼）。
    /// </summary>
    [Theory]
    [InlineData(new[] { "1" }, new[] { "0" })]
    [InlineData(new[] { "Y", "M" }, new[] { "N" })]
    public void ManualColumn_ComesBackWithTheLegacyCodes(string[] manualValues, string[] automaticValues)
    {
        var source = Columns(StandardGlColumns());
        var mapping = CoreGlMapping();
        mapping[GlMappingKeys.Amount] = "Amount";
        mapping[GlMappingKeys.IsManual] = "Manual Flag";
        var options = GlMappingOptions.NormalizeLegacy(mapping) with
        {
            ManualAutoPolicy = new GlManualAutoPolicy(manualValues, automaticValues)
        };
        var spec = PlanGl(source, mapping, GlAmountMode.SignedAmount, options: options);

        var outcome = WriteAndInfer(source, spec);

        Assert.Equal(manualValues, spec.Options.ManualAutoPolicy.ManualValues);
        Assert.Contains(new FieldInfoSheetRow("【Manual Flag】", "數字型態", "人工傳票否_JE_S"), outcome.Sheet.Gl);
        AssertGlDraftMatchesSpec(outcome.Result.Draft.Gl, spec, GlAmountModeNames.Signed);
        Assert.Empty(outcome.Result.Unmatched);
    }

    /// <summary>
    /// 攸關資料元素欄位（欄位配對時勾選保留、可用於條件的其他來源欄）：
    /// 名稱、值型別與來源欄都推回，識別碼由推得規則新產生。涵蓋三種值型別、兩個名稱相同但來源欄不同
    /// （輸出欄名寫成「地區（Region）_JE」），以及名稱加 _JE 剛好是標準欄名（寫成「傳票摘要（Memo 2）_JE」）。
    /// spec 裡攸關資料元素的順序和來源欄的順序相同。
    /// </summary>
    [Fact]
    public void RelevantDataElements_ComeBackWithTheirLabelsValueTypesAndSourceColumns()
    {
        var source = Columns([
            .. StandardGlColumns(),
            ("Region", Text),
            ("Region 2", Text),
            ("Due", Date),
            ("Cost", Number),
            ("Memo 2", Text)
        ]);
        var mapping = CoreGlMapping();
        mapping[GlMappingKeys.Amount] = "Amount";
        var options = GlMappingOptions.NormalizeLegacy(mapping) with
        {
            RdeFields =
            [
                new GlRdeFieldMetadata(SpecFieldId(1), "Region", "地區", RdeFieldValueTypeNames.Text),
                new GlRdeFieldMetadata(SpecFieldId(2), "Region 2", "地區", RdeFieldValueTypeNames.Text),
                new GlRdeFieldMetadata(SpecFieldId(3), "Due", "到期日", RdeFieldValueTypeNames.Date),
                new GlRdeFieldMetadata(SpecFieldId(4), "Cost", "成本", RdeFieldValueTypeNames.Amount),
                new GlRdeFieldMetadata(SpecFieldId(5), "Memo 2", "傳票摘要", RdeFieldValueTypeNames.Text)
            ]
        };
        var spec = PlanGl(source, mapping, GlAmountMode.SignedAmount, options: options);

        var outcome = WriteAndInfer(source, spec);

        Assert.Equal(
            ["地區（Region）_JE", "地區（Region 2）_JE", "到期日_JE", "成本_JE", "傳票摘要（Memo 2）_JE"],
            outcome.Sheet.Gl
                .Where(static row => row.AfterName is { } after
                                     && after.EndsWith("_JE", StringComparison.Ordinal)
                                     && row.BeforeName is "【Region】" or "【Region 2】" or "【Due】" or "【Cost】" or "【Memo 2】")
                .Select(static row => row.AfterName));
        AssertGlDraftMatchesSpec(outcome.Result.Draft.Gl, spec, GlAmountModeNames.Signed);
        Assert.Empty(outcome.Result.Unmatched);
    }

    /// <summary>
    /// 來源檔本來就有 JET 輸出欄名的欄位時，投影會把沒有配對的原始來源欄讓名成「原名（來源）」，
    /// 這種列的配對後欄位名稱空白，不算配對。涵蓋：
    /// 沒有配對的「傳票摘要_JE」（傳票摘要配給 Memo）、「傳票文件項次_JE_S」（傳票文件項次自動編號）、
    /// 「傳票金額_JE」（金額配給 Amount）、「地區_JE」（攸關資料元素「地區」配給 Area）；
    /// 過帳狀態的來源欄就叫「過帳碼_JE」，來源欄被讓名，說明句仍寫原名；傳票號碼的來源欄就叫「傳票號碼_JE」，在原位置改名、不讓名。
    /// </summary>
    [Fact]
    public void SourceColumnsAlreadyNamedLikeJetOutputs_AreYieldedAndTheMappingStillComesBack()
    {
        var source = Columns([
            ("傳票號碼_JE", Text),
            ("Post Date", Date),
            ("Acct", Text),
            ("Acct Name", Text),
            ("Memo", Text),
            ("傳票摘要_JE", Text),
            ("傳票文件項次_JE_S", Number),
            ("Amount", Number),
            ("傳票金額_JE", Number),
            ("過帳碼_JE", Text),
            ("Area", Text),
            ("地區_JE", Text)
        ]);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.VoucherNumber] = "傳票號碼_JE",
            [GlMappingKeys.GlDate] = "Post Date",
            [GlMappingKeys.AccountNumber] = "Acct",
            [GlMappingKeys.AccountName] = "Acct Name",
            [GlMappingKeys.Description] = "Memo",
            [GlMappingKeys.Amount] = "Amount",
            [GlMappingKeys.PostingStatus] = "過帳碼_JE"
        };
        var options = GlMappingOptions.NormalizeLegacy(mapping) with
        {
            RdeFields = [new GlRdeFieldMetadata(SpecFieldId(1), "Area", "地區", RdeFieldValueTypeNames.Text)]
        };
        var spec = PlanGl(source, mapping, GlAmountMode.SignedAmount, new GlPostingStatusPolicy(["Y"], false), options);

        var outcome = WriteAndInfer(source, spec);

        // 確認這個案例真的讓名：讓名的原始來源欄寫「原名（來源）」，配對後欄位名稱空白。
        foreach (var yielded in new[] { "傳票摘要_JE", "傳票文件項次_JE_S", "傳票金額_JE", "過帳碼_JE", "地區_JE" })
        {
            Assert.Contains(outcome.Sheet.Gl, row => row.BeforeName == yielded + FieldInfoProjector.SourceNameSuffix && row.AfterName is null);
        }

        Assert.Contains(new FieldInfoSheetRow("【傳票號碼_JE】", "文字型態", "傳票號碼_JE"), outcome.Sheet.Gl);
        AssertGlDraftMatchesSpec(outcome.Result.Draft.Gl, spec, GlAmountModeNames.Signed);
        Assert.Empty(outcome.Result.Unmatched);
    }

    /// <summary>
    /// TB 直接變動金額，以及期末與期初各分借貸：說明句看得出每一欄的角色，推回和當初相同。
    /// TB 的會計科目編號是數字型，也會留下「會計科目編號_TB_Temp」原欄。
    /// </summary>
    [Theory]
    [InlineData(TbChangeMode.DirectChange)]
    [InlineData(TbChangeMode.OpenCloseBySide)]
    public void TbChangeModesThatNameEachColumn_ComeBackTheSame(TbChangeMode changeMode)
    {
        var glSource = Columns(StandardGlColumns());
        var glMapping = CoreGlMapping();
        glMapping[GlMappingKeys.Amount] = "Amount";
        var glSpec = PlanGl(glSource, glMapping, GlAmountMode.SignedAmount);
        var tbSource = Columns(TbColumns);
        var tbSpec = PlanTb(tbSource, TbMapping(changeMode), changeMode);

        var outcome = WriteAndInfer(glSource, glSpec, tbSource, tbSpec);

        Assert.Contains(new FieldInfoSheetRow("【Account】", "文字型態", null), outcome.Sheet.Tb);
        AssertGlDraftMatchesSpec(outcome.Result.Draft.Gl, glSpec, GlAmountModeNames.Signed);
        Assert.NotNull(outcome.Result.Draft.Tb);
        Assert.Equal(tbSpec.Mapping, outcome.Result.Draft.Tb.Mapping);
        Assert.Equal(TbChangeModeNames.ToWireName(tbSpec.ChangeMode), outcome.Result.Draft.Tb.ChangeMode);
        Assert.Empty(outcome.Result.Unmatched);
    }

    /// <summary>
    /// TB 借減貸與期末減期初，說明句都寫成兩欄相減「【A】 - 【B】」。
    /// TB 只有兩欄相減時，看不出是借減貸還是期末減期初，不帶入金額，列出來請審計員選。
    /// 所以預期是 spec 去掉這兩欄，另列一項「年度變動金額」，來源寫說明句冒號後的算式。
    /// 草稿的金額方式不是 spec 的設定，這裡不比對；推得規則給的值另見 FieldInfoSheetMappingTests。
    /// </summary>
    [Theory]
    [InlineData(TbChangeMode.DebitCredit, TbMappingKeys.DebitAmount, TbMappingKeys.CreditAmount)]
    [InlineData(TbChangeMode.OpenClose, TbMappingKeys.ClosingBalance, TbMappingKeys.OpeningBalance)]
    public void TbTwoColumnDifference_ListsTheChangeAmountForTheAuditor(
        TbChangeMode changeMode,
        string minuendKey,
        string subtrahendKey)
    {
        var glSource = Columns(StandardGlColumns());
        var glMapping = CoreGlMapping();
        glMapping[GlMappingKeys.Amount] = "Amount";
        var glSpec = PlanGl(glSource, glMapping, GlAmountMode.SignedAmount);
        var tbSource = Columns(TbColumns);
        var tbSpec = PlanTb(tbSource, TbMapping(changeMode), changeMode);

        var outcome = WriteAndInfer(glSource, glSpec, tbSource, tbSpec);

        AssertGlDraftMatchesSpec(outcome.Result.Draft.Gl, glSpec, GlAmountModeNames.Signed);
        Assert.NotNull(outcome.Result.Draft.Tb);
        Assert.Equal(
            tbSpec.Mapping
                .Where(pair => pair.Key != minuendKey && pair.Key != subtrahendKey)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            outcome.Result.Draft.Tb.Mapping);
        Assert.Equal(
            [
                new UnmatchedMappingField(
                    "tb",
                    TbMappingKeys.ChangeAmount,
                    "年度變動金額",
                    $"【{tbSpec.Mapping[minuendKey]}】 - 【{tbSpec.Mapping[subtrahendKey]}】")
            ],
            outcome.Result.Unmatched);
    }

    /// <summary>
    /// GL 草稿和 spec 逐項比對。只有刻意的差異照規則調整，其餘一律等於 spec：
    /// <list type="bullet">
    /// <item>借貸代碼完整帶回，包含兩側都有填的情形。</item>
    /// <item>人工傳票否照舊版，「1」是人工、「0」是自動，不看當初選的代碼。</item>
    /// <item>攸關資料元素的識別碼由後端新產生，這裡依序是 InferredFieldId(1)、InferredFieldId(2)…；名稱、來源欄與值型別照 spec。</item>
    /// </list>
    /// 金額方式由呼叫端給，因為借貸別與借貸標誌依判斷欄的欄位型態決定。
    /// </summary>
    private static void AssertGlDraftMatchesSpec(GlMappingDraftMetadata draft, GlMappingSpec spec, string expectedAmountMode)
    {
        Assert.Equal(spec.Mapping, draft.Mapping);
        Assert.Equal(expectedAmountMode, draft.AmountMode);
        Assert.Equal(spec.Options.ApprovalDateMode, draft.ApprovalDateMode);
        if (spec.Options.PostingStatusPolicy is { } policy)
        {
            Assert.NotNull(draft.PostingStatusPolicy);
            Assert.Equal(policy.AcceptedValues, draft.PostingStatusPolicy.AcceptedValues);
            Assert.Equal(policy.IncludeBlank, draft.PostingStatusPolicy.IncludeBlank);
        }
        else
        {
            Assert.Null(draft.PostingStatusPolicy);
        }

        Assert.Equal(["1"], draft.ManualAutoPolicy.ManualValues);
        Assert.Equal(["0"], draft.ManualAutoPolicy.AutomaticValues);
        Assert.Null(draft.ManualAutoPolicy.UnlistedValueKind);
        Assert.Null(draft.ManualAutoPolicy.BlankValueKind);
        Assert.Equal(
            spec.Options.RdeFields.Select(static (field, index) => field with { FieldId = InferredFieldId(index + 1) }).ToArray(),
            draft.RdeFields.ToArray());
        Assert.Equal(spec.Options.FillDownFields, draft.FillDownFields);
    }

    /// <summary>
    /// 照正式流程寫出欄位資訊表，再交給推得規則：投影、存進資料庫再讀回、產生欄位資訊列、只取 A、B、E 欄、推得草稿。
    /// 沒有 TB 時，欄位資訊表的 TB 段是空的，目前匯入的 TB 欄名是 null（還沒匯入 TB）。
    /// </summary>
    private static WriteAndInferOutcome WriteAndInfer(
        IReadOnlyList<FieldInfoState> glSource,
        GlMappingSpec glSpec,
        IReadOnlyList<FieldInfoState>? tbSource = null,
        TbMappingSpec? tbSpec = null)
    {
        var glTarget = FieldInfoProjector.ProjectGl(glSource, glSpec, MoneyScale);
        IReadOnlyList<FieldInfoState> tbTarget = tbSpec is null
            ? []
            : FieldInfoProjector.ProjectTb(tbSource ?? throw new ArgumentNullException(nameof(tbSource)), tbSpec, MoneyScale);
        var projection = JetAuditProgram.ProjectFieldInfo(
            tbTarget.Select(ReadBackAsFieldInfo).ToArray(),
            glTarget.Select(ReadBackAsFieldInfo).ToArray());
        var sheet = new FieldInfoSheet(
            projection.TbRows.Select(VisibleColumns).ToArray(),
            projection.GlRows.Select(VisibleColumns).ToArray());
        var next = 0;
        var result = FieldInfoSheetMapping.Infer(
            sheet,
            Names(glSource),
            tbSpec is null ? null : Names(tbSource!),
            () => InferredFieldId(++next));
        return new WriteAndInferOutcome(glTarget, sheet, result);
    }

    /// <summary>
    /// 正式流程把投影結果存進 field_info，底稿再經 LocalFieldInfoFactsPort.ReadAsync 讀回成 FieldInfo
    /// （src/JET/JET/Infrastructure/Persistence/Local/LocalFieldInfoPersistence.cs）。存檔時文字長度只有文字型態才寫，
    /// 讀回時只有文字型態帶文字長度；序號、欄名、說明、型態、小數位數、來歷與對應的 JET 欄位照抄。這裡照同樣的對應轉換。
    /// </summary>
    private static FieldInfo ReadBackAsFieldInfo(FieldInfoState state) => new(
        state.Ordinal,
        state.FieldName,
        state.Description,
        state.Kind,
        state.Kind == LegacyFieldKind.Text ? state.TextLength : null,
        state.DecimalPlaces,
        state.Origin,
        state.JetField);

    /// <summary>
    /// 讀檔只取看得見的 A 欄「配對前欄位名稱」、B 欄「欄位型態」與 E 欄「配對後欄位名稱」，照儲存格原文
    /// （OpenXmlMappingMetadataReader.ReadSection）；底稿寫這三欄的是 WorkingPaperWriter 的 WriteCanonicalFieldInfoRow。
    /// </summary>
    private static FieldInfoSheetRow VisibleColumns(FieldInfoRow row) =>
        new(row.DisplayName, row.FieldType, row.ActualFieldName);

    private static GlMappingSpec PlanGl(
        IReadOnlyList<FieldInfoState> source,
        IReadOnlyDictionary<string, string> mapping,
        GlAmountMode amountMode,
        GlPostingStatusPolicy? postingStatusPolicy = null,
        GlMappingOptions? options = null) =>
        JetAuditProgram.Plan(new GlMappingRequest(
            "project",
            "batch",
            mapping,
            amountMode,
            Names(source),
            MoneyScale,
            DateParseOptions.Default)
        {
            PostingStatusPolicy = postingStatusPolicy,
            ProjectionOptions = options
        }).Spec;

    private static TbMappingSpec PlanTb(
        IReadOnlyList<FieldInfoState> source,
        IReadOnlyDictionary<string, string> mapping,
        TbChangeMode changeMode) =>
        JetAuditProgram.Plan(new TbMappingRequest(
            "project",
            "batch",
            mapping,
            changeMode,
            Names(source),
            MoneyScale,
            DateTimeOffset.UnixEpoch)).Spec;

    /// <summary>GL 必填欄位與幾個選填欄位的配對；金額另外加。</summary>
    private static Dictionary<string, string> CoreGlMapping() => new(StringComparer.Ordinal)
    {
        [GlMappingKeys.VoucherNumber] = "Doc No",
        [GlMappingKeys.GlDate] = "Post Date",
        [GlMappingKeys.AccountNumber] = "Acct",
        [GlMappingKeys.AccountName] = "Acct Name",
        [GlMappingKeys.Description] = "Memo",
        [GlMappingKeys.SourceModule] = "Module",
        [GlMappingKeys.CreatedBy] = "Maker",
        [GlMappingKeys.ApprovedBy] = "Checker"
    };

    private static (string Name, LegacyFieldKind Kind)[] StandardGlColumns(
        LegacyFieldKind voucherKind = Text,
        LegacyFieldKind accountKind = Text,
        LegacyFieldKind drCrKind = Text) =>
    [
        ("Doc No", voucherKind),
        ("Line", Number),
        ("Post Date", Date),
        ("Approve Date", Date),
        ("Acct", accountKind),
        ("Acct Name", Text),
        ("Memo", Text),
        ("Module", Text),
        ("Maker", Text),
        ("Checker", Text),
        ("Manual Flag", Text),
        ("Status", Text),
        ("Amount", Number),
        ("Debit", Number),
        ("Credit", Number),
        ("DC", drCrKind),
        ("Unused", Text)
    ];

    private static readonly (string Name, LegacyFieldKind Kind)[] TbColumns =
    [
        ("Account", Number),
        ("Account Name", Text),
        ("Movement", Number),
        ("Dr", Number),
        ("Cr", Number),
        ("Begin", Number),
        ("End", Number),
        ("Begin Dr", Number),
        ("Begin Cr", Number),
        ("End Dr", Number),
        ("End Cr", Number),
        ("Unused", Text)
    ];

    private static Dictionary<string, string> TbMapping(TbChangeMode changeMode)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TbMappingKeys.AccountNumber] = "Account",
            [TbMappingKeys.AccountName] = "Account Name"
        };
        switch (changeMode)
        {
            case TbChangeMode.DirectChange:
                mapping[TbMappingKeys.ChangeAmount] = "Movement";
                break;
            case TbChangeMode.DebitCredit:
                mapping[TbMappingKeys.DebitAmount] = "Dr";
                mapping[TbMappingKeys.CreditAmount] = "Cr";
                break;
            case TbChangeMode.OpenClose:
                mapping[TbMappingKeys.OpeningBalance] = "Begin";
                mapping[TbMappingKeys.ClosingBalance] = "End";
                break;
            case TbChangeMode.OpenCloseBySide:
                mapping[TbMappingKeys.OpeningDebitBalance] = "Begin Dr";
                mapping[TbMappingKeys.OpeningCreditBalance] = "Begin Cr";
                mapping[TbMappingKeys.ClosingDebitBalance] = "End Dr";
                mapping[TbMappingKeys.ClosingCreditBalance] = "End Cr";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changeMode), changeMode, null);
        }

        return mapping;
    }

    /// <summary>匯入時記下的來源欄位定義：欄序從 1 起，文字欄有長度，數字欄有兩位小數，每一欄都有觀察到的值。</summary>
    private static FieldInfoState[] Columns(IReadOnlyList<(string Name, LegacyFieldKind Kind)> columns) =>
        columns
            .Select(static (column, index) => column.Kind switch
            {
                LegacyFieldKind.Text => new FieldInfoState(index + 1, column.Name, null, LegacyFieldKind.Text, 12, null, 12, true),
                LegacyFieldKind.Number => new FieldInfoState(index + 1, column.Name, null, LegacyFieldKind.Number, 0, 2, 12, true),
                _ => new FieldInfoState(index + 1, column.Name, null, column.Kind, 0, null, 10, true)
            })
            .ToArray();

    private static string[] Names(IReadOnlyList<FieldInfoState> source) =>
        source.OrderBy(static state => state.Ordinal).Select(static state => state.FieldName).ToArray();

    /// <summary>spec 裡攸關資料元素的識別碼，和推得規則新產生的識別碼刻意不同。</summary>
    private static string SpecFieldId(int index) =>
        "rde." + (0xA000 + index).ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

    private static string InferredFieldId(int index) =>
        "rde." + index.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record WriteAndInferOutcome(
        IReadOnlyList<FieldInfoState> GlTarget,
        FieldInfoSheet Sheet,
        FieldInfoSheetMappingResult Result);
}
