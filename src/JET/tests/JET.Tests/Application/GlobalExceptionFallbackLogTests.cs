using System.Text.Json.Nodes;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class GlobalExceptionFallbackLogTests
{
    [Fact]
    public void Write_ThreeGlobalSources_ProducesOneJsonLinePerFailure()
    {
        using var root = new TempProjectRoot();
        var log = new GlobalExceptionFallbackLog(root.Path);

        log.Write("Application.ThreadException", new InvalidOperationException("thread\nfailure"));
        log.Write("AppDomain.UnhandledException", new ApplicationException("domain failure"), isTerminating: true);
        log.Write("TaskScheduler.UnobservedTaskException", new AggregateException("task failure"));

        var lines = File.ReadAllLines(log.FilePath);
        var entries = lines.Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();

        Assert.Equal(3, lines.Length);
        Assert.Equal(
            [
                "Application.ThreadException",
                "AppDomain.UnhandledException",
                "TaskScheduler.UnobservedTaskException"
            ],
            entries.Select(entry => entry["Source"]!.GetValue<string>()));
        Assert.False(entries[0]["IsTerminating"]!.GetValue<bool>());
        Assert.True(entries[1]["IsTerminating"]!.GetValue<bool>());
        Assert.Equal("thread\nfailure", entries[0]["Message"]!.GetValue<string>());
    }
}
