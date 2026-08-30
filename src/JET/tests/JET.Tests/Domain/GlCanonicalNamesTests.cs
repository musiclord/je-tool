using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 正準中文名表(logical key → *_JE / *_TB)的不變量與逐鍵對照。
/// oracle:樣本「自動化工具-檔案欄位資訊」工作表(Field Mapping Info)的配對後欄名
/// (見 E1 計畫 Task 1 Produces);這只保存樣本已列出的顯示名,不能反向重建完整 mapping。
/// 鎖「鍵屬於白名單 + 值非空 + 具體對照」,不鎖字典實作。
/// </summary>
public sealed class GlCanonicalNamesTests
{
    /// <summary>GL 正準名鍵必須是 <see cref="GlFieldWhitelist"/> 的已知 logical key(不得自創新欄位 id)。</summary>
    [Fact]
    public void Gl_AllKeys_AreKnownWhitelistFields()
    {
        foreach (var key in GlCanonicalNames.Gl.Keys)
        {
            Assert.True(GlFieldWhitelist.TryResolve(key, out _),
                $"GL 正準名鍵「{key}」不在 GlFieldWhitelist;正準名只得對應白名單既有 logical key。");
        }
    }

    /// <summary>有正準名者其值必為非空白字串(匯出表頭不得寫出空名)。</summary>
    [Fact]
    public void Gl_AllValues_AreNonEmpty()
    {
        foreach (var (key, name) in GlCanonicalNames.Gl)
        {
            Assert.False(string.IsNullOrWhiteSpace(name), $"GL 鍵「{key}」的正準名為空。");
        }
    }

    [Fact]
    public void Tb_AllValues_AreNonEmpty()
    {
        foreach (var (key, name) in GlCanonicalNames.Tb)
        {
            Assert.False(string.IsNullOrWhiteSpace(name), $"TB 鍵「{key}」的正準名為空。");
        }
    }

    /// <summary>
    /// GL 逐鍵對照(樣本 Field Mapping Info 配對後欄名)。決策表式逐列斷言,值＋鍵同鎖;
    /// 改名/改對應即紅。涵蓋計畫 Task 1 Produces 列出的全部 GL 正準名。
    /// </summary>
    [Theory]
    [InlineData("docNum", "傳票號碼_JE")]
    [InlineData("lineID", "傳票文件項次_JE_S")]
    [InlineData("postDate", "總帳日期_JE")]
    [InlineData("createBy", "傳票建立人員_JE")]
    [InlineData("approveBy", "傳票核准人員_JE")]
    [InlineData("accNum", "會計科目編號_JE")]
    [InlineData("accName", "會計科目名稱_JE")]
    [InlineData("amount", "傳票金額_JE")]
    [InlineData("description", "傳票摘要_JE")]
    public void Gl_LogicalKey_MapsToCanonicalName(string logicalKey, string expectedName)
    {
        Assert.True(GlCanonicalNames.Gl.TryGetValue(logicalKey, out var name),
            $"GL 正準名表缺少鍵「{logicalKey}」。");
        Assert.Equal(expectedName, name);
    }

    /// <summary>
    /// 無對應正準名的白名單鍵(docDate/voucherDate/jeSource)不得出現在 GL 正準名表
    /// (樣本 Field Mapping Info 未列;不得臆造)。等價分割:白名單鍵分「有正準名 / 無正準名」兩類。
    /// </summary>
    [Theory]
    [InlineData("docDate")]
    [InlineData("voucherDate")]
    [InlineData("jeSource")]
    public void Gl_KeysWithoutSampleCanonicalName_AreAbsent(string logicalKey)
    {
        Assert.False(GlCanonicalNames.Gl.ContainsKey(logicalKey),
            $"鍵「{logicalKey}」在樣本無正準名,不應列入 GL 正準名表。");
    }

    /// <summary>TB 逐鍵對照(會計科目編號/名稱/變動金額)。</summary>
    [Theory]
    [InlineData("accNum", "會計科目編號_TB")]
    [InlineData("accName", "會計科目名稱_TB")]
    [InlineData("changeAmount", "試算表變動金額_TB")]
    public void Tb_LogicalKey_MapsToCanonicalName(string logicalKey, string expectedName)
    {
        Assert.True(GlCanonicalNames.Tb.TryGetValue(logicalKey, out var name),
            $"TB 正準名表缺少鍵「{logicalKey}」。");
        Assert.Equal(expectedName, name);
    }
}
