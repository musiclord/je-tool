namespace JET.Application.Contracts
{
    // Metadata-only: rows are fetched separately via demo.fetchGlRows / demo.fetchTbRows /
    // demo.fetchAccountMappingRows so that the demo flow drives the same import.* pipeline
    // as user uploads.
    public sealed record DemoProjectDto(
        string ProjectCode,
        string EntityName,
        string OperatorId,
        string Industry,
        string PeriodStart,
        string PeriodEnd,
        string LastPeriodStart,
        string GlFileName,
        string TbFileName,
        string AccountMappingFileName,
        IReadOnlyDictionary<string, string> GlMapping,
        IReadOnlyDictionary<string, string> TbMapping,
        IReadOnlyList<string> Holidays,
        IReadOnlyList<string> MakeupDays,
        IReadOnlyList<int> Weekends);

    public sealed record DemoGlRowsDto(
        string FileName,
        IReadOnlyList<Dictionary<string, object?>> Rows,
        IReadOnlyList<string> Columns);

    public sealed record DemoTbRowsDto(
        string FileName,
        IReadOnlyList<Dictionary<string, object?>> Rows,
        IReadOnlyList<string> Columns);

    public sealed record DemoAccountMappingRowsDto(
        string FileName,
        IReadOnlyList<Dictionary<string, object?>> Rows);

    public sealed record DemoExportFileDto(
        string FilePath,
        string FileName);
}
