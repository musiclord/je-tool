// Only external transport/logging/options are substituted. The qualification
// compiles the actual pinned and patched AssemblyTestServer and boundary source.
namespace Microsoft.Extensions.Logging
{
    public interface ILogger { }
    public sealed class SilentLogger : ILogger { }
    public static class LoggerExtensions
    {
        public static void LogDebug(this ILogger logger, string message, params object?[] args) { }
        public static void LogDebug(this ILogger logger, Exception exception, string message, params object?[] args) { }
        public static void LogWarning(this ILogger logger, Exception exception, string message, params object?[] args) { }
    }
}
namespace Stryker.Abstractions.Options
{
    public interface IStrykerOptions { string? OutputPath { get; } LogOptions LogOptions { get; } }
    public sealed class LogOptions { public bool LogToFile { get; init; } }
}
namespace Stryker.TestRunner
{
    public sealed class TestHostCrashedException(string message) : Exception(message) { }
}
namespace Stryker.TestRunner.MicrosoftTestPlatform
{
    using Microsoft.Extensions.Logging;
    using Models;
    public sealed class ResponseListener
    {
        public Task WaitCompletionAsync() => Task.CompletedTask;
        public Task<bool> WaitCompletionAsync(TimeSpan timeout) => Task.FromResult(true);
    }
    public interface ITestingPlatformClient : IDisposable
    {
        Task InitializeAsync();
        Task ExitAsync(bool gracefully = true);
        Task<int> WaitServerProcessExitAsync();
        Task<ResponseListener> DiscoverTestsAsync(Guid requestId, Func<TestNodeUpdate[], Task> action);
        Task<ResponseListener> RunTestsAsync(Guid requestId, Func<TestNodeUpdate[], Task> action, TestNode[]? testNodes);
    }
    internal sealed class DefaultTestServerConnectionFactory(Stryker.Abstractions.Options.IStrykerOptions? options) : ITestServerConnectionFactory
    {
        public (ITestServerListener Listener, int Port) CreateListener() => throw new NotSupportedException();
        public ITestServerProcess StartProcess(string assembly, int port, Dictionary<string, string?> environmentVariables) => throw new NotSupportedException();
        public ITestingPlatformClient CreateClient(Stream stream, IProcessHandle processHandle, ILogger logger, string? rpcLogFilePath) => throw new NotSupportedException();
    }
}
