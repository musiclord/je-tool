namespace JET.Domain;

/// <summary>
/// 匯入文字上限，以 .NET UTF-16 字元數計算。
/// SQLite 沒有欄寬，投影時先套用這個上限：超過就回列錯誤，不截斷。
/// </summary>
public static class TextStorageLimits
{
    /// <summary>有索引的鍵：傳票號碼、科目編號、授權人員名稱與攸關資料元素的文字值。</summary>
    public const int KeyUtf16CodeUnits = 450;

    /// <summary>項次、科目名稱、來源模組、編製者、核准者與過帳狀態。摘要沒有上限。</summary>
    public const int TextUtf16CodeUnits = 400;

    /// <summary>假日與補班日的名稱。</summary>
    public const int CalendarDayNameUtf16CodeUnits = 256;
}
