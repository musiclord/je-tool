using System.Text;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 文字檔編碼解析（guide §3.1.1 確定性鏈，不做啟發式猜測）：
/// BOM（UTF-8 / UTF-16 LE/BE）→ 無 BOM 時嚴格 UTF-8 驗證取樣段 → 否則 Big5（CP950）。
/// 解碼一律採 exception fallback：不可解碼的位元組讓讀取端以 file_read_error 回報，
/// 而非默默替換成 U+FFFD 汙染審計資料。
/// </summary>
public static class EncodingDetector
{
    private const int SampleSizeBytes = 64 * 1024;

    static EncodingDetector()
    {
        // Big5（code page 950）需要 CodePages provider；RegisterProvider 可重複呼叫。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>manifest `encoding` 白名單。null = 走偵測鏈。</summary>
    public static Encoding Resolve(string? encodingName, string filePath)
    {
        return encodingName?.ToLowerInvariant() switch
        {
            null => Detect(filePath),
            "utf-8" => StrictUtf8(),
            "big5" => StrictBig5(),
            "utf-16" => Encoding.Unicode,
            _ => throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"encoding '{encodingName}' 無效，允許值：utf-8、big5、utf-16。")
        };
    }

    /// <summary>
    /// 偵測結果的 wire 名稱（manifest import.inspectFile 的 encoding 欄位）。
    /// 對齊 `encoding` 覆寫白名單，檢視結果可直接回填為匯入參數；
    /// UTF-16 大小端在 wire 上不區分（兩者皆靠 BOM 偵測，無需覆寫）。
    /// </summary>
    public static string WireNameOf(Encoding encoding)
    {
        if (encoding is UTF8Encoding)
        {
            return "utf-8";
        }

        if (encoding.CodePage == Encoding.Unicode.CodePage
            || encoding.CodePage == Encoding.BigEndianUnicode.CodePage)
        {
            return "utf-16";
        }

        return "big5";
    }

    public static Encoding Detect(string filePath)
    {
        var sample = ReadSample(filePath);

        // 1. BOM
        if (sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
        {
            return StrictUtf8();
        }

        if (sample.Length >= 2 && sample[0] == 0xFF && sample[1] == 0xFE)
        {
            return Encoding.Unicode; // UTF-16 LE
        }

        if (sample.Length >= 2 && sample[0] == 0xFE && sample[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode; // UTF-16 BE
        }

        // 2. 嚴格 UTF-8 驗證（容忍取樣切到多位元組字元尾端：最多回退 3 個位元組重試）
        if (IsValidUtf8Sample(sample))
        {
            return StrictUtf8();
        }

        // 3. Big5 fallback（台灣 ERP 匯出常態）。不可解碼時由讀取端報 file_read_error。
        return StrictBig5();
    }

    private static bool IsValidUtf8Sample(byte[] sample)
    {
        var strict = StrictUtf8();

        for (var trim = 0; trim <= 3 && trim < sample.Length; trim++)
        {
            try
            {
                strict.GetString(sample, 0, sample.Length - trim);
                return true;
            }
            catch (DecoderFallbackException)
            {
                // 可能是取樣邊界切斷多位元組字元：剝掉尾端再試
            }
        }

        return sample.Length == 0;
    }

    private static byte[] ReadSample(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[Math.Min(SampleSizeBytes, stream.Length)];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (IOException ex)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"無法讀取檔案 '{Path.GetFileName(filePath)}'：{ex.Message}");
        }
    }

    private static Encoding StrictUtf8()
    {
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    }

    private static Encoding StrictBig5()
    {
        return Encoding.GetEncoding(
            950,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }
}
