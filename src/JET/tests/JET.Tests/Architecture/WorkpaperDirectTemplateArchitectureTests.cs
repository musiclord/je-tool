using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class WorkpaperDirectTemplateArchitectureTests
{
    [Fact]
    public void BothProductionWriterEntrypoints_ReachStreamingDirectFillWithoutWholeWorksheetDom()
    {
        foreach (var root in ProductionEntrypoints())
        {
            var reachable = ReadReachableProductionCalls(root).ToArray();

            Assert.Contains(reachable, method =>
                method.DeclaringType == typeof(ReportTemplatePackage)
                && method.Name == "FillDirectStreamingAsync");
            Assert.Contains(reachable, method =>
                method.DeclaringType == typeof(DirectTemplateWorksheetOverlayWriter)
                && method.Name == "WriteRow");
            Assert.DoesNotContain(reachable, method =>
                method.DeclaringType == typeof(ReportTemplatePackage)
                && method.Name == "FillAsync");
            Assert.DoesNotContain(reachable, method =>
                method.DeclaringType == typeof(SpreadsheetDocument)
                && method.Name == "Create");
            Assert.DoesNotContain(reachable, method =>
                method.DeclaringType == typeof(WorksheetPart)
                && method.Name == "get_Worksheet");
            Assert.DoesNotContain(reachable, method =>
                method.DeclaringType == typeof(XDocument)
                && method.Name == nameof(XDocument.Load));
            Assert.DoesNotContain(reachable, method =>
                IsOpenXmlDescendants(method));
        }
    }

    [Fact]
    public void ProductionWriter_DoesNotUseWholeWorksheetDomOrCellDescendantTraversal()
    {
        var exportDirectory = Path.Combine(JetRoot(), "JET", "Infrastructure", "Export");
        var source = string.Join(
            Environment.NewLine,
            Directory.GetFiles(exportDirectory, "WorkpaperWriter*.cs", SearchOption.TopDirectoryOnly)
                .Append(Path.Combine(exportDirectory, "DirectTemplateWorksheetOverlay.cs"))
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));

        Assert.DoesNotContain("TemplateWorkbookMerger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Descendants<Cell>", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Worksheet.CloneNode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("XDocument.Load", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalizedStep41_UsesPreparedSessionAndCannotReachTypedPaging()
    {
        var root = typeof(WorkpaperWriter).GetMethod(
            "EmitStep41Async",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Finalized WorkingPaper Step4-1 emitter is missing.");
        var reachable = ReadReachableProductionCalls(root).ToArray();

        Assert.Contains(reachable, method =>
            method.DeclaringType == typeof(IWorkpaperStep41PreparedSessionFactory)
            && method.Name == nameof(IWorkpaperStep41PreparedSessionFactory.PrepareAsync));
        Assert.Contains(reachable, method =>
            method.DeclaringType == typeof(IWorkpaperStep41PreparedSession)
            && method.Name == nameof(IWorkpaperStep41PreparedSession.ReadRowsAsync));
        Assert.DoesNotContain(reachable, method =>
            method.DeclaringType == typeof(IWorkpaperStep41PageRepository)
            && method.Name == nameof(IWorkpaperStep41PageRepository.GetPageAsync));
    }

    [Fact]
    public void FinalizedStep41_RdeProjectionRunsAfterPreparedSessionScope()
    {
        var source = File.ReadAllText(Path.Combine(
            JetRoot(),
            "JET",
            "Infrastructure",
            "Export",
            "WorkpaperWriter.Step2To41.cs"));
        var start = source.IndexOf(
            "private async Task EmitStep41Async(",
            StringComparison.Ordinal);
        Assert.True(start >= 0, "Finalized WorkingPaper Step4-1 emitter is missing.");
        var end = source.IndexOf(
            "private static SheetWriter CreateStep41Page(",
            start,
            StringComparison.Ordinal);
        Assert.True(end > start, "Finalized WorkingPaper Step4-1 emitter boundary is missing.");
        var emitter = source[start..end];

        var preparedScope = emitter.IndexOf(
            "await using (var prepared = await _step41PreparedSessions.PrepareAsync(",
            StringComparison.Ordinal);
        var sourceReplay = emitter.IndexOf(
            "if (sourceSpool is not null)",
            StringComparison.Ordinal);
        var rdeRead = emitter.IndexOf(
            "ReadWorkpaperRdeValuesAsync(",
            StringComparison.Ordinal);

        Assert.True(preparedScope >= 0, "Step4-1 prepared session scope is missing.");
        Assert.True(sourceReplay > preparedScope, "RDE source replay must follow prepared setup.");
        Assert.True(rdeRead > sourceReplay, "RDE reads must start only after prepared disposal.");
        Assert.Contains("Step41SourceSpool.Create()", emitter, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReadWorkpaperRdeValuesAsync(",
            emitter[preparedScope..sourceReplay],
            StringComparison.Ordinal);
    }

    [Fact]
    public void FinalizedStep4_UsesSingleVoucherStreamAndCannotReachPublicPaging()
    {
        var source = File.ReadAllText(Path.Combine(
            JetRoot(),
            "JET",
            "Infrastructure",
            "Export",
            "WorkpaperWriter.Step2To41.cs"));
        var start = source.IndexOf(
            "private async IAsyncEnumerable<TaggedVoucherRow> StreamFinalizedStep4VoucherRowsAsync(",
            StringComparison.Ordinal);
        Assert.True(start >= 0, "Finalized WorkingPaper Step4 stream adapter is missing.");
        var end = source.IndexOf(
            "private async IAsyncEnumerable<WorkpaperStep41SourceRow> StreamWorkpaperStep41RowsAsync(",
            start,
            StringComparison.Ordinal);
        Assert.True(end > start, "Finalized WorkingPaper Step4 stream adapter boundary is missing.");
        var adapter = source[start..end];

        Assert.Contains("_step4Vouchers.StreamAsync(", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPageAsync(", adapter, StringComparison.Ordinal);
    }

    private static IReadOnlyList<MethodInfo> ProductionEntrypoints()
    {
        var interfaceMethod = typeof(IWorkpaperPlanWriter).GetMethod(
            nameof(IWorkpaperPlanWriter.WriteAsync))
            ?? throw new InvalidOperationException("Planned WorkingPaper writer method is missing.");
        var interfaceMap = typeof(WorkpaperWriter).GetInterfaceMap(typeof(IWorkpaperPlanWriter));
        var interfaceIndex = Array.FindIndex(
            interfaceMap.InterfaceMethods,
            candidate => candidate.MetadataToken == interfaceMethod.MetadataToken
                         && candidate.Module == interfaceMethod.Module);
        Assert.InRange(interfaceIndex, 0, interfaceMap.InterfaceMethods.Length - 1);

        var publicMethod = typeof(WorkpaperWriter)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method =>
                method.Name == nameof(WorkpaperWriter.WriteAsync)
                && method.GetParameters() is
                [
                    { ParameterType: var output },
                    { ParameterType: var context },
                    { ParameterType: var cancellation },
                    { ParameterType: var progress }
                ]
                && output == typeof(Stream)
                && context == typeof(WorkpaperContext)
                && cancellation == typeof(CancellationToken)
                && progress == typeof(Action<WorkpaperProgress>));
        return [interfaceMap.TargetMethods[interfaceIndex], publicMethod];
    }

    private static bool IsOpenXmlDescendants(MethodBase method)
    {
        if (method.Name != nameof(OpenXmlElement.Descendants)
            || method.DeclaringType is null)
        {
            return false;
        }

        var declaringType = method.DeclaringType.IsGenericType
            ? method.DeclaringType.GetGenericTypeDefinition()
            : method.DeclaringType;
        return declaringType == typeof(OpenXmlElement);
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
            if (type == typeof(WorkpaperWriter)
                || type == typeof(ReportTemplatePackage)
                || type == typeof(DirectTemplateWorkbookEditor)
                || type == typeof(DirectTemplateWorksheetOverlayWriter)
                || type == typeof(WorkbookStyleMap))
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
                    // Generic/compiler-generated metadata outside this bounded graph is irrelevant.
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

    private static string JetRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }

    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => opCode.Value);
}
