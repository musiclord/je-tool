using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SqlServerCommandFactoryTests
{
    private static SqlServerProjectDatabase NewDb()
        => new(new SqlServerConnectionOptions("Server=localhost;Database=JET_DEV;Integrated Security=True;"));

    [Fact]
    public void CreateCommand_SubstitutesSchemaToken()
    {
        using var conn = new SqlConnection();
        var schema = SqlServerProjectSchema.For("ACME-FY2025");
        using var cmd = NewDb().CreateCommand(conn, "ACME-FY2025", "SELECT * FROM {s}.target_gl_entry");
        Assert.Equal($"SELECT * FROM [{schema}].target_gl_entry", cmd.CommandText);
        Assert.DoesNotContain("{s}", cmd.CommandText);
    }
}
