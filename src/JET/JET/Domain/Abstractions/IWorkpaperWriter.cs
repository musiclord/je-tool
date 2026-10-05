namespace JET.Domain;

/// <summary>
/// 匯出底稿(WorkingPaper).xlsx 寫出器的窄介面(細節全藏在實作裡):一次呼叫把整份底稿
/// 串流寫進 <paramref name="output"/>,回 <see cref="ExportStats"/>(位元組數 + 各表列數)。
///
/// 為什麼介面在 Domain、實作在 Infrastructure:Application 的 export handler 注入此介面編排匯出,
/// 不該依賴 OpenXML;真實母體達百萬列,寫出器內部走 SAX 串流(實作藏在 Infrastructure)。
/// 介面只暴露「寫出→統計」,所有 OpenXML 元素順序/樣式/合併/inline string 細節對 caller 不可見。
/// </summary>
public interface IWorkpaperWriter
{
    /// <summary>
    /// 依 <paramref name="context"/> 把匯出底稿串流寫進 <paramref name="output"/>。
    /// caller 負責 <paramref name="output"/> 的生命週期(寫出器不關閉它,以便 caller 取長度/續寫)。
    /// </summary>
    Task<ExportStats> WriteAsync(
        Stream output,
        WorkpaperContext context,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}
