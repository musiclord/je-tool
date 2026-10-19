using System.Text;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// EncodingDetector 測試（guide §3.1.1）。
/// oracle：手寫 bytes fixture 與 manifest encoding 白名單。
/// 設計技術：等價分割——覆寫值（utf-8 / big5 / utf-16 / invalid）與偵測鏈（BOM / 嚴格 UTF-8 / Big5 fallback）。
/// </summary>
public sealed class EncodingDetectorTests
{
    [Fact]
    public void Resolve_Big5Name_ReturnsStrictBig5()
    {
        var encoding = EncodingDetector.Resolve("big5", filePath: "unused.csv");

        Assert.Equal(950, encoding.CodePage);
        Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes("😀"));
    }

    [Fact]
    public void Resolve_Utf16Name_ReturnsUnicode()
    {
        var encoding = EncodingDetector.Resolve("utf-16", filePath: "unused.csv");

        Assert.Equal(Encoding.Unicode.CodePage, encoding.CodePage);
    }

    // 低嚴重度事項 A3（使用者 2026-10-19 要求處理）：BOM 偵測出的 UTF-8 與 UTF-16 也是嚴格編碼，遇到壞位元組丟例外；
    // 前導位元組和 BOM 相同，StreamReader 才會沿用它，不換成默默替換字元的預設編碼。
    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }, 65001, new byte[] { 0xEF, 0xBB, 0xBF })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 }, 1200, new byte[] { 0xFF, 0xFE })]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x61 }, 1201, new byte[] { 0xFE, 0xFF })]
    public void Detect_BomEncodings_AreStrictAndKeepTheirPreamble(byte[] bytes, int codePage, byte[] preamble)
    {
        var path = TestCsvBuilder.WriteBytes(bytes);
        try
        {
            var encoding = EncodingDetector.Detect(path);

            Assert.Equal(codePage, encoding.CodePage);
            Assert.Equal(preamble, encoding.GetPreamble());
            var bad = codePage == 65001 ? new byte[] { 0xFF } : codePage == 1200 ? new byte[] { 0x00, 0xD8, 0x61, 0x00 } : new byte[] { 0xD8, 0x00, 0x00, 0x61 };
            Assert.Throws<DecoderFallbackException>(() => encoding.GetString(bad));
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public void Resolve_Utf16Name_OnBigEndianBom_ReturnsStrictBigEndian()
    {
        var path = TestCsvBuilder.WriteBytes([0xFE, 0xFF, 0x00, 0x61]);
        try
        {
            var encoding = EncodingDetector.Resolve("utf-16", path);

            Assert.Equal(Encoding.BigEndianUnicode.CodePage, encoding.CodePage);
            Assert.Throws<DecoderFallbackException>(() => encoding.GetString([0xD8, 0x00, 0x00, 0x61]));
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public void Resolve_InvalidName_ThrowsInvalidPayload()
    {
        var ex = Assert.Throws<JetActionException>(
            () => EncodingDetector.Resolve("shift-jis", filePath: "unused.csv"));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        // 使用者 2026-10-14 第三輪勾選頁確認 R3-65（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。 畫面只送三種編碼，
        // 其他值只有程式送錯資料才會出現：顯示非預期錯誤那一句，檢查代號記進支援日誌，送出的值不出現。
        Assert.Equal(JetPayloadRejection.UnexpectedErrorMessage, ex.Message);
        Assert.Equal("encoding_invalid", JetPayloadRejection.CheckOf(ex));
        Assert.DoesNotContain("shift-jis", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WireNameOf_Utf8Encoding_ReturnsUtf8()
    {
        var wireName = EncodingDetector.WireNameOf(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.Equal("utf-8", wireName);
    }

    [Fact]
    public void WireNameOf_Utf16LittleEndianEncoding_ReturnsUtf16()
    {
        var wireName = EncodingDetector.WireNameOf(Encoding.Unicode);

        Assert.Equal("utf-16", wireName);
    }

    [Fact]
    public void WireNameOf_Utf16BigEndianEncoding_ReturnsUtf16()
    {
        var wireName = EncodingDetector.WireNameOf(Encoding.BigEndianUnicode);

        Assert.Equal("utf-16", wireName);
    }

    [Fact]
    public void Detect_Utf16BigEndianBom_ReturnsBigEndianUnicode()
    {
        var path = WriteBytes([0xFE, 0xFF, 0x00, 0x41]);
        try
        {
            var encoding = EncodingDetector.Detect(path);

            Assert.Equal(Encoding.BigEndianUnicode.CodePage, encoding.CodePage);
        }
        finally
        {
            Delete(path);
        }
    }

    private static string WriteBytes(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-encoding-{Guid.NewGuid():N}.csv");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // temp 檔清不掉不影響測試結果
        }
    }
}
