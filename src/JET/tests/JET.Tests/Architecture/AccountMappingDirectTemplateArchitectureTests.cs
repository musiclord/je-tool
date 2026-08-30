using System.Reflection;
using System.Reflection.Emit;
using DocumentFormat.OpenXml.Packaging;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class AccountMappingDirectTemplateArchitectureTests
{
    [Fact]
    public void ProductionWriter_UsesDirectTemplatePackage_WithoutGeneratedWorkbook()
    {
        var directFill = typeof(ReportTemplatePackage).GetMethod(
            "FillDirectAsync",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Direct-template package entry point is missing.");
        var guardedMethods = EnumerateMethods(typeof(AccountMappingTemplateWriter))
            .Concat(EnumerateMethods(typeof(DirectTemplateWorkbookEditor)))
            .Concat(EnumerateMethods(typeof(DirectTemplateWorksheetRewriter)))
            .Concat(EnumerateAsyncImplementation(directFill));
        var calls = guardedMethods
            .SelectMany(ReadCalledMethods)
            .ToArray();

        Assert.Contains(calls, method =>
            method.DeclaringType == typeof(ReportTemplatePackage)
            && method.Name == "FillDirectAsync");
        Assert.DoesNotContain(calls, method =>
            method.DeclaringType == typeof(ReportTemplatePackage)
            && method.Name == "FillAsync");
        Assert.DoesNotContain(calls, method =>
            method.DeclaringType == typeof(SpreadsheetDocument)
            && method.Name == "Create");
    }

    private static IEnumerable<MethodInfo> EnumerateAsyncImplementation(MethodInfo method)
    {
        yield return method;
        var stateMachine = method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>();
        if (stateMachine is null)
        {
            yield break;
        }

        var moveNext = stateMachine.StateMachineType.GetMethod(
            "MoveNext",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("Async state machine has no MoveNext method.");
        yield return moveNext;
    }

    private static IEnumerable<MethodInfo> EnumerateMethods(Type root)
    {
        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Static
                                   | BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.DeclaredOnly;

        foreach (var method in root.GetMethods(flags))
        {
            yield return method;
        }

        foreach (var nested in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var method in EnumerateMethods(nested))
            {
                yield return method;
            }
        }
    }

    private static IEnumerable<MethodBase> ReadCalledMethods(MethodInfo method)
    {
        var body = method.GetMethodBody();
        var bytes = body?.GetILAsByteArray();
        if (bytes is null)
        {
            yield break;
        }

        var typeArguments = method.DeclaringType?.GetGenericArguments();
        var methodArguments = method.GetGenericArguments();
        for (var offset = 0; offset < bytes.Length;)
        {
            var opCode = ReadOpCode(bytes, ref offset);
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(bytes, offset);
                MethodBase? called = null;
                try
                {
                    called = method.Module.ResolveMethod(token, typeArguments, methodArguments);
                }
                catch (ArgumentException)
                {
                    // Invalid or context-specific metadata is irrelevant to this ownership guard.
                }

                if (called is not null)
                {
                    yield return called;
                }
            }

            offset += OperandSize(opCode.OperandType, bytes, offset);
        }
    }

    private static OpCode ReadOpCode(byte[] bytes, ref int offset)
    {
        var first = bytes[offset++];
        var value = first == 0xFE
            ? unchecked((short)(0xFE00 | bytes[offset++]))
            : (short)first;
        return OpCodesByValue[value];
    }

    private static int OperandSize(OperandType operandType, byte[] bytes, int offset) =>
        operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget
                or OperandType.InlineField
                or OperandType.InlineI
                or OperandType.InlineMethod
                or OperandType.InlineSig
                or OperandType.InlineString
                or OperandType.InlineTok
                or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(bytes, offset) * 4),
            _ => throw new InvalidOperationException($"Unknown IL operand type '{operandType}'.")
        };

    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => opCode.Value);
}
