using System.Reflection;
using System.Text.RegularExpressions;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 報告與底稿的寫出程式要讀完整份資料，畫面的分頁查詢一次只讀一頁，兩者是不同的讀取介面。
/// 寫出程式逐頁呼叫畫面的分頁查詢時，每一頁都可能重算整個範圍，資料量大就跑不完。
/// 這裡限制寫出程式只能依賴一個查詢讀完的讀取介面，而且要寫在建構函式的參數型別上，不在執行時才轉型。
/// </summary>
public sealed partial class ExportReadPortArchitectureTests
{
    private static readonly Type[] ExportWriters = [typeof(LegacyReportWriter), typeof(WorkingPaperWriter)];

    [Fact]
    public void ExportWriterConstructors_DoNotTakeScreenPagingRepositories()
    {
        var offending = ExportWriters
            .SelectMany(static writer => writer.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .SelectMany(static constructor => constructor.GetParameters()
                .Where(static parameter => parameter.ParameterType.Name.EndsWith(
                    "PageRepository",
                    StringComparison.Ordinal))
                .Select(parameter => $"{constructor.DeclaringType!.Name}({parameter.ParameterType.Name} {parameter.Name})"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offending);
    }

    [Fact]
    public void ExportSources_DoNotCallScreenPagingQueries()
    {
        var offending = new List<string>();
        foreach (var path in ExportSourceFiles())
        {
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                if (!IsComment(lines[index]) && ScreenPagingReference().IsMatch(lines[index]))
                {
                    offending.Add($"{Path.GetFileName(path)}:{index + 1}: {lines[index].Trim()}");
                }
            }
        }

        Assert.Empty(offending);
    }

    [Fact]
    public void ExportSources_DoNotCastRepositoriesToReadPortsAtRunTime()
    {
        var offending = new List<string>();
        foreach (var path in ExportSourceFiles())
        {
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                if (!IsComment(lines[index]) && RunTimeReadPortCast().IsMatch(lines[index]))
                {
                    offending.Add($"{Path.GetFileName(path)}:{index + 1}: {lines[index].Trim()}");
                }
            }
        }

        Assert.Empty(offending);
    }

    /// <summary>
    /// 把整張工作表載入記憶體，用的記憶體約是工作表 XML 的好幾倍，只適合大小不跟資料列數成長的工作表。
    /// 明細一律逐列寫出。要在匯出程式新增載入整張工作表的地方時，先確認那張工作表的列數有固定上限，
    /// 再把檔名、次數與理由加進這張清單。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (int Count, string Reason)> WholeWorksheetLoads =
        new Dictionary<string, (int Count, string Reason)>(StringComparer.Ordinal)
        {
            ["OpenXmlPartRoots.cs"] = (1, "取工作表根節點的共用方法本身。"),
            ["ExcelText.cs"] = (1, "把呼叫端已經載入的工作表存回去，自己不載入。"),
            ["DirectTemplateStreamingSheetWriter.cs"] = (1, "讀範本工作表原有的表頭與版面，範本的大小固定；資料列另外逐列寫出。"),
            ["DirectTemplateWorkbookEditor.cs"] = (3, "改範本工作表裡指定的固定格、確認要改寫的工作表沒有資料列、複製範本工作表當續頁的版面。"),
            ["LegacyReportWriter.Criteria.cs"] = (1, "條件篩選報告的摘要頁，每個情境一列，情境數有上限。"),
            ["LegacyReportWriter.Inf.cs"] = (1, "資料可靠性抽樣清單的主表，列數是抽樣筆數，預設 59 筆；抽樣筆數設得很大時，這張表會整份留在記憶體。"),
            ["LegacyReportWriter.Prescreen.cs"] = (2, "預篩選報告的摘要頁，每條規則一列；另一處讀範本的固定文字。"),
            ["LegacyReportWriter.Validation.cs"] = (2, "資料驗證報告的摘要頁與範本的固定格。"),
            ["ReportWorkbookMetadataCodec.cs"] = (1, "新建只有幾列的隱藏工作表，記錄報告的識別資料。"),
        };

    [Fact]
    public void ExportSources_LoadWholeWorksheetsOnlyWhereListedWithAReason()
    {
        var actual = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in ExportSourceFiles())
        {
            var count = File.ReadAllLines(path)
                .Where(static line => !IsComment(line))
                .Sum(static line => WholeWorksheetLoad().Matches(line).Count);
            if (count > 0)
            {
                actual.Add(Path.GetFileName(path), count);
            }
        }

        var differences = actual.Keys.Union(WholeWorksheetLoads.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(file => (
                File: file,
                Actual: actual.GetValueOrDefault(file),
                Listed: WholeWorksheetLoads.TryGetValue(file, out var listed) ? listed.Count : 0))
            .Where(static item => item.Actual != item.Listed)
            .Select(static item => $"{item.File}：程式裡 {item.Actual} 處，清單 {item.Listed} 處")
            .ToArray();

        Assert.Empty(differences);
        Assert.All(WholeWorksheetLoads.Values, static item => Assert.False(string.IsNullOrWhiteSpace(item.Reason)));
    }

    // 取工作表根節點的寫法：共用方法 RequiredWorksheet()，或直接讀寫 WorksheetPart 的 Worksheet 屬性。
    [GeneratedRegex(@"\bRequiredWorksheet\s*\(\s*\)|\.Worksheet\b")]
    private static partial Regex WholeWorksheetLoad();

    // 分頁查詢的介面名稱都以 PageRepository 結尾，方法名稱是 Get 開頭、PageAsync 結尾，例如 GetPageAsync。
    [GeneratedRegex(@"\bI\w+PageRepository\b|\bGet\w*PageAsync\s*\(")]
    private static partial Regex ScreenPagingReference();

    [GeneratedRegex(@"\b(is|as)\s+(not\s+)?I\w+(StreamRepository|PreparedSessionFactory)\b")]
    private static partial Regex RunTimeReadPortCast();

    private static bool IsComment(string line) =>
        line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    private static IReadOnlyList<string> ExportSourceFiles()
    {
        var directory = Path.Combine(JetRoot(), "JET", "Infrastructure", "Export");
        var files = Directory.GetFiles(directory, "*.cs", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(files);
        return files;
    }

    private static string JetRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
