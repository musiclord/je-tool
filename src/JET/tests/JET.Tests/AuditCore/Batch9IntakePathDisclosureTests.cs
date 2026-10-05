using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class Batch9IntakePathDisclosureTests
{
    private static string SyntheticPath(string name) => Path.Combine(Path.GetTempPath(), "synthetic-private-parent", name);

    [Theory]
    [InlineData(false, true, "合成.xlsx", "找不到檔案 '合成.xlsx'。")]
    [InlineData(true, false, "合成.bin", "不支援檔案 '合成.bin' 的類型 '.bin'，支援 .xlsx、.xlsm、.xls、.csv、.txt，以及 Access .mdb、.accdb。")]
    public void IntakeErrors_OnlyExposeFileName(bool exists, bool supports, string name, string expected)
    {
        var path = SyntheticPath(name);
        var error = Assert.Throws<JetActionException>(() => JetAuditProgram.Plan(new IntakeRequest(
            "import.gl.fromFile", "synthetic", DatasetKind.Gl, [new IntakeSourceRequest(path, exists, supports)], "replace")));
        Assert.Equal(expected, error.Message);
        Assert.DoesNotContain(Path.GetDirectoryName(path)!, error.Message);
    }

    [Theory]
    [InlineData("accountMapping")]
    [InlineData("authorizedPreparer")]
    [InlineData("calendar")]
    public void ReferenceImportMissingFile_OnlyExposesFileName(string kind)
    {
        var path = SyntheticPath("合成.xlsx");
        var error = Assert.Throws<JetActionException>(() =>
        {
            if (kind == "accountMapping") _ = JetAuditProgram.Plan(new AccountMappingRequest("synthetic", path, false, ".xlsx", "replace"));
            else if (kind == "authorizedPreparer") _ = JetAuditProgram.Plan(new AuthorizedPreparerRequest("synthetic", path, false, ".xlsx", "replace", "Code"));
            else _ = JetAuditProgram.Plan(new CalendarFileRequest("import.holiday.fromFile", "synthetic", CalendarDayType.Holiday, path, false, ".xlsx"));
        });
        Assert.Equal("找不到檔案 '合成.xlsx'。", error.Message);
        Assert.DoesNotContain(Path.GetDirectoryName(path)!, error.Message);
    }
}
