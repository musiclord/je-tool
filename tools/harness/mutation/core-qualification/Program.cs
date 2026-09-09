using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Configuration;
using Stryker.Configuration.Options;
using Stryker.Core.InjectedHelpers;
using Stryker.Core.Mutants;
using Stryker.Core.MutationTest;
using Stryker.Core.ProjectComponents.Csharp;
using Stryker.Utilities;
using Stryker.Utilities.Logging;

const string selectedPath = "Domain/Rules/GlProjectionGuard.cs";
const string selectedSource = "public static class Selected { public static bool Rule(long count, long debit, long credit) => count > 0 && debit == 0 && credit == 0; }";
// The out-var declaration moves conditional-expression mutations to block level.
// In a record constructor initializer they remain pending, but the block-bodied
// constructor has no expression body for the pinned injector to dereference.
const string controlSource = "public sealed record Control(int Value) { public Control(string text) : this(int.TryParse(text, out var parsed) ? parsed : 0) { } }";
using var loggerFactory = new LoggerFactory();
ApplicationLogging.LoggerFactory = loggerFactory;
var assertions = 0;
var cases = new List<string>();
void Check(bool condition, string reason) { assertions++; if (!condition) throw new InvalidOperationException(reason); }
void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidOperationException ex) when (ex.Message.StartsWith("JET mutation source scope:", StringComparison.Ordinal)) { assertions++; cases.Add(name); return; }
    throw new InvalidOperationException("Expected source selection rejection: " + name);
}
StrykerOptions Options(string pattern = selectedPath) => new()
{
    MutationLevel = MutationLevel.Standard,
    OptimizationMode = OptimizationModes.CoverageBasedTest,
    Mutate = [FilePattern.Parse(FilePathUtils.NormalizePathSeparators(pattern))]
};
CsharpMutantOrchestrator Orchestrator() => new(new MutantPlacer(new CodeInjection()), options: Options());
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Distinct().Select(path => MetadataReference.CreateFromFile(path)).ToArray();
var selectedTree = CSharpSyntaxTree.ParseText(selectedSource, path: selectedPath);
var controlTree = CSharpSyntaxTree.ParseText(controlSource, path: "Unselected/Control.cs");
var compilation = CSharpCompilation.Create("SyntheticMutationQualification", [selectedTree, controlTree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "synthetic inputs must compile without mutation");
var reproduced = false;
try { Orchestrator().Mutate(controlTree, compilation.GetSemanticModel(controlTree)); }
catch (NullReferenceException ex) when (ex.StackTrace?.Contains("RoslynHelper.InjectMutation", StringComparison.Ordinal) == true) { reproduced = true; }
Check(reproduced, "the unselected synthetic record must reproduce the pinned upstream NullReferenceException");
cases.Add("unselected_record_reproduces_original_crash");

var baselineOrchestrator = Orchestrator();
baselineOrchestrator.Mutate(selectedTree, compilation.GetSemanticModel(selectedTree));
var baseline = baselineOrchestrator.GetLatestMutantBatch().ToArray();
var selected = new CsharpFileLeaf { RelativePath = selectedPath, FullPath = selectedPath, SourceCode = selectedSource, SyntaxTree = selectedTree };
var control = new CsharpFileLeaf { RelativePath = "Unselected/Control.cs", FullPath = "Unselected/Control.cs", SourceCode = controlSource, SyntaxTree = controlTree };
Environment.SetEnvironmentVariable("JET_MUTATION_TEST_CLASS", "JET.Tests.Domain.GlProjectionGuardTests");
var selectedFiles = JetMutationSourceSelection.Select([selected, control], Options());
Check(selectedFiles.Count == 1 && ReferenceEquals(selectedFiles[0], selected), "exactly the authorized file reaches the orchestrator");
Check(ReferenceEquals(control.MutatedSyntaxTree, controlTree), "unselected syntax tree identity is preserved");
Check(control.CompilationSyntaxTrees.Single().ToString() == controlTree.ToString(), "unselected compilation tree text is identical");
Check(!control.Mutants.Any(), "unselected file has zero mutants");
var guardedOrchestrator = Orchestrator();
foreach (var file in selectedFiles)
{
    file.MutatedSyntaxTree = guardedOrchestrator.Mutate(file.SyntaxTree!, compilation.GetSemanticModel(file.SyntaxTree!));
    file.Mutants = guardedOrchestrator.GetLatestMutantBatch();
}
string Signature(IMutant mutant) => string.Join("|", mutant.Mutation.OriginalNode.Span.Start, mutant.Mutation.OriginalNode.Span.Length, mutant.Mutation.ReplacementNode.ToFullString());
Check(baseline.Length > 0 && baseline.Select(Signature).Order().SequenceEqual(selected.Mutants.Select(Signature).Order()), "all default mutations of the selected function are retained");
cases.Add("selected_default_mutators_unchanged");
cases.Add("unselected_tree_preserved_in_compilation");
var logical = selected.Mutants.Where(m => m.Mutation.OriginalNode.IsKind(SyntaxKind.LogicalAndExpression) && m.Mutation.ReplacementNode.IsKind(SyntaxKind.LogicalOrExpression)).ToArray();
Check(logical.Length == 2, "both known logical AND to OR mutants must exist");
var oracle = new (long Count, long Debit, long Credit, bool Expected)[] { (0, 0, 0, false), (1, 0, 0, true), (1, 1, 0, false), (1, 0, 1, false) };
var killed = 0;
foreach (var mutant in logical)
{
    var rawMutatedTree = selectedTree.WithRootAndOptions(selectedTree.GetRoot().ReplaceNode(mutant.Mutation.OriginalNode, mutant.Mutation.ReplacementNode), selectedTree.Options);
    using var image = new MemoryStream();
    var result = compilation.ReplaceSyntaxTree(selectedTree, rawMutatedTree).Emit(image);
    Check(result.Success, "each known logical mutant compiles together with the unchanged record");
    image.Position = 0;
    var context = new AssemblyLoadContext("synthetic-" + mutant.Id, isCollectible: true);
    try
    {
        var assembly = context.LoadFromStream(image);
        var method = assembly.GetType("Selected")!.GetMethod("Rule", BindingFlags.Public | BindingFlags.Static)!;
        if (oracle.Any(row => (bool)method.Invoke(null, [row.Count, row.Debit, row.Credit])! != row.Expected)) killed++;
    }
    finally { context.Unload(); }
}
Check(killed == 2, "independent fixed examples must detect both logical mutants");
cases.Add("independent_oracle_kills_both_logical_mutants");
Reject(() => JetMutationSourceSelection.Select([selected, control], Options("**/*")), "wildcard_source_rejected");
Reject(() => JetMutationSourceSelection.Select([selected, control], Options("Domain/Primitives/MoneyScaling.cs")), "different_source_rejected");
Reject(() => JetMutationSourceSelection.Select([selected, control], Options(selectedPath + "{1..5}")), "partial_source_rejected");
Reject(() => JetMutationSourceSelection.Select([selected, control], new StrykerOptions { Mutate = [] }), "empty_source_scope_rejected");
Reject(() => JetMutationSourceSelection.Select([control], Options()), "missing_selected_tree_rejected");
Reject(() => JetMutationSourceSelection.Select([selected, selected], Options()), "duplicate_selected_tree_rejected");
Environment.SetEnvironmentVariable("JET_MUTATION_TEST_CLASS", "*");
Reject(() => JetMutationSourceSelection.Select([selected, control], Options()), "invalid_test_scope_rejected");
Environment.SetEnvironmentVariable("JET_MUTATION_TEST_CLASS", "JET.Tests.Domain.MoneyScalingTests");
var money = new CsharpFileLeaf { RelativePath = "Domain\\Primitives\\MoneyScaling.cs", SyntaxTree = selectedTree };
Check(JetMutationSourceSelection.Select([money, control], Options("Domain/Primitives/MoneyScaling.cs")).Single() == money, "second fixed scope and Windows path separators are supported");
cases.Add("money_scaling_scope");
var actualProcess = File.ReadAllText(Path.Combine(args[0], "src/Stryker.Core/Stryker.Core/MutationTest/CsharpMutationProcess.cs"));
Check(actualProcess.Contains("foreach (var file in JetMutationSourceSelection.Select(semanticModels.Keys.ToArray(), _options))", StringComparison.Ordinal), "actual mutation process selects files before calling the unchanged orchestrator");
Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", assertions, cases, baselineMutants = baseline.Length, selectedMutants = selected.Mutants.Count(), logicalMutantsKilled = killed, originalNullReferenceReproduced = reproduced, productSourceRead = false }));
