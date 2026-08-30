using System.Text;

namespace JET.Tests.Infrastructure;

/// <summary>
/// CSV / .txt 測試 fixture：bytes 級寫入 temp 檔，可控編碼與 BOM。
/// 每個測試自建自刪（FIRST：Independent）。
/// </summary>
internal static class TestCsvBuilder
{
    static TestCsvBuilder()
    {
        // 測試端產 Big5 bytes 也需要 CodePages provider（與 EncodingDetector 相同註冊，冪等）。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static Encoding Big5 => Encoding.GetEncoding(950);

    public static Encoding Utf8NoBom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static Encoding Utf8Bom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static string WriteFile(string content, Encoding encoding, string extension = ".csv")
    {
        var path = NewPath(extension);
        File.WriteAllText(path, content, encoding);
        return path;
    }

    public static string WriteBytes(byte[] bytes, string extension = ".csv")
    {
        var path = NewPath(extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static void Delete(string path)
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

    private static string NewPath(string extension)
    {
        return Path.Combine(Path.GetTempPath(), $"jet-csv-{Guid.NewGuid():N}{extension}");
    }
}
