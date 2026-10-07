using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class AuditDependencyPolicyTests
{
    public static IEnumerable<object[]> ExactMatrix =>
    [
        [AuditMutation.GlImport, true, true, true],
        [AuditMutation.GlProjection, true, true, true],
        [AuditMutation.TbImport, true, true, true],
        [AuditMutation.TbProjection, true, true, true],
        [AuditMutation.Calendar, false, true, true],
        [AuditMutation.AccountMapping, false, true, true],
        [AuditMutation.AuthorizedPreparer, false, true, true],
        [AuditMutation.PreparationDate, false, true, true],
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
        // 使用者 2026-10-07 裁定上游修改清除下游：情境定義改為與命中同進退，不再是保留例外。
        // 第一次失敗收據 20261007-033332600-7474a4e6d5034eec90a0e1e97389993a。
        Assert.Equal(filterHits, impact.InvalidateFilterScenarioDefinitions);
        Assert.False(impact.ClearGlControlTotal);
    }
}
