using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class JetProjectFolderTests
{
    [Fact]
    public void GetProjectDirectory_InvalidProjectId_ThrowsProjectNotFound()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);

        var exception = Assert.Throws<JetActionException>(() => folder.GetProjectDirectory(@"..\..\evil"));

        Assert.Equal(JetErrorCodes.ProjectNotFound, exception.Code);
        Assert.Contains(@"..\..\evil", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetProjectDirectory_ValidProjectId_ReturnsRootCombinedPath()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);

        var directory = folder.GetProjectDirectory("案件 A-01");

        Assert.Equal(Path.Combine(root.Path, "案件 A-01"), directory);
    }

    [Fact]
    public void EnumerateProjectIds_MissingRoot_ReturnsEmptySequence()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);

        var projectIds = folder.EnumerateProjectIds().ToArray();

        Assert.Empty(projectIds);
    }

    [Fact]
    public void EnumerateProjectIds_MixedDirectories_ReturnsOnlyValidProjectsWithMetadata()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var validProjectDirectory = Path.Combine(root.Path, "案件 A-01");
        var missingMetadataDirectory = Path.Combine(root.Path, "案件 B-02");
        var invalidProjectDirectory = Path.Combine(root.Path, "bad.id");
        Directory.CreateDirectory(validProjectDirectory);
        Directory.CreateDirectory(missingMetadataDirectory);
        Directory.CreateDirectory(invalidProjectDirectory);
        File.WriteAllText(Path.Combine(validProjectDirectory, JetProjectFolder.ProjectJsonFileName), "{}");
        File.WriteAllText(Path.Combine(invalidProjectDirectory, JetProjectFolder.ProjectJsonFileName), "{}");

        var projectIds = folder.EnumerateProjectIds().ToArray();

        Assert.Equal(new[] { "案件 A-01" }, projectIds);
    }

}
