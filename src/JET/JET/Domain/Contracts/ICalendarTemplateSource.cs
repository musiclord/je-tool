namespace JET.Domain;

/// <summary>提供隨程式發布的假日與補班日範本，不匯入資料。</summary>
public interface ICalendarTemplateSource
{
    IReadOnlyList<string> FileNames { get; }
    Task CopyToAsync(string fileName, Stream output, CancellationToken cancellationToken);
}
