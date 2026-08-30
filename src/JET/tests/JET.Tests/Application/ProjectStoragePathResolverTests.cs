using Xunit;

namespace JET.Tests.Application;

public sealed class ProjectStoragePathResolverTests
{
    [Fact]
    public void Resolve_Default_UsesUserProfileJetRoot()
    {
        var configuration = ProjectStoragePathResolver.Resolve(
            appBaseDirectory: @"C:\JET\app",
            userProfileDirectory: @"C:\Users\tester",
            projectsRootOverride: null);

        Assert.Equal(Path.GetFullPath(@"C:\Users\tester\JET"), configuration.ProjectsRootPath);
    }

    [Fact]
    public void Resolve_DefaultWithUncAppBase_UsesLocalUserProfileRoot()
    {
        var configuration = ProjectStoragePathResolver.Resolve(
            appBaseDirectory: @"\\server\share\JET",
            userProfileDirectory: @"C:\Users\tester",
            projectsRootOverride: null,
            driveTypeResolver: _ => DriveType.Fixed);

        Assert.Equal(Path.GetFullPath(@"C:\Users\tester\JET"), configuration.ProjectsRootPath);
    }

    [Fact]
    public void Resolve_AbsoluteProjectsOverrideWithUncAppBase_UsesFixedRoot()
    {
        var configuration = ProjectStoragePathResolver.Resolve(
            appBaseDirectory: @"\\server\share\JET",
            userProfileDirectory: @"C:\Users\tester",
            projectsRootOverride: @"D:\JET-projects",
            driveTypeResolver: _ => DriveType.Fixed);

        Assert.Equal(Path.GetFullPath(@"D:\JET-projects"), configuration.ProjectsRootPath);
    }

    [Fact]
    public void Resolve_RelativeProjectsOverrideWithUncAppBase_RejectsUnsupportedRoot()
    {
        Assert.Throws<InvalidOperationException>(() => ProjectStoragePathResolver.Resolve(
            appBaseDirectory: @"\\server\share\JET",
            userProfileDirectory: @"C:\Users\tester",
            projectsRootOverride: @"data\projects",
            driveTypeResolver: _ => DriveType.Fixed));
    }

    [Fact]
    public void Resolve_RelativeProjectsOverride_AnchorsAtAppBase()
    {
        var configuration = ProjectStoragePathResolver.Resolve(
            appBaseDirectory: @"C:\JET\portable",
            userProfileDirectory: @"C:\Users\tester",
            projectsRootOverride: @"data\projects");

        Assert.Equal(Path.GetFullPath(@"C:\JET\portable\data\projects"), configuration.ProjectsRootPath);
    }

    [Fact]
    public void Resolve_UncProjectsOverride_RejectsUnsupportedRoot()
    {
        Assert.Throws<InvalidOperationException>(() => ProjectStoragePathResolver.Resolve(
            appBaseDirectory: @"C:\JET\app",
            userProfileDirectory: @"C:\Users\tester",
            projectsRootOverride: @"\\server\share\projects"));
    }

    [Fact]
    public void Resolve_MappedNetworkDriveProjectsOverride_RejectsUnsupportedRoot()
    {
        Assert.Throws<InvalidOperationException>(() => ProjectStoragePathResolver.Resolve(
            appBaseDirectory: @"C:\JET\app",
            userProfileDirectory: @"C:\Users\tester",
            projectsRootOverride: @"Z:\mapped-share\projects",
            driveTypeResolver: root => root.StartsWith("Z:", StringComparison.OrdinalIgnoreCase)
                ? DriveType.Network
                : DriveType.Fixed));
    }

    [Theory]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.Unknown)]
    [InlineData(DriveType.NoRootDirectory)]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.Ram)]
    public void Resolve_ProjectsOverrideOnUnsupportedDriveType_RejectsRoot(DriveType driveType)
    {
        Assert.Throws<InvalidOperationException>(() => ProjectStoragePathResolver.Resolve(
            appBaseDirectory: @"C:\JET\app",
            userProfileDirectory: @"C:\Users\tester",
            projectsRootOverride: @"Z:\projects",
            driveTypeResolver: root => root.StartsWith("Z:", StringComparison.OrdinalIgnoreCase)
                ? driveType
                : DriveType.Fixed));
    }

    [Fact]
    public void Resolve_ProjectsOverrideOnRemovableDrive_AcceptsRoot()
    {
        var configuration = ProjectStoragePathResolver.Resolve(
            appBaseDirectory: @"C:\JET\app",
            userProfileDirectory: @"C:\Users\tester",
            projectsRootOverride: @"E:\portable\projects",
            driveTypeResolver: root => root.StartsWith("E:", StringComparison.OrdinalIgnoreCase)
                ? DriveType.Removable
                : DriveType.Fixed);

        Assert.Equal(Path.GetFullPath(@"E:\portable\projects"), configuration.ProjectsRootPath);
    }
}
