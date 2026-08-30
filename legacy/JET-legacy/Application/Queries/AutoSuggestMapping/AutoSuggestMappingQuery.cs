namespace JET.Application.Queries.AutoSuggestMapping
{
    public sealed record AutoSuggestMappingQuery(
        IReadOnlyList<FieldDefinition> Fields,
        IReadOnlyList<string> Columns);

    public sealed record FieldDefinition(string Key, string Label, bool Req, string Type);
}
