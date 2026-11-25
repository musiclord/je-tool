using System.Security.AccessControl;
using System.Security.Principal;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 匯出時檔案寫不進去：案件所在磁碟已滿、案件資料夾不能寫，或 Windows 暫存資料夾所在磁碟已滿、暫存資料夾不能寫。
/// 這幾種情形重新下載 JET 也沒有用，訊息要寫出是哪裡、原因與審計員接下來能做什麼，不能說成範本無法讀取或填入；
/// 也不能把暫存資料夾的問題說成案件資料夾的問題。暫存檔寫到一半失敗後，緩衝區裡還沒寫出的內容在關檔時會再寫一次，
/// 所在磁碟已滿時又會失敗，這時仍要回報第一次的錯誤。
/// 能在這台電腦上真的做出來的情形就用真的做：磁碟已滿是在超過所在磁碟剩餘空間的位置寫一個位元組，
/// Windows 回報磁碟已滿，檔案大小不變；案件資料夾與案件位置不能寫，是在資料夾權限加上拒絕建立檔案的規則，
/// 測試結束時移除。做不出來的情形改用合成的例外或記憶體裡的替身，各測試的說明寫明是哪一種：
/// 寫資料列途中才磁碟已滿、逐一核對輸出檔的每項操作、取鎖與發布時磁碟已滿，以及暫存資料夾不能寫。資料都由程式產生。
/// </summary>
public sealed class ExportFileWriteErrorTests
{
    /// <summary>Windows 的 ERROR_DISK_FULL 包成 HRESULT 的值。</summary>
    private const int DiskFullHResult = unchecked((int)0x80070070);

    private const string CaseDiskFull = "案件所在磁碟的空間不足，檔案無法寫入，本次未產生。請釋放空間後再試一次。";

    private const string CaseFolderNoWriteAccess =
        "無法寫入案件資料夾，本次未產生。請確認案件資料夾不是唯讀，而且目前的 Windows 帳號可以寫入，再試一次。";

    private const string CaseId = "匯出檔寫入錯誤案件";

    [Fact]
    public async Task ReportStagingFile_DiskFullOnFirstWrite_TellsToFreeSpace()
    {
        // 填範本第一次碰到輸出檔就遇到磁碟已滿；寫資料列途中才滿的情形由下一則測試看。
        using var root = new TempProjectRoot();
        var (store, directory) = CreateStore(root);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.WriteAsync(
            CaseId,
            Request(async (output, _) =>
            {
                WriteByteBeyondFreeSpace(output, directory);
                await FillCriteriaTemplateAsync(output);
            }),
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.Equal(CaseDiskFull, exception.Message);
        ReportStoreTempFiles.AssertNoneLeft(directory);
        Assert.Empty(await store.ListAsync(CaseId, CancellationToken.None));
    }

    [Fact]
    public async Task FillingTemplate_OutputDiskFullWhileWritingRows_TellsToFreeSpace()
    {
        // 明細一列一列壓縮進輸出檔，寫到一半磁碟才滿。錯誤要經過壓縮、XML 寫出與工作表寫出程式之後，仍是案件磁碟已滿的訊息。
        const int detailRows = 20_000;
        var output = new LocalExportOutputFile(new FullDiskAfter(64 * 1024));
        var rowsWritten = 0;
        try
        {
            var exception = await Assert.ThrowsAsync<JetActionException>(() => ReportTemplatePackage.FillDirectStreamingAsync(
                ReportTemplateCatalog.Default,
                ReportTemplateCatalog.CriteriaSelection,
                output,
                ["Summary Inforamtion"],
                (editor, cancellationToken) =>
                {
                    editor.RewriteWorksheetPart("Summary Inforamtion", static (_, _) => { }, cancellationToken);
                    using var sheet = AddDetailSheet(editor.Document, "合成明細", SheetUsedRange.Table(detailRows, 10));
                    for (uint row = 1; row <= detailRows; row++)
                    {
                        sheet.WriteDataRow(row, [.. Enumerable.Range(1, 10)
                            .Select(column => sheet.TextCell(row, (uint)column, $"合成明細 {row}-{column}", style: 0))]);
                        rowsWritten++;
                    }

                    var stat = sheet.CloseAndSummarize(cancellationToken);
                    editor.Document.WorkbookPart!.RequiredWorkbook().Save();
                    return Task.FromResult<IReadOnlyList<SheetStat>>([stat]);
                },
                CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
            Assert.Equal(CaseDiskFull, exception.Message);
            Assert.InRange(rowsWritten, 1, detailRows - 1);
        }
        finally
        {
            await output.CloseAfterFailedWriteAsync();
        }
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("WriteSpan")]
    [InlineData("WriteByte")]
    [InlineData("WriteAsync")]
    [InlineData("WriteAsyncMemory")]
    [InlineData("Seek")]
    [InlineData("SetLength")]
    [InlineData("Position")]
    [InlineData("Flush")]
    [InlineData("FlushAsync")]
    [InlineData("FlushToDisk")]
    [InlineData("Dispose")]
    public async Task OutputFile_EachOperationOnFullDisk_TellsToFreeSpace(string operation)
    {
        var output = new LocalExportOutputFile(new FullDiskAfter(0));
        try
        {
            var exception = await Assert.ThrowsAsync<JetActionException>(async () =>
            {
                switch (operation)
                {
                    case "Write": output.Write(new byte[1], 0, 1); break;
                    case "WriteSpan": output.Write((ReadOnlySpan<byte>)new byte[1]); break;
                    case "WriteByte": output.WriteByte(0); break;
                    case "WriteAsync": await output.WriteAsync(new byte[1], 0, 1); break;
                    case "WriteAsyncMemory": await output.WriteAsync(new byte[1].AsMemory()); break;
                    case "Seek": output.Seek(0, SeekOrigin.Begin); break;
                    case "SetLength": output.SetLength(0); break;
                    case "Position": output.Position = 0; break;
                    case "Flush": output.Flush(); break;
                    case "FlushAsync": await output.FlushAsync(); break;
                    case "FlushToDisk": output.FlushToDisk(); break;
                    case "Dispose": output.Dispose(); break;
                    default: throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
                }
            });

            Assert.Equal(CaseDiskFull, exception.Message);
            Assert.Equal(DiskFullHResult, Assert.IsAssignableFrom<IOException>(exception.InnerException).HResult);
        }
        finally
        {
            await output.CloseAfterFailedWriteAsync();
        }
    }

    [Fact]
    public void CaseLocationTranslation_DiskFull_TellsToFreeSpace()
    {
        // 取報告鎖時在案件位置建立鎖檔；真的磁碟已滿無法在建立空檔時重現，所以直接核對取鎖用的轉換。
        var translated = ExportFileWriteErrors.TryTranslateCaseLocation(new IOException("磁碟已滿（合成）", DiskFullHResult));

        Assert.NotNull(translated);
        Assert.Equal(JetErrorCodes.FileReadError, translated.Code);
        Assert.Equal("案件所在磁碟的空間不足，系統沒有開始作業。請釋放空間後再試一次。", translated.Message);
    }

    [Fact]
    public async Task WorkFile_DiskFullWhileWriting_TellsToFreeSpace()
    {
        using var root = new TempProjectRoot();
        Directory.CreateDirectory(root.Path);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => ProjectWorkFileWriter.WriteAsync(
            new SingleProjectLocator(root.Path),
            new LocalExportOutputFiles(),
            CaseId,
            "合成工作檔.xlsx",
            async (output, cancellationToken) =>
            {
                WriteByteBeyondFreeSpace(output, root.Path);
                await output.FlushAsync(cancellationToken);
            },
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.Equal(CaseDiskFull, exception.Message);
        Assert.Empty(Directory.GetFiles(root.Path));
    }

    [Fact]
    public async Task ReportStagingFile_CloseFailsOnFullDisk_KeepsTheWriterError()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = CreateStore(root);
        var writerError = new JetActionException(JetErrorCodes.FileReadError, "寫出程式回報的錯誤（合成）");

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.WriteAsync(
            CaseId,
            Request((output, _) =>
            {
                WriteByteBeyondFreeSpace(output, directory);
                AssertFlushReportsCaseDiskFull(output);
                throw writerError;
            }),
            CancellationToken.None));

        Assert.Same(writerError, exception);
        ReportStoreTempFiles.AssertNoneLeft(directory);
        Assert.Empty(await store.ListAsync(CaseId, CancellationToken.None));
    }

    [Fact]
    public async Task WorkFile_CloseFailsOnFullDisk_KeepsTheWriterError()
    {
        using var root = new TempProjectRoot();
        Directory.CreateDirectory(root.Path);
        var writerError = new JetActionException(JetErrorCodes.FileReadError, "寫出程式回報的錯誤（合成）");

        var exception = await Assert.ThrowsAsync<JetActionException>(() => ProjectWorkFileWriter.WriteAsync(
            new SingleProjectLocator(root.Path),
            new LocalExportOutputFiles(),
            CaseId,
            "合成工作檔.xlsx",
            (output, _) =>
            {
                WriteByteBeyondFreeSpace(output, root.Path);
                AssertFlushReportsCaseDiskFull(output);
                throw writerError;
            },
            CancellationToken.None));

        Assert.Same(writerError, exception);
        Assert.Empty(Directory.GetFiles(root.Path));
    }

    [Fact]
    public async Task FillingTemplate_TemporaryFolderDiskFull_TellsToFreeTemporaryFolderSpace()
    {
        await using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<JetActionException>(() => FillCriteriaTemplateAsync(
            output,
            () =>
            {
                // 寫出程式把明細先放進 Windows 暫存資料夾的暫存檔；這裡照同樣的開法建一個，寫到剩餘空間之外。
                using var spool = new FileStream(
                    Path.Combine(Path.GetTempPath(), $"jet-export-error-spool-{Guid.NewGuid():N}.tmp"),
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 4_096,
                    FileOptions.DeleteOnClose);
                WriteByteBeyondFreeSpace(spool, Path.GetTempPath());
                spool.Flush();
            }));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.Equal("Windows 暫存資料夾所在磁碟的空間不足，本次未產生。請釋放空間後再試一次。", exception.Message);
        Assert.Equal(DiskFullHResult, Assert.IsAssignableFrom<IOException>(exception.InnerException).HResult);
    }

    /// <summary>
    /// 寫出程式在 Windows 暫存資料夾建立暫存檔時沒有權限。測試不改這台電腦暫存資料夾的權限，以免影響其他程式，
    /// 所以在填範本途中丟出合成的拒絕存取例外。
    /// </summary>
    [Fact]
    public async Task FillingTemplate_TemporaryFolderWithoutWriteAccess_TellsToCheckTemporaryFolderAccess()
    {
        await using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<JetActionException>(() => FillCriteriaTemplateAsync(
            output,
            () => throw new UnauthorizedAccessException("拒絕存取（合成）")));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.Equal(
            "無法寫入 Windows 暫存資料夾，本次未產生。請確認目前的 Windows 帳號可以寫入暫存資料夾，再試一次。",
            exception.Message);
        Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
    }

    /// <summary>
    /// 填範本途中遇到磁碟已滿與沒有權限以外的讀寫錯誤，例如網路磁碟斷線。這和範本無關，重新下載 JET 也沒有用，
    /// 所以不說成範本無法填入，和其他沒有歸類的意外錯誤顯示同一句。測試不切斷這台電腦的磁碟，用合成的讀寫錯誤代替。
    /// </summary>
    [Fact]
    public async Task FillingTemplate_OtherFileError_ShowsTheCommonUnexpectedError()
    {
        await using var output = new MemoryStream();
        var fileError = new IOException("合成的讀寫錯誤");

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => FillCriteriaTemplateAsync(
            output,
            () => throw fileError));

        Assert.Equal(JetPayloadRejection.UnexpectedErrorMessage, JET.Bridge.JetWebMessageBridge.ToErrorDto(exception).Message);
        Assert.Same(fileError, exception);
    }

    [Fact]
    public async Task ReportStagingFile_CaseFolderDeniesNewFiles_TellsToCheckCaseFolderAccess()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = CreateStore(root);
        var writerCalled = false;

        DenyCreatingFiles(directory);
        try
        {
            var exception = await Assert.ThrowsAsync<JetActionException>(() => store.WriteAsync(
                CaseId,
                Request((_, _) =>
                {
                    writerCalled = true;
                    return Task.CompletedTask;
                }),
                CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
            Assert.Equal(CaseFolderNoWriteAccess, exception.Message);
            Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
        }
        finally
        {
            AllowCreatingFiles(directory);
        }

        Assert.False(writerCalled);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task WorkFile_CaseFolderDeniesNewFiles_TellsToCheckCaseFolderAccess()
    {
        using var root = new TempProjectRoot();
        Directory.CreateDirectory(root.Path);
        var writerCalled = false;

        DenyCreatingFiles(root.Path);
        try
        {
            var exception = await Assert.ThrowsAsync<JetActionException>(() => ProjectWorkFileWriter.WriteAsync(
                new SingleProjectLocator(root.Path),
                new LocalExportOutputFiles(),
                CaseId,
                "合成工作檔.xlsx",
                (_, _) =>
                {
                    writerCalled = true;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
            Assert.Equal(CaseFolderNoWriteAccess, exception.Message);
        }
        finally
        {
            AllowCreatingFiles(root.Path);
        }

        Assert.False(writerCalled);
        Assert.Empty(Directory.GetFiles(root.Path));
    }

    [Fact]
    public async Task ReportLease_CaseLocationDeniesNewFiles_TellsToCheckCaseLocationAccess()
    {
        // 報告的鎖檔放在案件位置的根目錄、案件資料夾旁邊；案件位置不能建立檔案時，在取得這把鎖時就失敗。
        // 案件資料夾本身照常可以寫，所以訊息要指向案件位置，不能叫審計員去檢查案件資料夾。
        using var root = new TempProjectRoot();
        var (store, directory) = CreateStore(root);

        DenyCreatingFiles(root.Path);
        try
        {
            var exception = await Assert.ThrowsAsync<JetActionException>(() => store.WriteAsync(
                CaseId,
                Request((_, _) => Task.CompletedTask),
                CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
            Assert.Equal(
                "無法寫入 JET 的案件位置，系統沒有開始作業。請確認案件位置不是唯讀，而且目前的 Windows 帳號可以寫入，再試一次。",
                exception.Message);
            Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
        }
        finally
        {
            AllowCreatingFiles(root.Path);
        }

        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task Publishing_DiskFull_TellsToFreeSpace()
    {
        // 發布時備份同名舊檔、寫報告清單索引與改名，失敗都走同一段處理。備份與寫索引沒有替換點，
        // 這裡讓改名報告清單索引這一步回報 Windows 的磁碟已滿。
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var directory = folder.GetProjectDirectory(CaseId);
        Directory.CreateDirectory(directory);
        var store = new ProjectReportStore(folder, (source, destination, overwrite) =>
        {
            if (string.Equals(Path.GetFileName(destination), ProjectReportStore.ManifestFileName, StringComparison.Ordinal))
            {
                throw new IOException("磁碟已滿（合成）", DiskFullHResult);
            }

            File.Move(source, destination, overwrite);
        });

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.WriteAsync(
            CaseId,
            Request((output, cancellationToken) => output.WriteAsync(new byte[] { 1, 2 }, cancellationToken).AsTask()),
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.Equal(CaseDiskFull, exception.Message);
        ReportStoreTempFiles.AssertNoneLeft(directory);
        Assert.Empty(Directory.GetFiles(directory));
    }

    private static (ProjectReportStore Store, string Directory) CreateStore(TempProjectRoot root)
    {
        var folder = new JetProjectFolder(root.Path);
        var directory = folder.GetProjectDirectory(CaseId);
        Directory.CreateDirectory(directory);
        return (new ProjectReportStore(folder), directory);
    }

    private static ReportWriteRequest Request(ReportContentWriter writer) => new(
        ReportKind.ValidationReport,
        new ReportSourceRefs(ValidationResultId: "11111111111111111111111111111111"),
        writer);

    /// <summary>
    /// 用條件篩選報告的範本填一次，只改寫摘要頁、內容照範本原樣，其他工作表照範本複製到輸出。
    /// <paramref name="duringFill"/> 在填範本的途中執行，用來模擬寫出程式在途中遇到的錯誤。
    /// </summary>
    private static Task<ExportStats> FillCriteriaTemplateAsync(Stream output, Action? duringFill = null) =>
        ReportTemplatePackage.FillDirectStreamingAsync(
            ReportTemplateCatalog.Default,
            ReportTemplateCatalog.CriteriaSelection,
            output,
            ["Summary Inforamtion"],
            (editor, cancellationToken) =>
            {
                duringFill?.Invoke();
                editor.RewriteWorksheetPart("Summary Inforamtion", static (_, _) => { }, cancellationToken);
                return Task.FromResult<IReadOnlyList<SheetStat>>([]);
            },
            CancellationToken.None);

    /// <summary>
    /// 在超過所在磁碟剩餘空間的位置寫一個位元組。這個位元組先留在緩衝區，之後寫出、定位或關檔時，Windows 會回報磁碟已滿；
    /// 檔案大小不變，不會真的占用磁碟。
    /// </summary>
    private static void WriteByteBeyondFreeSpace(Stream output, string directory)
    {
        var freeBytes = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).TotalFreeSpace;
        output.Position = freeBytes + (1L << 30);
        output.WriteByte(0);
    }

    /// <summary>先確認把緩衝區寫出時，案件資料夾的輸出檔回報磁碟已滿；這個位元組仍留在緩衝區，關檔時會再失敗一次。</summary>
    private static void AssertFlushReportsCaseDiskFull(Stream output)
    {
        var flush = Record.Exception(() => output.Flush());
        Assert.True(
            flush is JetActionException { Message: CaseDiskFull },
            $"在剩餘空間之外寫出應該回報案件所在磁碟已滿，實際是 {flush?.GetType().Name ?? "沒有錯誤"}：{flush?.Message}");
    }

    private static FileSystemAccessRule CreatingFilesDenied() => new(
        WindowsIdentity.GetCurrent().User!,
        FileSystemRights.CreateFiles,
        AccessControlType.Deny);

    /// <summary>在這個資料夾本身加上拒絕目前帳號建立檔案的規則，不往子資料夾繼承。</summary>
    private static void DenyCreatingFiles(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(CreatingFilesDenied());
        info.SetAccessControl(security);
    }

    private static void AllowCreatingFiles(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.RemoveAccessRule(CreatingFilesDenied());
        info.SetAccessControl(security);
    }

    private static ReportSheetWriter AddDetailSheet(SpreadsheetDocument document, string name, SheetUsedRange usedRange)
    {
        var workbookPart = document.WorkbookPart!;
        var sheets = workbookPart.RequiredWorkbook().Sheets!;
        var part = workbookPart.AddNewPart<WorksheetPart>();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(part),
            SheetId = sheets.Elements<Sheet>().Max(sheet => sheet.SheetId?.Value ?? 0U) + 1U,
            Name = name
        });
        return new ReportSheetWriter(name, part, usedRange);
    }

    /// <summary>
    /// 放在記憶體裡的輸出。累計寫入超過 <c>limit</c> 位元組時磁碟就算滿了：那一次寫入與之後的每一項操作，
    /// 包括定位、清空、寫出緩衝與關閉，都丟出 Windows 磁碟已滿的例外。<c>limit</c> 是 0 時一開始就是滿的。
    /// </summary>
    private sealed class FullDiskAfter(long limit) : Stream
    {
        private readonly MemoryStream _inner = new();
        private bool _full = limit == 0;
        private long _written;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set
            {
                ThrowIfFull();
                _inner.Position = value;
            }
        }

        public override void Flush() => ThrowIfFull();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfFull();
            return _inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            ThrowIfFull();
            _inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfFull();
            if (_written + count > limit)
            {
                _full = true;
                ThrowIfFull();
            }

            _inner.Write(buffer, offset, count);
            _written += count;
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    _inner.Dispose();
                    ThrowIfFull();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        private void ThrowIfFull()
        {
            if (_full)
            {
                throw new IOException("磁碟已滿（合成）", DiskFullHResult);
            }
        }
    }

    private sealed class SingleProjectLocator(string directory) : IProjectExportLocator
    {
        public string GetProjectDirectory(string projectId) => directory;
    }
}
