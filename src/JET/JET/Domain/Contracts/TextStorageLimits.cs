namespace JET.Domain;

/// <summary>
/// 匯入文字上限，以 .NET UTF-16 字元數計算，和 SQL Server 的 <c>NVARCHAR(n)</c> 欄寬相同。
/// SQLite 沒有欄寬，投影時先套用同一上限：超過就回列錯誤，不截斷，也不讓本機接受 SQL Server 存不下的值
/// （文字欄長度三方一致，裁定 2026-10-19-28）。目前只支援 SQLite（裁定 2026-11-06-08），上限照舊保留，
/// 日後恢復 SQL Server 時兩邊仍一致。
/// </summary>
public static class TextStorageLimits
{
    /// <summary>有索引的鍵：傳票號碼、科目編號、授權人員名稱與攸關資料元素的文字值。SQL Server 索引鍵上限是 900 位元組。</summary>
    public const int KeyUtf16CodeUnits = 450;

    /// <summary>項次、科目名稱、來源模組、編製者、核准者與過帳狀態。摘要沒有上限。</summary>
    public const int TextUtf16CodeUnits = 400;

    /// <summary>假日與補班日的名稱。</summary>
    public const int CalendarDayNameUtf16CodeUnits = 256;
}
