using System.Text.Encodings.Web;
using System.Text.Json;

namespace JET.Domain;

/// <summary>
/// 儲存層（DB TEXT / project.json）專用的 JSON 設定：保留非 ASCII 原文，
/// 供人工檢視與 SQL JSON 函式（SQLite json_extract）直接讀取。
/// Bridge wire 回應仍用各自的預設設定，不受影響。
/// 置於 Domain（BCL-only、無 I/O）供 Application 與 Infrastructure 共用，維持依賴方向。
/// <see cref="Parse"/> 是讀回已存 JSON 時共用的解析。
/// </summary>
public static class JetJsonStorage
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly JsonSerializerOptions IndentedOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    // 只用來試寫一次，內容直接丟掉；不必再檢查結構，也不必把非 ASCII 字元改寫成跳脫寫法。
    private static readonly JsonWriterOptions ReadableTextCheck = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true
    };

    /// <summary>
    /// 解析 JET 自己存下、之後讀回來的 JSON，例如案件資料庫裡的結果摘要與篩選情境定義。
    /// <c>JsonDocument.Parse</c> 會放行不成對代理字元的跳脫寫法（例如 <c>\ud800</c>），之後讀字串、讀欄名或原樣寫出時
    /// 才丟 <see cref="InvalidOperationException"/>；經 <c>JsonSerializer</c> 序列化到回應時，丟的是包著它的
    /// <see cref="JsonException"/>。兩種都離讀取點很遠。JET 寫出時會把這種字元換成 U+FFFD，
    /// 所以讀到就表示內容在 JET 以外被改過。這裡解析後把整份文件試寫一次，寫不出來就當成損壞的 JSON 丟
    /// <see cref="JsonException"/>，讓各讀取點沿用它們讀到壞 JSON 的既有規則；通過之後，文件裡每個字串與欄名都讀得出來。
    /// 單一字串、欄名或數字長到超過寫出的長度上限時（實測是超過 166,666,666 個半形字元），試寫丟的是
    /// <see cref="ArgumentException"/>，同樣當成損壞的 JSON。
    /// 值的型別不對不在這裡檢查。畫面剛送來的內容、外部檔案與逐列讀的 GL 原始資料不用這個函式。
    /// </summary>
    public static JsonDocument Parse(string json)
    {
        // 回應的封套、資料物件與清單還會包在已存內容外面；預留四層，避免開案成功後才在 Bridge 寫出時失敗。
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 60 });
        try
        {
            using var writer = new Utf8JsonWriter(Stream.Null, ReadableTextCheck);
            document.RootElement.WriteTo(writer);
            return document;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // try 裡只有試寫，ArgumentException 只會來自長度超過上限的字串、欄名或數字。
            document.Dispose();
            throw new JsonException("Stored JSON contains text that cannot be read.", exception);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }
}
