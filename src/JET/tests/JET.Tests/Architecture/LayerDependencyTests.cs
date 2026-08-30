using System;
using System.Collections.Generic;
using System.Linq;
using NetArchTest.Rules;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 依賴方向鐵律的機器守衛（AGENTS.md §Non-Negotiable Architecture）：
/// <code>Bridge / Form1(Host) → Application → AuditCore → Domain ← Infrastructure</code>
///
/// 正式程式全部型別同在單一組件 <c>JET.dll</c>，層級以「命名空間」界定
/// （<c>JET.Application</c> / <c>JET.AuditCore</c> / <c>JET.Domain</c> /
/// <c>JET.Infrastructure</c> / <c>JET.Bridge</c>；
/// 合成根 <c>JET</c>、Host 不在受測範圍，允許看見全部）。NetArchTest 以 Mono.Cecil 掃 IL 依賴，
/// 因此連「在 handler 方法本體內 <c>new LocalXxxRepository()</c>」這種只在方法內部出現、
/// 型別簽章看不到的違規也抓得到——把 code review 才守得住的邊界升級為 CI 可擋。
///
/// 測試策略：以 <see cref="Domain.JetActionException"/> 定位正式組件，避免誤載入測試組件本身。
/// 每條 ShouldNot 都可能在「命名空間篩出空集合」時真空通過，故另立
/// <see cref="NetArchTest_DetectsRealDependencies_PositiveControl"/> 作正向對照，
/// 確認分析引擎真的讀到了 IL 依賴。
/// </summary>
public class LayerDependencyTests
{
    /// <summary>正式組件 JET.dll（以 Domain 的公開型別定位，不載入測試組件）。</summary>
    private static readonly System.Reflection.Assembly ProductionAssembly =
        typeof(global::JET.Domain.JetActionException).Assembly;

    private const string ApplicationNamespace = "JET.Application";
    private const string AuditCoreNamespace = "JET.AuditCore";
    private const string DomainNamespace = "JET.Domain";
    private const string InfrastructureNamespace = "JET.Infrastructure";
    private const string BridgeNamespace = "JET.Bridge";

    /// <summary>
    /// Application 可依賴 AuditCore 與既有 Domain 契約，**不得**依賴 Infrastructure
    /// （provider 分歧、SQL 執行、檔案 I/O 一律留在 Infrastructure）。
    /// </summary>
    [Fact]
    public void Application_ShouldNot_DependOn_Infrastructure()
    {
        var result = Types.InAssembly(ProductionAssembly)
            .That().ResideInNamespace(ApplicationNamespace)
            .ShouldNot().HaveDependencyOn(InfrastructureNamespace)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            Format("Application 依賴了 Infrastructure（違反依賴方向，provider/SQL/I-O 應留在 Infrastructure）", result));
    }

    /// <summary>
    /// Domain 是最內層純邏輯，**不得**反向依賴任何外層
    /// （Application / AuditCore / Infrastructure / Bridge）。
    /// </summary>
    [Fact]
    public void Domain_ShouldNot_DependOn_OuterLayers()
    {
        var result = Types.InAssembly(ProductionAssembly)
            .That().ResideInNamespace(DomainNamespace)
            .ShouldNot().HaveDependencyOnAny(
                ApplicationNamespace, AuditCoreNamespace, InfrastructureNamespace, BridgeNamespace)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            Format("Domain 反向依賴了外層（Application / AuditCore / Infrastructure / Bridge）", result));
    }

    [Fact]
    public void AuditCore_ShouldNot_DependOn_OuterLayers()
    {
        var result = Types.InAssembly(ProductionAssembly)
            .That().ResideInNamespace(AuditCoreNamespace)
            .ShouldNot().HaveDependencyOnAny(ApplicationNamespace, InfrastructureNamespace, BridgeNamespace)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            Format("AuditCore 依賴了外層（Application / Infrastructure / Bridge）", result));
    }

    [Fact]
    public void AuditCore_ShouldNot_DependOn_WebView2()
    {
        var result = Types.InAssembly(ProductionAssembly)
            .That().ResideInNamespace(AuditCoreNamespace)
            .ShouldNot().HaveDependencyOn("Microsoft.Web.WebView2")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            Format("AuditCore 依賴了 Microsoft.Web.WebView2", result));
    }

    [Fact]
    public void AuditCore_PublicTypes_MatchDocumentedWhitelist()
    {
        var expected = new[]
        {
            "JET.AuditCore.AuditCaseSnapshot",
            "JET.AuditCore.AuditExecutionPlan",
            "JET.AuditCore.AuditOutcome",
            "JET.AuditCore.AuditRunManifest",
            "JET.AuditCore.AuditUserParameters",
            "JET.AuditCore.ISqlDialect",
            "JET.AuditCore.JetAuditProgram",
            "JET.AuditCore.ProcedureDefinition",
            "JET.AuditCore.ProcedureVerdict",
            "JET.AuditCore.ValidationProcedures"
        };
        var actual = ProductionAssembly.GetExportedTypes()
            .Where(type => string.Equals(type.Namespace, AuditCoreNamespace, StringComparison.Ordinal)
                || (type.Namespace?.StartsWith(AuditCoreNamespace + ".", StringComparison.Ordinal) ?? false))
            .Select(type => type.FullName ?? type.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            expected.OrderBy(static name => name, StringComparer.Ordinal).SequenceEqual(actual),
            "JET.AuditCore 的 public 型別必須精確等於文件化白名單。" +
            "\n預期：\n  " + string.Join("\n  ", expected) +
            "\n實際：\n  " + string.Join("\n  ", actual) +
            "\n新增 public 型別前，先評估是否破壞 JetAuditProgram 唯一 facade。");
    }

    /// <summary>
    /// Bridge 只做 WebMessage 傳輸與 action 分派，依賴 Application/Domain，**不得**依賴 Infrastructure
    /// （wire 契約與 provider 實作之間不可短路）。合成根 <c>JET</c> 與 Host 不在此命名空間內，不受此規束。
    /// </summary>
    [Fact]
    public void Bridge_ShouldNot_DependOn_Infrastructure()
    {
        var result = Types.InAssembly(ProductionAssembly)
            .That().ResideInNamespace(BridgeNamespace)
            .ShouldNot().HaveDependencyOn(InfrastructureNamespace)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            Format("Bridge 依賴了 Infrastructure（傳輸層不得短路到 provider 實作）", result));
    }

    [Fact]
    public void Infrastructure_TypesDependingOnApplication_MatchDocumentedWhitelist()
    {
        var expected = new[]
        {
            "JET.Infrastructure.DemoWorkbookWriter",
            "JET.Infrastructure.SqlServerBackendProbe",
            "JET.Infrastructure.SqlServerHealthCheck"
        };
        var actual = Types.InAssembly(ProductionAssembly)
            .That().ResideInNamespace(InfrastructureNamespace)
            .And().HaveDependencyOn(ApplicationNamespace)
            .GetTypes()
            .Select(type => type.FullName ?? type.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            expected.OrderBy(static name => name, StringComparer.Ordinal).SequenceEqual(actual),
            "Infrastructure 依賴 Application 的型別必須精確等於文件化白名單。" +
            "\n預期：\n  " + string.Join("\n  ", expected) +
            "\n實際：\n  " + string.Join("\n  ", actual) +
            "\n新增反向依賴前，先評估把共用 DTO 下沉 Domain。");
    }

    /// <summary>
    /// 正向對照（harness 自我驗證）：若分析引擎正常，Application 內「依賴 Domain」的型別必為非空
    /// （大量 handler 都注入並使用 Domain 契約）。這條若失敗，代表 NetArchTest 根本沒讀到 IL 依賴，
    /// 上面三條 ShouldNot 便全數形同虛設（空集合真空通過）。
    /// </summary>
    [Fact]
    public void NetArchTest_DetectsRealDependencies_PositiveControl()
    {
        var applicationTypesUsingDomain = Types.InAssembly(ProductionAssembly)
            .That().ResideInNamespace(ApplicationNamespace)
            .And().HaveDependencyOn(DomainNamespace)
            .GetTypes();

        Assert.NotEmpty(applicationTypesUsingDomain);
    }

    private static string Format(string headline, NetArchTest.Rules.TestResult result)
    {
        IEnumerable<Type> failing = result.FailingTypes ?? Enumerable.Empty<Type>();
        var names = failing
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToList();

        return names.Count == 0
            ? headline
            : headline + "：\n  " + string.Join("\n  ", names);
    }
}
