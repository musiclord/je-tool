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

    [Fact]
    public void Resolve_InvalidName_ThrowsInvalidPayload()
    {
        var ex = Assert.Throws<JetActionException>(
            () => EncodingDetector.Resolve("shift-jis", filePath: "unused.csv"));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        Assert.Contains("shift-jis", ex.Message, StringComparison.Ordinal);
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
