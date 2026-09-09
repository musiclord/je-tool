// JET-only early file selection. The pinned upstream applies its mutate filter
// after rewriting every file (upstream issues #3573 and #962).
using System;
using System.Collections.Generic;
using System.Linq;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Configuration;
using Stryker.Core.ProjectComponents.Csharp;
using Stryker.Utilities;

namespace Stryker.Core.MutationTest;

public static class JetMutationSourceSelection
{
    public static IReadOnlyList<CsharpFileLeaf> Select(IReadOnlyCollection<CsharpFileLeaf> files, IStrykerOptions options)
    {
        var selectedPath = Environment.GetEnvironmentVariable("JET_MUTATION_TEST_CLASS") switch
        {
            "JET.Tests.Domain.GlProjectionGuardTests" => "Domain/Rules/GlProjectionGuard.cs",
            "JET.Tests.Domain.MoneyScalingTests" => "Domain/Primitives/MoneyScaling.cs",
            _ => throw new InvalidOperationException("JET mutation source scope: invalid test class.")
        };
        var patterns = options.Mutate?.ToArray();
        var expectedPattern = FilePattern.Parse(FilePathUtils.NormalizePathSeparators(selectedPath));
        if (patterns is null || patterns.Length != 1 || !expectedPattern.Equals(patterns[0]))
            throw new InvalidOperationException("JET mutation source scope: expected exactly the complete authorized source file.");
        var selected = files.Where(file => string.Equals(file.RelativePath?.Replace('\\', '/'), selectedPath, StringComparison.Ordinal)).ToArray();
        if (selected.Length != 1)
            throw new InvalidOperationException("JET mutation source scope: expected exactly one matching source tree.");

        // Keep every other tree in the same product compilation without asking
        // the orchestrator to generate, place, or later discard mutations there.
        foreach (var file in files)
        {
            if (ReferenceEquals(file, selected[0])) continue;
            file.MutatedSyntaxTree = file.SyntaxTree;
            file.Mutants = Array.Empty<IMutant>();
        }
        return selected;
    }
}
