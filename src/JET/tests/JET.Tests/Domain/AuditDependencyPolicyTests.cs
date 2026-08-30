using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class AuditDependencyPolicyTests
{
    public static IEnumerable<object[]> ExactMatrix =>
    [
        [AuditMutation.GlImport, true, true, true],
        [AuditMutation.GlProjection, true, true, true],
        [AuditMutation.TbImport, true, false, false],
        [AuditMutation.TbProjection, true, false, false],
        [AuditMutation.Calendar, false, true, true],
        [AuditMutation.AccountMapping, false, true, true],
        [AuditMutation.AuthorizedPreparer, false, true, true],
        [AuditMutation.SchemaV7Migration, true, true, true],
        [AuditMutation.AccountTaxonomy, false, true, true]
    ];

    [Fact]
    public void ExactMatrix_CoversEveryMutationExactlyOnce()
    {
        var registered = ExactMatrix
            .Select(row => Assert.IsType<AuditMutation>(row[0]))
            .Order()
            .ToArray();

        Assert.Equal(Enum.GetValues<AuditMutation>().Order(), registered);
    }

    [Theory]
    [MemberData(nameof(ExactMatrix))]
    internal void MutationEffects_MatchFrozenInvalidationMatrix(
        AuditMutation mutation,
        bool validation,
        bool prescreen,
        bool filterHits)
    {
        var impact = AuditDependencyPolicy.For(mutation);

        Assert.Equal(validation, impact.InvalidateValidation);
        Assert.Equal(prescreen, impact.InvalidatePrescreen);
        Assert.Equal(filterHits, impact.InvalidateFilterHits);
        Assert.False(impact.InvalidateFilterScenarioDefinitions);
        Assert.False(impact.ClearGlControlTotal);
    }
}
