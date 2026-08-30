using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class FieldInfoProjectionTests
{
    [Fact]
    public void ProjectFieldInfo_UsesCompleteTargetFactsInStableOrdinalOrder()
    {
        var targetTbDefinitions = new List<LegacyFieldDefinition>
        {
            Definition(3, "期末餘額", null, LegacyFieldKind.Number, textLength: 99, decimalPlaces: 0),
            Definition(1, "會計科目編號_TB", "客戶科目代號", LegacyFieldKind.Text, textLength: 18),
            Definition(2, "原始科目名稱", string.Empty, LegacyFieldKind.Text, textLength: 40)
        };
        var targetGlDefinitions = new List<LegacyFieldDefinition>
        {
            Definition(4, "核准時間", null, LegacyFieldKind.Time),
            Definition(2, "傳票文件項次_JE_S", "來源項次", LegacyFieldKind.Number, decimalPlaces: 0),
            Definition(3, "總帳日期_JE", "傳票日期", LegacyFieldKind.Date),
            Definition(1, "傳票號碼_JE", "來源傳票號碼", LegacyFieldKind.Text, textLength: 25),
            Definition(5, "傳票金額_JE", "金額", LegacyFieldKind.Number, decimalPlaces: 4)
        };

        var actual = JetAuditProgram.ProjectFieldInfo(targetTbDefinitions, targetGlDefinitions);

        Assert.Equal(
            new[]
            {
                new FieldInfoRow("客戶科目代號", "文字型態", 18, null, "會計科目編號_TB"),
                new FieldInfoRow("原始科目名稱", "文字型態", 40, null, null),
                new FieldInfoRow("期末餘額", "數字型態", null, 0, null)
            },
            actual.TbRows);
        Assert.Equal(
            new[]
            {
                new FieldInfoRow("來源傳票號碼", "文字型態", 25, null, "傳票號碼_JE"),
                new FieldInfoRow("來源項次", "數字型態", null, 0, "傳票文件項次_JE_S"),
                new FieldInfoRow("傳票日期", "日期型態", null, null, "總帳日期_JE"),
                new FieldInfoRow("核准時間", "時間型態", null, null, null),
                new FieldInfoRow("金額", "數字型態", null, 4, "傳票金額_JE")
            },
            actual.GlRows);

        targetTbDefinitions.Clear();
        targetGlDefinitions.Clear();
        Assert.Equal(3, actual.TbRows.Count);
        Assert.Equal(5, actual.GlRows.Count);
    }

    [Fact]
    public void ProjectFieldInfo_CanonicalColumnRequiresTheExactDatasetSuffix()
    {
        var actual = JetAuditProgram.ProjectFieldInfo(
            [
                Definition(1, "UPPER_TB", null, LegacyFieldKind.Text, textLength: 1),
                Definition(2, "lower_tb", null, LegacyFieldKind.Text, textLength: 1),
                Definition(3, "GL_NAME_JE", null, LegacyFieldKind.Text, textLength: 1)
            ],
            [
                Definition(1, "BASE_JE", null, LegacyFieldKind.Text, textLength: 1),
                Definition(2, "SUB_JE_S", null, LegacyFieldKind.Text, textLength: 1),
                Definition(3, "lower_je", null, LegacyFieldKind.Text, textLength: 1),
                Definition(4, "TB_NAME_TB", null, LegacyFieldKind.Text, textLength: 1)
            ]);

        Assert.Equal(new string?[] { "UPPER_TB", null, null },
            actual.TbRows.Select(row => row.ActualFieldName));
        Assert.Equal(new string?[] { "BASE_JE", "SUB_JE_S", null, null },
            actual.GlRows.Select(row => row.ActualFieldName));
    }

    private static LegacyFieldDefinition Definition(
        int ordinal,
        string fieldName,
        string? description,
        LegacyFieldKind kind,
        int? textLength = null,
        int? decimalPlaces = null) =>
        new(ordinal, fieldName, description, kind, textLength, decimalPlaces);
}
