using JET.AuditCore;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class FilterCompilationOwnershipTests
{
    private static readonly System.Reflection.Assembly ProductionAssembly = typeof(JetAuditProgram).Assembly;

    [Theory]
    [InlineData("GlFilterWhereBuilder")]
    [InlineData("GlPopulationScopeSql")]
    [InlineData("GlEffectivePopulation")]
    [InlineData("NullRecordsCategoryPredicate")]
    [InlineData("GlRulePredicates")]
    [InlineData("RuleCatalog")]
    [InlineData("RuleDescriptor")]
    [InlineData("RuleShape")]
    [InlineData("JetSchemaCatalog")]
    [InlineData("SchemaTableEntry")]
    [InlineData("SchemaLayer")]
    [InlineData("SchemaAudience")]
    public void CoreSemanticType_IsInternalAndOwnedByAuditCore(string typeName)
    {
        var coreType = ProductionAssembly.GetType("JET.AuditCore." + typeName);

        Assert.NotNull(coreType);
        Assert.False(coreType.IsPublic);
        Assert.Null(ProductionAssembly.GetType("JET.Infrastructure." + typeName));
        Assert.Null(ProductionAssembly.GetType("JET.Domain." + typeName));
    }
}
