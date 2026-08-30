using JET.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SqlConnectionStringFactoryTests
{
    private static IConfiguration Config(Dictionary<string, string?> kv)
        => new ConfigurationBuilder().AddInMemoryCollection(kv).Build();

    [Fact]
    public void EnvOverride_Wins()
    {
        var cs = SqlConnectionStringFactory.Build(Config(new()), "Server=x;Database=y;Integrated Security=True;");
        Assert.Contains("Server=x", cs);
    }

    [Fact]
    public void Missing_Override_Returns_Empty_Even_When_Sql_Section_Has_Values()
    {
        var cs = SqlConnectionStringFactory.Build(Config(new()
        {
            ["Sql:Server"] = "localhost",
            ["Sql:Database"] = "JET_DEV",
            ["Sql:IntegratedSecurity"] = "true",
        }), envOverride: null);

        Assert.Empty(cs);
    }
}
