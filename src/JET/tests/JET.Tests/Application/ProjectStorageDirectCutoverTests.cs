using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>直接切換新根後，舊根不再形成產品契約或自動處理來源。</summary>
public sealed class ProjectStorageDirectCutoverTests
{
    [Fact]
    public async Task ProjectListAndCreate_UseUserProfileRootWithoutDiscoveringOrModifyingOldRoot()
    {
        using var sandbox = new TempProjectRoot();
        var appBase = Path.Combine(sandbox.Path, "app");
        var userProfile = Path.Combine(sandbox.Path, "user-profile");
        var oldRoot = Path.Combine(sandbox.Path, "local-app-data", "JET", "projects");
        var oldAppBaseRoot = Path.Combine(appBase, "projects");
        const string oldProjectId = "Old-root-sentinel-case";
        const string oldAppBaseProjectId = "Old-appbase-sentinel-case";
        const string newProjectId = "New-root-case";
        CreateProject(oldRoot, oldProjectId);
        CreateProject(oldAppBaseRoot, oldAppBaseProjectId);
        var sentinelPath = Path.Combine(oldRoot, oldProjectId, "sentinel.bin");
        var appBaseSentinelPath = Path.Combine(oldAppBaseRoot, oldAppBaseProjectId, "sentinel.bin");
        var sentinelBytes = new byte[] { 0, 1, 2, 3, 255 };
        var appBaseSentinelBytes = new byte[] { 255, 3, 2, 1, 0 };
        File.WriteAllBytes(sentinelPath, sentinelBytes);
        File.WriteAllBytes(appBaseSentinelPath, appBaseSentinelBytes);
        var timestamp = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var appBaseTimestamp = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sentinelPath, timestamp);
        File.SetLastWriteTimeUtc(appBaseSentinelPath, appBaseTimestamp);

        var configuration = ProjectStoragePathResolver.Resolve(
            appBase,
            userProfile,
            projectsRootOverride: null,
            driveTypeResolver: _ => DriveType.Fixed);
        using (var host = new HandlerTestHost(projectsRootPath: configuration.ProjectsRootPath))
        {
            var beforeCreate = await host.DispatchAsync("project.list");
            await host.DispatchAsync(
                "project.create",
                $$"""
                {
                  "caseName": "{{newProjectId}}",
                  "projectCode": "NEW-ROOT",
                  "entityName": "New root entity",
                  "operatorId": "operator",
                  "periodStart": "2025-01-01",
                  "periodEnd": "2025-12-31",
                  "databaseProvider": "sqlite"
                }
                """);

            JsonShape.HasExactKeys(beforeCreate, "projects", "online");
            Assert.DoesNotContain(
                beforeCreate.GetProperty("projects").EnumerateArray(),
                project => project.GetProperty("projectId").GetString() == oldProjectId);
            Assert.DoesNotContain(
                beforeCreate.GetProperty("projects").EnumerateArray(),
                project => project.GetProperty("projectId").GetString() == oldAppBaseProjectId);
            Assert.Equal(Path.GetFullPath(Path.Combine(userProfile, "JET")), configuration.ProjectsRootPath);
            Assert.True(File.Exists(Path.Combine(configuration.ProjectsRootPath, newProjectId, "project.json")));
            Assert.False(Directory.Exists(Path.Combine(oldRoot, newProjectId)));
            Assert.False(Directory.Exists(Path.Combine(oldAppBaseRoot, newProjectId)));
            Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(sentinelPath));
            Assert.Equal(appBaseSentinelBytes, File.ReadAllBytes(appBaseSentinelPath));
            Assert.Equal(appBaseTimestamp, File.GetLastWriteTimeUtc(appBaseSentinelPath));
        }

        SqliteTestPool.Clear(configuration.ProjectsRootPath, newProjectId);
    }

    [Fact]
    public async Task RemovedLegacyStorageAction_IsUnknown()
    {
        using var host = new HandlerTestHost();
        var removedAction = string.Concat("project.", "migrate", "LegacyStorage");

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => host.DispatchAsync(removedAction));
    }

    private static void CreateProject(string root, string projectId)
    {
        var directory = Path.Combine(root, projectId);
        Directory.CreateDirectory(directory);
        var document = new ProjectDocument(
            projectId,
            "OLD-ROOT",
            "Old root entity",
            "operator",
            "2025-01-01",
            "2025-12-31",
            null,
            ProjectDocument.DefaultMoneyScale,
            ProjectDocument.DefaultRoundingMode,
            new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            0,
            ProjectDocument.CurrentSchemaVersion);
        File.WriteAllText(
            Path.Combine(directory, "project.json"),
            JsonSerializer.Serialize(document, JetJsonStorage.IndentedOptions));
    }
}
