using System.Data.Common;
using System.Reflection;
using JET.Domain;
using NetArchTest.Rules;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// AuditCore 不得持有 <see cref="DbCommand"/> 或 System.Data.Common execution seam。
/// SQL fragment 與 ordered values 是純計畫，命令建立與綁定只屬於 Infrastructure。
/// </summary>
public sealed class AuditCoreDataCommonMigrationTests
{
    private const string AuditCoreNamespace = "JET.AuditCore";

    private static readonly Assembly ProductionAssembly =
        typeof(JetActionException).Assembly;

    [Fact]
    public void AuditCore_SystemDataCommonAndDbCommandOwners_MatchCurrentMigrationWhitelist()
    {
        var expected = Array.Empty<string>();

        var dataCommonOwners = Types.InAssembly(ProductionAssembly)
            .That().ResideInNamespace(AuditCoreNamespace)
            .And().HaveDependencyOn("System.Data.Common")
            .GetTypes()
            .Select(TopLevelOwner)
            .Select(type => type.FullName ?? type.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        var dbCommandSignatureOwners = ProductionAssembly.GetTypes()
            .Where(type => string.Equals(type.Namespace, AuditCoreNamespace, StringComparison.Ordinal))
            .Where(DeclaresDbCommandSignature)
            .Select(TopLevelOwner)
            .Select(type => type.FullName ?? type.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        AssertExactOwners(
            expected,
            dataCommonOwners,
            "AuditCore 直接依賴 System.Data.Common 的型別");
        AssertExactOwners(
            expected,
            dbCommandSignatureOwners,
            "AuditCore 宣告 DbCommand signature 的型別");
    }

    private static Type TopLevelOwner(Type type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type;
    }

    private static bool DeclaresDbCommandSignature(Type type)
    {
        const BindingFlags flags = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        return type.GetConstructors(flags)
                .Any(constructor => constructor.GetParameters().Any(parameter =>
                    ContainsDbCommand(parameter.ParameterType)))
            || type.GetMethods(flags)
                .Any(method => ContainsDbCommand(method.ReturnType)
                    || method.GetParameters().Any(parameter =>
                        ContainsDbCommand(parameter.ParameterType)))
            || type.GetFields(flags).Any(field => ContainsDbCommand(field.FieldType))
            || type.GetProperties(flags).Any(property => ContainsDbCommand(property.PropertyType));
    }

    private static bool ContainsDbCommand(Type type)
    {
        if (typeof(DbCommand).IsAssignableFrom(type))
        {
            return true;
        }

        if (type.HasElementType)
        {
            return ContainsDbCommand(type.GetElementType()!);
        }

        return type.IsGenericType && type.GetGenericArguments().Any(ContainsDbCommand);
    }

    private static void AssertExactOwners(
        IReadOnlyCollection<string> expected,
        IReadOnlyCollection<string> actual,
        string label)
    {
        var orderedExpected = expected.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        var orderedActual = actual.OrderBy(static name => name, StringComparer.Ordinal).ToArray();

        Assert.True(
            orderedExpected.SequenceEqual(orderedActual, StringComparer.Ordinal),
            label + "必須精確等於目前 migration whitelist。" +
            "\n預期：\n  " + string.Join("\n  ", orderedExpected) +
            "\n實際：\n  " + string.Join("\n  ", orderedActual));
    }
}
