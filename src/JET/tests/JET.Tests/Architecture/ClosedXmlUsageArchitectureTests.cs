using JET.Infrastructure;
using NetArchTest.Rules;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class ClosedXmlUsageArchitectureTests
{
    private static readonly System.Reflection.Assembly ProductionAssembly =
        typeof(global::JET.Domain.JetActionException).Assembly;

    private static readonly HashSet<string> AllowedOwners =
        new(StringComparer.Ordinal)
        {
            typeof(DemoWorkbookWriter).FullName!,
            typeof(AccountMappingTemplateWriter).FullName!
        };

    [Fact]
    public void ProductionClosedXmlDependencies_AreLimitedToDocumentedWriters()
    {
        var actualOwners = Types.InAssembly(ProductionAssembly)
            .That().HaveDependencyOn("ClosedXML")
            .GetTypes()
            .Select(TopLevelOwner)
            .Select(type => type.FullName ?? type.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(typeof(DemoWorkbookWriter).FullName!, actualOwners);

        var unexpected = actualOwners
            .Where(owner => !AllowedOwners.Contains(owner))
            .ToArray();

        Assert.True(
            unexpected.Length == 0,
            "ClosedXML production dependency exceeds the documented owner allowlist:" +
            "\n  " + string.Join("\n  ", unexpected) +
            "\nAll detected owners:" +
            "\n  " + string.Join("\n  ", actualOwners));
    }

    private static Type TopLevelOwner(Type type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type;
    }
}
