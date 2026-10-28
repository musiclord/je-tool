using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 支援日誌的事件名與 project.releaseLock 這個動作名各只定義一次：寫入端（動作分派、案件紀錄、案件開啟）與讀取端
/// （失敗摘要、案件歷史匯出）都用 Domain 的具名常數，不用字串比對（技術債一輪，2026-10-22）。
/// </summary>
public sealed class SupportEventNameOwnershipTests
{
    [Fact]
    public void EventNames_AreNamedConstants_SharedByWritersAndReaders()
    {
        Assert.NotNull(ProductSources.ProductType("JET.Domain.SupportEventNames"));

        var export = ProductSources.Product("Application", "Support", "SupportCaseHistoryExport.cs");
        Assert.Contains("SupportEventNames.", export, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"", export, StringComparison.Ordinal);

        var ring = ProductSources.Product("Infrastructure", "Diagnostics", "SupportRingBufferLoggerProvider.cs");
        Assert.Contains("SupportEventNames.ActionError", ring, StringComparison.Ordinal);
        Assert.DoesNotContain("== \"action.error\"", ring, StringComparison.Ordinal);

        var dispatcher = ProductSources.Product("Bridge", "ActionDispatcher.cs");
        Assert.Contains("EventName = SupportEventNames.ActionStart", dispatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("EventName = \"action.", dispatcher, StringComparison.Ordinal);

        var store = ProductSources.Product("Infrastructure", "Diagnostics", "SupportCaseHistoryStore.cs");
        Assert.DoesNotContain("\"support.history_started\"", store, StringComparison.Ordinal);
        Assert.DoesNotContain("\"support.history_gap\"", store, StringComparison.Ordinal);

        var diagnostics = ProductSources.Product("Application", "Support", "SupportActionDiagnostics.cs");
        Assert.DoesNotContain("EventName = \"support.case_opened\"", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseLockActionName_IsOneDomainConstant()
    {
        Assert.NotNull(ProductSources.ProductType("JET.Domain.JetActionNames"));

        var session = ProductSources.Product("Application", "Handlers", "Project", "ProjectSessionHandlers.cs");
        Assert.Contains("Action => JetActionNames.ProjectReleaseLock", session, StringComparison.Ordinal);
        var policy = ProductSources.Product("Domain", "ActionExecutionPolicy.cs");
        Assert.DoesNotContain("\"project.releaseLock\"", policy, StringComparison.Ordinal);
        var export = ProductSources.Product("Application", "Support", "SupportCaseHistoryExport.cs");
        Assert.Contains("JetActionNames.ProjectReleaseLock", export, StringComparison.Ordinal);
        Assert.DoesNotContain("\"project.releaseLock\"", export, StringComparison.Ordinal);
    }
}
