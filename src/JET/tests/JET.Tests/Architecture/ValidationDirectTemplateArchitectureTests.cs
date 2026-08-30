using System.Reflection;
using System.Reflection.Emit;
using DocumentFormat.OpenXml.Packaging;
using JET.AuditCore;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class ValidationDirectTemplateArchitectureTests
{
    [Fact]
    public void PlannedProductionWriter_ReachesStreamingDirectFillWithoutGeneratedWorkbook()
    {
        var interfaceMethod = typeof(IPlannedValidationReportWriter).GetMethod(
            nameof(IPlannedValidationReportWriter.WritePlannedAsync))
            ?? throw new InvalidOperationException("Planned validation writer method is missing.");
        var interfaceMap = typeof(LegacyReportWriter).GetInterfaceMap(
            typeof(IPlannedValidationReportWriter));
        var interfaceIndex = Array.FindIndex(
            interfaceMap.InterfaceMethods,
            candidate => candidate.MetadataToken == interfaceMethod.MetadataToken
                         && candidate.Module == interfaceMethod.Module);
        Assert.InRange(interfaceIndex, 0, interfaceMap.InterfaceMethods.Length - 1);
        var root = interfaceMap.TargetMethods[interfaceIndex];

        var reachable = ReadReachableProductionCalls(root).ToArray();

        Assert.Contains(reachable, method =>
            method.DeclaringType == typeof(ReportTemplatePackage)
            && method.Name == "FillDirectStreamingAsync");
        Assert.DoesNotContain(reachable, method =>
            method.DeclaringType == typeof(ReportTemplatePackage)
            && method.Name == "FillAsync");
        Assert.DoesNotContain(reachable, method =>
            method.DeclaringType == typeof(LegacyReportWriter)
            && method.Name == "CreateWorkbook");
        Assert.DoesNotContain(reachable, method =>
            method.DeclaringType == typeof(SpreadsheetDocument)
            && method.Name == "Create");
    }

    private static IEnumerable<MethodBase> ReadReachableProductionCalls(MethodInfo root)
    {
        var pending = new Queue<MethodInfo>();
        var visited = new HashSet<MethodInfo>();
        var calls = new List<MethodBase>();
        pending.Enqueue(root);

        while (pending.TryDequeue(out var method))
        {
            if (!visited.Add(method))
            {
                continue;
            }

            foreach (var implementation in EnumerateAsyncImplementation(method))
            {
                if (!visited.Add(implementation) && implementation != method)
                {
                    continue;
                }

                foreach (var called in ReadCalledMethods(implementation))
                {
                    calls.Add(called);
                    if (called is MethodInfo calledMethod && IsProductionOwned(calledMethod.DeclaringType))
                    {
                        pending.Enqueue(calledMethod);
                    }
                }
            }
        }

        return calls;
    }

    private static IEnumerable<MethodInfo> EnumerateAsyncImplementation(MethodInfo method)
    {
        yield return method;
        var stateMachine = method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>();
        if (stateMachine is null)
        {
            yield break;
        }

        yield return stateMachine.StateMachineType.GetMethod(
                         "MoveNext",
                         BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                     ?? throw new InvalidOperationException("Async state machine has no MoveNext method.");
    }

    private static bool IsProductionOwned(Type? type)
    {
        while (type is not null)
        {
            if (type == typeof(LegacyReportWriter) || type == typeof(ReportTemplatePackage))
            {
                return true;
            }

            type = type.DeclaringType;
        }

        return false;
    }

    private static IEnumerable<MethodBase> ReadCalledMethods(MethodInfo method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
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
                    // Generic/compiler generated metadata outside this bounded graph is irrelevant.
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
