using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 欄位資訊的「配對後欄位名稱」依每一欄的來歷填，不看欄名結尾；
/// 改名欄位的「配對前欄位名稱」描述的是來源欄，加【】。
/// </summary>
public sealed class FieldInfoProjectionTests
{
    [Fact]
    public void ProjectFieldInfo_UsesCompleteTargetFactsInStableOrdinalOrder()
    {
        var targetTbDefinitions = new List<FieldInfo>
        {
            Definition(3, "期末餘額", null, LegacyFieldKind.Number, textLength: 99, decimalPlaces: 0),
            Definition(1, "會計科目編號_TB", "客戶科目代號", LegacyFieldKind.Text, textLength: 18, origin: FieldInfoOrigin.Renamed, jetField: JetFieldCatalog.TbAccountNumber),
            Definition(2, "原始科目名稱", string.Empty, LegacyFieldKind.Text, textLength: 40)
        };
        var targetGlDefinitions = new List<FieldInfo>
        {
            Definition(4, "核准時間", null, LegacyFieldKind.Time),
            Definition(2, "傳票文件項次_JE_S", "來源項次", LegacyFieldKind.Number, decimalPlaces: 0, origin: FieldInfoOrigin.Renamed, jetField: JetFieldCatalog.GlLineItem),
            Definition(3, "總帳日期_JE", "傳票日期", LegacyFieldKind.Date, origin: FieldInfoOrigin.Renamed, jetField: JetFieldCatalog.GlDate),
            Definition(1, "傳票號碼_JE", "來源傳票號碼", LegacyFieldKind.Text, textLength: 25, origin: FieldInfoOrigin.Renamed, jetField: JetFieldCatalog.GlVoucherNumber),
            Definition(5, "傳票金額_JE", "【傳票金額_JE】由系統產生 : 【金額】", LegacyFieldKind.Number, decimalPlaces: 4, origin: FieldInfoOrigin.Generated, jetField: JetFieldCatalog.GlAmount)
        };

        var actual = JetAuditProgram.ProjectFieldInfo(targetTbDefinitions, targetGlDefinitions);

        Assert.Equal(
            new[]
            {
                new FieldInfoRow("【客戶科目代號】", "文字型態", 18, null, "會計科目編號_TB"),
                new FieldInfoRow("原始科目名稱", "文字型態", 40, null, null),
                new FieldInfoRow("期末餘額", "數字型態", null, 0, null)
            },
            actual.TbRows);
        Assert.Equal(
            new[]
            {
                new FieldInfoRow("【來源傳票號碼】", "文字型態", 25, null, "傳票號碼_JE"),
                new FieldInfoRow("【來源項次】", "數字型態", null, 0, "傳票文件項次_JE_S"),
                new FieldInfoRow("【傳票日期】", "日期型態", null, null, "總帳日期_JE"),
                new FieldInfoRow("核准時間", "時間型態", null, null, null),
                new FieldInfoRow("【傳票金額_JE】由系統產生 : 【金額】", "數字型態", null, 4, "傳票金額_JE")
            },
            actual.GlRows);

        targetTbDefinitions.Clear();
        targetGlDefinitions.Clear();
        Assert.Equal(3, actual.TbRows.Count);
        Assert.Equal(5, actual.GlRows.Count);
    }

    /// <summary>配對後欄位名稱看每一欄的來歷，不看欄名是否以 _TB、_JE、_JE_S 結尾。</summary>
    [Fact]
    public void ProjectFieldInfo_ActualFieldNameFollowsOriginNotNameSuffix()
    {
        var actual = JetAuditProgram.ProjectFieldInfo(
            [
                Definition(1, "UPPER_TB", null, LegacyFieldKind.Text, textLength: 1),
                Definition(2, "lower_tb", "來源", LegacyFieldKind.Text, textLength: 1, origin: FieldInfoOrigin.Renamed, jetField: JetFieldCatalog.TbAccountName),
                Definition(3, "GL_NAME_JE", "由系統產生", LegacyFieldKind.Text, textLength: 1, origin: FieldInfoOrigin.Generated, jetField: JetFieldCatalog.TbChangeAmount)
            ],
            [
                Definition(1, "BASE_JE", null, LegacyFieldKind.Text, textLength: 1),
                Definition(2, "SUB_JE_S", "來源", LegacyFieldKind.Text, textLength: 1, origin: FieldInfoOrigin.Temp, jetField: JetFieldCatalog.GlVoucherNumber),
                Definition(3, "lower_je", "來源", LegacyFieldKind.Text, textLength: 1, origin: FieldInfoOrigin.Renamed, jetField: JetFieldCatalog.GlDescription),
                Definition(4, "TB_NAME_TB", "由系統產生", LegacyFieldKind.Text, textLength: 1, origin: FieldInfoOrigin.Generated, jetField: JetFieldCatalog.GlAmount)
            ]);

        Assert.Equal(new string?[] { null, "lower_tb", "GL_NAME_JE" },
            actual.TbRows.Select(row => row.ActualFieldName));
        Assert.Equal(new string?[] { null, null, "lower_je", "TB_NAME_TB" },
            actual.GlRows.Select(row => row.ActualFieldName));
        // 原始來源欄寫欄名本身，改名欄與 _Temp 原欄寫【來源欄名】，系統產生的欄寫說明句。
        Assert.Equal(new[] { "UPPER_TB", "【來源】", "由系統產生" }, actual.TbRows.Select(row => row.DisplayName));
        Assert.Equal(new[] { "BASE_JE", "【來源】", "【來源】", "由系統產生" }, actual.GlRows.Select(row => row.DisplayName));
    }

    private static FieldInfo Definition(
        int ordinal,
        string fieldName,
        string? description,
        LegacyFieldKind kind,
        int? textLength = null,
        int? decimalPlaces = null,
        FieldInfoOrigin origin = FieldInfoOrigin.Source,
        string? jetField = null) =>
        new(ordinal, fieldName, description, kind, textLength, decimalPlaces, origin, jetField);
}
