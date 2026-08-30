using JET.AuditCore;
using JET.Domain;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 審計程序總表的漂移守衛：程序步驟、RuleCatalog slug、JetSchemaCatalog 正準名與
/// dispatcher action 必須各自仍指向既有單一事實來源。
/// </summary>
public sealed class AuditProgramParityTests
{
    [Fact]
    public void ProgramDefinitions_CoverSixStepMainline()
    {
        var steps = JetAuditProgram.Procedures
            .Select(definition => definition.WorkflowStep)
            .Distinct()
            .OrderBy(static step => step)
            .ToArray();

        Assert.Equal(Enumerable.Range(0, 6), steps);
    }

    [Fact]
    public void ValidationProcedureSlugs_EqualRuleCatalogValidationSlugs()
    {
        var expected = RuleCatalog.All
            .Where(rule => rule.Shape == RuleShape.Validation)
            .Select(rule => rule.Slug)
            .OrderBy(static slug => slug, StringComparer.Ordinal)
            .ToArray();
        var actual = JetAuditProgram.Procedures
            .Where(definition => definition.ActionName == "validate.run")
            .Select(definition => definition.Slug)
            .OrderBy(static slug => slug, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PrescreenProcedureSlugs_EqualRuleCatalogPrescreenSlugs()
    {
        var expected = RuleCatalog.All
            .Where(rule => rule.Shape is RuleShape.RowTag or RuleShape.Aggregate)
            .Select(rule => rule.Slug)
            .OrderBy(static slug => slug, StringComparer.Ordinal)
            .ToArray();
        var actual = JetAuditProgram.Procedures
            .Where(definition => definition.ActionName == "prescreen.run")
            .Select(definition => definition.Slug)
            .OrderBy(static slug => slug, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ProgramArtifacts_AreJetSchemaCatalogCanonicalNames()
    {
        var canonicalNames = JetSchemaCatalog.All
            .Select(entry => entry.CanonicalName)
            .ToHashSet(StringComparer.Ordinal);
        var artifacts = JetAuditProgram.Procedures
            .SelectMany(definition => definition.RequiredInputs
                .Concat(definition.SoftInputs)
                .Concat(definition.Outputs))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(artifacts);
        Assert.All(artifacts, artifact => Assert.Contains(artifact, canonicalNames));
    }

    [Fact]
    public void ExecutableProgramActions_AreDispatcherRegisteredActions()
    {
        var actions = JetAuditProgram.Procedures
            .Select(definition => definition.ActionName)
            .Where(static action => action is not null)
            .Select(static action => action!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(actions);
        using var host = new HandlerTestHost(enableDevTools: true);
        Assert.All(actions, action => Assert.Contains(action, host.Dispatcher.RegisteredActions));
    }
}
