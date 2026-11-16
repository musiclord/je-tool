using System.Text;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 判定文字檔的編碼。依固定順序判定，不靠統計猜測。
/// 先看 BOM，認得 UTF-8 與 UTF-16 的小端、大端。沒有 BOM 時取檔案開頭一段，通過嚴格的 UTF-8 驗證就是 UTF-8，
/// 否則當成 Big5（code page 950）。
/// 這裡回傳的編碼遇到無法解碼的位元組都會丟出例外，讀取端因此回報 file_read_error，
/// 不會把壞位元組默默換成 U+FFFD 混進審計資料。
/// 讀取端的 StreamReader 會認 BOM：BOM 和這裡給的編碼的前導位元組相同時沿用這個嚴格編碼，
/// 不同時會換成 .NET 預設、遇到壞位元組就默默替換的編碼。所以 UTF-8 與 UTF-16 的嚴格編碼都帶前導位元組，
/// 指定「utf-16」時也照 BOM 選大端或小端。
/// </summary>
public static class EncodingDetector
{
    private const int SampleSizeBytes = 64 * 1024;

    static EncodingDetector()
    {
        // Big5（code page 950）需要 CodePages provider；RegisterProvider 可重複呼叫。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>把匯入參數 encoding 換成編碼。只接受 utf-8、big5 與 utf-16，其他值回 invalid_payload；null 時自動偵測。</summary>
    public static Encoding Resolve(string? encodingName, string filePath)
    {
        return encodingName?.ToLowerInvariant() switch
        {
            null => Detect(filePath),
            "utf-8" => StrictUtf8(),
            "big5" => StrictBig5(),
            "utf-16" => StrictUtf16(bigEndian: StartsWithBigEndianBom(filePath)),
            _ => throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "encoding_invalid")
        };
    }

    /// <summary>
    /// 編碼在 import.inspectFile 回應的 encoding 欄位裡的寫法。
    /// 寫法和匯入參數 encoding 接受的值相同，檢視的結果可以直接填回匯入參數。
    /// UTF-16 不分大端小端，都寫 utf-16；兩者都靠 BOM 判定，不需要另外指定。
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

    /// <summary>訊息裡的編碼寫一般常見的寫法，和匯入清單的選項相同；不是這三種時原樣寫出。</summary>
    public static string DisplayNameOf(string wireName) => wireName switch
    {
        "utf-8" => "UTF-8",
        "big5" => "Big5",
        "utf-16" => "UTF-16",
        _ => wireName
    };

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
            return StrictUtf16(bigEndian: false); // UTF-16 LE
        }

        if (sample.Length >= 2 && sample[0] == 0xFE && sample[1] == 0xFF)
        {
            return StrictUtf16(bigEndian: true); // UTF-16 BE
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
            using var stream = SourceFileAccess.OpenRead(filePath);
            var buffer = new byte[Math.Min(SampleSizeBytes, stream.Length)];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw SourceFileErrors.CannotOpen(filePath, ex);
        }
    }

    // 只用來選大端或小端；檔案打不開時照小端，打不開的原因由讀取端照常回報。
    private static bool StartsWithBigEndianBom(string filePath)
    {
        try
        {
            using var stream = SourceFileAccess.OpenRead(filePath);
            return stream.ReadByte() == 0xFE && stream.ReadByte() == 0xFF;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // 前導位元組 EF BB BF 讓 StreamReader 認得 BOM 後沿用這個嚴格編碼。
    private static Encoding StrictUtf8()
    {
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
    }

    private static Encoding StrictUtf16(bool bigEndian)
    {
        return new UnicodeEncoding(bigEndian, byteOrderMark: true, throwOnInvalidBytes: true);
    }

    private static Encoding StrictBig5()
    {
        return Encoding.GetEncoding(
            950,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }
}
