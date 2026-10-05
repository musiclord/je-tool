using JET.Domain;
using JET.Infrastructure;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Domain;

public sealed class UserFeedbackManualAutoProjectionTests
{
    [Theory]
    [InlineData("ß", "SS", false)]
    [InlineData("ı", "I", false)]
    [InlineData("ſ", "S", false)]
    [InlineData("K", "K", false)]
    [InlineData("ΐ", "ΐ", false)]
    [InlineData("ß", "ẞ", false)]
    [InlineData("Σ", "ς", true)]
    [InlineData("é", "É", true)]
    [InlineData("ᾀ", "ᾈ", true)]
    [InlineData("\u0085M\u0085", "m", true)]
    [InlineData("\uFEFFM\uFEFF", "M", false)]
    public void UnicodeCodes_UseOrdinalIgnoreCaseWithoutLinguisticExpansion(string manual, string automatic, bool overlap)
    {
        var mapping = new Dictionary<string, string> { [GlMappingKeys.Manual] = "mode" };
        var options = GlMappingOptions.NormalizeLegacy(mapping) with
        {
            ManualAutoPolicy = new GlManualAutoPolicy([manual], [automatic])
        };
        if (overlap)
        {
            Assert.Throws<ArgumentException>(() => GlMappingOptionsRules.NormalizeAndValidate(mapping, ["mode"], options));
            return;
        }
        var canonical = GlMappingOptionsRules.NormalizeAndValidate(mapping, ["mode"], options);
        var spec = new GlMappingSpec(mapping, GlAmountMode.SignedAmount) { Options = canonical };
        Assert.True(GlRowProjector.TryProject(new StagingRow(2, new Dictionary<string, string> { ["mode"] = automatic }),
            spec, ProjectDocument.DefaultMoneyScale, out var projected, out var error), error?.Reason);
        Assert.False(projected!.IsManual);
    }

    [Theory]
    [InlineData("automatic", "reject", "M", true)]
    [InlineData("automatic", "reject", "NEW", false)]
    [InlineData("manual", "reject", "A", false)]
    [InlineData("manual", "reject", "NEW", true)]
    [InlineData("automatic", "manual", " ", true)]
    [InlineData("manual", "automatic", "", false)]
    [InlineData("automatic", "unclassified", "", null)]
    public void OneSidedPolicy_PreservesExplicitComplementAndBlankChoiceThroughMetadata(
        string unlisted, string blank, string raw, bool? expected)
    {
        var mapping = new Dictionary<string, string>
        {
            [GlMappingKeys.DocNum] = "doc", [GlMappingKeys.PostDate] = "date",
            [GlMappingKeys.AccNum] = "account", [GlMappingKeys.AccName] = "name",
            [GlMappingKeys.Description] = "description", [GlMappingKeys.Amount] = "amount",
            [GlMappingKeys.Manual] = "mode"
        };
        var json = JsonSerializer.Serialize(new
        {
            approvalDateMode = "unmapped", postingStatusPolicy = (object?)null,
            manualAutoPolicy = new
            {
                manualValues = unlisted == "automatic" ? new[] { "M" } : [],
                automaticValues = unlisted == "manual" ? new[] { "A" } : [],
                unlistedValueKind = unlisted, blankValueKind = blank
            }, rdeFields = Array.Empty<object>()
        });
        var options = GlMappingOptionsJsonCodec.Decode(json, mapping);
        options = GlMappingOptionsRules.NormalizeAndValidate(mapping, mapping.Values.ToArray(), options);
        var encoded = GlMappingOptionsJsonCodec.Encode(options, mapping);
        options = GlMappingOptionsJsonCodec.Decode(encoded, mapping);
        Assert.Contains("\"unlistedValueKind\":\"" + unlisted + "\"", encoded);
        Assert.Contains("\"blankValueKind\":\"" + blank + "\"", encoded);
        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["doc"] = "JV-1", ["date"] = "2026-01-15", ["account"] = "1000",
            ["name"] = "合成科目", ["description"] = "合成分錄", ["amount"] = "1", ["mode"] = raw
        });
        var spec = new GlMappingSpec(mapping, GlAmountMode.SignedAmount) { Options = options };
        Assert.True(GlRowProjector.TryProject(row, spec, ProjectDocument.DefaultMoneyScale, out var projected, out var error), error?.Reason);
        Assert.Equal(expected, projected!.IsManual);
    }

    [Theory]
    [InlineData("", "是空白")]
    [InlineData("UNKNOWN", "未歸類為人工或自動")]
    public void ProjectionFailure_ExplainsManualAutoValueAndCorrection(string raw, string expected)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.DocNum] = "傳票號碼",
            [GlMappingKeys.PostDate] = "過帳日期",
            [GlMappingKeys.AccNum] = "科目編號",
            [GlMappingKeys.AccName] = "科目名稱",
            [GlMappingKeys.Description] = "摘要",
            [GlMappingKeys.Amount] = "金額",
            [GlMappingKeys.Manual] = "人工自動代碼"
        };
        var options = GlMappingOptions.NormalizeLegacy(mapping) with
        {
            ManualAutoPolicy = new GlManualAutoPolicy(["M"], ["A"])
        };
        var spec = new GlMappingSpec(mapping, GlAmountMode.SignedAmount) { Options = options };
        var row = new StagingRow(
            7,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["傳票號碼"] = "JV-001",
                ["過帳日期"] = "2026-01-15",
                ["科目編號"] = "1000",
                ["科目名稱"] = "現金",
                ["摘要"] = "測試",
                ["金額"] = "1",
                ["人工自動代碼"] = raw
            });

        Assert.False(GlRowProjector.TryProject(
            row,
            spec,
            ProjectDocument.DefaultMoneyScale,
            out _,
            out var error));
        Assert.NotNull(error);
        Assert.Contains(expected, error.Reason, StringComparison.Ordinal);
        Assert.Contains("回到欄位配對", error.Reason, StringComparison.Ordinal);
        Assert.Contains(raw.Length == 0 ? "來源空白時" : "判定方式", error.Reason, StringComparison.Ordinal);
        if (raw.Length == 0)
        {
            Assert.Contains("不判定", error.Reason, StringComparison.Ordinal);
        }
    }
}
