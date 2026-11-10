using System.Text.RegularExpressions;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 報告檔儲存的最小單元測試（2026-09-02 重寫後）。只涵蓋這個 store 自己的承諾：寫壞或取消不留暫存檔、
/// 工作底稿每次新檔、其餘覆蓋同名檔、舊 manifest 仍能列出、檔案狀態如實回報、殘留的舊控制檔
/// 直接清掉。不核對內容、不擋載入，那些是它刻意不做的事。
/// </summary>
public sealed class ProjectReportStoreTests
{
    private const string ProjectId = "store-unit-project";

    [Fact]
    public async Task PeriodNames_RetryVersionsAndMovedProject_RecomputeLocalPathWithoutPersistingIt()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root, new FixedTimeProvider(DateTimeOffset.Parse("2026-09-17T00:00:00Z")));
        var request = Request(ReportKind.WorkingPaper, Bytes(1)) with
        { PeriodStart = "2025-01-01", PeriodEnd = "2025-12-31" };
        var first = await store.WriteAsync(ProjectId, request, default);
        var again = await store.WriteAsync(ProjectId, request, default);
        var afterPeriod = await store.WriteAsync(ProjectId, request with
        { PeriodStart = "2026-01-01", PeriodEnd = "2026-03-31" }, default);
        Assert.Contains("_20250101-20251231_WorkingPaper_", first.RelativeFileName);
        Assert.Contains("_20260101-20260331_WorkingPaper_", afterPeriod.RelativeFileName);
        Assert.NotEqual(first.RelativeFileName, again.RelativeFileName);
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(first.FullPath!));
        var manifest = await File.ReadAllTextAsync(Path.Combine(directory, ProjectReportStore.ManifestFileName));
        Assert.DoesNotContain("fullPath", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root.Path, manifest);

        using var movedRoot = new TempProjectRoot();
        var (movedStore, movedDirectory) = Create(movedRoot);
        foreach (var path in Directory.GetFiles(directory)) File.Copy(path, Path.Combine(movedDirectory, Path.GetFileName(path)));
        var moved = await movedStore.ListAsync(ProjectId, default);
        Assert.Equal(3, moved.Count);
        Assert.All(moved, report => Assert.Equal(Path.Combine(movedDirectory, report.RelativeFileName), report.FullPath));
        var next = await movedStore.WriteAsync(ProjectId, request, default);
        Assert.True(File.Exists(next.FullPath));
        Assert.True(File.Exists(Path.Combine(movedDirectory, first.RelativeFileName)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4_096)]
    public async Task WriteAsync_FullManifest_HistoryRestoredDuringWriting_IsRetained(int restoredCount)
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var original = await WriteSyntheticHistoryAsync(directory, 4_096);
        var request = Request(ReportKind.WorkingPaper, (output, token) =>
        {
            for (var index = 0; index < restoredCount; index++)
            {
                File.WriteAllBytes(Path.Combine(directory, $"history-{index}.xlsx"), []);
            }
            return output.WriteAsync(new byte[] { 17 }, token).AsTask();
        });
        if (restoredCount == 4_096)
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => store.WriteAsync(ProjectId, request, CancellationToken.None));
            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(directory, ProjectReportStore.ManifestFileName)));
            Assert.Equal(4_096, Directory.GetFiles(directory, "*.xlsx").Length);
        }
        else
        {
            await store.WriteAsync(ProjectId, request, CancellationToken.None);
            var listed = await store.ListAsync(ProjectId, CancellationToken.None);
            Assert.Equal(2, listed.Count);
            Assert.Contains(listed, item => item.RelativeFileName == "history-0.xlsx");
        }
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_FullManifest_PrunesOnlyMissingHistoryAndPublishesNewVersion()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        await WriteSyntheticHistoryAsync(directory, 4_096);
        await File.WriteAllBytesAsync(Path.Combine(directory, "history-0.xlsx"), [5, 6]);
        // 一個同名目錄不能因為 File.Exists 回傳 false 就被當成已刪檔。
        Directory.CreateDirectory(Path.Combine(directory, "history-1.xlsx"));

        var written = await store.WriteAsync(ProjectId, Request(ReportKind.WorkingPaper, Bytes(7)), CancellationToken.None);

        var listed = await store.ListAsync(ProjectId, CancellationToken.None);
        Assert.Equal(3, listed.Count);
        Assert.Contains(listed, item => item.RelativeFileName == "history-0.xlsx");
        Assert.Contains(listed, item => item.RelativeFileName == "history-1.xlsx");
        Assert.Contains(listed, item => item.ReportId == written.ReportId);
        Assert.Equal(new byte[] { 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(directory, "history-0.xlsx")));
        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(Path.Combine(directory, written.RelativeFileName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_FullManifest_ContentFailureDoesNotPruneHistory(bool cancel)
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var original = await WriteSyntheticHistoryAsync(directory, 4_096);
        using var cancellation = new CancellationTokenSource();
        var called = false;
        var request = Request(ReportKind.WorkingPaper, (_, token) =>
        {
            called = true;
            if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new InvalidOperationException("synthetic writer failure");
        });
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(ProjectId, request, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(ProjectId, request, cancellation.Token));
        }
        Assert.True(called);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(directory, ProjectReportStore.ManifestFileName)));
        Assert.Empty(Directory.GetFiles(directory, "*.xlsx"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_BelowCapacity_KeepsMissingHistory()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        await WriteSyntheticHistoryAsync(directory, 2);
        await store.WriteAsync(ProjectId, Request(ReportKind.WorkingPaper, Bytes(7)), CancellationToken.None);
        var listed = await store.ListAsync(ProjectId, CancellationToken.None);
        Assert.Equal(3, listed.Count);
        Assert.Equal(2, listed.Count(item => item.FileState == ReportFileState.Missing));
    }

    private static async Task<byte[]> WriteSyntheticHistoryAsync(string directory, int count)
    {
        var entries = Enumerable.Range(0, count).Select(index => new
        {
            reportId = index.ToString("x32"), kind = "workingPaper",
            relativeFileName = $"history-{index}.xlsx", generatedUtc = "2026-09-01T00:00:00Z", bytes = 0,
            sourceRef = new { validationResultId = "old-run" }, stale = true
        }).ToArray();
        var original = JsonSerializer.SerializeToUtf8Bytes(entries);
        await File.WriteAllBytesAsync(Path.Combine(directory, ProjectReportStore.ManifestFileName), original);
        return original;
    }

    [Fact]
    public async Task WriteAsync_FullManifest_RejectsBeforeWritingAndLeavesOriginalManifest()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        // 只有零位元組檔案與合成 metadata，不生成數千份工作簿。
        var entries = Enumerable.Range(0, 4_096).Select(index => new
        {
            reportId = index.ToString("x32"), kind = "workingPaper",
            relativeFileName = $"history-{index}.xlsx", generatedUtc = "2026-09-01T00:00:00Z", bytes = 0,
            sourceRef = new { validationResultId = "old-run" }, stale = true
        }).ToArray();
        foreach (var entry in entries) { File.WriteAllBytes(Path.Combine(directory, entry.relativeFileName), []); }
        var manifestPath = Path.Combine(directory, ProjectReportStore.ManifestFileName);
        var original = JsonSerializer.SerializeToUtf8Bytes(entries);
        await File.WriteAllBytesAsync(manifestPath, original);
        var writerCalled = false;

        var error = await Assert.ThrowsAsync<JetActionException>(() => store.WriteAsync(ProjectId,
            Request(ReportKind.WorkingPaper, (_, _) => { writerCalled = true; return Task.CompletedTask; }),
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        Assert.False(writerCalled);
        Assert.Equal(4_096, Directory.GetFiles(directory, "*.xlsx").Length);
        Assert.Equal(original, await File.ReadAllBytesAsync(manifestPath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_WorkingPaperNameAppearsBeforePublication_DoesNotOverwriteIt()
    {
        using var root = new TempProjectRoot();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 5, 1, 2, 3, TimeSpan.Zero));
        var (store, directory) = Create(root, time);
        var fileName = $"{ProjectFileNames.SafePrefix(ProjectId)}_WorkingPaper_{time.Now.ToLocalTime():yyyyMMdd-HHmmss}.xlsx";
        var path = Path.Combine(directory, fileName);
        var publisher = (IReportPublishingStore)store;

        var error = await Assert.ThrowsAsync<JetActionException>(() => publisher.WriteWithPublishingAsync(
            ProjectId,
            Request(ReportKind.WorkingPaper, Bytes(1, 2)),
            _ => File.WriteAllBytes(path, [7, 8, 9]),
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        Assert.Contains("重新匯出", error.Message, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(path));
        Assert.Empty(await store.ListAsync(ProjectId, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        var retried = await store.WriteAsync(ProjectId, Request(ReportKind.WorkingPaper, Bytes(4)), CancellationToken.None);
        Assert.NotEqual(fileName, retried.RelativeFileName);
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task WriteAsync_OldWorkingPaperIsLocked_NewVersionStillPublishes()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var first = await store.WriteAsync(ProjectId, Request(ReportKind.WorkingPaper, Bytes(1)), CancellationToken.None);
        var path = Path.Combine(directory, first.RelativeFileName);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var second = await store.WriteAsync(ProjectId, Request(ReportKind.WorkingPaper, Bytes(2)), CancellationToken.None);
            Assert.NotEqual(first.RelativeFileName, second.RelativeFileName);
            Assert.Equal(new byte[] { 2 }, await File.ReadAllBytesAsync(Path.Combine(directory, second.RelativeFileName)));
        }
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, (await store.ListAsync(ProjectId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task WriteAsync_ContentWriterThrows_LeavesNoTemporaryFileAndNoManifest()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(
            ProjectId,
            Request(ReportKind.ValidationReport, async (output, cancellationToken) =>
            {
                await output.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
                throw new InvalidOperationException("writer failed halfway");
            }),
            CancellationToken.None));

        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Empty(Directory.GetFiles(directory, "*.xlsx"));
        Assert.False(File.Exists(Path.Combine(directory, ProjectReportStore.ManifestFileName)));
        Assert.Empty(await store.ListAsync(ProjectId, CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_CancelledInsideWriter_LeavesNoTemporaryFileAndKeepsPriorReport()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var prior = await store.WriteAsync(
            ProjectId,
            Request(ReportKind.InfReport, Bytes(7, 7, 7)),
            CancellationToken.None);
        var priorPath = Path.Combine(directory, prior.RelativeFileName);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(
            ProjectId,
            Request(ReportKind.InfReport, async (output, cancellationToken) =>
            {
                await output.WriteAsync(new byte[] { 9 }, cancellationToken);
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }),
            cancellation.Token));

        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Equal(new byte[] { 7, 7, 7 }, await File.ReadAllBytesAsync(priorPath));
        var listed = Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None));
        Assert.Equal(prior.ReportId, listed.ReportId);
        Assert.Equal(ReportFileState.AsPublished, listed.FileState);
    }

    [Fact]
    public async Task WriteAsync_WorkingPaper_WritesNewVersionFileEachTimeAndKeepsOldVersions()
    {
        using var root = new TempProjectRoot();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 2, 1, 2, 3, TimeSpan.Zero));
        var (store, directory) = Create(root, time);

        var first = await store.WriteAsync(ProjectId, Request(ReportKind.WorkingPaper, Bytes(1)), CancellationToken.None);
        var second = await store.WriteAsync(ProjectId, Request(ReportKind.WorkingPaper, Bytes(2)), CancellationToken.None);
        time.Now = time.Now.AddMinutes(1);
        var third = await store.WriteAsync(ProjectId, Request(ReportKind.WorkingPaper, Bytes(3)), CancellationToken.None);

        var prefix = ProjectFileNames.SafePrefix(ProjectId);
        var pattern = new Regex($"^{Regex.Escape(prefix)}_WorkingPaper_\\d{{8}}-\\d{{6}}(-\\d+)?\\.xlsx$");
        foreach (var report in new[] { first, second, third })
        {
            Assert.Matches(pattern, report.RelativeFileName);
            Assert.True(File.Exists(Path.Combine(directory, report.RelativeFileName)));
        }

        // 同一秒內第二次匯出補流水號；時間走了以後又回到純時間戳。
        Assert.NotEqual(first.RelativeFileName, second.RelativeFileName);
        Assert.NotEqual(second.RelativeFileName, third.RelativeFileName);
        Assert.Equal(3, Directory.GetFiles(directory, "*_WorkingPaper_*.xlsx").Length);
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(Path.Combine(directory, first.RelativeFileName)));

        var listed = await store.ListAsync(ProjectId, CancellationToken.None);
        Assert.Equal(
            new[] { first.ReportId, second.ReportId, third.ReportId }.Order(StringComparer.Ordinal),
            listed.Select(report => report.ReportId).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(ReportKind.ValidationReport, "ValidationReport")]
    [InlineData(ReportKind.InfReport, "INFReport")]
    [InlineData(ReportKind.PrescreenReport, "PrescreeningReport")]
    [InlineData(ReportKind.CriteriaSelectionReport, "CriteriaSelectionReport")]
    public async Task WriteAsync_OtherReportKinds_OverwriteSameFileNameAndKeepOneEntryPerKind(
        ReportKind kind, string fileSuffix)
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);

        var first = await store.WriteAsync(ProjectId, Request(kind, Bytes(1)), CancellationToken.None);
        var second = await store.WriteAsync(ProjectId, Request(kind, Bytes(2, 2)), CancellationToken.None);

        Assert.Equal(first.RelativeFileName, second.RelativeFileName);
        Assert.Equal($"{ProjectFileNames.SafePrefix(ProjectId)}_{fileSuffix}.xlsx", second.RelativeFileName);
        Assert.Single(Directory.GetFiles(directory, "*.xlsx"));
        Assert.Equal(new byte[] { 2, 2 }, await File.ReadAllBytesAsync(Path.Combine(directory, second.RelativeFileName)));

        var listed = Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None));
        Assert.Equal(second.ReportId, listed.ReportId);
        Assert.Equal(2, listed.Bytes);
    }

    [Fact]
    public async Task WriteAsync_LockedReport_KeepsPriorFileAndSucceedsAfterRelease()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var first = await store.WriteAsync(ProjectId, Request(ReportKind.ValidationReport, Bytes(1, 2)), CancellationToken.None);
        var path = Path.Combine(directory, first.RelativeFileName);

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => store.WriteAsync(
                ProjectId, Request(ReportKind.ValidationReport, Bytes(3, 4)), CancellationToken.None));
            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
            Assert.Contains("關閉", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }

        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(first.ReportId, Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None)).ReportId);
        var retried = await store.WriteAsync(ProjectId, Request(ReportKind.ValidationReport, Bytes(3, 4)), CancellationToken.None);
        Assert.Equal(first.RelativeFileName, retried.RelativeFileName);
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(ReportFileState.AsPublished, Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None)).FileState);
    }

    [Fact]
    public async Task ListAsync_SameLengthEdit_UsesWriteTimeAndReexportClearsWarning()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var first = await store.WriteAsync(ProjectId, Request(ReportKind.InfReport, Bytes(1, 2)), CancellationToken.None);
        var path = Path.Combine(directory, first.RelativeFileName);
        await File.WriteAllBytesAsync(path, new byte[] { 3, 4 });
        File.SetLastWriteTimeUtc(path, first.LastWriteUtc!.Value.UtcDateTime.AddMinutes(1));
        Assert.Equal(first.Bytes, new FileInfo(path).Length);
        Assert.Equal(ReportFileState.ModifiedOutside, Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None)).FileState);

        await store.WriteAsync(ProjectId, Request(ReportKind.InfReport, Bytes(5, 6)), CancellationToken.None);
        Assert.Equal(new byte[] { 5, 6 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(ReportFileState.AsPublished, Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None)).FileState);
    }

    [Fact]
    public async Task ListAsync_ReportsFileStateWithoutBlocking()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var untouched = await store.WriteAsync(ProjectId, Request(ReportKind.ValidationReport, Bytes(1)), CancellationToken.None);
        var edited = await store.WriteAsync(ProjectId, Request(ReportKind.InfReport, Bytes(1)), CancellationToken.None);
        var deleted = await store.WriteAsync(ProjectId, Request(ReportKind.PrescreenReport, Bytes(1)), CancellationToken.None);

        // 模擬審計員用 Excel 存檔（內容變長）與直接刪檔。
        await File.AppendAllTextAsync(Path.Combine(directory, edited.RelativeFileName), "edited outside JET");
        File.Delete(Path.Combine(directory, deleted.RelativeFileName));

        var listed = (await store.ListAsync(ProjectId, CancellationToken.None))
            .ToDictionary(report => report.ReportId);
        Assert.Equal(ReportFileState.AsPublished, listed[untouched.ReportId].FileState);
        Assert.Equal(ReportFileState.ModifiedOutside, listed[edited.ReportId].FileState);
        Assert.Equal(ReportFileState.Missing, listed[deleted.ReportId].FileState);
        Assert.Equal(3, listed.Count);
    }

    // 報告清單舊設計的欄名 sourceRefs、createdUtc 不再讀回（裁定 2026-10-29-17：改名前的案件刪除後重建，不寫讀取相容）。
    // 原本這裡用舊欄名的紀錄核對照樣列出；現在列出的那一筆改用現行欄名，另加一筆只有舊欄名的紀錄，核對它不列出。
    // 第一次失敗：Focused 收據 20261030-084625784-a36dec767d6d4962a737ce65bf661999（舊欄名的紀錄不再列出）。
    [Fact]
    public async Task ListAsync_Manifest_IgnoresSha256AccountMappingAndOldFieldNameEntries()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var validationFile = "legacy_ValidationReport.xlsx";
        await File.WriteAllBytesAsync(Path.Combine(directory, validationFile), new byte[] { 5, 5, 5, 5 });
        await File.WriteAllTextAsync(
            Path.Combine(directory, ProjectReportStore.ManifestFileName),
            $$"""
            [
              {
                "reportId": "0123456789abcdef0123456789abcdef",
                "kind": "validationReport",
                "relativeFileName": "{{validationFile}}",
                "sourceRef": { "validationResultId": "run-1" },
                "generatedUtc": "2026-08-01T00:00:00+00:00",
                "bytes": 4,
                "sha256": "{{new string('a', 64)}}",
                "stale": false
              },
              {
                "reportId": "00112233445566778899aabbccddeeff",
                "kind": "validationReport",
                "relativeFileName": "{{validationFile}}",
                "sourceRefs": { "validationResultId": "run-0" },
                "createdUtc": "2026-07-01T00:00:00+00:00",
                "bytes": 4,
                "stale": false
              },
              {
                "reportId": "fedcba9876543210fedcba9876543210",
                "kind": "accountMapping",
                "relativeFileName": "legacy_AccountMapping.xlsx",
                "sourceRefs": { "validationResultId": "run-1" },
                "createdUtc": "2026-08-01T00:00:00+00:00",
                "bytes": 10,
                "sha256": "{{new string('b', 64)}}",
                "stale": false
              }
            ]
            """);

        var listed = Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None));

        Assert.Equal("0123456789abcdef0123456789abcdef", listed.ReportId);
        Assert.Equal(ReportKind.ValidationReport, listed.Kind);
        Assert.Equal("run-1", listed.SourceRef.ValidationResultId);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), listed.GeneratedUtc);
        Assert.NotEqual(ReportFileState.Missing, listed.FileState);
    }

    [Fact]
    public async Task ListAsync_UnreadableManifest_IsSetAsideAndListingContinuesEmpty()
    {
        using var root = new TempProjectRoot();
        var logger = new RecordingLogger();
        var (store, directory) = Create(root, logger: logger);
        var manifestPath = Path.Combine(directory, ProjectReportStore.ManifestFileName);
        await File.WriteAllTextAsync(manifestPath, "{");

        var listed = await store.ListAsync(ProjectId, CancellationToken.None);

        Assert.Empty(listed);
        Assert.False(File.Exists(manifestPath));
        Assert.Single(Directory.GetFiles(directory, "reports.unreadable-*.json"));
        Assert.Contains("report.manifest.reset", logger.EventNames);

        // 之後照常寫入，清單從空的重新開始。
        var written = await store.WriteAsync(ProjectId, Request(ReportKind.InfReport, Bytes(1)), CancellationToken.None);
        Assert.Equal(written.ReportId, Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None)).ReportId);
    }

    // 檔名不能當成案件資料夾裡的一個檔案的紀錄，和其他讀不懂的紀錄一樣不列出；報告清單不因此擋住讀取（docs/jet-guide.md
    // 寫報告清單「不會因此拒絕載入案件」）。JET 自己不會寫出這種檔名，只會出自 JET 以外對 reports.json 的改動。
    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("NUL")]
    [InlineData("null-character")]
    public async Task ListAsync_EntryWhoseFileNameCannotBeAFileInTheFolder_IsNotListed(string fileNameCase)
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var kept = await store.WriteAsync(ProjectId, Request(ReportKind.ValidationReport, Bytes(1)), CancellationToken.None);
        var fileName = fileNameCase == "null-character" ? "report" + (char)0 + ".xlsx" : fileNameCase;
        var manifestPath = Path.Combine(directory, ProjectReportStore.ManifestFileName);
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
        {
            var keptEntry = Assert.Single(manifest.RootElement.EnumerateArray().ToArray()).GetRawText();
            var damagedEntry = $$"""
                {
                  "reportId": "0123456789abcdef0123456789abcdef",
                  "kind": "infReport",
                  "relativeFileName": {{JsonSerializer.Serialize(fileName)}},
                  "sourceRef": { "validationResultId": "run-1" },
                  "generatedUtc": "2026-08-01T00:00:00+00:00",
                  "bytes": 4,
                  "stale": false
                }
                """;
            await File.WriteAllTextAsync(manifestPath, $"[{keptEntry},{damagedEntry}]");
        }

        var listed = Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None));
        Assert.Equal(kept.ReportId, listed.ReportId);
        Assert.Equal(ReportFileState.AsPublished, listed.FileState);
        Assert.Equal(Path.Combine(directory, kept.RelativeFileName), listed.FullPath);

        // 之後照常標過期與寫入，不會因為那一筆而失敗。
        Assert.Equal(1, await store.MarkStaleAsync(ProjectId, ReportKind.ValidationReport, CancellationToken.None));
        var written = await store.WriteAsync(ProjectId, Request(ReportKind.InfReport, Bytes(2)), CancellationToken.None);
        Assert.Equal(
            new[] { kept.ReportId, written.ReportId }.Order(StringComparer.Ordinal),
            (await store.ListAsync(ProjectId, CancellationToken.None)).Select(report => report.ReportId).Order(StringComparer.Ordinal));
    }

    private static (ProjectReportStore Store, string ProjectDirectory) Create(
        TempProjectRoot root,
        TimeProvider? timeProvider = null,
        ILogger<ProjectReportStore>? logger = null)
    {
        var folder = new JetProjectFolder(root.Path);
        var directory = folder.GetProjectDirectory(ProjectId);
        Directory.CreateDirectory(directory);
        return (new ProjectReportStore(folder, timeProvider, logger), directory);
    }

    private static ReportWriteRequest Request(
        ReportKind kind,
        ReportContentWriter writer) =>
        new(kind, new ReportSourceRefs(ValidationResultId: "run-1", PrescreenResultId: "prescreen-1", ScenarioRevision: "rev-1", ScenarioPositions: [1]), writer);

    private static ReportContentWriter Bytes(params byte[] content) =>
        (output, cancellationToken) => output.WriteAsync(content, cancellationToken).AsTask();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingLogger : ILogger<ProjectReportStore>
    {
        public List<string> EventNames { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name is not null)
            {
                EventNames.Add(eventId.Name);
            }
        }
    }
}
