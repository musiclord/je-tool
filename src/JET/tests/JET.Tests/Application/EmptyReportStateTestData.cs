using JET.Domain;

namespace JET.Tests.Application;

// 第9批高3；Public首敗100911120：匯出現在須讀完整目前來源，這兩個替身只供明示沒有篩選情境的seam fixture。
// 不加到Unconfigured的預設值；其他未宣告依賴仍保持null並立即失敗。
internal static class EmptyReportStateTestData
{
    internal static IResultStaleStateStore StaleStates { get; } = new EmptyStaleStates();
    internal static IFilterScenarioStore Scenarios { get; } = new EmptyScenarios();

    private sealed class EmptyStaleStates : IResultStaleStateStore
    {
        public Task InvalidateForPreparationDateChangeAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AuditResultStaleState> ReadAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult(new AuditResultStaleState(false, false, false));
        public Task<string> ReadFilterDataRevisionAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult("0");
    }

    private sealed class EmptyScenarios : IFilterScenarioStore
    {
        public Task ReplaceAllAsync(string projectId, IReadOnlyList<SavedFilterScenario> scenarios, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SavedFilterScenario>> ListAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SavedFilterScenario>>([]);
    }
}
