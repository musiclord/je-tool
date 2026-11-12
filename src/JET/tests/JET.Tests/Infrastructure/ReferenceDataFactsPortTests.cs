using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class ReferenceDataFactsPortTests
{
    // 科目配對已經寫進案件後才送到的取消，不能讓這次匯入看起來被取消：寫入後讀匯入狀態不受取消影響，
    // 回應才帶得回匯入後的分類清單與新的匯入狀態。
    [Fact]
    public async Task AccountMappingImport_CancelledAfterTheWrite_StillReturnsImportAndState()
    {
        using var cancellation = new CancellationTokenSource();
        var port = new ReferenceDataFactsPort(new CancelAfterWritePersistence(cancellation), null!, null!);
        string[] columns = ["GL_Number", "GL_Name", "Standardized Account Name*"];

        var facts = await port.ExecuteAsync(
            JetAuditProgram.Plan(new AccountMappingRequest(
                "synthetic-cancel-after-write",
                "mapping.csv",
                FileExists: true,
                Extension: ".csv",
                Mode: "replace")),
            new ImportSourceDescriptor("mapping.csv", "mapping.csv", null, null, null),
            columns,
            JetAuditProgram.PrepareAccountMappingProjection(columns),
            NoRows(),
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("synthetic-batch", facts.Import.BatchId);
        Assert.Equal("synthetic-batch", facts.State.BatchId);
    }

    private static async IAsyncEnumerable<StagingRow> NoRows()
    {
        await Task.Yield();
        yield break;
    }

    /// <summary>寫入完成的同時收到取消；之後用已取消的權杖讀狀態會照資料庫連線的行為回報取消。</summary>
    private sealed class CancelAfterWritePersistence(CancellationTokenSource cancellation) : IAccountMappingImportPersistence
    {
        public Task<AccountMappingImportResult> ImportAsync(
            string projectId,
            ImportSourceDescriptor source,
            IReadOnlyList<string> columns,
            AccountMappingProjection projection,
            IAsyncEnumerable<StagingRow> rows,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromResult(new AccountMappingImportResult(
                "synthetic-batch",
                0,
                columns,
                source.FileName,
                DateTimeOffset.UnixEpoch,
                AccountCategoryCatalog.BuiltInSnapshot));
        }

        public Task<AccountMappingState?> FindStateAsync(string projectId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<AccountMappingState?>(new AccountMappingState(
                "synthetic-batch",
                0,
                "mapping.csv",
                DateTimeOffset.UnixEpoch,
                HasAnyCategory: false,
                HasRevenue: false,
                HasCounterpart: false));
        }
    }
}
