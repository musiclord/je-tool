using JET.AuditCore;
using JET.Domain;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 審計程序總表的漂移守衛：RuleCatalog slug 與 dispatcher action 必須各自仍指向既有單一事實來源。
/// </summary>
public sealed class AuditProgramParityTests
{
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
    public void ExecutableProgramActions_AreDispatcherRegisteredActions()
    {
        var actions = JetAuditProgram.Procedures
            .Select(definition => definition.ActionName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(actions);
        using var host = new HandlerTestHost(enableDevTools: true);
        Assert.All(actions, action => Assert.Contains(action, host.Dispatcher.RegisteredActions));
    }

    // 自 AuditCore/ProgramGraphTests 搬來：舊的遷移狀態型別已在最後一次切換後移除，不得再出現。
    [Fact]
    public void MigrationStateType_IsRemovedAfterFinalGate()
    {
        var assembly = typeof(JetAuditProgram).Assembly;

        Assert.Null(assembly.GetType("JET.AuditCore.ProgramMigrationState"));
    }
}
