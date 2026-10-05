using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>只讀取JS文字常數及具名整數的串接；不執行JS，也不拿Domain答案代替前端實際值。</summary>
internal static class FrontendConstantTextReader
{
    private const string Token = @"(?:'(?:[^'\\]|\\['\\nrt])*'|PRESCREEN_DEFAULTS\.[A-Za-z]\w*)";
    private const string Expression = Token + @"(?:\s*\+\s*" + Token + @")*";

    internal static Dictionary<string, string> ValueLabels(string source, string arrayName)
    {
        var array = Regex.Match(source, Regex.Escape(arrayName) + @"\s*=\s*\[(?<body>.*?)\];", RegexOptions.Singleline);
        Assert.True(array.Success, "找不到前端標籤陣列" + arrayName);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match item in Regex.Matches(array.Groups["body"].Value,
            @"\{\s*value:\s*'(?<key>[^']+)'\s*,\s*label:\s*(?<expression>" + Expression + @")\s*(?:,|\})"))
        {
            // Add拒絕重複key，不能讓後列悄悄覆蓋前列後還通過雙向鏡像。
            result.Add(item.Groups["key"].Value, Evaluate(source, item.Groups["expression"].Value));
        }
        Assert.NotEmpty(result);
        return result;
    }

    internal static string ObjectString(string source, string objectName, string key)
    {
        var body = Regex.Match(source, @"\bvar\s+" + Regex.Escape(objectName) + @"\s*=\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
        Assert.True(body.Success, "找不到前端文字表" + objectName);
        var property = Regex.Match(body.Groups["body"].Value,
            @"(?m)^\s*" + Regex.Escape(key) + @"\s*:\s*(?<expression>" + Expression + @")\s*(?:,|$)");
        Assert.True(property.Success, "找不到完整前端文字運算式" + objectName + "." + key);
        return Evaluate(source, property.Groups["expression"].Value);
    }

    private static string Evaluate(string source, string expression)
    {
        Assert.Matches(@"\A\s*" + Expression + @"\s*\z", expression);
        var constants = Regex.Match(source, @"\bvar\s+PRESCREEN_DEFAULTS\s*=\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
        Assert.True(constants.Success, "找不到前端PRESCREEN_DEFAULTS宣告。");
        var numbers = Regex.Matches(constants.Groups["body"].Value, @"(?<key>[A-Za-z]\w*)\s*:\s*(?<value>\d+)\b")
            .ToDictionary(item => item.Groups["key"].Value, item => item.Groups["value"].Value, StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (Match token in Regex.Matches(expression, Token))
        {
            var value = token.Value;
            if (value[0] == '\'')
            {
                text.Append(Regex.Replace(value[1..^1], @"\\(['\\nrt])", escape => escape.Groups[1].Value switch
                { "n" => "\n", "r" => "\r", "t" => "\t", var literal => literal }));
            }
            else
            {
                var key = value["PRESCREEN_DEFAULTS.".Length..];
                Assert.True(numbers.TryGetValue(key, out var number), "前端未宣告整數常數" + key);
                text.Append(number);
            }
        }
        return text.ToString();
    }
}
