using System.Reflection;
using System.Text.RegularExpressions;
using JET.AuditCore;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>架構測試共用：讀產品原始碼、前端腳本與報告範本，以及用反射查產品組件裡的型別。</summary>
internal static class ProductSources
{
    internal static readonly Assembly ProductionAssembly = typeof(JetAuditProgram).Assembly;

    internal static string ProductRoot { get; } = Path.Combine(TestRepositoryPaths.RepositoryRoot, "src", "JET", "JET");

    internal static string Product(params string[] parts) =>
        File.ReadAllText(Path.Combine([ProductRoot, .. parts]));

    internal static string Frontend(params string[] parts) =>
        Product(["wwwroot", "js", .. parts]);

    /// <summary>第五步的前端程式。預篩選收斂計畫階段十（2026-10-25）把 filter-step.js 依職責拆成五個檔案，index.html 依序載入；
    /// 讀第五步字樣、常數與掛鉤的架構測試把五個檔案合起來讀，範圍和拆檔前讀單一檔案相同。</summary>
    internal static readonly string[] FilterStepFiles =
        ["filter-step-view.js", "filter-step-sources.js", "filter-step-rules.js", "filter-step-saved.js", "filter-step.js"];

    internal static string FilterStepFrontend() =>
        string.Join("\n", FilterStepFiles.Select(file => Frontend("steps", file)));

    internal static string TemplatePath(string fileName) => Path.Combine(ProductRoot, "Templates", fileName);

    internal static Type? ProductType(string fullName) => ProductionAssembly.GetType(fullName);

    /// <summary>前端 <c>var NAME = 123;</c> 形式的整數常數。</summary>
    internal static int FrontendInt(string source, string name)
    {
        var match = Regex.Match(source, @"\bvar\s+" + Regex.Escape(name) + @"\s*=\s*(?<value>\d+)\s*;");
        Assert.True(match.Success, "找不到前端常數 " + name);
        return int.Parse(match.Groups["value"].Value);
    }
}
