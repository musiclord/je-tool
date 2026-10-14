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
        // 使用者 2026-10-14 第三輪勾選頁確認 R3-65（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。 案件識別碼由案件清單帶來，
        // 格式錯誤只有程式送錯資料才會出現：顯示非預期錯誤那一句，送出的值不出現在訊息。
        Assert.Equal(JetPayloadRejection.UnexpectedErrorMessage, exception.Message);
        Assert.Equal("project_id_invalid", JetPayloadRejection.CheckOf(exception));
        Assert.DoesNotContain(@"..\..\evil", exception.Message, StringComparison.Ordinal);
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
    public void EnumerateProjectIds_MixedDirectories_ReturnsValidProjectFolders()
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

        var projectIds = folder.EnumerateProjectIds().Order(StringComparer.Ordinal).ToArray();

        // 原本只回傳有 project.json 的「案件 A-01」。2026-10-12 起，資料夾裡有沒有 project.json 改由 JsonFileProjectStore
        // 在讀寫共用的鎖裡判斷：儲存換檔的一瞬間正式檔名會暫時不存在，在這裡判斷會讓正在儲存的案件從案件清單上消失。
        // 改動後這裡依預期失敗，第一次失敗收據 20261012-082551122-a67ac804db224e158a00d3c53940c187。
        // 沒有 project.json 的資料夾仍不列為案件，改由 JsonFileProjectStoreTests.List_WhileASaveIsSwappingTheFile_KeepsTheProjectListed 確認。
        Assert.Equal(new[] { "案件 A-01", "案件 B-02" }, projectIds);
    }

}
