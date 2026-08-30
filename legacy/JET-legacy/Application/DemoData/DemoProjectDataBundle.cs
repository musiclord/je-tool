using JET.Application.Contracts;

namespace JET.Application.DemoData
{
    public sealed record DemoProjectDataBundle(
        DemoProjectDto Project,
        DemoGlRowsDto Gl,
        DemoTbRowsDto Tb,
        DemoAccountMappingRowsDto AccountMapping,
        IReadOnlyList<Dictionary<string, object?>> InvalidGlRows);
}
