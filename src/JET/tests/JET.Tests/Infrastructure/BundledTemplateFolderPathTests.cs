using System.IO.Compression;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class BundledTemplateFolderPathTests
{
    public static TheoryData<string> TemplateFileNames()
    {
        var names = new TheoryData<string>();
        foreach (var name in ReportTemplateCatalog.FileNames)
        {
            names.Add(name);
        }

        return names;
    }

    // Excel 存檔時會把檔案當時所在的資料夾路徑記進活頁簿設定。範本隨程式發出去，
    // 匯出的報告也從範本複製而來，所以範本裡不能留著製作範本那台電腦的資料夾路徑。
    [Theory]
    [MemberData(nameof(TemplateFileNames))]
    public async Task BundledTemplate_DoesNotRecordTheFolderItWasSavedIn(string fileName)
    {
        await using var copy = new MemoryStream();
        await ReportTemplateCatalog.Default.CopyToAsync(fileName, copy, CancellationToken.None);
        copy.Position = 0;
        using var archive = new ZipArchive(copy, ZipArchiveMode.Read);
        var workbook = archive.GetEntry("xl/workbook.xml");
        Assert.NotNull(workbook);
        using var reader = new StreamReader(workbook.Open());
        var xml = await reader.ReadToEndAsync();

        Assert.DoesNotContain("absPath", xml, StringComparison.Ordinal);
    }
}
