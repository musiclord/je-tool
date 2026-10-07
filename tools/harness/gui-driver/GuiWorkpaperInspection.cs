using System.IO.Compression;
using System.Xml.Linq;

namespace Jet.GuiDriver;

/// <summary>只讀 Gui 情境剛匯出的合成底稿。錯誤不帶檔案路徑或儲存格原文。</summary>
internal static class GuiWorkpaperInspection
{
    internal static void RequireManualAutoText(string path, string ownedProjectsRoot)
    {
        var root = Path.GetFullPath(ownedProjectsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("gui_workpaper_outside_owned_root");
        using var zip = ZipFile.OpenRead(path);
        XDocument Read(string name)
        {
            using var stream = (zip.GetEntry(name) ?? throw new InvalidDataException("gui_workpaper_part_missing")).Open();
            return XDocument.Load(stream);
        }
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? [] : Read("xl/sharedStrings.xml")
            .Descendants(s + "si").Select(item => string.Concat(item.Descendants(s + "t").Select(t => t.Value))).ToArray();
        string Value(XElement cell) => (string?)cell.Attribute("t") switch
        {
            "s" => shared[int.Parse(cell.Element(s + "v")!.Value, System.Globalization.CultureInfo.InvariantCulture)],
            "inlineStr" => string.Concat(cell.Descendants(s + "t").Select(t => t.Value)),
            _ => cell.Element(s + "v")?.Value ?? ""
        };
        var relationships = Read("xl/_rels/workbook.xml.rels").Root!.Elements()
            .ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);
        var found = false;
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sheet in Read("xl/workbook.xml").Descendants(s + "sheet")
            .Where(e => ((string?)e.Attribute("name"))?.StartsWith("step4-1 符合高風險條件傳票明細", StringComparison.Ordinal) == true))
        {
            var target = new Uri(new Uri("https://synthetic.invalid/xl/workbook.xml"), relationships[(string)sheet.Attribute(r + "id")!]).AbsolutePath.TrimStart('/');
            var rows = Read(target).Descendants(s + "row").ToArray();
            var header = rows.Single(row => (string?)row.Attribute("r") == "5");
            var cell = header.Elements(s + "c").Single(c => Value(c) == "人工傳票否_JE_S");
            var column = new string(((string)cell.Attribute("r")!).TakeWhile(char.IsAsciiLetter).ToArray());
            found = true;
            foreach (var row in rows.Where(row => (int)row.Attribute("r")! > 5))
            {
                var flag = row.Elements(s + "c").SingleOrDefault(c => (string?)c.Attribute("r") == column + (string)row.Attribute("r")!);
                var value = flag is null ? "" : Value(flag);
                if (value.Length == 0) continue;
                if (value is not ("人工" or "自動") || (string?)flag!.Attribute("t") is not ("s" or "inlineStr" or "str"))
                    throw new InvalidDataException("gui_workpaper_manual_flag_not_text");
                values.Add(value);
            }
        }
        if (!found || !values.SetEquals(["人工", "自動"])) throw new InvalidDataException("gui_workpaper_manual_flags_incomplete");
    }
}
